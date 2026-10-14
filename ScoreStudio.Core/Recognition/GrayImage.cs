namespace ScoreStudio.Recognition;

/// <summary>An 8-bit grayscale picture, rows from the top, 0 black and 255 white.</summary>
public sealed class GrayImage
{
    public GrayImage(int width, int height, byte[]? pixels = null)
    {
        if (width <= 0 || height <= 0) throw new ArgumentOutOfRangeException(nameof(width), "그림 크기가 올바르지 않습니다.");
        if (pixels is not null && pixels.Length != (long)width * height) throw new ArgumentException("그림 크기와 화소 수가 맞지 않습니다.", nameof(pixels));
        Width = width;
        Height = height;
        Pixels = pixels ?? Enumerable.Repeat((byte)255, width * height).ToArray();
    }

    public int Width { get; }
    public int Height { get; }
    public byte[] Pixels { get; }

    public byte this[int x, int y]
    {
        get => Pixels[y * Width + x];
        set => Pixels[y * Width + x] = value;
    }
}

/// <summary>Reads a picture file as grayscale (the window uses Windows' decoders; tests use <see cref="PngCodec"/>).</summary>
public interface IImageReader
{
    GrayImage Read(string path);
}
