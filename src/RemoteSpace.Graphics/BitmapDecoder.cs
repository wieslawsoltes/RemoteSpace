using RemoteSpace.Core;
using RemoteSpace.Protocol;

namespace RemoteSpace.Graphics;

/// <summary>Independent decoder for MS-RDPBCGR 2.2.9.1.1.3.1.2. All compressed runs and allocations are bounded.</summary>
public static class BitmapDecoder
{
    public const int MaxPixels = 16_777_216;
    public static byte[] Decode(ReadOnlySpan<byte> bytes, int width, int height, int bitsPerPixel, bool compressed, uint[]? palette = null)
    {
        if (width is < 1 or > 8192 || height is < 1 or > 8192 || (long)width * height > MaxPixels)
            throw new RdpProtocolException("Bitmap dimensions exceed the decoder limit.");
        if (bitsPerPixel is not (8 or 15 or 16 or 24 or 32) || (compressed && bitsPerPixel == 32))
            throw new RdpProtocolException("Unsupported bitmap encoding; planar/AVC codecs are not negotiated.");
        int size = (bitsPerPixel + 7) / 8, count = checked(width * height);
        uint[] pixels = new uint[count];
        if (compressed) DecodeInterleaved(bytes, pixels, width, size, bitsPerPixel);
        else
        {
            int stride = (width * size + 3) & ~3;
            if (bytes.Length != checked(stride * height)) throw new RdpProtocolException("Uncompressed bitmap has an invalid padded size.");
            for (int y = 0; y < height; y++)
            {
                var row = new PacketReader(bytes.Slice(y * stride, width * size));
                for (int x = 0; x < width; x++) pixels[y * width + x] = ReadPixel(ref row, size);
            }
        }
        byte[] rgba = new byte[checked(count * 4)];
        for (int y = 0; y < height; y++)
        for (int x = 0; x < width; x++)
        {
            uint p = pixels[(height - y - 1) * width + x]; int offset = (y * width + x) * 4;
            switch (bitsPerPixel)
            {
                case 8:
                    if (palette is null || p >= palette.Length) throw new RdpProtocolException("Indexed bitmap has no valid palette.");
                    p = palette[p]; goto default;
                case 15:
                    rgba[offset] = Expand5((p >> 10) & 31); rgba[offset + 1] = Expand5((p >> 5) & 31); rgba[offset + 2] = Expand5(p & 31); break;
                case 16:
                    rgba[offset] = Expand5((p >> 11) & 31); uint g = (p >> 5) & 63;
                    rgba[offset + 1] = (byte)((g << 2) | (g >> 4)); rgba[offset + 2] = Expand5(p & 31); break;
                default:
                    rgba[offset] = (byte)(p >> 16); rgba[offset + 1] = (byte)(p >> 8); rgba[offset + 2] = (byte)p; break;
            }
            rgba[offset + 3] = 255;
        }
        return rgba;
    }
    private static byte Expand5(uint n) => (byte)((n << 3) | (n >> 2));
    private static uint ReadPixel(ref PacketReader r, int size)
    {
        uint pixel = 0; for (int i = 0; i < size; i++) pixel |= (uint)r.U8() << (i * 8); return pixel;
    }
    private static void DecodeInterleaved(ReadOnlySpan<byte> data, uint[] target, int width, int bytesPerPixel, int bpp)
    {
        var r = new PacketReader(data); int pos = 0; uint foreground = bpp == 15 ? 0x7fffU : bpp == 16 ? 0xffffU : bpp == 8 ? 255U : 0xffffffU;
        uint white = foreground; bool backgroundPreceded = false;
        while (r.Remaining > 0)
        {
            if (pos >= target.Length) throw new RdpProtocolException("Compressed bitmap has excess orders.");
            int header = r.U8(), op, length, form;
            if (header < 0xc0) { op = header >> 5; length = header & 31; form = 0; }
            else if (header < 0xf0) { op = header >> 4; length = header & 15; form = 1; }
            else
            {
                form = 2;
                op = header switch { 0xf0 => 0, 0xf1 => 1, 0xf2 => 2, 0xf3 => 3, 0xf4 => 4, 0xf6 => 12, 0xf7 => 13, 0xf8 => 14,
                    0xf9 => 25, 0xfa => 26, 0xfd => 29, 0xfe => 30, _ => throw new RdpProtocolException("Reserved bitmap compression opcode.") };
                length = op < 15 ? r.U16() : op is 25 or 26 ? 8 : 1;
            }
            if (form != 2)
            {
                bool maskRun = op is 2 or 13;
                length = length == 0 ? r.U8() + (maskRun ? 1 : form == 0 ? 32 : 16) : maskRun ? length * 8 : length;
            }
            if (op is not (0 or 1 or 2 or 3 or 4 or 12 or 13 or 14 or 25 or 26 or 29 or 30))
                throw new RdpProtocolException("Invalid bitmap compression opcode.");
            int outputCount = op == 14 ? checked(length * 2) : length;
            if (length <= 0 || outputCount > target.Length - pos) throw new RdpProtocolException("Bitmap run exceeds the destination.");
            if (op is 12 or 13) foreground = ReadPixel(ref r, bytesPerPixel);
            uint colorA = 0, colorB = 0;
            if (op is 3 or 14) colorA = ReadPixel(ref r, bytesPerPixel);
            if (op == 14) colorB = ReadPixel(ref r, bytesPerPixel);
            int mask = op == 25 ? 3 : op == 26 ? 5 : 0;
            bool insertForeground = op == 0 && backgroundPreceded && pos != width;
            for (int i = 0; i < outputCount; i++)
            {
                uint previous = pos < width ? 0 : target[pos - width];
                uint pixel;
                switch (op)
                {
                    case 0: pixel = previous ^ (insertForeground && i == 0 ? foreground : 0); break;
                    case 1: case 12: pixel = previous ^ foreground; break;
                    case 2: case 13:
                        if ((i & 7) == 0) mask = r.U8();
                        pixel = previous ^ (((mask >> (i & 7)) & 1) != 0 ? foreground : 0); break;
                    case 3: pixel = colorA; break;
                    case 4: pixel = ReadPixel(ref r, bytesPerPixel); break;
                    case 14: pixel = (i & 1) == 0 ? colorA : colorB; break;
                    case 25: case 26: pixel = previous ^ (((mask >> i) & 1) != 0 ? foreground : 0); break;
                    case 29: pixel = white; break;
                    default: pixel = 0; break;
                }
                target[pos++] = pixel;
            }
            backgroundPreceded = op == 0;
        }
        if (pos != target.Length) throw new RdpProtocolException("Compressed bitmap ended before all pixels were decoded.");
    }
    public static void DecodeUpdates(ReadOnlySpan<byte> packet, Action<SurfaceUpdate> accept, uint[]? palette = null)
    {
        var r = new PacketReader(packet);
        if (r.U16() != 1) throw new RdpProtocolException("Expected bitmap update type.");
        int count = r.U16(); if (count > 4096) throw new RdpProtocolException("Too many bitmap rectangles.");
        long budget = 0;
        for (int i = 0; i < count; i++)
        {
            int x = r.U16(), y = r.U16(), right = r.U16(), bottom = r.U16();
            int width = r.U16(), height = r.U16(), bpp = r.U16(), flags = r.U16(), length = r.U16();
            int displayWidth = right - x + 1, displayHeight = bottom - y + 1;
            budget += (long)width * height;
            if (displayWidth < 1 || displayHeight < 1 || displayWidth > width || displayHeight > height || budget > MaxPixels * 2L)
                throw new RdpProtocolException("Invalid bitmap rectangle or update budget exceeded.");
            var content = r.Bytes(length); bool compressed = (flags & 1) != 0;
            if ((flags & ~0x401) != 0) throw new RdpProtocolException("Unknown bitmap flags.");
            if (compressed && (flags & 0x400) == 0)
            {
                var h = new PacketReader(content); h.U16(); int bodyLength = h.U16(); h.U16(); h.U16();
                if (bodyLength != h.Remaining) throw new RdpProtocolException("Compressed bitmap header size mismatch.");
                content = h.Bytes(bodyLength);
            }
            byte[] pixels = Decode(content, width, height, bpp, compressed, palette);
            if (displayWidth != width || displayHeight != height)
            {
                byte[] clipped = new byte[checked(displayWidth * displayHeight * 4)];
                for (int row = 0; row < displayHeight; row++) pixels.AsSpan(row * width * 4, displayWidth * 4).CopyTo(clipped.AsSpan(row * displayWidth * 4));
                pixels = clipped;
            }
            accept(new(x, y, displayWidth, displayHeight, pixels));
        }
        r.End();
    }
}
