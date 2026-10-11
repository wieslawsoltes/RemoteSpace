# Performance and correctness review

This review covers the current preview, not full Windows App parity or a formal security audit. The exact commit's CI results remain the authority for validation.

## Implemented optimizations

`FrameMailbox.TryTakeBatch` replaces the bridge's single dirty-union snapshot with exact bounds in fixed 32-row bands. Matching adjacent bands merge. At most 256 disjoint rectangles are emitted; tracking memory does not grow with the number of updates. Two opposite-corner pixels on a 1920 by 1080 desktop require eight snapshot pixel bytes, versus 8,294,400 with the previous union. Dense full repaints still produce one snapshot. This is not a general minimum-area tiler: separated updates within one band can include unchanged pixels. Legacy `TryTake` remains available and shares the same drain state.

`BridgeSurfaceWriter` uses one reusable pooled packet buffer of at most 1 MiB. Each strip is copied once into the existing RSU1 framing rather than allocated as a pixel array and copied into another packet. Sending is serialized and awaited before reuse. The buffer is cleared before returning it to the pool. The caller owns the immutable input batch and socket; this class is not for concurrent calls or disposal during a send.

Bitmap decoding allocates only the final RGBA pixel buffer. Uncompressed padded rows convert directly into top-down output. RLE operates on a uint view of that same storage, then reads opposing row pixels before converting either in place. Odd-height images and all implemented color depths are covered. This avoids a second full-sized pixel allocation without changing XOR history semantics. TPKT/X.224 output also constructs its final envelope in one allocation.

The Uno control copies contiguous full-width updates in one operation and publishes changed pixels once per render/capture, rather than once per incoming strip. `PixelInvalidations`, `AppliedUpdates`, and `PresentedFrames` expose separate counts. Capture flushes pending pixel changes. Resizing clears the old input transform before the next frame is presented.

## Reproducible measurements

Run `dotnet run --project tests/RemoteSpace.Performance.Tests -c Release`. The test compares randomized sparse reconstruction with a full reference framebuffer, checks packet bytes and cancellation, and measures the warmed sender against the retained legacy encoder. It gates allocation reduction, not wall-clock speed.

Ubuntu CI at `e58dfb37fd1666d2eca71f1bca7c9d031a38d6ab`, Validate run 38145294217, reported for six 1920 by 1080 encodes: legacy allocations 99,567,888 bytes; pooled allocations 7,760 bytes; legacy elapsed 16.9209 ms; pooled elapsed 2.4696 ms. These are in-memory sender measurements, not network, presentation, power, latency or GPU benchmarks. Timing varies by host and is not an SLA. A separate RLE check allocated 80,024 bytes for 80,000 output bytes. Windows and Linux both run allocation and pixel-integrity regressions.

## Negotiated graphics output

`IRemoteGraphicsOutput` exposes server General Capability flags, pause/resume and full-desktop refresh. Codecs follow MS-RDPBCGR 2.2.7.1.1, 2.2.11.2.1 and 2.2.11.3.1. Suppression uses a four-byte body when disabled and adds an inclusive desktop rectangle only when enabled. Unsupported commands are disabled in the UI and rejected by the engine. Initial refresh is no longer sent unconditionally to a server which did not advertise it. A paused session retains its output choice across server reactivation when still supported.

The browser opts into the extension. The bridge forwards negotiated capabilities, bounds command frequency and reports the native engine's output state after sending the request. The browser waits for that report with a deadline; this is not an acknowledgement from the RDP server, since these RDP PDUs have none. The UI releases held input before pause and disables interaction with the paused surface. Remote programs and clipboard continue running. There is no automatic tab-visibility suppression policy yet.

## Review fixes and limits

Malformed static-channel and fast-path fragments now reset and release partial storage; static channels use an exact allocation rather than a growing stream plus a completion copy. Session cleanup resets fragment assemblers. Bridge validation errors now include InvalidDataException in the expected-error boundary. The pinned modern interoperability fixture selects its own pkg-config metadata and enables the server-side painter required for bitmap-only clients. It is an external test server, never linked into the client.

Review examined framing, activation, decoding, virtual channels, transport queues, input dispatch, controls, credentials and persistence boundaries. Regression tests do not establish absence of defects. Remaining review/validation includes all live Windows NLA/licensing versions, concurrent lifecycle/cancellation stress, network impairment and reconnection, input-layout/IME/accessibility matrices, fuzzing, hardware GPU performance, and enterprise security auditing. General primary drawing orders, modern GFX/AVC, audio/devices, cloud feeds/accounts and other missing parity remain listed in compatibility.md. No successful XRDP test implies full Windows App compatibility.
