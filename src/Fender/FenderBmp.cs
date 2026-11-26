using System.Buffers.Binary;

namespace OpenTone;

/// <summary>
/// Minimal BMP reader/writer (uncompressed 24- and 32-bit) for exporting and importing firmware images
/// without an imaging dependency. Pixels are 0xRRGGBB, row-major, top row first.
/// </summary>
public static class FenderBmp
{
    public static (int Width, int Height, int[] Pixels) Read(string path)
    {
        byte[] d = File.ReadAllBytes(path);
        if (d.Length < 54 || d[0] != (byte)'B' || d[1] != (byte)'M') throw new InvalidDataException($"{path} is not a BMP file.");

        int dataOffset = BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(10));
        int width = BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(18));
        int rawHeight = BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(22));
        int bpp = BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan(28));
        int compression = BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(30));

        if (bpp != 24 && bpp != 32) throw new InvalidDataException($"{path}: only 24- or 32-bit BMP is supported (got {bpp}-bit).");
        if (compression != 0 && !(compression == 3 && bpp == 32)) throw new InvalidDataException($"{path}: compressed BMP is not supported.");

        bool topDown = rawHeight < 0;
        int height = Math.Abs(rawHeight);
        int bytesPerPixel = bpp / 8;
        int stride = (width * bytesPerPixel + 3) & ~3;
        if (dataOffset + stride * height > d.Length) throw new InvalidDataException($"{path} is truncated.");

        var pixels = new int[width * height];
        for (int y = 0; y < height; y++)
        {
            int row = dataOffset + (topDown ? y : height - 1 - y) * stride;
            for (int x = 0; x < width; x++)
            {
                int p = row + x * bytesPerPixel;
                pixels[y * width + x] = (d[p + 2] << 16) | (d[p + 1] << 8) | d[p];   // stored as B, G, R
            }
        }
        return (width, height, pixels);
    }

    public static void Write(string path, int width, int height, int[] pixels)
    {
        int stride = (width * 3 + 3) & ~3;
        byte[] d = new byte[54 + stride * height];
        d[0] = (byte)'B'; d[1] = (byte)'M';
        BinaryPrimitives.WriteInt32LittleEndian(d.AsSpan(2), d.Length);
        BinaryPrimitives.WriteInt32LittleEndian(d.AsSpan(10), 54);
        BinaryPrimitives.WriteInt32LittleEndian(d.AsSpan(14), 40);
        BinaryPrimitives.WriteInt32LittleEndian(d.AsSpan(18), width);
        BinaryPrimitives.WriteInt32LittleEndian(d.AsSpan(22), height);       // bottom-up
        BinaryPrimitives.WriteUInt16LittleEndian(d.AsSpan(26), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(d.AsSpan(28), 24);
        BinaryPrimitives.WriteInt32LittleEndian(d.AsSpan(34), stride * height);

        for (int y = 0; y < height; y++)
        {
            int row = 54 + (height - 1 - y) * stride;
            for (int x = 0; x < width; x++)
            {
                int c = pixels[y * width + x];
                d[row + x * 3] = (byte)c;
                d[row + x * 3 + 1] = (byte)(c >> 8);
                d[row + x * 3 + 2] = (byte)(c >> 16);
            }
        }
        File.WriteAllBytes(path, d);
    }

    /// <summary>0xRRGGBB to RGB565 (truncating, as the display has 5/6/5 bits).</summary>
    public static ushort ToRgb565(int rgb) =>
        (ushort)((((rgb >> 16) & 0xFF) >> 3 << 11) | (((rgb >> 8) & 0xFF) >> 2 << 5) | ((rgb & 0xFF) >> 3));

    /// <summary>RGB565 to 0xRRGGBB, scaling each channel to the full 0-255 range.</summary>
    public static int FromRgb565(ushort v) =>
        ((((v >> 11) & 31) * 255 / 31) << 16) | ((((v >> 5) & 63) * 255 / 63) << 8) | ((v & 31) * 255 / 31);
}
