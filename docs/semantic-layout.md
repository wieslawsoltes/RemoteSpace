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
