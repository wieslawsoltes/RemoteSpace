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

Fourteen exact-module Node tests cover actual-state notifications, synchronous and
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
