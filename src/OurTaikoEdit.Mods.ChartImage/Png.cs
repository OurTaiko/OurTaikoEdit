using System.Buffers.Binary;
using System.IO.Compression;

namespace OurTaikoEdit.Mods.ChartImage;

// An image as red, green and blue bytes, row by row. Asking outside it matches nothing.
public sealed class Pixels(int width, int height, byte[] rgb)
{
    public int Width { get; } = width;
    public int Height { get; } = height;

    bool Inside(int x, int y) => x >= 0 && y >= 0 && x < Width && y < Height;

    public (int R, int G, int B) At(int x, int y)
    {
        int i = (y * Width + x) * 3;
        return (rgb[i], rgb[i + 1], rgb[i + 2]);
    }

    public bool Is(int x, int y, Rgb color)
    {
        if (!Inside(x, y)) return false;
        int i = (y * Width + x) * 3;
        return rgb[i] == color.R && rgb[i + 1] == color.G && rgb[i + 2] == color.B;
    }

    public bool IsAny(int x, int y, IReadOnlyList<Rgb> colors)
    {
        foreach (var c in colors) if (Is(x, y, c)) return true;
        return false;
    }

    public bool IsDark(int x, int y)
    {
        if (!Inside(x, y)) return false;
        var (r, g, b) = At(x, y);
        return r < 80 && g < 80 && b < 80;
    }

    public bool IsRedText(int x, int y)
    {
        if (!Inside(x, y)) return false;
        var (r, g, b) = At(x, y);
        return r > 140 && g < 110 && b < 110;
    }

    public bool IsBlueText(int x, int y)
    {
        if (!Inside(x, y)) return false;
        var (r, g, b) = At(x, y);
        return b > 140 && r < 110 && g < 110;
    }
}

public readonly record struct Rgb(byte R, byte G, byte B);

// Reads a PNG's pixels exactly as stored: the decoder compares colours for equality, so no
// colour management, scaling or smoothing may come between the file and the numbers. Every
// colour type and bit depth of the format; not interlaced files. Transparency is ignored.
public static class Png
{
    static readonly byte[] Signature = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };

    public static bool IsPng(ReadOnlySpan<byte> file) => file.Length >= 8 && file[..8].SequenceEqual(Signature);

    // Throws DecodeException for a file that is not a PNG this can read.
    public static Pixels Read(byte[] file)
    {
        if (!IsPng(file)) throw new DecodeException("ChartImageNotPng");
        int width = 0, height = 0, depth = 0, colorType = 0, interlace = 0;
        byte[]? palette = null;
        using var data = new MemoryStream();
        bool header = false, ended = false;
        for (int at = 8; at + 12 <= file.Length && !ended;)
        {
            int length = (int)BinaryPrimitives.ReadUInt32BigEndian(file.AsSpan(at));
            string type = System.Text.Encoding.ASCII.GetString(file, at + 4, 4);
            int body = at + 8;
            if (length < 0 || body + length + 4 > file.Length) throw new DecodeException("ChartImageBadPng");
            switch (type)
            {
                case "IHDR":
                    if (length < 13) throw new DecodeException("ChartImageBadPng");
                    width = (int)BinaryPrimitives.ReadUInt32BigEndian(file.AsSpan(body));
                    height = (int)BinaryPrimitives.ReadUInt32BigEndian(file.AsSpan(body + 4));
                    depth = file[body + 8];
                    colorType = file[body + 9];
                    interlace = file[body + 12];
                    header = true;
                    break;
                case "PLTE":
                    palette = file.AsSpan(body, length).ToArray();
                    break;
                case "IDAT":
                    data.Write(file, body, length);
                    break;
                case "IEND":
                    ended = true;
                    break;
            }
            at = body + length + 4;
        }
        int channels = colorType switch { 0 => 1, 2 => 3, 3 => 1, 4 => 2, 6 => 4, _ => 0 };
        bool depthOk = colorType switch
        {
            0 => depth is 1 or 2 or 4 or 8 or 16,
            3 => depth is 1 or 2 or 4 or 8,
            _ => depth is 8 or 16,
        };
        // 64 megapixels is far past any chart image and keeps a broken header from asking for the moon.
        if (!header || channels == 0 || !depthOk || width <= 0 || height <= 0 || (long)width * height > 64_000_000
            || colorType == 3 && palette == null)
            throw new DecodeException("ChartImageBadPng");
        if (interlace != 0) throw new DecodeException("ChartImageInterlaced");

        int bitsPerPixel = channels * depth;
        int bytesPerPixel = Math.Max(1, bitsPerPixel / 8);
        int stride = (width * bitsPerPixel + 7) / 8;
        byte[] raw = new byte[(stride + 1) * height];
        try
        {
            data.Position = 0;
            using var zlib = new ZLibStream(data, CompressionMode.Decompress);
            zlib.ReadExactly(raw);
        }
        catch (Exception e) when (e is InvalidDataException or EndOfStreamException)
        {
            throw new DecodeException("ChartImageBadPng");
        }

        // Each row is stored as a filter type and the row's bytes as differences from their
        // neighbours (to the left, above, and above left).
        byte[] rows = new byte[stride * height];
        for (int y = 0; y < height; y++)
        {
            int filter = raw[y * (stride + 1)];
            int source = y * (stride + 1) + 1, row = y * stride, above = row - stride;
            for (int i = 0; i < stride; i++)
            {
                int a = i >= bytesPerPixel ? rows[row + i - bytesPerPixel] : 0;
                int b = y > 0 ? rows[above + i] : 0;
                int c = y > 0 && i >= bytesPerPixel ? rows[above + i - bytesPerPixel] : 0;
                int predicted = filter switch
                {
                    0 => 0,
                    1 => a,
                    2 => b,
                    3 => (a + b) / 2,
                    4 => Paeth(a, b, c),
                    _ => throw new DecodeException("ChartImageBadPng"),
                };
                rows[row + i] = (byte)(raw[source + i] + predicted);
            }
        }

        byte[] rgb = new byte[width * height * 3];
        int step = depth == 16 ? 2 : 1;
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                int o = (y * width + x) * 3, row = y * stride;
                switch (colorType)
                {
                    case 2:
                    case 6:
                        int p = row + x * channels * step;
                        rgb[o] = rows[p];
                        rgb[o + 1] = rows[p + step];
                        rgb[o + 2] = rows[p + 2 * step];
                        break;
                    case 3:
                        int index = Sample(rows, row, x, depth) * 3;
                        if (index + 2 >= palette!.Length) throw new DecodeException("ChartImageBadPng");
                        rgb[o] = palette[index];
                        rgb[o + 1] = palette[index + 1];
                        rgb[o + 2] = palette[index + 2];
                        break;
                    default:
                        // Grey, with or without alpha: a sample of fewer than 8 bits is spread over the byte.
                        byte grey = depth >= 8
                            ? rows[row + x * channels * step]
                            : (byte)(Sample(rows, row, x, depth) * 255 / ((1 << depth) - 1));
                        rgb[o] = rgb[o + 1] = rgb[o + 2] = grey;
                        break;
                }
            }
        return new Pixels(width, height, rgb);
    }

    // A sample of 1, 2, 4 or 8 bits, packed from the high bits of each byte.
    static int Sample(byte[] rows, int row, int x, int depth)
    {
        if (depth == 8) return rows[row + x];
        int bit = x * depth;
        return (rows[row + bit / 8] >> (8 - depth - bit % 8)) & ((1 << depth) - 1);
    }

    static int Paeth(int a, int b, int c)
    {
        int p = a + b - c, pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c);
        return pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
    }
}
