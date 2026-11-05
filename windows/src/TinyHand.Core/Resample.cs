namespace TinyHand.Core;

/// <summary>High quality resizing. A widened triangle filter averages every source pixel when shrinking.</summary>
public static class Resample
{
    public static Pixels Resize(Pixels source, int width, int height)
    {
        width = Math.Max(1, width);
        height = Math.Max(1, height);
        if (width == source.Width && height == source.Height)
        {
            return source;
        }
        int srcW = source.Width;
        int srcH = source.Height;
        byte[] src = source.Data;

        var across = Weights(srcW, width);
        var middle = new float[width * srcH * 4];
        for (int y = 0; y < srcH; y++)
        {
            int rowIn = y * srcW;
            int rowOut = y * width;
            for (int o = 0; o < width; o++)
            {
                var (start, w) = across[o];
                float b = 0, g = 0, r = 0, a = 0;
                for (int k = 0; k < w.Length; k++)
                {
                    int s = (rowIn + start + k) * 4;
                    float wk = w[k];
                    b += src[s] * wk;
                    g += src[s + 1] * wk;
                    r += src[s + 2] * wk;
                    a += src[s + 3] * wk;
                }
                int d = (rowOut + o) * 4;
                middle[d] = b;
                middle[d + 1] = g;
                middle[d + 2] = r;
                middle[d + 3] = a;
            }
        }

        var down = Weights(srcH, height);
        var data = new byte[width * height * 4];
        for (int o = 0; o < height; o++)
        {
            var (start, w) = down[o];
            for (int x = 0; x < width; x++)
            {
                float b = 0, g = 0, r = 0, a = 0;
                for (int k = 0; k < w.Length; k++)
                {
                    int s = ((start + k) * width + x) * 4;
                    float wk = w[k];
                    b += middle[s] * wk;
                    g += middle[s + 1] * wk;
                    r += middle[s + 2] * wk;
                    a += middle[s + 3] * wk;
                }
                int d = (o * width + x) * 4;
                byte alpha = ToByte(a);
                data[d] = Math.Min(ToByte(b), alpha);
                data[d + 1] = Math.Min(ToByte(g), alpha);
                data[d + 2] = Math.Min(ToByte(r), alpha);
                data[d + 3] = alpha;
            }
        }
        return new Pixels(width, height, data);
    }

    public static Pixels ResizeToHeight(Pixels source, int height)
    {
        int width = Math.Max(1, (int)Math.Round(source.Width * (double)height / source.Height));
        return Resize(source, width, height);
    }

    private static byte ToByte(float v) => (byte)Math.Clamp((int)(v + 0.5f), 0, 255);

    private static (int Start, float[] Weights)[] Weights(int srcLen, int dstLen)
    {
        double scale = (double)dstLen / srcLen;
        double support = Math.Max(1.0, 1.0 / scale);
        var result = new (int, float[])[dstLen];
        for (int o = 0; o < dstLen; o++)
        {
            double center = (o + 0.5) / scale - 0.5;
            int start = Math.Max(0, (int)Math.Floor(center - support));
            int end = Math.Min(srcLen - 1, (int)Math.Ceiling(center + support));
            var w = new float[Math.Max(1, end - start + 1)];
            double total = 0;
            for (int i = start; i <= end; i++)
            {
                double v = Math.Max(0, 1 - Math.Abs(i - center) / support);
                w[i - start] = (float)v;
                total += v;
            }
            if (total <= 0)
            {
                result[o] = (Math.Clamp((int)Math.Round(center), 0, srcLen - 1), [1f]);
                continue;
            }
            for (int k = 0; k < w.Length; k++)
            {
                w[k] = (float)(w[k] / total);
            }
            result[o] = (start, w);
        }
        return result;
    }
}
