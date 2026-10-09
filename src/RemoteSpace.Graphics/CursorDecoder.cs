using RemoteSpace.Core;
using RemoteSpace.Protocol;

namespace RemoteSpace.Graphics;

/// <summary>Cached monochrome, color and alpha pointers. XOR inversion is preserved separately from alpha.</summary>
public sealed class CursorDecoder
{
    private readonly Dictionary<ushort, RemoteCursor> _cache = new();
    public RemoteCursor Decode(ReadOnlySpan<byte> bytes, int bpp)
    {
        var r = new PacketReader(bytes); ushort index = r.U16(); int hotX = r.U16(), hotY = r.U16(), width = r.U16(), height = r.U16();
        int andLength = r.U16(), xorLength = r.U16();
        if (index >= 20 || width is < 1 or > 96 || height is < 1 or > 96 || hotX >= width || hotY >= height || bpp is not (1 or 16 or 24 or 32))
            throw new RdpProtocolException("Unsupported pointer dimensions, hotspot, depth or cache index.");
        int xorStride = ((width * bpp + 15) / 16) * 2, andStride = ((width + 15) / 16) * 2;
        if (xorLength != xorStride * height || (andLength != andStride * height && !(bpp == 32 && andLength == 0)))
            throw new RdpProtocolException("Invalid pointer mask size.");
        var xor = r.Bytes(xorLength); var and = r.Bytes(andLength);
        if (r.Remaining == 1) r.U8(); r.End();
        byte[] rgba = new byte[width * height * 4], invert = new byte[width * height]; bool anyInversion = false;
        for (int y = 0; y < height; y++) for (int x = 0; x < width; x++)
        {
            int sourceY = height - 1 - y, offset = (y * width + x) * 4;
            bool mask = andLength != 0 && (and[sourceY * andStride + x / 8] & (0x80 >> (x & 7))) != 0;
            if (bpp == 1)
            {
                bool bit = (xor[sourceY * xorStride + x / 8] & (0x80 >> (x & 7))) != 0;
                rgba[offset] = rgba[offset + 1] = rgba[offset + 2] = bit ? (byte)255 : (byte)0;
                rgba[offset + 3] = mask ? (byte)0 : (byte)255;
                if (mask && bit) { invert[y * width + x] = 255; anyInversion = true; }
            }
            else
            {
                int at = sourceY * xorStride + x * (bpp / 8); uint value = 0;
                for (int i = 0; i < bpp / 8; i++) value |= (uint)xor[at + i] << (8 * i);
                if (bpp == 16)
                {
                    uint red = (value >> 11) & 31, green = (value >> 5) & 63, blue = value & 31;
                    rgba[offset] = (byte)((red << 3) | (red >> 2)); rgba[offset + 1] = (byte)((green << 2) | (green >> 4)); rgba[offset + 2] = (byte)((blue << 3) | (blue >> 2));
                }
                else { rgba[offset] = (byte)(value >> 16); rgba[offset + 1] = (byte)(value >> 8); rgba[offset + 2] = (byte)value; }
                rgba[offset + 3] = bpp == 32 ? (byte)(value >> 24) : mask ? (byte)0 : (byte)255;
                if (bpp != 32 && mask && (value & 0xffffff) != 0) { invert[y * width + x] = 255; anyInversion = true; }
            }
        }
        var cursor = new RemoteCursor(width, height, hotX, hotY, rgba, anyInversion ? invert : null); _cache[index] = cursor; return cursor;
    }
    public RemoteCursor Cached(ushort index) => _cache.TryGetValue(index, out var cursor) ? cursor : throw new RdpProtocolException("Server referenced an uncached pointer.");
}
