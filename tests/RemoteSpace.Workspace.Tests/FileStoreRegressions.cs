using System.Text;
using RemoteSpace.Core;
using RemoteSpace.Profiles;
using RemoteSpace.Workspace;

internal static class FileStoreRegressions
{
    internal static async Task RunAsync()
    {
        int checks = 0;
        void Check(bool condition, string name) { if (!condition) throw new Exception("FAILED " + name); checks++; }
        async Task Reject(Func<Task> action, string name)
        {
            bool failed = false;
            try { await action(); } catch (Exception e) when (e is IOException or InvalidDataException or InvalidOperationException or System.Text.Json.JsonException or OperationCanceledException) { failed = true; }
            Check(failed, name);
        }
        var directory = Path.Combine(Path.GetTempPath(), "RemoteSpace-store-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string path = Path.Combine(directory, "workspace.json");
            var first = new FileWorkspaceStore(path); var second = new FileWorkspaceStore(path);
            await Reject(() => first.SaveAsync(new()), "save before load rejected");
            var a = await first.LoadAsync(); var b = await second.LoadAsync();
            var profile = new ConnectionProfile { Host = "pc.test.invalid", Name = "Test PC" };
            a.Upsert(profile); b.Upsert(profile with { Name = "Other PC" });
            async Task<bool> Save(FileWorkspaceStore store, DeviceCatalog catalog)
            {
                try { await store.SaveAsync(catalog); return true; } catch (IOException) { return false; }
            }
            var attempts = await Task.WhenAll(Save(first,a), Save(second,b));
            Check(attempts.Count(v => v) == 1, "concurrent writers with equal revisions have exactly one winner");
            var actual = await first.LoadAsync(); string current = await File.ReadAllTextAsync(path);
            Check(actual.Count == 1, "winner is a complete valid document");
            // Simulate a different complete document with the SAME numeric revision.
            var edited = current.Replace("Test PC", "Replacement").Replace("Other PC", "Replacement");
            await File.WriteAllTextAsync(path, edited);
            actual.Upsert(profile with { Name = "Attempted overwrite" });
            await Reject(() => first.SaveAsync(actual), "same-revision external changes detected");
            Check(await File.ReadAllTextAsync(path) == edited, "conflict preserves external data");
            actual = await first.LoadAsync(); File.Delete(path);
            await Reject(() => first.SaveAsync(actual), "deleted document is not silently recreated");
            actual = await first.LoadAsync(); actual.Upsert(profile); await first.SaveAsync(actual);
            await File.WriteAllTextAsync(path, "{broken");
            await Reject(async () => { _ = await first.LoadAsync(); }, "corrupted reload rejected");
            await Reject(() => first.SaveAsync(actual), "failed reload disables stale writes");
            Check(await File.ReadAllTextAsync(path) == "{broken", "corrupt document preserved for recovery");
            await File.WriteAllTextAsync(path, "\ufeff" + actual.Serialize(), new UTF8Encoding(false));
            Check((await first.LoadAsync()).Count == 1, "legacy UTF-8 BOM document accepted");
            using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
            await Reject(() => first.SaveAsync(actual, cancelled.Token), "cancelled save preserves disk");
            using (var held = new FileStream(path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
            {
                using var deadline = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
                await Reject(async () => { _ = await second.LoadAsync(deadline.Token); }, "lock contention honors cancellation");
            }
            Check(!Directory.EnumerateFiles(directory, "*.tmp").Any(), "temporary replacements are cleaned");
            Check((await second.LoadAsync()).Count == 1, "lock reusable after contention");
            foreach (int length in new[] { 0, 1, 65536, 100000 })
            {
                byte[] bytes = new byte[length]; new Random(34).NextBytes(bytes);
                using var stream = new MemoryStream(bytes);
                Check((await BoundedInput.ReadAsync(stream, length)).SequenceEqual(bytes), "exact bounded stream length");
            }
            using var overflow = new EndlessStream();
            await Reject(() => BoundedInput.ReadAsync(overflow, 1000), "nonseekable oversized stream rejected");
            Check(overflow.ReadBytes == 1001, "oversize detection reads only one excess byte");
            Check(overflow.CanRead, "bounded reader does not own its input stream");
            var copied = new DeviceCatalog(); copied.Upsert(profile with { Name = new string('x',248) + "🙂" });
            var copy = copied.Duplicate(profile.Id);
            Check(!copy.Name.Contains('\ud83d'), "duplicate truncation does not split a surrogate pair");
            var fork = copied.Fork();
            var query = copied.Query(); fork = copied.Fork();
            Check(ReferenceEquals(query, fork.Query()), "copy-on-write transactions reuse immutable query caches");
            Console.WriteLine($"PASS {checks} native workspace locking/conflict/atomic-save and bounded-input regressions");
        }
        finally { Directory.Delete(directory, true); }
    }
    private sealed class EndlessStream : Stream
    {
        public long ReadBytes { get; private set; }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) { Array.Fill(buffer, (byte)7, offset, count); ReadBytes += count; return count; }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) { ct.ThrowIfCancellationRequested(); buffer.Span.Fill(7); ReadBytes += buffer.Length; return ValueTask.FromResult(buffer.Length); }
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
