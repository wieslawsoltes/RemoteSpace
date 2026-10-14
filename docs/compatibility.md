# Compatibility matrix

This document describes source implementation scope, not blanket Windows App parity. A successful build is not proof of live interoperability; the exact commit's CI artifacts are the validation record.

| Area | Implemented scope | Important boundary |
| --- | --- | --- |
| Direct RDP | Independent framing, MCS/GCC, TLS, activation, licensing, bitmap desktop and input | Live automated fixture is XRDP/TLS; Windows/RDS version matrix is not certified |
| NLA / CredSSP | ASN.1 exchange, channel binding and platform authentication integration | Codec/security-policy tests exist; no live Windows NLA/domain matrix is established |
| Graphics | 16/24-bit bitmap path, interleaved RLE, fast-path assembly, framebuffer bounds | No modern GFX/AVC/RemoteFX/progressive/bulk-compressed graphics pipeline |
| Cursor | Color/ARGB and monochrome AND/XOR behavior, cache and position | Full device/OS cursor interaction matrix is not established |
| Input | Negotiated input batches, scan codes/Unicode, five pointer buttons and horizontal/vertical wheel, Pause/PrintScreen, focus-loss releases and secure-attention sequence | Browser uses virtual-key fallback and adjacent pending motion coalescing; IME/touch/pen and every keyboard/device layout are not at parity |
| Clipboard | Opt-in Unicode text channel and explicit local clipboard commands; bidirectional browser/remote test | No clipboard file transfer, rich formats or drive redirection; 1 MiB UTF-16 / 2 MiB encoded bridge-message limits |
| Dynamic channels | v1/v2 negotiation, variable IDs, fragmentation, bounded per-channel state, v2 priorities | v3 compression, UDP/multitransport and unrelated dynamic-channel services are not advertised |
| Display control | Server capabilities, full-layout codecs, bounded monitor geometry, remote-resolution presets, custom dimensions, bounded automatic window resizing and server-confirmed reactivation | Multi-monitor wire layout support is not a full per-monitor native-window UI or browser multi-screen implementation |
| Graphics output | Negotiated refresh and pause/resume through native engine, bridge and Uno controls | Manual control; remote programs keep running; no automatic visibility policy |
| Device workspace | Add/edit/delete, favorites, search, sorting, tile/list, theme, `.rdp` import/export and saved device documents | No cloud account discovery, workspace subscriptions, roaming or enterprise administration |
| Browser persistence | IndexedDB transactions, exact-document compare-and-replace, reload persistence, legacy local-file migration | Browser storage can be cleared/evicted; export saved devices for a backup. Native file persistence is not a distributed lock |
| Desktop / browser UI | Shared Uno application, lazy General/Display settings pages and reusable controls | Independent Fluent-style design; not pixel-perfect Windows App visuals or complete UI parity |
| Rendering | Uno Skia compositor, bounded sparse-frame mailbox, single-buffer decode, batched dirty presentation, cursor composition, screenshot pixel checks | CPU decoding; hardware GPU acceleration depends on host. CI software rendering is not a hardware benchmark |
| Browser connectivity | Authenticated WebSocket bridge, explicit origin/target allowlists, limits and cancellation | Trusted bridge terminates RDP and sees credentials. Static hosting is not direct TCP RDP or RD Gateway |

## Not implemented

Windows 365, Azure Virtual Desktop, Dev Box, organizational sign-in and feed discovery; RemoteApp/RAIL; RD Gateway; modern GFX/H.264/AVC codecs; UDP/multitransport; audio playback and microphone/camera redirection; file/drive/printer/USB/serial/smart-card redirection; WebAuthn and enterprise SSO/MFA workflows; seamless reconnect and automatic-reconnection-cookie handling; complete accessibility/localization/IME/touch/pen parity; full multi-monitor and detached-session window management; enterprise policy/auditing and credential-vault integration.

Unsupported `.rdp` settings produce import warnings. Unsupported protocol paths are rejected rather than treated as working features. This preview is not ready to replace an enterprise remote-access deployment without further interoperability, security, load and platform testing.

Performance measurements and correctness changes are documented in [performance and review](performance-and-review.md).

Input batching, additional input formats and disposal fixes are documented in [input and lifecycle review](input-and-lifecycle-review.md).
