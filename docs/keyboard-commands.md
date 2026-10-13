# Remote keyboard commands and input allocation review

The session toolbar now has a Keyboard menu with nine explicitly remote commands:
Windows Security, Task Manager, Start menu, next/previous application, window menu,
Print Screen, active-window screenshot, and Escape. Each item shows its chord as
an accelerator hint, not as a new locally registered accelerator. These commands
help when the local browser or operating system reserves the physical key chord.
They do not enable keyboard locking, IME composition, arbitrary hotkey remapping,
or a full on-screen keyboard. The remote operating system determines the effect.

Microsoft's [Windows App input documentation](https://learn.microsoft.com/en-us/windows-app/input-keyboard-mouse-touch-pen)
describes the corresponding remote Windows operations and alternative RDP shortcuts.
RemoteSpace sends the underlying physical chords from explicit menu commands;
it does not claim to implement that entire shortcut translation table. It does
not invoke local Windows Security or claim to circumvent a host/browser shortcut.
Artwork and controls remain independent, using the existing Uno theme resources.

## Reusable API and correctness

`RemoteKeyboardCommands` belongs to the platform-independent Input package. Its
read-only catalog provides labels and chord hints. `Write` fills a caller-owned
span with balanced scan-code make/break events, pressing modifiers first and
releasing them last in reverse order. Extended Delete/PrintScreen retain E0 flags.
Invalid commands and insufficient destination spans are rejected before writing.

`InputState.TryEnqueueCommand` combines releases for every tracked physical key
and pointer button with the complete shortcut in one `RemoteInputQueue` admission.
Failure on capacity or a stopped queue leaves held state and queued events intact.
Success clears held state only after the whole transaction is accepted. The queue
may transmit it in more than one batch but never admits only part of the command;
transport errors still follow the normal disconnection path. No networking layer
is replaced and no queue bound is enlarged.

The normal small release list uses stack memory. A large list uses a pooled buffer
only when its size fits the queue's fixed capacity; the buffer is cleared and
returned even if enqueue fails. The method retains InputState's single-UI-owner
contract. It is not a concurrent replacement for the view's input state.

`RemoteDesktopView.SendKeyboardCommand` validates UI-thread ownership, disposal,
active selected view and graphics-output state at invocation, not only at menu
construction. Commands are disabled while paused and a stale opened menu cannot
send to a disposed or inactive view. Existing `SendSecureAttention` now uses this
same transaction. No command stores passwords or typed text in diagnostics.

## Allocation improvements

Empty releases, zero wheel motion and empty Unicode text return shared empty
arrays. Populated release, wheel and Unicode arrays are allocated at their exact
length instead of creating a List plus its backing storage and a second output
array. UTF-16 is fully validated before the output array is allocated. Wheel
splitting still preserves signed nine-bit deltas, axis flags and coordinates.
These changes remove temporary encoding allocations, not RDP packet or network
allocations, and do not establish end-to-end latency, FPS or power improvements.

## Validation and boundaries

The input suite compares every chord against an independent scan-code reference,
checks reverse releases, small-buffer immutability, queue failure/success,
extended physical releases, late keyups, large pooled release lists, strict UTF-16
and all 48,002 supported signed wheel/axis combinations. Allocation checks include
100,000 warmed empty-operation/chord iterations and single-array output budgets.
They run alongside all pre-existing input, graphics and lifecycle regressions.

The live browser suite uses real Uno menus and the original RDP engine against an
authorized loopback XRDP fixture. It checks named commands, compares Alt+Tab and
Alt+Shift+Tab make/break sequences against an independent Xorg raw-input log,
checks no duplicate menu activation keys, verifies paused availability, and checks
seven non-overlapping toolbar targets at a narrow window width. It does not use a
fake framebuffer, private managed UI entry point or alternate RDP library.
Windows-only effects such as the Windows Security screen and Task Manager need
separate Windows-host qualification; XRDP proves transport of the tested physical
chords, not those Windows effects. Actual passing results are in exact-head CI
artifacts. This work is independent of the unpublished PR #7 tab/automatic-resolution
patches and does not complete the remaining Windows App parity matrix.

The pinned xorgxrdp 0.10.5 keyboard driver deliberately generates an extra raw
Tab release before a Tab press to reconcile X11 repeats. The independent Xorg
assertion therefore requires five/seven raw events for the two tested chords,
while the browser-to-RDP check still requires exactly four/six input events.
RemoteSpace does not synthesize that server-only release. The fixture behavior
is explicit in [sendDownUpKeyEvent and the Tab case](https://github.com/neutrinolabs/xorgxrdp/blob/v0.10.5/xrdpkeyb/rdpKeyboard.c);
the two observation points must not be treated as identical event streams.
