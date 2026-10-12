# Automatic session resolution and display pages

The General and Display tabs in additional PC settings separate connection fields
from display configuration. `SettingsSectionTabs` is a reusable lazy page host:
only the selected page owns an attached input subtree, while visited pages retain
the same controls and edited values. The Display editor is not constructed before
its first visit. Re-entry recreates the bounded native tab selectors, not editors.
Save reads both retained pages; Cancel writes neither. Device cards/rows are not
changed by this work.

Display settings offer **Update the resolution on resize**. When selected, fixed
width/height are hidden, and Fit/local zoom are disabled without losing their
values. The stored width and height remain the initial connection fallback until
the server advertises display control. Restore display defaults disables automatic
resolution and resets only the display preferences. A fresh session starts in Fit
when automatic resolution is selected; transient toolbar zoom remains local.

This follows the distinction in [Microsoft's Windows App display settings](https://learn.microsoft.com/en-us/windows-app/display-settings):
Fit scales locally, whereas automatic resolution changes the remote desktop's
pixel dimensions. It is an independent single-display implementation, not a claim
of complete Windows App UI, service, multi-monitor or high-DPI parity.

## Reusable components and performance

`DisplayResizeTarget` maps one remote pixel to one local layout unit. Fractional
units are floored and odd widths lose one pixel to satisfy display-control rules.
This is not Retina/native-device-pixel scaling or remote DPI configuration. Bounds
are checked before integer conversion: finite axes 200–8192, even width, negotiated
monitor/area limits and the local 16-megapixel ceiling. Tiny/minimized, oversized,
and unnegotiated viewports do not request a layout; the actual desktop is retained.
The existing Match window command uses the same validation instead of casting and
silently clamping an invalid viewport.

`DisplayResizeQueue` is a deterministic, single-owner Core planner with an injected
monotonic millisecond clock. It retains at most one in-flight request plus one latest
target. A 250 ms quiet interval combines resize bursts; a confirmed request is
followed by at least one further settle interval before another request. Repeated
identical offers neither change the deadline nor allocate a target object. There
are no tasks, timers, queues of old layouts or callbacks inside this planner.

`SessionDisplayController` integrates the planner with an injected session and
`RemoteDesktopView`. It uses a demand-driven DispatcherTimer only for a pending
settle deadline, never a periodic idle poll. The **full view** size is observed,
not the canvas size reduced by local scrollbars, avoiding resolution/scrollbar
feedback. Loaded, presentation-selected, active, negotiated and graphics-output
states gate new automatic requests. Hidden or paused views retain the latest target
without sending it and settle once on resumption. A request already sent cannot be
undone by hiding the view or clearing the option; its confirmation is still drained.

Manual presets, custom resolutions and Match window turn off automatic mode and
share the same serialized writer. They wait behind an already submitted request
rather than overlapping server reactivations. Held input is released and flushed
before a request. No framebuffer is resized speculatively and no synthetic pixels
are substituted; the existing server events control the actual desktop allocation.

`RemoteDisplayRequest` subscribes before sending and requires matching geometry
plus Active state, not just a completed WebSocket write. Pre-request geometry does
not count. Send failure, terminal state, caller cancellation and a ten-second host
timeout stop the operation and unsubscribe listeners. Automatic failure disables
the mode and reports an error instead of retrying forever. Session closure cancels
and drains the coordinator before detaching/disposal of the view.

## Persistence and RDP files

`ConnectionProfile.UpdateResolutionOnResize` is false for new PCs and omitted
legacy workspace JSON, preserving existing behavior. Save/export/import/duplicate
and transaction candidates retain it. This setting does not alter authentication,
clipboard consent, `ConnectionOptions`, or server DPI negotiation.

RDP files use `dynamic resolution:i:0|1`. The absent-field default is **1**, matching
[Microsoft's supported RDP properties](https://learn.microsoft.com/en-us/azure/virtual-desktop/rdp-properties#dynamic-resolution).
Export writes the value explicitly, so a new RemoteSpace PC's opt-out remains an
opt-out on round trip. Other unsupported RDP settings still generate warnings.
Malformed numeric types, non-boolean values and NUL are rejected. Local zoom and
Fit are retained when switching automatic mode off and on.

## Evidence and limitations

Core tests cover bounds, changing capability limits, superseding bursts, stale
completion, visibility/state gates, zero-allocation repeated offers, synchronous
confirmation, delayed activation, wrong-size replies, cancellation, timeout and
send/terminal failure cleanup. Model tests cover legacy defaults, strict import,
round trips and isolation. These are deterministic/fixture tests, not server
interoperability or hardware-GPU throughput measurements.

The browser workspace test uses native General/Display tabs and real export/reload
commands. It verifies input-subtree isolation, retained edits, automatic mode and
Cancel/display-only reset. The live browser test uses the actual Uno app, bridge
and authorized XRDP fixture: it compares server geometry to real view bounds and
requires independently rendered desktop colors, latest-size convergence, idle
request counts, hide/show and graphics-pause behavior, and a manual override.
All earlier real input, clipboard, pixel, focus and reconnect checks are retained.
Passing results must be read from the exact revision's workflow artifacts; the
presence of these tests does not itself claim they have passed.

Multi-monitor/window management, native-pixel High DPI, IME/touch/accessibility
qualification, live Windows NLA/licensing, advanced graphics/UDP, redirected devices,
cloud services and the other compatibility-matrix gaps remain separate work.

The first real tab-click run exposed a semantic coordinate defect: tab selectors
were painted inside the dialog but their early detached registrations retained
an off-window position. The page host now removes its provisional root and
attaches the retained hierarchy under a live semantic geometry owner on Loaded.
Unloading removes it cleanly. The click regression still uses the actual native
tab and additionally requires an in-window hit target; it does not substitute
keyboard selection, guessed canvas coordinates or modified DOM geometry.
