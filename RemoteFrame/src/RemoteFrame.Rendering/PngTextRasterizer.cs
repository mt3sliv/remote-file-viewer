namespace RemoteFrame.Rendering;

internal static class PngTextRasterizer
{
    private const int Scale = 2, GlyphW = 8 * Scale, GlyphH = 12 * Scale;
    public static byte[] Render(string[] lines, int firstLine, int W, int H, int streamWidth, int streamHeight)
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
        var bgra = new byte[W * H * 4];
        for (int y = 0; y < H; y++)
        for (int x = 0; x < W; x++)
        {
            int source = y * (W * 3 + 1) + 1 + x * 3, target = (y * W + x) * 4;
            bgra[target] = pixels[source + 2]; bgra[target + 1] = pixels[source + 1]; bgra[target + 2] = pixels[source];
        }
        return PngFrameEncoder.EncodeBgra(bgra, W, H, streamWidth, streamHeight);
    }
}
