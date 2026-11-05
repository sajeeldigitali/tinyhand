namespace TinyHand.Core;

/// <summary>
/// Where the parts of a hand-on-a-stick image are, as fractions of its size measured from the top.
/// A port of HandAsset.measure in the macOS app.
/// </summary>
public sealed record HandMetrics(
    double Aspect,
    double HandTop,
    double Tip,
    double Wrist,
    double RodEnd,
    double TipX,
    double RodX,
    double HandMinX,
    double HandMaxX)
{
    public static HandMetrics? Measure(Pixels image) => Measure(image.Width, image.Height, image.AlphaPlane());

    public static HandMetrics? Measure(int w, int h, byte[] alpha)
    {
        int Count(int y)
        {
            int n = 0;
            int row = y * w;
            for (int x = 0; x < w; x++)
            {
                if (alpha[row + x] > 128)
                {
                    n++;
                }
            }
            return n;
        }

        double Center(int y)
        {
            int row = y * w;
            int lo = -1;
            int hi = -1;
            for (int x = 0; x < w; x++)
            {
                if (alpha[row + x] > 128)
                {
                    if (lo < 0)
                    {
                        lo = x;
                    }
                    hi = x;
                }
            }
            return lo < 0 ? 0.5 : (lo + hi) / 2.0 / w;
        }

        var counts = new int[h];
        for (int y = 0; y < h; y++)
        {
            counts[y] = Count(y);
        }
        int top = Array.FindIndex(counts, c => c > 0);
        if (top < 0)
        {
            return null;
        }
        int searchEnd = Math.Min(h, top + (int)(0.45 * h));
        int maxW = 0;
        for (int y = top; y < searchEnd; y++)
        {
            maxW = Math.Max(maxW, counts[y]);
        }
        if (maxW <= 0)
        {
            return null;
        }
        int fist = -1;
        for (int y = top; y < h; y++)
        {
            if (counts[y] > 0.6 * maxW)
            {
                fist = y;
                break;
            }
        }
        if (fist < 0)
        {
            return null;
        }
        int wrist = -1;
        for (int y = fist; y < h; y++)
        {
            if (counts[y] < 0.35 * maxW)
            {
                wrist = y;
                break;
            }
        }
        if (wrist < 0)
        {
            return null;
        }
        int rodW = counts[Math.Min(h - 1, wrist + h / 50)];
        int lastRow = Array.FindLastIndex(counts, c => c > 0);
        if (lastRow < 0)
        {
            lastRow = h - 1;
        }
        int from = Math.Min(h - 1, wrist + h / 33);
        int handle = -1;
        for (int y = from; y < h; y++)
        {
            if (counts[y] > 1.8 * rodW)
            {
                handle = y;
                break;
            }
        }
        int rodEnd = handle >= 0 ? handle : lastRow;

        int minX = w;
        int maxX = 0;
        for (int y = top; y < wrist; y++)
        {
            int row = y * w;
            for (int x = 0; x < w; x++)
            {
                if (alpha[row + x] > 128)
                {
                    minX = Math.Min(minX, x);
                    maxX = Math.Max(maxX, x);
                }
            }
        }

        double fh = h;
        return new HandMetrics(
            Aspect: w / fh,
            HandTop: Math.Max(0, top - 0.004 * fh) / fh,
            Tip: top / fh,
            Wrist: wrist / fh,
            RodEnd: rodEnd / fh,
            TipX: Center(Math.Min(h - 1, top + h / 200)),
            RodX: Center((wrist + rodEnd) / 2),
            HandMinX: minX / (double)w,
            HandMaxX: maxX / (double)w);
    }

    /// <summary>The square around the hand, in pixels, used for toolbar and settings thumbnails.</summary>
    public (int X, int Y, int Size) ThumbnailCrop(int width, int height)
    {
        double handW = (HandMaxX - HandMinX) * width;
        double handH = (Wrist - HandTop) * height;
        double side = Math.Max(handW, handH) * 1.06;
        double cx = (HandMinX + HandMaxX) / 2 * width;
        double cy = (HandTop + Wrist) / 2 * height;
        return ((int)Math.Round(cx - side / 2), (int)Math.Round(cy - side / 2), (int)Math.Round(side));
    }
}
