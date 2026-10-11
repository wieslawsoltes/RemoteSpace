using System.Diagnostics;
using System.Net.WebSockets;
using System.Text.Json;
using RemoteSpace.Core;
using RemoteSpace.Graphics;
using RemoteSpace.Transport;

int checks = 0;
void Check(bool value, string name) { if (!value) throw new Exception("FAILED: " + name); checks++; Console.WriteLine("PASS " + name); }
using var mailbox = new FrameMailbox();
Check(!mailbox.TryTakeBatch(out _), "empty sparse mailbox");
mailbox.Resize(1920, 1080);
Check(mailbox.TryTakeBatch(out var geometry) && geometry!.Updates.Length == 0, "geometry-only batch");
mailbox.Apply(new(0, 0, 1, 1, [1, 2, 3, 255]));
mailbox.Apply(new(1919, 1079, 1, 1, [4, 5, 6, 255]));
Check(mailbox.TryTakeBatch(out var corners) && corners!.Updates.Length == 2 && corners.Updates.Sum(u => u.Rgba.Length) == 8,
    "two opposite-corner pixels copy 8 bytes instead of a whole 8294400-byte desktop");
var saved = corners!.Updates[0].Rgba.ToArray();
mailbox.Apply(new(0, 0, 1, 1, [9, 8, 7, 255]));
Check(corners.Updates[0].Rgba.SequenceEqual(saved), "in-flight sparse batch is immutable to later writes");
mailbox.TryTake(out _);
Check(!mailbox.TryTakeBatch(out _), "legacy and sparse APIs share one drain state");
byte[] desktop = new byte[1920 * 1080 * 4];
var full = new SurfaceUpdate(0, 0, 1920, 1080, desktop);
mailbox.Apply(full);
Check(mailbox.TryTakeBatch(out var repaint) && repaint!.Updates.Length == 1 && repaint.Updates[0].Rgba.Length == desktop.Length,
    "full repaint coalesces all bands into one exact snapshot");
mailbox.Apply(new(1800, 1000, 1, 1, [7, 7, 7, 255]));
mailbox.Resize(256, 256); mailbox.Apply(new(0, 0, 1, 1, [4, 3, 2, 255]));
Check(mailbox.TryTakeBatch(out var resized) && resized!.Generation > corners.Generation && resized.Size.Width == 256 && resized.Updates.Single().Width == 1,
    "resize removes every old-generation sparse region");
var actual = new FrameBuffer(); actual.Resize(256, 256);
var expected = new FrameBuffer(); expected.Resize(256, 256);
mailbox.Resize(256, 256); mailbox.TryTakeBatch(out _);
var random = new Random(4271);
for (int batch = 0; batch < 100; batch++)
{
    for (int i = 0; i < 30; i++)
    {
        int w = random.Next(1, 33), h = random.Next(1, 65), x = random.Next(257-w), y = random.Next(257-h);
        byte[] pixels = new byte[w*h*4]; random.NextBytes(pixels);
        var update = new SurfaceUpdate(x, y, w, h, pixels); mailbox.Apply(update); expected.Apply(update);
    }
    Check(mailbox.TryTakeBatch(out var frame) && frame!.Updates.Length <= FrameMailbox.MaximumRegions, "sparse batch count bound " + batch);
    foreach (var update in frame!.Updates) actual.Apply(update);
    Check(actual.Snapshot().Rgba.SequenceEqual(expected.Snapshot().Rgba), "randomized sparse/full framebuffer equivalence " + batch);
}
using var writer = new BridgeSurfaceWriter();
using var capture = new CountingSocket(true);
byte[] original = new byte[1280 * 137 * 4]; random.NextBytes(original);
await writer.SendAsync(capture, new(11, 13, 1280, 137, original));
var reconstructed = new List<byte>(); int nextY = 13;
foreach (var packet in capture.Packets)
{
    var strip = BridgeWire.ParseSurface(packet);
    Check(strip.X == 11 && strip.Y == nextY && strip.Width == 1280 && packet.Length <= BridgeSurfaceWriter.MaximumPacketBytes, "pooled strip retains legacy framing and bounds");
    reconstructed.AddRange(strip.Rgba); nextY += strip.Height;
}
Check(nextY == 150 && reconstructed.SequenceEqual(original), "pooled strips reconstruct exact source bytes");
using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
long before = capture.Bytes;
try { await writer.SendAsync(capture, full, cancelled.Token); throw new Exception("Cancellation not honored"); }
catch (OperationCanceledException) { Check(capture.Bytes == before, "cancelled send emits no bytes"); }
try { await writer.SendAsync(capture, new(8191, 0, 2, 1, new byte[8])); throw new Exception("Bad bounds accepted"); }
catch (InvalidDataException) { Check(capture.Bytes == before, "invalid send rejected before transport"); }
using var counter = new CountingSocket(false);
for (int i = 0; i < 2; i++) { foreach (var part in BridgeWire.Split(full)) _ = BridgeWire.Surface(part); await writer.SendAsync(counter, full); }
const int iterations = 6;
long allocated = GC.GetAllocatedBytesForCurrentThread(), started = Stopwatch.GetTimestamp(), legacyBytes = 0;
for (int i = 0; i < iterations; i++) foreach (var part in BridgeWire.Split(full)) legacyBytes += BridgeWire.Surface(part).Length;
long legacyAllocated = GC.GetAllocatedBytesForCurrentThread() - allocated;
double legacyMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
allocated = GC.GetAllocatedBytesForCurrentThread(); started = Stopwatch.GetTimestamp(); before = counter.Bytes;
for (int i = 0; i < iterations; i++) await writer.SendAsync(counter, full);
long pooledAllocated = GC.GetAllocatedBytesForCurrentThread() - allocated;
double pooledMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
Check(counter.Bytes - before == legacyBytes, "pooled and legacy paths emit the same byte count");
Check(pooledAllocated < legacyAllocated / 10, "warm pooled encoder eliminates at least 90 percent of measured allocations");
Console.WriteLine("PERFORMANCE " + JsonSerializer.Serialize(new { iterations, width=1920, height=1080, legacyAllocated, pooledAllocated, legacyMs, pooledMs,
    scope="in-memory sender only; not network, presentation, or hardware GPU throughput" }));
Console.WriteLine($"RESULT: {checks} performance and pixel-integrity checks passed.");

sealed class CountingSocket(bool capture) : WebSocket
{
    public long Bytes { get; private set; }
    public List<byte[]> Packets { get; } = [];
    public override WebSocketCloseStatus? CloseStatus => null;
    public override string? CloseStatusDescription => null;
    public override WebSocketState State => WebSocketState.Open;
    public override string? SubProtocol => BridgeWire.SubProtocol;
    public override void Abort() { }
    public override Task CloseAsync(WebSocketCloseStatus status, string? description, CancellationToken ct) => Task.CompletedTask;
    public override Task CloseOutputAsync(WebSocketCloseStatus status, string? description, CancellationToken ct) => Task.CompletedTask;
    public override void Dispose() { }
    public override Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken ct) => throw new NotSupportedException();
    public override Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType type, bool endOfMessage, CancellationToken ct) => throw new NotSupportedException();
    public override ValueTask SendAsync(ReadOnlyMemory<byte> buffer, WebSocketMessageType type, bool endOfMessage, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (type != WebSocketMessageType.Binary || !endOfMessage) throw new Exception("Unexpected wire framing");
        Bytes += buffer.Length; if (capture) Packets.Add(buffer.ToArray()); return ValueTask.CompletedTask;
    }
}
