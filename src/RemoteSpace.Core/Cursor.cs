namespace RemoteSpace.Core;

public sealed record RemoteCursor(int Width, int Height, int HotX, int HotY, byte[] Rgba, byte[]? InvertMask = null, bool Hidden = false, bool SystemDefault = false);
public interface IRemoteCursorSource
{
    event Action<RemoteCursor>? CursorChanged;
    event Action<ushort, ushort>? CursorPositionChanged;
}
