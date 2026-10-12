# Owned scrollbar navigation review

The file-lifecycle PR's first full live-session run passed authentication, remote
input, resolution changes, clipboard round trips and capture. Its rapid local
scrollbar handoffs then left horizontal scroll at its start while vertical scroll
reached its end; the original 200% raster comparison correctly failed (499/544
flat samples, below its unchanged 97% threshold). This was not a file-read error
or an infrastructure failure, and the job was not retried just to obtain green CI.

The old adapter relied on focusing a registered native scrollbar and then letting
the document-level keyboard router deliver the same event according to native
focus. That leaves correctness dependent on focus synchronization. The adapter
now applies supported navigation to the registered scrollbar itself, then consumes
that local navigation event so the normal keyboard router cannot apply it again
or send it to the remote desktop. A key release performs no second action.

Home, End, PageUp/PageDown and the appropriate axis's arrow keys share the same
native `ScrollBarNavigation` implementation as desktop routed input. It updates
the actual control Value, which uses the existing ValueChanged and viewport paths.
`ScrollNavigation` is an allocation-free, host-independent bounded arithmetic
model; finite inputs, ordered bounds and nonnegative increments are required.
Overflowing additions clamp to a finite range endpoint. No DOM coordinate/value
is rewritten, no RDP event is fabricated, and no private Uno API is invoked.

This is a deliberate change from the earlier claim that owned scrollbar keys were
never consumed. Recognized local navigation is now consumed exactly once; text,
Tab, Escape, opposite-axis arrows, modifier shortcuts, already-handled events and
unregistered/off-focus targets keep their normal routing. Public native focus is
still requested for accessibility, but the navigation operation no longer uses
the global focused element to choose its target. Focus notifications themselves
remain passive and cannot cause a callback echo loop.

Source context: Uno's public focus APIs and focus/peer implementation:
https://platform.uno/docs/articles/features/focus-management.html
https://github.com/unoplatform/uno/blob/11d8bf14efec8aaebdd241cbb42680932dd8c0de/src/Uno.UI.Runtime.WebAssembly.Browser/Accessibility/FocusSynchronizer.cs

Exact-module tests preserve bounded registration, disposal, reentrancy, late
focus and unrelated-input coverage, and add every navigation key/axis, releases,
modifiers and delayed action delivery. Pure-model tests cover range/increment
behavior, invalid values, arithmetic overflow and 100,000 warmed operations.
The full browser raster, no-remote-key, inverse-pointer, toolbar and reconnect
assertions are unchanged. The exact candidate's CI results are required before
claiming this resolved the observed live-session failure.
