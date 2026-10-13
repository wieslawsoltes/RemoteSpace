# Measured session traffic

`IRemoteSessionTraffic` is an optional, reusable capability implemented by the
native `RdpSession` and browser-capable `BridgeSession`. It adds no required
members to `IRemoteSession`; existing third-party session implementations remain
compatible. A snapshot contains a measurement layer and two cumulative byte
counts, not endpoint strings, credentials, packets, clipboard data or input text.

## Measurement layers

Native sessions measure completed reads and writes through `TrafficCountingStream`
beneath `SslStream`. This includes security negotiation, RDP framing and TLS bytes,
but excludes TCP/IP headers, retransmission overhead and unknown partial bytes
when an underlying write throws. A completed stream write is not an application
acknowledgement from the remote PC. The old `RdpSession.ReceivedBytes` keeps its
original post-establishment packet-body meaning; it is not silently redefined.

Bridge sessions measure completed WebSocket message payloads in both directions,
including the initial connect message. They exclude HTTP handshake, WebSocket
framing, TLS/TCP/IP overhead and all bridge-to-PC traffic. Cumulative lengths can
include the sizes of credential-bearing messages, but the metrics retain none of
their contents. The two layers must not be compared as though they measure the
same network boundary.

Counters increase only after successful I/O completion. A short read counts its
returned length, EOF counts zero, and a failed write does not count the whole
requested buffer. Flush, seek and length changes are not network traffic. Totals
remain available after disconnect; they do not prove that the session is still
healthy or that received bytes were successfully decoded.

## Performance and concurrency

`SessionTrafficCounter` stores two numeric totals and a layer. Atomic additions
are safe for concurrent send and receive paths. Totals saturate at `long.MaxValue`
instead of wrapping. A snapshot samples the two directions independently; it is
not a transactionally synchronized view of both I/O loops. There is no global
transport lock, packet history, callback list or per-increment allocation.

`SessionTrafficSampler` takes snapshots and caller-supplied monotonic milliseconds.
It computes observed rates from the actual interval, not an assumed polling
period. The first sample, source-layer change or counter reset establishes a new
baseline. Equal/backward timestamps do not consume the baseline. An unavailable
source clears it. The sampler has one owner and no timers, network probes or
per-sample allocations. Rates describe observed throughput, not bandwidth capacity,
RTT, connection quality, GPU performance, frame rate or end-to-end input latency.

The stream wrapper delegates array, span, memory and byte operations without
copying their buffers or reading ahead. Synchronously completed ValueTasks avoid
an asynchronous continuation. Ownership is explicit through `leaveOpen`; disposing
the wrapper disposes an owned underlying stream only once.

## Validation and scope

The lifecycle suite tests concurrent additions, interval/reset arithmetic, extreme
clock values, short reads, EOF, synchronous and asynchronous operation variants,
asynchronous completion/failure, pre-cancellation, invalid buffers, CopyToAsync,
leave-open and repeated disposal. A warmed 100,000-iteration counter/sampler loop
requires zero managed allocation. This is a narrow allocation check, not a network
throughput benchmark.

The existing authorized XRDP interoperability fixture checks that the native
TCP-stream totals exceed decoded packet-body bytes and include writes. The full
Uno workspace and browser-to-bridge-to-XRDP suites still run unchanged. They
exercise the real engine, not an alternative RDP client or a synthetic desktop.
Read exact-revision workflow artifacts for the executed results.

The first telemetry revision failed component compilation because its intentionally
failing read test ignored a returned byte count (CA2022). The test now consumes the
count and fails explicitly if the expected exception does not occur. No analyzer
rule was disabled. Async array overloads now also reject a null buffer explicitly,
including a zero-length call, instead of allowing null AsMemory to become empty.

The dependent live connection-information panel and expanded browser test remain
local because publication of the expanded test was blocked by the tool platform.
They are not shipped by this core-only change. PR #7's unrelated blocked settings
tab and automatic-resolution changes also remain excluded. Full Windows App
functional/visual parity and Windows-host, hardware-GPU and security qualification
are not established by these counters.
