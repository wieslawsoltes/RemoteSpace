# Session display and presentation review

RemoteSpace separates **local display transforms**, **server resolution**, and
**RDP output suppression**. They are not interchangeable. Microsoft's public
[Windows App display settings](https://learn.microsoft.com/en-us/windows-app/display-settings)
document similarly distinguishes fit-to-window scaling from resolution changes.
This work follows those interaction conventions, not a claim of pixel-perfect
visual parity or support for Microsoft's cloud services.

## Connection commands

`SessionCommandBar` is a reusable Uno control. The host supplies actions and owns
the session. Six compact, named icon commands replace the wide row of text
buttons: Fit, Display settings, Remote resolution, Clipboard, Session actions,
and Full screen. Context menus are constructed when requested and hidden when
the bar unloads. The PC identity and actual desktop status occupy the remaining
space; the identity collapses at narrow widths without hiding primary commands.
Clipboard permission-sensitive operations still originate from explicit user
commands and await the actual platform operation.

Fit scales without changing server geometry. Display settings provide local
100–400% presets, incremental zoom, and pan-limit commands. Zoom preserves the
remote pixel under the center of the view where bounds permit. Local scrollbars
expose the whole desktop at enlarged scale; arrows, Home/End and PageUp/PageDown
control their positions and do not generate remote keystrokes. Capture exports
the original remote-resolution raster, not the locally zoomed view.

Remote resolution separately validates the requested layout against negotiated
limits and flushes held input before sending. The visible dimensions change
only when the server actually reactivates. Pause/resume affects RDP graphics
output rather than remote program execution. Hidden local session tabs continue
to retain incoming pixels; switching tabs is not a network pause.

## Reusable models and correctness

`DesktopViewportLayout.Calculate` uses bounded, finite host sizes, remote pixel
dimensions, local zoom and remote-pixel offsets. It solves interdependent
scrollbar visibility before clamping offsets and returns value types without
allocating a zoom-sized buffer. `Viewport.MapVisible` maps only the intersection
of the image and the last presented host area. Captured drags clamp to that
visible intersection; they cannot jump to off-screen pixels when the image is
larger than the viewport. Nonfinite input is rejected before conversion.

`RemoteDesktopView.SetScale`, `PanTo`, `ViewLayout` and `ViewChanged` expose the
same behavior to other hosts. A changed transform is not used for input until
it is painted. Reapplying an unchanged layout does not invalidate a valid
transform or request an unnecessary redraw.

`CommandActivation.WaitForReleaseAsync` gives semantic/native input routing a
complete event turn and waits for Enter/Space release before creating a new
editable dialog. `ShowDialogAsync` also takes a normal button-click deferral
until the closing key is released, keeping focus inside the dialog so Save
cannot reopen the previously focused device menu. It avoids treating the
gesture that opens Edit as its default Save command. The UI-thread timer stops on completion, cancellation, unload or
a bounded timeout. The dialog retains its normal default button and explicit
Add/Save behavior. It does not set DOM input values or bypass user interaction.

## Demand-driven presentation

`PresentationGate` coalesces concurrent dirty requests into one ticket. The
owner schedules a one-shot 16 ms dispatcher timer; the timer stops before
invalidating the surface. Without new dirt, no periodic presentation timer is
left running. Disabling presentation invalidates queued tickets while retaining
dirty state. Re-enabling schedules a catch-up callback. Stale callbacks cannot
consume a newer generation's dirty flag. Native/default cursor movement does
not request a bitmap redraw when there is no client-composed cursor to repaint.

The application sets `IsPresentationActive` from its selected session and
navigation section. This does not promise zero total CPU while hidden: RDP
decoding, network input, other app controls, and the Uno host still perform
work. It is not an automatic browser-tab/page-visibility or remote-output policy.
`ScheduledPresentations`, `AppliedUpdates`, `PresentedFrames` and
`PixelInvalidations` count different stages; none is an FPS, latency or GPU
throughput measurement by itself.

## Validation

Run `dotnet run --project tests/RemoteSpace.Presentation.Tests -c Release` for
finite geometry, scrollbar interdependence, pixel reachability, captured input,
concurrent dirty bursts, stale callbacks, disposal and allocation regressions.
The warm layout workload runs 100,000 calculations and requires zero managed
allocation. The concurrency workload issues 100,000 requests against one
scheduled ticket; hidden requests must produce one catch-up ticket, not a queue.

`tests/browser/session_display.py` drives actual menus and scrollbars against
the authorized XRDP fixture. It captures the real remote raster, compares flat
pixel samples at 200% and both pan limits, checks inverse pointer coordinates,
requires no server resolution change from local zoom, verifies all six primary
commands at narrow width, and changes the remote background while the view is
hidden to test catch-up. Other session checks retain independent server-side
keyboard effects, resolution changes, clipboard round trips, graphics
suppression and reconnect. Workspace tests hold the initiating Enter across
multiple frames and then require an editable dialog and a successful save.

The exact commit's CI artifacts determine which checks passed; source presence
or successful compilation alone is not browser-runtime evidence. Browser CI
uses Chromium/SwiftShader, not hardware-GPU performance validation. Remaining
protocol/services, live Windows NLA/licensing qualification, multi-monitor,
IME/touch/accessibility, native-host matrix and security-audit gaps remain in
[compatibility](compatibility.md).

Workspace and live-session browser jobs run independently from the same compiled
artifact, so a failure in one does not hide the other suite's diagnostics. Pages
deployment depends on both jobs as well as the build; neither runtime gate is
bypassed. The workspace regression holds both opening and Save keys through
multiple frames and requires no leftover or reopened menu after saving.

Menu-opening buttons use the same activation-release boundary before transferring
focus into a flyout. Device flyouts are hidden when their owning card unloads;
this does not change the tile/row design or implement the excluded compact-row
and captured-profile-identity follow-up. The browser driver waits for the real
input or its expander to appear after deferred dialog creation before deciding
whether to expand. It still uses one real keyboard gesture and checks the result.
