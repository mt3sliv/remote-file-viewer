using System.IO.Compression;
using System.Buffers.Binary;
using System.Text;

namespace RemoteFrame.Rendering;

public static class PngFrameEncoder
{
    public static byte[] EncodeBgra(byte[] bgra, int width, int height)
    {
        if (width <= 0 || height <= 0 || bgra.Length != checked(width * height * 4))
            throw new ArgumentException("Invalid BGRA frame dimensions.");
        var pixels = new byte[checked((width * 3 + 1) * height)];
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            var source = (y * width + x) * 4;
            var target = y * (width * 3 + 1) + 1 + x * 3;
            pixels[target] = bgra[source + 2];
            pixels[target + 1] = bgra[source + 1];
            pixels[target + 2] = bgra[source];
        }
        using var output = new MemoryStream();
        output.Write(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });
        Span<byte> header = stackalloc byte[13];
        header.Clear();
        BinaryPrimitives.WriteInt32BigEndian(header[..4], width);
        BinaryPrimitives.WriteInt32BigEndian(header.Slice(4, 4), height);
        header[8] = 8; header[9] = 2;
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
