using System.Buffers.Binary;
using System.IO.Compression;

namespace ScoreStudio.Recognition;

/// <summary>
/// Reads non-interlaced PNG files of any color type (bit depths 1–16) as grayscale, transparency over white, and writes
/// 8-bit grayscale PNG files. Enough for preparing pictures for music recognition without platform libraries.
/// </summary>
public sealed class PngCodec : IImageReader
{
    private static readonly byte[] Signature = { 137, 80, 78, 71, 13, 10, 26, 10 };

    public GrayImage Read(string path)
    {
        using var stream = File.OpenRead(path);
        return Decode(stream);
    }

    public static GrayImage Decode(Stream stream)
    {
        var signature = new byte[8];
        if (stream.Read(signature, 0, 8) != 8 || !signature.SequenceEqual(Signature)) throw new InvalidDataException("PNG 파일이 아닙니다.");

        int width = 0, height = 0, depth = 0, colorType = 0;
        byte[]? palette = null, paletteAlpha = null;
        using var data = new MemoryStream();
        var header = new byte[8];
        while (true)
        {
            if (stream.Read(header, 0, 8) != 8) throw new InvalidDataException("PNG 파일이 중간에 끝났습니다.");
            var length = BinaryPrimitives.ReadInt32BigEndian(header);
            var type = System.Text.Encoding.ASCII.GetString(header, 4, 4);
            if (length < 0 || length > 64 * 1024 * 1024) throw new InvalidDataException("PNG 조각 크기가 올바르지 않습니다.");
            var body = new byte[length];
            if (stream.ReadAtLeast(body, length, throwOnEndOfStream: false) != length) throw new InvalidDataException("PNG 파일이 중간에 끝났습니다.");
            stream.Seek(4, SeekOrigin.Current); // CRC

            switch (type)
            {
                case "IHDR":
                    width = BinaryPrimitives.ReadInt32BigEndian(body);
                    height = BinaryPrimitives.ReadInt32BigEndian(body.AsSpan(4));
                    (depth, colorType) = (body[8], body[9]);
                    if (body[12] != 0) throw new NotSupportedException("인터레이스 PNG는 지원하지 않습니다. 다른 형식으로 저장해 주세요.");
                    if (width <= 0 || height <= 0 || (long)width * height > 200_000_000) throw new InvalidDataException("PNG 그림 크기가 올바르지 않습니다.");
                    break;
                case "PLTE":
                    palette = body;
                    break;
                case "tRNS":
                    paletteAlpha = body;
                    break;
                case "IDAT":
                    data.Write(body);
                    break;
                case "IEND":
                    return Unpack(Inflate(data.ToArray()), width, height, depth, colorType, palette, paletteAlpha);
            }
        }
    }

    private static byte[] Inflate(byte[] compressed)
    {
        using var input = new ZLibStream(new MemoryStream(compressed), CompressionMode.Decompress);
        using var output = new MemoryStream();
        input.CopyTo(output);
        return output.ToArray();
    }

    private static GrayImage Unpack(byte[] raw, int width, int height, int depth, int colorType, byte[]? palette, byte[]? transparency)
    {
        var channels = colorType switch { 0 => 1, 2 => 3, 3 => 1, 4 => 2, 6 => 4, _ => throw new InvalidDataException("알 수 없는 PNG 색 형식입니다.") };
        if (depth is not (1 or 2 or 4 or 8 or 16)) throw new InvalidDataException("PNG 비트 깊이가 올바르지 않습니다.");
        var bitsPerPixel = channels * depth;
        var stride = (width * bitsPerPixel + 7) / 8;
        var bytesPerPixel = Math.Max(1, bitsPerPixel / 8);
        if (raw.Length < (long)(stride + 1) * height) throw new InvalidDataException("PNG 그림 자료가 모자랍니다.");

        var image = new GrayImage(width, height);
        var previous = new byte[stride];
        var line = new byte[stride];
        var max = (1 << Math.Min(depth, 8)) - 1;
        for (var y = 0; y < height; y++)
        {
            var filter = raw[y * (stride + 1)];
            Array.Copy(raw, y * (stride + 1) + 1, line, 0, stride);
            Unfilter(filter, line, previous, bytesPerPixel);

            for (var x = 0; x < width; x++)
            {
                int Sample(int channel)
                {
                    if (depth == 16) return line[(x * channels + channel) * 2];
                    if (depth == 8) return line[x * channels + channel];
                    var bit = (x * channels + channel) * depth;
                    return (line[bit / 8] >> (8 - depth - bit % 8)) & max;
                }

                double gray, alpha = 1;
                switch (colorType)
                {
                    case 0:
                        gray = Sample(0) * 255.0 / max;
                        if (transparency is { Length: >= 2 } && Sample(0) == (depth == 16 ? transparency[0] : transparency[1])) alpha = 0;
                        break;
                    case 2:
                        gray = Luma(Sample(0), Sample(1), Sample(2));
                        break;
                    case 3:
                        var index = Sample(0);
                        if (palette is null || index * 3 + 2 >= palette.Length) throw new InvalidDataException("PNG 팔레트가 올바르지 않습니다.");
                        gray = Luma(palette[index * 3], palette[index * 3 + 1], palette[index * 3 + 2]);
                        if (transparency is not null && index < transparency.Length) alpha = transparency[index] / 255.0;
                        break;
                    case 4:
                        gray = Sample(0) * 255.0 / max;
                        alpha = Sample(1) / (double)max;
                        break;
                    default:
                        gray = Luma(Sample(0), Sample(1), Sample(2));
                        alpha = Sample(3) / (double)max;
                        break;
                }

                image[x, y] = (byte)Math.Clamp(Math.Round(gray * alpha + 255 * (1 - alpha)), 0, 255);
            }

            (previous, line) = (line, previous);
        }

        return image;
    }

    private static double Luma(int r, int g, int b) => 0.299 * r + 0.587 * g + 0.114 * b;

    private static void Unfilter(byte filter, byte[] line, byte[] previous, int bpp)
    {
        for (var i = 0; i < line.Length; i++)
        {
            int left = i >= bpp ? line[i - bpp] : 0, up = previous[i], upLeft = i >= bpp ? previous[i - bpp] : 0;
            line[i] = (byte)(line[i] + filter switch
            {
                0 => 0,
                1 => left,
                2 => up,
                3 => (left + up) / 2,
                4 => Paeth(left, up, upLeft),
                _ => throw new InvalidDataException("PNG 필터 종류가 올바르지 않습니다."),
            });
        }
    }

    private static int Paeth(int a, int b, int c)
    {
        var p = a + b - c;
        var (pa, pb, pc) = (Math.Abs(p - a), Math.Abs(p - b), Math.Abs(p - c));
        return pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
    }

    /// <summary>Writes an 8-bit grayscale PNG.</summary>
    public static void Write(GrayImage image, string path)
    {
        using var file = File.Create(path);
        Encode(image, file);
    }

    public static void Encode(GrayImage image, Stream output)
    {
        output.Write(Signature);
        var header = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(header, image.Width);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), image.Height);
        header[8] = 8; // bit depth; color type 0 (gray), no interlace
        WriteChunk(output, "IHDR", header);

        using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionLevel.Fastest, leaveOpen: true))
        {
            var row = new byte[image.Width + 1];
            for (var y = 0; y < image.Height; y++)
            {
                // Filter "up": runs of white compress well.
                row[0] = 2;
                for (var x = 0; x < image.Width; x++)
                    row[x + 1] = (byte)(image[x, y] - (y > 0 ? image[x, y - 1] : 0));
                zlib.Write(row);
            }
        }

        WriteChunk(output, "IDAT", compressed.ToArray());
        WriteChunk(output, "IEND", Array.Empty<byte>());
    }

    private static void WriteChunk(Stream output, string type, byte[] body)
    {
        var length = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(length, body.Length);
        output.Write(length);
        var typeBytes = System.Text.Encoding.ASCII.GetBytes(type);
        output.Write(typeBytes);
        output.Write(body);
        var crc = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(crc, Crc32(typeBytes, body));
        output.Write(crc);
    }

    private static readonly uint[] CrcTable = Enumerable.Range(0, 256).Select(n =>
    {
        var c = (uint)n;
        for (var k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
        return c;
    }).ToArray();

    private static uint Crc32(byte[] type, byte[] body)
    {
        var c = 0xFFFFFFFFu;
        foreach (var b in type.Concat(body)) c = CrcTable[(c ^ b) & 0xFF] ^ (c >> 8);
        return c ^ 0xFFFFFFFFu;
    }
}
