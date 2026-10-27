using System.IO.Compression;
using System.Buffers.Binary;
using System.Text;

namespace RemoteFrame.Rendering;

internal static class PngTextRasterizer
{
    private const int W = 960, H = 600, Scale = 2, GlyphW = 8 * Scale, GlyphH = 12 * Scale;
    public static byte[] Render(string[] lines, int firstLine)
    {
        var pixels = new byte[(W * 3 + 1) * H];
        for (int y = 0; y < H; y++)
        {
            var start = y * (W * 3 + 1);
            pixels[start] = 0; // PNG scanline filter: none
            for (int x = 0; x < W; x++)
            {
                var offset = start + 1 + x * 3;
                pixels[offset] = 13; pixels[offset + 1] = 19; pixels[offset + 2] = 30;
            }
        }
        int visibleRows = (H - 24) / GlyphH;
        int visibleCols = (W - 32) / GlyphW;
        for (int row = 0; row < visibleRows && row + firstLine < lines.Length; row++)
        {
            var line = lines[row + firstLine].Replace("\t", "    ");
            for (int col = 0; col < Math.Min(visibleCols, line.Length); col++)
            {
                for (int gy = 0; gy < 12; gy++)
                {
                    byte glyph = BitmapFont.Row(line[col], gy);
                    for (int gx = 0; gx < 8; gx++)
                    {
                        if ((glyph & (0x80 >> gx)) == 0) continue;
                        int px = 16 + col * GlyphW + gx * Scale;
                        int py = 12 + row * GlyphH + gy * Scale;
                        for (int sy = 0; sy < Scale; sy++)
                        for (int sx = 0; sx < Scale; sx++)
                        {
                            int offset = (py + sy) * (W * 3 + 1) + 1 + (px + sx) * 3;
                            pixels[offset] = 222; pixels[offset + 1] = 231; pixels[offset + 2] = 243;
                        }
                    }
                }
            }
        }
        using var output = new MemoryStream();
        output.Write(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });
        Span<byte> header = stackalloc byte[13];
        BinaryPrimitives.WriteInt32BigEndian(header[..4], W);
        BinaryPrimitives.WriteInt32BigEndian(header.Slice(4, 4), H);
        header[8] = 8; header[9] = 2; // 8-bit RGB
        WriteChunk(output, "IHDR", header);
        using var compressed = new MemoryStream();
        using (var z = new ZLibStream(compressed, CompressionLevel.Fastest, true)) z.Write(pixels);
        WriteChunk(output, "IDAT", compressed.ToArray());
        WriteChunk(output, "IEND", ReadOnlySpan<byte>.Empty);
        return output.ToArray();
    }
    private static void WriteChunk(Stream stream, string type, ReadOnlySpan<byte> data)
    {
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(length, (uint)data.Length);
        stream.Write(length);
        byte[] tag = Encoding.ASCII.GetBytes(type);
        stream.Write(tag);
        stream.Write(data);
        uint crc = 0xffffffff;
        foreach (byte b in tag) crc = CrcByte(crc, b);
        foreach (byte b in data) crc = CrcByte(crc, b);
        BinaryPrimitives.WriteUInt32BigEndian(length, ~crc);
        stream.Write(length);
    }
    private static uint CrcByte(uint crc, byte value)
    {
        crc ^= value;
        for (int k = 0; k < 8; k++) crc = (crc >> 1) ^ ((crc & 1) != 0 ? 0xedb88320u : 0u);
        return crc;
    }
}
