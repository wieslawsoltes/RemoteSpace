# Collapsible fullscreen connection toolbar

The fullscreen connection bar has an explicit **Collapse connection toolbar**
command. It reduces the local chrome to a small top-centred **Expand connection
toolbar** button, allowing the remote surface to occupy the full window. Enter
and Space activate either control. The normal browser/OS fullscreen exit remains
available. Leaving fullscreen restores the expanded connection bar, including
when the bar was collapsed at the time of the platform exit. Every new session
starts with expanded commands; no device preference is silently changed.

This follows the expand/collapse interaction documented for the
[Windows App browser toolbar](https://learn.microsoft.com/en-us/windows-app/get-started-connect-devices-desktops-apps).
It is an independent implementation using native Fluent buttons and system glyphs,
not Microsoft application source or copied image assets. It implements explicit
collapse/expand, not timed auto-hide, a draggable bar, detached windows or complete
Windows App visual/functional parity.

## Reusable components and ownership

`ConnectionToolbarState` is a small Core model with `SetFullscreen` and
`SetCollapsed`. Both report whether presentation needs changing. Duplicate
fullscreen notifications preserve the user's collapse choice. A windowed collapse
is rejected before mutation. There is no timer, history, native dependency or
remote-session dependency.

`SessionToolbarPresenter` in Controls accepts a `SessionCommandBar` and a native
FrameworkElement desktop. The app still owns those supplied objects and the
connection. The presenter owns only local layout/activation state. The same
remote view stays under a retained `SemanticLayoutHost` as the toolbar row changes
height; it is not cleared, detached or recreated by collapse/expand. It does not
request a remote display layout, reconnect, modify zoom, or pause remote graphics.

Only the bounded toolbar subtree is detached while collapsed. Its menus and
invisible command peers therefore cannot receive input. A sibling reveal button
is outside the remote input surface and does not route its activation to RDP.
When the bar reappears, normal visual-tree attachment recreates its semantic
command peers without rebuilding the desktop. Theme styling comes from native
buttons rather than hard-coded light/dark colors.

`SetCollapsedAsync` waits for the local activation gesture before removing its
control, retains local focus on a reachable command, and has at most one admitted
transition. Fullscreen changes and Unloaded cancel its pending release wait. The
host's `LocalCommandStarting` callback releases tracked remote input; cancellation
is checked again afterward because a host callback can reentrantly unload the
presenter or leave fullscreen. Command errors remain visible through the app's
existing error surface. There is no idle polling, pointer-motion subscription,
per-frame reparenting or opacity-only hiding of focusable controls.

## Validation

The managed state tests exercise rejection, duplicate notifications, expanded
restoration, independent presenters and repeated collapse cycles, plus a warmed
100,000-iteration unchanged-state allocation/no-layout assertion. Runtime counts
and measurements are recorded by the exact revision's CI; the existence of this
assertion is not itself a performance measurement.

The real Uno/Chromium/XRDP suite uses the user-facing Capture command for its
original-resolution raster. It then performs repeated Enter/Space collapse and
expand, a held opening key, and an external platform exit while collapsed. It
requires absence of hidden command peers, a compact in-window reveal target,
unchanged remote-view semantic identity, one-pixel geometry restoration, and the
existing 97% flat-raster sample threshold with independently observed inverse
pointer coordinates. Remote sizes and display-layout request counts must not
change, and local command activation must not become remote keyboard input.
No private managed entry points, fabricated remote frames, DOM geometry writes
or modified fullscreen policy are used.

Desktop/browser compilation and both full application browser suites remain
required. Hardware GPU throughput, native OS Escape behavior across platforms,
assistive-technology certification, timed auto-hide and touch/IME parity are not
established by these tests.
