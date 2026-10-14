using System.Runtime.CompilerServices;
using RemoteSpace.Core;
using RemoteSpace.Input;

internal static class KeyboardCommandTests
{
    internal static async Task RunAsync()
    {
        int checks = 0;
        void Check(bool condition, string message)
        {
            if (!condition) throw new Exception("FAILED keyboard commands: " + message);
            checks++;
        }
        void Reject(Action action, string message)
        {
            bool rejected = false;
            try { action(); } catch (ArgumentException) { rejected = true; }
            Check(rejected, message);
        }
        var commands = RemoteKeyboardCommands.All;
        Check(commands.Count == 9 && commands.Select(c => c.Command).Distinct().Count() == commands.Count, "unique command catalog");
        ushort[][] reference = [[0x1d,0x38,0x153], [0x1d,0x2a,0x01], [0x1d,0x01], [0x38,0x0f],
            [0x38,0x2a,0x0f], [0x38,0x39], [0x137], [0x38,0x137], [0x01]];
        for (int index = 0; index < commands.Count; index++)
        {
            var entry = commands[index];
            var events = new RemoteInput[RemoteKeyboardCommands.MaximumEvents + 1];
            var sentinel = new RemoteInput(InputKind.Synchronize, 0, 7);
            Array.Fill(events, sentinel);
            int written = RemoteKeyboardCommands.Write(entry.Command, events);
            Check(written == reference[index].Length * 2 && written == RemoteKeyboardCommands.GetEventCount(entry.Command), entry.Label + " exact length");
            var held = new List<ushort>();
            for (int i = 0; i < written; i++)
            {
                var e = events[i]; RemoteInputValidation.Validate(e, InputCapabilities.Basic);
                if (i < written / 2)
                {
                    ushort expected = reference[index][i];
                    Check(e.Code == (expected & 0x7f) && e.Flags == (expected & 0x100), entry.Label + " independent scan-code reference");
                    held.Add((ushort)(e.Code | e.Flags));
                }
                else
                {
                    Check((e.Code | (e.Flags & 0x100)) == held[^1] && (e.Flags & 0x8000) != 0, entry.Label + " reverse balanced release");
                    held.RemoveAt(held.Count - 1);
                }
            }
            Check(held.Count == 0 && events[written] == sentinel, "complete chord does not touch trailing storage");
            Array.Fill(events, sentinel);
            Reject(() => RemoteKeyboardCommands.Write(entry.Command, events.AsSpan(0, written - 1)), "short buffer rejected before modification");
            Check(events.All(e => e == sentinel), "short buffer unchanged");
        }
        var storage = new RemoteInput[6];
        RemoteKeyboardCommands.Write(RemoteKeyboardCommand.SecureAttention, storage);
        Check(storage.SequenceEqual(InputState.SecureAttention()), "existing secure-attention wire sequence preserved");
        Reject(() => RemoteKeyboardCommands.Write((RemoteKeyboardCommand)999, storage), "undefined command rejected");

        var state = new InputState();
        var queue = new RemoteInputQueue(12);
        queue.TryEnqueue(state.KeyDown(0xa3)!.Value);
        queue.TryEnqueue(state.Button(0x1000, true, 40, 50)!.Value);
        queue.TryEnqueue(state.ExtendedButton(1, true, 40, 50)!.Value);
        state.Move(70, 80);
        Check(state.TryEnqueueCommand(queue, RemoteKeyboardCommand.SecureAttention), "release plus complete chord admitted");
        Check(state.HeldKeyCount == 0 && state.HeldButtonCount == 0 && queue.Count == 12, "successful admission clears held state once");
        var captured = await DrainAsync(queue);
        Check(captured[3] == new RemoteInput(InputKind.ScanCode, 0x8100, 0x1d), "right Ctrl released before shortcut");
        Check(captured[4] == new RemoteInput(InputKind.Pointer, 0x1000, 0, 70, 80) &&
            captured[5] == new RemoteInput(InputKind.ExtendedPointer, 1, 0, 70, 80), "both pointer types released at current position");
        Check(captured.Skip(6).SequenceEqual(storage), "queue preserves all command events after releases");
        Check(state.KeyUp(0xa3) is null && state.ReleaseAll().Length == 0, "late physical release cannot repeat a released modifier");

        state = new InputState(); queue = new RemoteInputQueue(6);
        queue.TryEnqueue(state.KeyDown(0xa3)!.Value);
        Check(!state.TryEnqueueCommand(queue, RemoteKeyboardCommand.SecureAttention), "capacity failure rejects entire transaction");
        Check(queue.Count == 1 && state.HeldKeyCount == 1, "failed transaction preserves queue and held state");
        Check(state.KeyUp(0xa3) == new RemoteInput(InputKind.ScanCode, 0x8100, 0x1d), "failed command does not forget held key");
        Check((await DrainAsync(queue)).Length == 1, "failure queued no partial command or release");
        queue = new RemoteInputQueue(); queue.Complete(); state.KeyDown(0x12);
        Check(!state.TryEnqueueCommand(queue, RemoteKeyboardCommand.Escape) && state.HeldKeyCount == 1, "closed queue leaves state intact");
        Reject(() => state.TryEnqueueCommand(queue, (RemoteKeyboardCommand)(-1)), "invalid command never changes state");
        Check(state.HeldKeyCount == 1, "invalid command preserves input state");
        state.ReleaseAll(); queue = new RemoteInputQueue(128);
        for (uint i = 0; i < 80; i++) state.KeyDown(1000 + i, (i % 127) + 1);
        Check(state.TryEnqueueCommand(queue, RemoteKeyboardCommand.Escape), "large release lists use bounded pooled path");
        Check((await DrainAsync(queue)).Length == 82 && state.HeldKeyCount == 0, "pooled path preserves complete command");

        // All signed deltas, both axes; compare reconstructed rotation, length and flags.
        for (int delta = -12000; delta <= 12000; delta++)
        {
            foreach (bool horizontal in new[] { false, true })
            {
                int sum = 0; var wheel = state.Wheel(delta, 20, 30, horizontal);
                if (wheel.Length != (Math.Abs(delta) + 254) / 255) throw new Exception("Incorrect wheel length");
                foreach (var e in wheel)
                {
                    RemoteInputValidation.Validate(e);
                    if ((e.Flags & 0x0600) != (horizontal ? 0x0400 : 0x0200) || e.X != 20 || e.Y != 30)
                        throw new Exception("Wheel flags or position changed");
                    int part = e.Flags & 0x01ff; sum += (part & 0x100) == 0 ? part : part - 512;
                }
                if (sum != delta) throw new Exception("Wheel signed sum changed");
            }
        }
        Check(true, "48,002 wheel cases preserve exact signed rotation");
        Reject(() => state.Wheel(12001, 0, 0), "oversized wheel delta rejected");
        foreach (string text in new[] { "", "ABC", "Zażółć 日本語 😀", "\r\n\t", new string('x', 16384) })
        {
            var result = InputState.UnicodeText(text);
            Check(result.Length == 2 * text.Length, "UTF-16 command exact length");
            Check(result.Select(e => e.Code).SequenceEqual(text.SelectMany(c => new ushort[] { c, c })), "UTF-16 unit order preserved");
            Check(result.Where((_, i) => i % 2 == 0).All(e => e.Flags == 0) &&
                result.Where((_, i) => i % 2 == 1).All(e => e.Flags == 0x8000), "Unicode make/break pairs preserved");
        }
        foreach (string text in new[] { "x\ud800", "\udc00x", "\ud800x", "x\0", new string('x', 16385) })
            Reject(() => InputState.UnicodeText(text), "invalid UTF-16 or bound rejected before event allocation");

        var idle = new InputState();
        _ = IdleKernel(idle);
        long before = GC.GetAllocatedBytesForCurrentThread(); int checksum = IdleKernel(idle);
        long idleBytes = GC.GetAllocatedBytesForCurrentThread() - before;
        Check(idleBytes == 0 && checksum == 0, "100000 idle release/wheel/text/command writes allocate zero bytes");
        var text4096 = new string('x', 4096); _ = InputState.UnicodeText(text4096);
        before = GC.GetAllocatedBytesForCurrentThread(); var encoded = InputState.UnicodeText(text4096);
        long textBytes = GC.GetAllocatedBytesForCurrentThread() - before;
        Check(textBytes <= (long)encoded.Length * Unsafe.SizeOf<RemoteInput>() + 32, "Unicode output uses one array allocation");
        _ = state.Wheel(12000, 0, 0);
        before = GC.GetAllocatedBytesForCurrentThread(); var output = state.Wheel(12000, 0, 0);
        long wheelBytes = GC.GetAllocatedBytesForCurrentThread() - before;
        Check(wheelBytes <= (long)output.Length * Unsafe.SizeOf<RemoteInput>() + 32, "wheel output uses one array allocation");
        Console.WriteLine($"PASS {checks} keyboard transaction and input allocation checks; 48002 wheel cases; idle={idleBytes} Unicode={textBytes} wheel={wheelBytes} allocated bytes.");
    }

    private static async Task<RemoteInput[]> DrainAsync(RemoteInputQueue queue)
    {
        var events = new List<RemoteInput>(); queue.Complete();
        await queue.RunAsync((inputs, _) => { events.AddRange(inputs.ToArray()); return ValueTask.CompletedTask; });
        return events.ToArray();
    }
    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)]
    private static int IdleKernel(InputState state)
    {
        Span<RemoteInput> chord = stackalloc RemoteInput[6]; int sum = 0;
        for (int i = 0; i < 100_000; i++)
        {
            sum += state.ReleaseAll().Length + state.Wheel(0, 0, 0).Length + InputState.UnicodeText("").Length;
            RemoteKeyboardCommands.Write(RemoteKeyboardCommand.SecureAttention, chord);
        }
        return sum;
    }
}
