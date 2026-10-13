# Full-screen session layout

Full screen now presents the selected remote desktop beneath its existing compact
connection toolbar, without the workspace title, navigation rail, page heading,
session selectors, margins or footer. The toolbar stays visible and its diagonal
arrow becomes **Exit full screen**. Exiting restores the workspace and current
session without reconnecting or changing the server desktop resolution. The live
surface and its decoded framebuffer are retained; this is not another renderer.

The behavior follows the browser session controls described in Microsoft's
[Windows App display settings](https://learn.microsoft.com/en-us/windows-app/display-settings#full-screen).
No Microsoft image or proprietary UI asset is included, and this is not complete
Windows App visual/functional parity. A pinned toolbar is intentional: auto-hide,
multi-window fullscreen and starting automatically in fullscreen remain separate.

## Reusable controller

`WindowFullscreenController` belongs to the Controls package and takes a native
FrameworkElement owner; it has no RDP, device-catalog or app-layout dependency.
Initialize it before any user command. `SetAsync` invokes the browser request on
the activation stack without deferring for module import, focus or a timer. The
promise and actual `fullscreenElement` establish completion; a rejected or no-op
request does not optimistically claim fullscreen. A single pending operation is
admitted rather than accumulating commands. Requests do not bypass browser policy.

The browser controller observes actual `fullscreenchange` events, including exits
initiated by the browser/OS or `document.exitFullscreen()`. There is no idle timer,
keyboard locking, Escape interception or mutation of private Uno state. The host
retains its normal escape mechanism. The application resets its chrome and button
labels on an observed exit. Closing the owning session leaves fullscreen first;
terminal session state also requests exit. A session that ends while entry is
pending is checked again before committing the full-screen layout.

Disposal removes event subscriptions, drains an outstanding platform request and
exits fullscreen that this controller acquired. It does not exit another element
or unrelated pre-existing fullscreen. Registration remains reserved during pending
cleanup so another owner cannot race that exit. Native desktop uses the public
ApplicationView API and size-change observations. The complete Windows/macOS/Linux
native full-screen/OS-shortcut matrix has not been qualified merely by compilation.

The app moves the same retained session panel between a windowed host and a root-level
fullscreen host rather than reconstructing the remote view.
Only the small native session-tab selector set is refreshed when returning, using
the existing pinned-Uno accessibility workaround. No new RDP negotiation, remote
resize request, synthetic surface or alternate client is introduced.

## Validation

Sixteen exact-module Node tests cover actual-state notifications, synchronous and
asynchronous failures, one-slot admission, repeated events, disposed commands,
late completion, listener removal and ownership isolation. A native Chromium
component test uses real fullscreen requests/promises/events and a real denied
iframe policy. No platform method is replaced in that integration test.

The full Uno/XRDP test enters/exits using actual toolbar commands, verifies the
remote view fills the host beneath the toolbar and contains independently received
remote pixels, checks an external platform exit, requires restored tab/close
controls, and asserts unchanged server sizes/layout-request counts and no leaked
local activation keys. The headless external-exit scenario uses the public browser
exit API; it does not claim that every OS's physical Escape accelerator was tested.
All existing session and workspace checks remain required. Exact revision workflow
artifacts, not test source alone, establish passing execution.

Platform behavior references: [Fullscreen standard](https://fullscreen.spec.whatwg.org/)
and [requestFullscreen](https://developer.mozilla.org/en-US/docs/Web/API/Element/requestFullscreen).

## Geometry ownership correction

The first full Uno test entered real browser fullscreen and painted the desktop,
but the semantic NavigationView content presenter retained its former 105-pixel
horizontal offset. Its width updated while its origin did not, leaving toolbar
hit targets outside the window. This was a product geometry bug, not a failed
fullscreen permission request. The pixel/geometry thresholds were not relaxed.

A separate root-level semantic host now owns the retained panel while fullscreen
is active. The normal navigation tree is hidden unchanged, instead of trying to
remove its pane in place. The same toolbar/view/framebuffer instances move through
the normal native removal/addition lifecycle once per transition; no per-frame
reparenting or browser coordinate adjustments are used. An InfoBar in the fullscreen
host keeps command failures visible even while the workspace is hidden. Tests also
require all seven toolbar targets to remain in the window and the remote view to
belong to the appropriate host before and after entry/exit.

## Confirmed ownership and cleanup

A new regression reproduces disposal exiting unrelated document fullscreen after
our own pending entry was denied. Browser ownership is now recorded only when the
platform entry promise succeeds with the document element active, not when the
request is merely submitted. Pending successful entry still drains and exits on
disposal, while rejection never grants ownership. Native desktop disposal likewise
tracks successful controller-initiated entry rather than exiting any pre-existing
ApplicationView fullscreen state. Initialization after disposal is rejected even
when a previous initialization task has completed. Native-host runtime qualification
remains separate from the exercised browser lifecycle and successful compilation.

Both fullscreen entries also compare flat samples from the real user-downloaded
remote-resolution PNG against its fitted on-screen image, requiring at least
97% matching samples. The independent pointer observation requires inverse mapping
to the same remote pixel, not merely an enlarged HTML/semantic rectangle. Capture
and entry commands must emit no remote keystrokes; deliberate pointer packets
are accounted separately before testing the exit commands. This retains the same
original remote framebuffer and does not fabricate a test surface.


## Responsive windowed geometry regression

The restoration baseline is taken only after validating the windowed image and
inverse pointer mapping. The preceding narrow-window test restores the browser
viewport, which can precede Uno's responsive layout and paint. The additional
baseline check exposed a persistent defect rather than merely a transient frame:
its recorded origin kept the former 12-pixel padding instead of the restored
28-pixel padding, and the pointer arrived at remote x=111 instead of x=127 even
after waiting. Uniform raster samples alone did not expose this 16-pixel shift;
the independent inverse-input assertion did.

A real `SemanticLayoutHost` now owns the workspace's content row. Responsive
padding moves that retained semantic parent instead of a pruned structural Grid,
so descendant hit targets inherit the actual offset without DOM coordinate writes,
per-frame tree rebuilding, or hard-coded pointer corrections. The hidden windowed
workspace no longer changes to zero padding while a separate fullscreen root is
active. The same native layouts and dimensions remain in use.

The test retains its one-pixel restoration tolerance and original raster threshold,
verifies the live view belongs to this workspace owner, and checks inverse pointer
mapping before entry, during both fullscreen entries and after restoration. Both
rectangles and expected/observed pointer coordinates are reported on failure.
