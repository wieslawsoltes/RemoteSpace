# Retained cursor composition and damage tracking

Legacy AND/XOR cursors depend on the desktop pixels beneath the mask. The
existing `CursorCompositor.Resolve` performs this bitwise calculation; it is not
Skia's Porter-Duff Xor blend mode. See Microsoft's
[MS-RDPBCGR protocol specification](https://learn.microsoft.com/en-us/openspecs/windows_protocols/ms-rdpbcgr/5073f4ed-1e93-45e1-b039-6e30c385867c)
for the pointer protocol. This change does not alter wire decoding or negotiated
capabilities.

`CursorCompositionDamage` is a reusable Graphics component that decides when a
retained cursor texture needs that calculation again. Shape admission validates
before replacing state and scans the mask once to find the bounding rectangle of
background-dependent pixels. Ordinary alpha pixels outside it cannot become
stale from a desktop write. A mask with no nonzero entries needs no background
composition. Holes inside the rectangle are conservatively treated as dependent.

The renderer follows this sequence under its existing canvas lock:

1. Call `SetCursor` after a new shape is validated, and `MoveCursor` on position
   updates. Reoffering the same shape explicitly still invalidates its contents.
2. Call `Invalidate` after a successful framebuffer write. Half-open rectangle
   intersection does not dirty the cursor for an adjacent, nonoverlapping write.
   `InvalidateAll` handles framebuffer replacement even at the same dimensions.
3. On paint, run the original compositor only when `NeedsResolve` is true. Copy
   the result into the retained cursor texture and notify Skia. Call
   `MarkResolved` only after those steps succeed. Failed composition remains dirty.
4. Clear the state when clearing the canvas. The planner owns no buffers,
   subscriptions, clocks, growing history or per-frame allocations.

The caller must serialize the planner with the framebuffer and texture it
represents. It is not an independently thread-safe snapshot of arbitrary memory.
Changing cursor buffers requires an explicit `SetCursor`; changing desktop bytes
requires invalidation. Local fit/zoom/pan or unrelated damage changes neither the
cursor's source pixels nor its source-coordinate background dependence.

## Validation and limits

`CursorDamageTests` runs in the existing performance suite on Windows and Linux.
It compares 2,000 mixed cached renders with the unchanged always-resolve raster,
including padded rows, clipping, shape replacement, movement and desktop writes.
Other checks cover half-open edges, extreme integer coordinates, admission
atomicity, hidden/system/alpha cursors, failed resolution and a warmed 100,000
unrelated-update allocation/no-recomposition workload. Consult the exact CI
revision's logs for executed results; the source assertions alone are not measured
performance evidence.

All existing real Uno browser and authorized RDP pixel/pointer/clipboard/input
checks remain merge gates. This optimization is not a hardware-GPU, network
latency, frame-rate or power benchmark, nor full Windows cursor/device certification.
