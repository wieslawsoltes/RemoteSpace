# Retained session layout geometry

`SemanticLayoutHost` supplies a non-focusable native automation peer for the
session's content row. A name on a plain Border was not sufficient in the
pinned runtime: the semantic tree omitted it, and child bounds were calculated
before its tab-strip offset was applied. Subsequent offset changes to that
pruned ancestor did not refresh descendant bounds. Keeping the row as a real
semantic parent propagates its placement without hard-coded offsets, DOM
rewrites, reflection into Uno, or a polling geometry observer.
The real-browser raster comparison keeps its original thresholds and now also
requires the remote desktop to be inside that semantic layout host.

Source reference: Uno WebAssemblyAccessibility.OnSizeOrOffsetChanged updates the changed visual, not all descendants of a pruned structural ancestor. A real automation peer retains that parent in the semantic coordinate tree.

The first retained-parent change exposed a second boundary: detached controls
already registered at the fallback root are not reparented by the runtime's
OnChildAdded duplicate guard. ShowPanel removes the cached panel's children,
attaches the empty panel to its live semantic owner, and reattaches the same
child instances. Normal OnChildRemoved clears the stale registrations first.
Only selection changes perform this lifecycle transition; no per-frame tree
rebuild, explicit DOM changes or recreated framebuffer is involved. Non-session
navigation now detaches the session panel while the session keeps its pixels.

The full top-left pixel comparison passed after this change (543 of 543 sampled
flat pixels matched). The next failure exposed a separate focus boundary: the
shipped generic scrollbar DOM factory sets tabindex but does not install the
focus callback used by typed controls. BrowserScrollBarFocus registers only the
two owned scrollbar IDs, relaying focusin to ScrollBar.Focus through source-
generated interop. It never intercepts keystrokes or changes values/DOM geometry.
Registrations are bounded, reentrancy guarded and removed on view disposal.
Native desktop hosts need no adapter. MouseIndicator makes the actual native
scrollbar tracks visible rather than leaving their default indicator mode None.

After restoring scrollbar focus, the live suite passed zoomed pixels at both pan
limits, pointer mapping, narrow command layout, hidden catch-up and graphics
pause/resume. The final close test found that showing the native tab strip again
had restored its item as an option without a close-button subtree. The host now
realizes fresh TabViewItem selector containers when re-entering session navigation,
with reentrant selection events guarded. At most eight selectors are recreated;
live session objects, selected identity, view controls and decoded pixels remain.
State changes update the current selector, not a captured obsolete container.
Browser coverage still clicks the actual native Close Tab button after navigation
and requires successful disconnect followed by a fresh credential-based reconnect.

Toolbar keyboard activation is also scoped explicitly. An owned native HTML
button keeps its standard Enter/Space default, which invokes Uno's existing
button provider; the same gesture does not also enter the document-level native
keyboard router. Only buttons bearing the generated RemoteSpace session-command
AutomationId are affected. Other keys, unowned buttons and canvas input retain
normal routing. Repeats and a release after focus changes cannot activate a
second command. No command implementation, DOM value or private API is replaced.


## Input-boundary scrollbar synchronization

The post-merge main run exposed a remaining asynchronous focus echo: native DOM
focus requests for horizontal and vertical bars arrived on different turns and
the old focusin relay responded to each by focusing the native bar again. A
microtask guard only coalesced one turn; it did not bound the cross-turn loop.
The regression now models these independent delayed replies and reproduces an
unbounded queue with the previous adapter.

The adapter synchronizes the currently focused, explicitly registered scrollbar
at its keyboard-input boundary, before Uno's document-level keyboard router.
Focus-only notifications trigger no callback or timer. The original key is not
cancelled, rewritten, replayed or converted to a value; the native scrollbar
still performs its normal operation. Non-scrollbar input and off-focus events
are untouched. This avoids sustaining asynchronous focus echoes while ensuring
local scrollbar keys cannot act on a previously focused remote desktop. Pure
DOM focus does not itself move XAML focus; synchronization occurs on the first
keyboard event. Native pointer navigation and automation range providers retain
their existing paths. Broader assistive-technology qualification remains open.

Tests cover delayed cross-bar replies, 1,000 alternating input handoffs, 10,000
focus-only notifications with no scheduled work, callback reentrancy/errors,
registration/disposal bounds and untouched unrelated input. Browser zoom coverage
performs repeated real Home/End focus handoffs before checking original remote
pixels, no outgoing remote keys, pointer coordinates and subsequent toolbar focus.

## Target-owned navigation supersedes focus-only key routing

The subsequent live-session review found that native focus plus ordinary keyboard
routing was still insufficient during rapid handoffs. Supported owned-scrollbar
navigation now targets its registered control directly and consumes that event
once, rather than relying on the current global native focus. Other input routes
and passive focus notifications are unchanged. See [owned scrollbar navigation](owned-scrollbar-navigation.md)
for the failed raster evidence, exact routing scope and validation requirements.
