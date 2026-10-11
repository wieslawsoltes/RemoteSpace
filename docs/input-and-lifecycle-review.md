# Input performance and lifecycle review

This incremental review builds on PR #1. It does not complete the Windows App parity matrix or constitute a security audit.

## Negotiated input features

`InputCapabilities` exposes server support for Unicode input, extended mouse buttons and horizontal wheel events. `Activation.ParseDemand` reads the server's Input Capability Set; the direct engine validates input against that negotiated snapshot. The Uno control reports unsupported extended input explicitly rather than silently substituting other buttons. Mouse buttons 4/5, horizontal wheel events, PrintScreen and the prescribed four-event Pause sequence are implemented. These are protocol and control implementations, not certification of every operating system, keyboard layout or pointing device.

`IRemoteInputBatch` sends up to 64 ordered events in one slow-path RDP Input Event PDU. Every event is validated before the encoder returns any bytes. The browser explicitly requests the bridge extension and uses batches only after the bridge advertises support; older bridges retain the serialized single-event fallback. Bridge rate limits count events, not JSON messages, so batching cannot multiply the allowed input rate. Encoders take ownership of input data before asynchronous waits.

The reproducible input microbenchmark in `tests/RemoteSpace.Input.Tests` compares 64 separate events with one batch: share-PDU bytes fall from 2,176 to 790. The test also checks measured encoder allocations. This excludes TCP/TLS/WebSocket headers, server processing, presentation, latency and hardware GPU throughput. Batching is opportunistic: no timer delays an otherwise ready event to fill a batch.

## Bounded queue and release ordering

`RemoteInputQueue` uses a fixed-capacity ring and one reusable 64-event consumer buffer. Only adjacent pending pure pointer moves coalesce. Key presses/releases, mouse buttons, wheel events and synchronization are ordering barriers. Coalescing preserves the latest position, not the full intermediate motion trajectory. For applications requiring every sampled drag point, that distinction matters.

A multi-event enqueue either fits completely or changes nothing. This avoids partially sending a shortcut or Unicode text command before reporting overflow. `FlushAsync` waits for the sender to finish the accepted sequence, not merely to dequeue it. Pause and detachment use this barrier for held-key releases. The control waits through server reactivation rather than discarding queued input, and clamps queued pointer positions to the current presented desktop dimensions. Queue saturation still terminates the session explicitly when ordinary input cannot be accepted; it never silently drops a key release.

Regression tests cover 10,000 coalesced moves, ordering barriers, atomic overflow, actual-send flush, cancelled senders, 4,000 events from concurrent producers and cancellation-safe reactivation subscriptions. The browser suite retains independent remote keyboard effects and adds a real Pause batch, tab close, and a fresh connection using explicitly re-entered credentials. Fresh-session reconnection is not automatic or cookie-based reconnect.

## Session lifetime fixes

`SessionLifetime` serializes start/dispose ownership. All dispose callers observe one task; stopping and resource release execute once. The native and bridge run methods signal completion from their outermost `finally`, including when application event handlers throw. Event failures remain observable; they are not presented as a successful connection. Disposal before `RunAsync` prevents a subsequent start.

Transport cancellation occurs before disconnect notifications. Failures after entering a packet write terminate the transport, because a partial TPKT or fragmented message cannot safely be followed by a new packet. Dynamic-channel cleanup shares the channel writer gate. Queued operations cannot encounter a disposed semaphore when they later release their gate; these gates never request native wait handles and are reclaimed with the session. The stop source and socket are still explicitly disposed.

Lifecycle tests exercise concurrent disposal, reentrant observers, stop/release failures, disposal before start, pre-cancellation and throwing observers on the actual native/bridge session classes. The observer tests fail before connecting; they do not probe external machines. Live browser checks separately exercise the full authorized loopback connection lifecycle.

## Additional review fixes

Physical-key fallback lookup now uses a read-only constant span instead of allocating
an array for every lookup. A warm-up-aware test measures 100,000 actual lookups.

Workspace serialization now enforces the same encoded-byte limit as parsing. Previously,
individually valid device fields and counts could expand beyond 16 MiB after JSON
escaping, allowing a saved document that the next load would reject. Serialization
now fails before a storage adapter replaces the old document. The regression constructs
this case with 3,000 individually valid Unicode profiles, checks that data remains
available in memory, and verifies smaller documents still round trip.

## Review boundaries

Reviewed changes cover input negotiation/validation, event serialization, bridge rate accounting, bounded queues, reactivation waiters, control attachment, writer cancellation and session disposal. Existing graphics, clipboard, security and protocol regression suites remain required. Residual validation includes sustained network impairment, all callback/reentrancy patterns, live Windows NLA/licensing versions, hardware input/IME/touch/pen matrices and security auditing. Applications must not synchronously block an event callback waiting for the same session's receive loop to finish.

## Primary references

- [MS-RDPBCGR Input Capability Set](https://learn.microsoft.com/en-us/openspecs/windows_protocols/ms-rdpbcgr/b3bc76ae-9ee5-454f-b197-ede845ca69cc)
- [MS-RDPBCGR Input Event PDU Data](https://learn.microsoft.com/en-us/openspecs/windows_protocols/ms-rdpbcgr/ff7f06f8-0dcf-4c8d-be1f-596ae60c4396)
- [MS-RDPBCGR Mouse Event](https://learn.microsoft.com/en-us/openspecs/windows_protocols/ms-rdpbcgr/2c1ced34-340a-46cd-be6e-fc8cab7c3b17)
- [MS-RDPBCGR Extended Mouse Event](https://learn.microsoft.com/en-us/openspecs/windows_protocols/ms-rdpbcgr/2ef7632f-2f2a-4de7-ab58-2585cedcdf48)
- [.NET SemaphoreSlim thread-safety requirements](https://learn.microsoft.com/en-us/dotnet/api/system.threading.semaphoreslim)
