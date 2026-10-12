# Custom remote resolution

The Remote resolution menu now offers a user-entered width and height in addition
to presets and Match window. Its dialog is initialized from the **actual** remote
desktop, not a pending request. Apply validates both values and keeps the dialog
open with a readable error when input is invalid. Cancel sends no resize.

`DisplayResolution` is a reusable Core parser. It accepts bounded decimal integers,
checks the 200–8192 axis and even-width rules, and applies the existing server
monitor-count/area limits and local 16-megapixel ceiling. It does not round, clamp,
silently downgrade, or allocate a framebuffer while the user edits. The
`DisplayResolutionEditor` Uno control has no session or transport dependency.

The host owns confirmation, flushes held input, and invokes the same serialized
display-control channel as preset resolutions. Existing limits are checked again
by the session when sending. The desktop allocation changes only on server
reactivation. Local zoom remains separate and the scale and resolution menus show
checkmarks for the active scale and confirmed server size, not requested values.

Regression tests cover null/malformed/oversized inputs, nondecimal and fractional
notation, odd widths, exact allocation boundaries, and restrictive server
capabilities. The actual browser test changes the authorized XRDP desktop to
1152 by 864, requires the new server dimensions and new visible pixels, and checks
that invalid input and cancellation leave the previous size unchanged. This is
additional local-PC display functionality, not complete Windows App parity or
multi-monitor configuration UI. The exact revision's workflow artifacts are the
validation evidence; source presence does not imply runtime test success.

Full-width framebuffer updates and crops also use one contiguous copy instead of
one copy per row. Partial-width rectangles retain the row-stride path and all
bounds/storage-isolation checks. The regression compares both paths and verifies
unrelated rows and caller-owned arrays remain isolated. This reduces copy-call
overhead for wide update strips; it is not an end-to-end throughput claim.

Inline validation uses the reusable `ValidationMessage` control. It inserts a
fully populated text peer on error, replaces it for a different error, and removes
it on success. Creating an empty TextBlock first left a visibly rendered error
absent from the pinned browser accessibility tree. The live test now requires
both the odd-width and out-of-range messages, removal of the replaced peer, and
no outgoing request for either error. It does not weaken validation to accept
invalid geometry or substitute screenshot-only evidence for accessibility.
