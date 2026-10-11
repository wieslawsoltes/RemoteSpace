namespace RemoteSpace.Core;

public sealed record InputCapabilities(bool Unicode, bool ExtendedMouse, bool HorizontalWheel)
{
    public static InputCapabilities Basic { get; } = new(false, false, false);
}

public interface IRemoteInputCapabilities
{
    InputCapabilities? InputCapabilities { get; }
    event Action<InputCapabilities?>? InputCapabilitiesChanged;
}

/// <summary>Optional ordered batch sender. Implementations copy/encode input before their
/// first asynchronous wait. A successful return means sent, not server-acknowledged.</summary>
public interface IRemoteInputBatch
{
    ValueTask SendInputsAsync(ReadOnlyMemory<RemoteInput> inputs, CancellationToken cancellationToken = default);
}

public static class RemoteInputValidation
{
    public const int MaximumBatch = 64;
    public static void Validate(RemoteInput input, InputCapabilities? capabilities = null)
    {
        bool valid = input.Kind switch
        {
            InputKind.ScanCode => input.Code is > 0 and <= 255 && (input.Flags & ~0xc300) == 0 && input.X == 0 && input.Y == 0,
            InputKind.Unicode => (input.Flags & ~0x8000) == 0 && input.X == 0 && input.Y == 0,
            InputKind.Synchronize => input.Flags == 0 && input.Code <= 15 && input.X == 0 && input.Y == 0,
            InputKind.ExtendedPointer => input.Code == 0 && (input.Flags & ~0x8003) == 0 && (input.Flags & 3) != 0,
            InputKind.Pointer => input.Code == 0 && ValidPointer(input.Flags),
            _ => false
        };
        if (!valid) throw new ArgumentException("Invalid remote input event or reserved flags.", nameof(input));
        if (capabilities is not null &&
            ((input.Kind == InputKind.Unicode && !capabilities.Unicode) ||
             (input.Kind == InputKind.ExtendedPointer && !capabilities.ExtendedMouse) ||
             (input.Kind == InputKind.Pointer && (input.Flags & 0x0400) != 0 && !capabilities.HorizontalWheel)))
            throw new NotSupportedException("The server did not advertise this input format.");
    }
    public static bool IsMotion(RemoteInput input) => input.Kind == InputKind.Pointer && input.Flags == 0x0800 && input.Code == 0;
    public static bool IsPositioned(RemoteInput input) => input.Kind == InputKind.ExtendedPointer ||
        (input.Kind == InputKind.Pointer && (input.Flags & 0x0600) == 0);
    private static bool ValidPointer(ushort flags)
    {
        int wheel = flags & 0x0600;
        if (wheel != 0) return wheel != 0x0600 && (flags & ~(wheel | 0x01ff)) == 0;
        return (flags & ~0xf800) == 0 && (flags & 0x7800) != 0 && ((flags & 0x8000) == 0 || (flags & 0x7000) != 0);
    }
}
