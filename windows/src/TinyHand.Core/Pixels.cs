namespace TinyHand.Core;

/// <summary>An image as premultiplied BGRA bytes, 8 bits per channel, rows from top to bottom.</summary>
public sealed class Pixels
{
    public int Width { get; }
    public int Height { get; }
    public byte[] Data { get; }
    public int Stride => Width * 4;

    public Pixels(int width, int height, byte[] data)
    {
        if (width <= 0 || height <= 0 || data.Length < width * height * 4)
        {
            throw new ArgumentException("Pixel buffer does not match the size.");
        }
        Width = width;
        Height = height;
        Data = data;
    }

    public Pixels(int width, int height) : this(width, height, new byte[width * height * 4])
    {
    }

    public byte Alpha(int x, int y) => Data[(y * Width + x) * 4 + 3];

    public byte[] AlphaPlane()
    {
        var alpha = new byte[Width * Height];
        for (int i = 0; i < alpha.Length; i++)
        {
            alpha[i] = Data[i * 4 + 3];
        }
        return alpha;
    }

    public bool IsFullyOpaque()
    {
        for (int i = 3; i < Data.Length; i += 4)
        {
            if (Data[i] != 255)
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>Builds premultiplied BGRA from straight RGBA, the layout most decoders hand back.</summary>
    public static Pixels FromStraightRgba(int width, int height, byte[] rgba)
    {
        var data = new byte[width * height * 4];
        for (int i = 0; i < width * height; i++)
        {
            int a = rgba[i * 4 + 3];
            data[i * 4 + 0] = (byte)((rgba[i * 4 + 2] * a + 127) / 255);
            data[i * 4 + 1] = (byte)((rgba[i * 4 + 1] * a + 127) / 255);
            data[i * 4 + 2] = (byte)((rgba[i * 4 + 0] * a + 127) / 255);
            data[i * 4 + 3] = (byte)a;
        }
        return new Pixels(width, height, data);
    }

    public Pixels Crop(int x, int y, int width, int height)
    {
        int x0 = Math.Clamp(x, 0, Width);
        int y0 = Math.Clamp(y, 0, Height);
        int x1 = Math.Clamp(x + width, 0, Width);
        int y1 = Math.Clamp(y + height, 0, Height);
        if (x1 <= x0 || y1 <= y0)
        {
            return this;
        }
        var result = new Pixels(x1 - x0, y1 - y0);
        for (int row = y0; row < y1; row++)
        {
            Buffer.BlockCopy(Data, (row * Width + x0) * 4, result.Data, (row - y0) * result.Stride, result.Stride);
        }
        return result;
    }

    /// <summary>Draws <paramref name="source"/> over this image with its top-left corner at (x, y).</summary>
    public void DrawOver(Pixels source, int x, int y)
    {
        for (int sy = 0; sy < source.Height; sy++)
        {
            int dy = y + sy;
            if (dy < 0 || dy >= Height)
            {
                continue;
            }
            for (int sx = 0; sx < source.Width; sx++)
            {
                int dx = x + sx;
                if (dx < 0 || dx >= Width)
                {
                    continue;
                }
                int s = (sy * source.Width + sx) * 4;
                int sa = source.Data[s + 3];
                if (sa == 0)
                {
                    continue;
                }
                int d = (dy * Width + dx) * 4;
                int inv = 255 - sa;
                for (int c = 0; c < 4; c++)
                {
                    Data[d + c] = (byte)Math.Min(255, source.Data[s + c] + (Data[d + c] * inv + 127) / 255);
                }
            }
        }
    }
}
