# Local session control shortcuts

Focus the connected remote desktop before using either shortcut:

| Shortcut | Local action |
| --- | --- |
| Ctrl+Alt+Home | Reveal the connection bar and focus its first ordinary enabled command. |
| Ctrl+Alt+Pause | Enter or leave fullscreen using the host's actual Fullscreen API. |

Home also works when the fullscreen connection bar is collapsed. The toolbar's
normal Enter/Space commands and browser/OS fullscreen exit are unchanged.
Tooltips on Full screen and Expand connection toolbar expose these shortcuts;
the remote input view provides the same accessible help. No remote resolution,
zoom preference, graphics-output setting or saved PC profile is changed.

These are the two local-control chords documented by Microsoft for
[Windows App keyboard input](https://learn.microsoft.com/en-us/windows-app/input-keyboard-mouse-touch-pen).
RemoteSpace implements this interaction independently; this is not all Windows
App remote shortcut translations or complete platform/visual parity. Some host
keyboards lack a Pause key, and a browser/OS can reserve a chord before an app
receives it. The visible toolbar remains available. Browser permissions are not
changed to make a shortcut work.

## Ownership and performance

`SessionControlShortcutState` in Core stores two bits of native gesture state.
`SessionControlKeys` in Controls attaches that policy to a supplied native input
scope, UI root and Window. It has one admitted asynchronous local action, not a
queue. Dispose it on the owning UI thread before releasing the remote view.
Disposal cancels pending focus work and detaches even when a host cancellation
callback throws. The app includes this owner in its failure-tolerant close path.

The browser's exact embedded module shares one capture listener among bounded
session registrations. It examines only Home/Pause command keys and matches the
registered view's public automation ID in the event path. It does not copy
arbitrary typing, event objects, credentials, or a key history. Ordinary input
returns before event-path creation. The native path similarly rejects other keys
before walking ancestors or querying modifier state. No idle timer, polling,
remote pixel work or layout invalidation is caused by idle shortcut registration.

Only initial exact Ctrl+Alt chords are admitted. Shift, Windows/Command, AltGraph,
composition, unrelated views, dialogs and editable inputs are excluded. The
admitted Home/Pause repeat and matching release stay local even if modifiers are
released first or the command moves focus. Blur/deactivation, page exit and
unregister clear ownership; no synthetic remote input is replayed.

The Control/Alt make events can already have reached the remote PC before the
last key identifies a local chord. The app therefore releases its existing held
remote input through the original ordered queue before changing local focus.
Home/Pause itself is intercepted before remote input dispatch. This preserves
ordinary Control/Alt combinations without speculative buffering and avoids
leaving the remote PC with stuck modifiers.

Fullscreen entry is invoked on the original user activation stack, before any
focus/layout wait. The normal permission-aware controller reports actual
completion or a visible error; the shortcut does not mock or override platform
policy. Showing the bar uses native control focus after live layout readiness,
not a DOM focus/coordinate patch. The same remote view and framebuffer remain.

## Validation boundaries

Core tests enumerate both keys across all modifier/repeat combinations and
verify release ownership, deactivation and a warmed 100,000-gesture allocation
assertion. Exact-module JavaScript tests check routing, exclusion, repeated keys,
registration bounds and lifecycle. Real Chromium component tests exercise actual
keyboard/focus events and repeated real fullscreen transitions without replacing
platform APIs. Consult exact-revision logs for executed results; assertions alone
are not measurements.

The full Uno/authorized XRDP suite independently observes the real transport:
only balanced modifier makes/releases may accompany a shortcut. It checks local
focus after held Home, actual fullscreen entry/exit, original-Capture PNG samples,
inverse remote pointer mapping, windowed geometry restoration, and unchanged
server sizes/layout requests. It reuses the existing PNG rather than increasing
the production download-retention limit. Earlier input, clipboard, viewport,
toolbar, close/reconnect and workspace tests remain merge gates.

These tests do not establish native OS shortcut behavior on every keyboard,
assistive-technology certification, end-to-end latency or hardware-GPU performance.
