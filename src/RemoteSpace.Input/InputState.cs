using RemoteSpace.Core;

namespace RemoteSpace.Input;

/// <summary>Platform-neutral keyboard/pointer state. Call from one UI thread.
/// Tracks exactly what was sent so focus loss can release held input without synthesizing new presses.</summary>
public sealed class InputState
{
    private readonly Dictionary<uint, (ushort Scan, bool Extended)> _keys = new();
    private readonly HashSet<ushort> _buttons = new();
    private ushort _x, _y;
    public int HeldKeyCount => _keys.Count;
    public int HeldButtonCount => _buttons.Count;

    public RemoteInput? KeyDown(uint virtualKey, uint scanCode = 0, bool extended = false)
    {
        if (!_keys.TryGetValue(virtualKey, out var key))
        {
            if (scanCode is > 0 and <= 0x7f) key = ((ushort)scanCode, extended);
            else if (!TryMapKey(virtualKey, out key)) return null;
            _keys.Add(virtualKey, key);
        }
        // Repeated downs are preserved for remote key repeat. The original mapping owns the release.
        return new(InputKind.ScanCode, key.Extended ? (ushort)0x0100 : (ushort)0, key.Scan);
    }
    public RemoteInput? KeyUp(uint virtualKey)
    {
        if (!_keys.Remove(virtualKey, out var key)) return null;
        return new(InputKind.ScanCode, (ushort)(0x8000 | (key.Extended ? 0x0100 : 0)), key.Scan);
    }
    public RemoteInput Move(ushort x, ushort y)
    {
        _x = x; _y = y; return new(InputKind.Pointer, 0x0800, 0, x, y);
    }
    public RemoteInput? Button(ushort buttonFlag, bool down, ushort x, ushort y)
    {
        if (buttonFlag is not (0x1000 or 0x2000 or 0x4000)) throw new ArgumentOutOfRangeException(nameof(buttonFlag));
        _x = x; _y = y;
        if (down ? !_buttons.Add(buttonFlag) : !_buttons.Remove(buttonFlag)) return null;
        return new(InputKind.Pointer, (ushort)(0x0800 | buttonFlag | (down ? 0x8000 : 0)), 0, x, y);
    }
    public RemoteInput[] Wheel(int delta, ushort x, ushort y)
    {
        if (delta is < -12000 or > 12000) throw new ArgumentOutOfRangeException(nameof(delta));
        _x = x; _y = y;
        var result = new List<RemoteInput>();
        while (delta != 0)
        {
            int part = Math.Clamp(delta, -255, 255); delta -= part;
            // MS-RDPBCGR TS_POINTER_EVENT: signed 9-bit rotation includes PTRFLAGS_WHEEL_NEGATIVE.
            result.Add(new(InputKind.Pointer, (ushort)(0x0200 | (part & 0x01ff)), 0, x, y));
        }
        return result.ToArray();
    }
    public RemoteInput[] ReleaseAll()
    {
        var result = _keys.Values.Select(k => new RemoteInput(InputKind.ScanCode,
            (ushort)(0x8000 | (k.Extended ? 0x0100 : 0)), k.Scan)).ToList();
        result.AddRange(_buttons.Select(b => new RemoteInput(InputKind.Pointer, b, 0, _x, _y)));
        _keys.Clear(); _buttons.Clear(); return result.ToArray();
    }
    public static RemoteInput[] UnicodeText(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text.Length > 16384 || text.Contains('\0')) throw new ArgumentException("Text exceeds the input limit or contains NUL.", nameof(text));
        var result = new List<RemoteInput>(text.Length * 2);
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (char.IsHighSurrogate(c))
            {
                if (i + 1 >= text.Length || !char.IsLowSurrogate(text[i + 1])) throw new ArgumentException("Unpaired UTF-16 surrogate.", nameof(text));
            }
            else if (char.IsLowSurrogate(c) && (i == 0 || !char.IsHighSurrogate(text[i - 1]))) throw new ArgumentException("Unpaired UTF-16 surrogate.", nameof(text));
            result.Add(new(InputKind.Unicode, 0, c)); result.Add(new(InputKind.Unicode, 0x8000, c));
        }
        return result.ToArray();
    }
    public static RemoteInput[] SecureAttention() =>
    [new(InputKind.ScanCode, 0, 0x1d), new(InputKind.ScanCode, 0, 0x38), new(InputKind.ScanCode, 0x0100, 0x53),
     new(InputKind.ScanCode, 0x8100, 0x53), new(InputKind.ScanCode, 0x8000, 0x38), new(InputKind.ScanCode, 0x8000, 0x1d)];

    /// <summary>Fallback for platforms that do not expose hardware scan codes. Values are Win32 virtual keys.
    /// The remote keyboard layout, not a US text substitution, interprets these physical keys.</summary>
    public static bool TryMapKey(uint key, out (ushort Scan, bool Extended) mapped)
    {
        const string letters = "ABCDEFGHIJKLMNOPQRSTUVWXYZ";
        ushort[] scans = [0x1e,0x30,0x2e,0x20,0x12,0x21,0x22,0x23,0x17,0x24,0x25,0x26,0x32,0x31,0x18,0x19,0x10,0x13,0x1f,0x14,0x16,0x2f,0x11,0x2d,0x15,0x2c];
        if (key >= letters[0] && key <= letters[^1]) { mapped = (scans[key - 'A'], false); return true; }
        if (key is >= 0x31 and <= 0x39) { mapped = ((ushort)(key - 0x31 + 2), false); return true; }
        if (key is >= 0x70 and <= 0x79) { mapped = ((ushort)(key - 0x70 + 0x3b), false); return true; }
        mapped = key switch
        {
            0x08 => (0x0e, false), 0x09 => (0x0f, false), 0x0d => (0x1c, false),
            0x10 or 0xa0 => (0x2a, false), 0xa1 => (0x36, false),
            0x11 or 0xa2 => (0x1d, false), 0xa3 => (0x1d, true),
            0x12 or 0xa4 => (0x38, false), 0xa5 => (0x38, true),
            0x14 => (0x3a, false), 0x1b => (0x01, false), 0x20 => (0x39, false),
            0x21 => (0x49, true), 0x22 => (0x51, true), 0x23 => (0x4f, true), 0x24 => (0x47, true),
            0x25 => (0x4b, true), 0x26 => (0x48, true), 0x27 => (0x4d, true), 0x28 => (0x50, true),
            0x2d => (0x52, true), 0x2e => (0x53, true), 0x30 => (0x0b, false),
            0x5b => (0x5b, true), 0x5c => (0x5c, true), 0x5d => (0x5d, true),
            0x60 => (0x52, false), 0x61 => (0x4f, false), 0x62 => (0x50, false), 0x63 => (0x51, false),
            0x64 => (0x4b, false), 0x65 => (0x4c, false), 0x66 => (0x4d, false), 0x67 => (0x47, false),
            0x68 => (0x48, false), 0x69 => (0x49, false), 0x6a => (0x37, false), 0x6b => (0x4e, false),
            0x6d => (0x4a, false), 0x6e => (0x53, false), 0x6f => (0x35, true),
            0x7a => (0x57, false), 0x7b => (0x58, false), 0x90 => (0x45, false), 0x91 => (0x46, false),
            0xba => (0x27, false), 0xbb => (0x0d, false), 0xbc => (0x33, false), 0xbd => (0x0c, false),
            0xbe => (0x34, false), 0xbf => (0x35, false), 0xc0 => (0x29, false),
            0xdb => (0x1a, false), 0xdc => (0x2b, false), 0xdd => (0x1b, false), 0xde => (0x28, false),
            _ => (0, false)
        };
        return mapped.Scan != 0;
    }
}
