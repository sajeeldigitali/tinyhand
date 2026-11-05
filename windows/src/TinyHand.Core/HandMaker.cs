namespace TinyHand.Core;

public sealed class HandProblem(string message) : Exception(message);

/// <summary>Checks a picture of a hand and puts it on a stick. A port of HandMaker in the macOS app.</summary>
public static class HandMaker
{
    private const string StylePlaceholder = "[describe your hand, e.g. knitted wool in mustard yellow]";

    public const string Prompt = """
    Make a pointer image for TinyHand, a Windows app that shows a toy hand on screen.

    Draw one cartoon right hand in this style: [describe your hand, e.g. knitted wool in mustard yellow]

    Rules:
    - We see the BACK of the hand. The palm faces away from us. The knuckles and the smooth back of the hand face the viewer.
    - The index finger points straight up. The other three fingers are folded down, so we only see their knuckles, never their fingertips or fingernails.
    - The raised index finger sits at the far left of the knuckle row, right next to the thumb. Three folded knuckles sit to its right. It must never look like a middle finger.
    - The thumb is tucked against the left side of the hand. Nothing crosses the front of the fist: no thumb, no fingertips, no fingernails, no palm.
    - The fingertip is the highest point of the image. Nothing above or beside it.
    - The hand ends at the bottom in a wrist cuff or a clean flat wrist, facing straight down, centred.
    - Upright, not tilted. Whole hand visible, nothing cropped.
    - Transparent background. No shadow, no floor, no outline glow, no text, no stick, no arm.
    - Soft 3D toy look, bright and readable at small sizes.
    - Portrait image, hand filling most of the height with a little empty space around it.
    """;

    public static string PromptFor(string idea)
    {
        var trimmed = idea.Trim();
        var style = trimmed.Length == 0 ? "a soft vinyl toy in a bright colour" : trimmed;
        return Prompt.Replace(StylePlaceholder, style);
    }

    public static (Pixels Image, uint Ink) Make(Pixels source)
    {
        if (source.IsFullyOpaque())
        {
            throw new HandProblem("The image needs a transparent background. Save it as a PNG with transparency.");
        }
        if (source.Width < 64 || source.Height < 64)
        {
            throw new HandProblem("The image is too small. Use one at least 512 pixels tall.");
        }
        Check(source);
        var composed = Compose(source) ?? throw new HandProblem("TinyHand couldn't put that hand on a stick.");
        return (composed, Ink(source));
    }

    public static void Check(Pixels p)
    {
        int w = p.Width;
        int h = p.Height;
        int edge = 0;
        int edgeSolid = 0;
        for (int x = 0; x < w; x++)
        {
            edge += 2;
            edgeSolid += (p.Alpha(x, 0) > 32 ? 1 : 0) + (p.Alpha(x, h - 1) > 32 ? 1 : 0);
        }
        for (int y = 0; y < h; y++)
        {
            edge += 2;
            edgeSolid += (p.Alpha(0, y) > 32 ? 1 : 0) + (p.Alpha(w - 1, y) > 32 ? 1 : 0);
        }
        if ((double)edgeSolid / edge > 0.02)
        {
            throw new HandProblem("The background isn't transparent, or the hand touches the edge of the image.");
        }

        int step = Math.Max(1, Math.Min(w, h) / 256);
        int gw = w / step;
        int gh = h / step;
        var grid = new bool[gw * gh];
        for (int gy = 0; gy < gh; gy++)
        {
            for (int gx = 0; gx < gw; gx++)
            {
                if (p.Alpha(gx * step, gy * step) > 128)
                {
                    grid[gy * gw + gx] = true;
                }
            }
        }
        var seen = new bool[gw * gh];
        var sizes = new List<int>();
        var stack = new Stack<int>();
        for (int start = 0; start < grid.Length; start++)
        {
            if (!grid[start] || seen[start])
            {
                continue;
            }
            stack.Push(start);
            seen[start] = true;
            int n = 0;
            while (stack.Count > 0)
            {
                int i = stack.Pop();
                n++;
                int x = i % gw;
                int y = i / gw;
                Visit(x + 1, y);
                Visit(x - 1, y);
                Visit(x, y + 1);
                Visit(x, y - 1);
            }
            sizes.Add(n);
        }

        void Visit(int nx, int ny)
        {
            if (nx < 0 || ny < 0 || nx >= gw || ny >= gh)
            {
                return;
            }
            int j = ny * gw + nx;
            if (grid[j] && !seen[j])
            {
                seen[j] = true;
                stack.Push(j);
            }
        }

        if (sizes.Count == 0)
        {
            throw new HandProblem("The image is empty.");
        }
        if ((double)sizes.Max() / sizes.Sum() < 0.97)
        {
            throw new HandProblem("The image has more than one thing in it. Use a single hand.");
        }

        var box = Bounds(p) ?? throw new HandProblem("The image is empty.");
        int bw = box.MaxX - box.MinX + 1;
        int bh = box.MaxY - box.MinY + 1;
        double aspect = (double)bh / bw;
        if (aspect < 1.05 || aspect > 2.8)
        {
            throw new HandProblem("The hand should stand upright, taller than it is wide.");
        }
        if ((double)bh / h < 0.5)
        {
            throw new HandProblem("The hand is too small in the image. Crop it closer.");
        }

        int topBand = Math.Max(2, bh * 8 / 100);
        int fingerMin = w;
        int fingerMax = 0;
        for (int y = box.MinY; y < box.MinY + topBand; y++)
        {
            for (int x = box.MinX; x <= box.MaxX; x++)
            {
                if (p.Alpha(x, y) > 128)
                {
                    fingerMin = Math.Min(fingerMin, x);
                    fingerMax = Math.Max(fingerMax, x);
                }
            }
        }
        if ((double)(fingerMax - fingerMin + 1) / bw > 0.4)
        {
            throw new HandProblem("The top of the image should be a single finger pointing up.");
        }

        var baseSpan = BottomSpan(p, box);
        if ((double)baseSpan.Width / bw < 0.2)
        {
            throw new HandProblem("The hand should end in a flat wrist or cuff at the bottom.");
        }

        int soft = 0;
        int any = 0;
        for (int y = 0; y < h; y += 2)
        {
            for (int x = 0; x < w; x += 2)
            {
                int a = p.Alpha(x, y);
                if (a > 20)
                {
                    any++;
                    if (a < 200)
                    {
                        soft++;
                    }
                }
            }
        }
        if ((double)soft / Math.Max(1, any) > 0.08)
        {
            throw new HandProblem("The hand has a soft shadow or glow around it. Ask for no shadow.");
        }
    }

    public readonly record struct Box(int MinX, int MinY, int MaxX, int MaxY);

    public static Box? Bounds(Pixels p)
    {
        int minX = p.Width;
        int minY = p.Height;
        int maxX = -1;
        int maxY = -1;
        for (int y = 0; y < p.Height; y++)
        {
            for (int x = 0; x < p.Width; x++)
            {
                if (p.Alpha(x, y) > 128)
                {
                    minX = Math.Min(minX, x);
                    maxX = Math.Max(maxX, x);
                    minY = Math.Min(minY, y);
                    maxY = Math.Max(maxY, y);
                }
            }
        }
        return maxX < 0 ? null : new Box(minX, minY, maxX, maxY);
    }

    private static (int Width, double Center) BottomSpan(Pixels p, Box box)
    {
        int bh = box.MaxY - box.MinY + 1;
        int lo = p.Width;
        int hi = -1;
        for (int y = Math.Max(box.MinY, box.MaxY - Math.Max(2, bh * 4 / 100)); y <= box.MaxY; y++)
        {
            for (int x = box.MinX; x <= box.MaxX; x++)
            {
                if (p.Alpha(x, y) > 128)
                {
                    lo = Math.Min(lo, x);
                    hi = Math.Max(hi, x);
                }
            }
        }
        return hi < 0 ? (0, (box.MinX + box.MaxX) / 2.0) : (hi - lo + 1, (lo + hi) / 2.0);
    }

    public const int CanvasWidth = 1024;
    public const int CanvasHeight = 1536;

    /// <summary>Scales the hand onto a 1024×1536 canvas and draws a black stick under it, like the built-in hands.</summary>
    public static Pixels? Compose(Pixels p)
    {
        if (Bounds(p) is not Box box)
        {
            return null;
        }
        const double handTop = 150;
        const double handHeight = 560;
        double bw = box.MaxX - box.MinX + 1;
        double bh = box.MaxY - box.MinY + 1;
        double scale = Math.Min(handHeight / bh, 700 / bw);
        double drawW = p.Width * scale;
        double drawH = p.Height * scale;
        double wristX = (BottomSpan(p, box).Center + 0.5) * scale;
        double originX = CanvasWidth / 2.0 - wristX;
        double originTop = handTop - box.MinY * scale;
        double handBottom = handTop + bh * scale;

        var canvas = new Pixels(CanvasWidth, CanvasHeight);
        double rodW = Math.Min(76, Math.Max(54, bw * scale * 0.18));
        double rodTop = handBottom - 30;
        const double rodBottom = 1462;
        DrawStick(canvas, CanvasWidth / 2.0 - rodW / 2, rodTop, rodW, rodBottom - rodTop);

        var scaled = Resample.Resize(p, (int)Math.Round(drawW), (int)Math.Round(drawH));
        canvas.DrawOver(scaled, (int)Math.Round(originX), (int)Math.Round(originTop));
        return canvas;
    }

    private static readonly (double At, uint Color)[] StickStops =
    [
        (0, 0x000000), (0.22, 0x121214), (0.45, 0x3A3A3E), (0.75, 0x121214), (1, 0x000000),
    ];

    private static void DrawStick(Pixels canvas, double left, double top, double width, double height)
    {
        double r = width / 2;
        double cx = left + r;
        double y0 = top + r;
        double y1 = top + height - r;
        int xs = Math.Max(0, (int)Math.Floor(left));
        int xe = Math.Min(canvas.Width - 1, (int)Math.Ceiling(left + width));
        int ys = Math.Max(0, (int)Math.Floor(top));
        int ye = Math.Min(canvas.Height - 1, (int)Math.Ceiling(top + height));
        for (int y = ys; y <= ye; y++)
        {
            double py = y + 0.5;
            double sy = Math.Clamp(py, y0, y1);
            for (int x = xs; x <= xe; x++)
            {
                double px = x + 0.5;
                double d = Math.Sqrt((px - cx) * (px - cx) + (py - sy) * (py - sy));
                double coverage = Math.Clamp(r - d + 0.5, 0, 1);
                if (coverage <= 0)
                {
                    continue;
                }
                uint color = Gradient((px - left) / width);
                int i = (y * canvas.Width + x) * 4;
                byte a = (byte)Math.Round(coverage * 255);
                canvas.Data[i] = (byte)Math.Round((color & 0xFF) * coverage);
                canvas.Data[i + 1] = (byte)Math.Round(((color >> 8) & 0xFF) * coverage);
                canvas.Data[i + 2] = (byte)Math.Round(((color >> 16) & 0xFF) * coverage);
                canvas.Data[i + 3] = a;
            }
        }
    }

    private static uint Gradient(double t)
    {
        t = Math.Clamp(t, 0, 1);
        for (int i = 1; i < StickStops.Length; i++)
        {
            var (at, color) = StickStops[i];
            if (t <= at)
            {
                var (prevAt, prev) = StickStops[i - 1];
                double f = (t - prevAt) / (at - prevAt);
                uint Mix(int shift) => (uint)Math.Round(((prev >> shift) & 0xFF) * (1 - f) + ((color >> shift) & 0xFF) * f);
                return Mix(16) << 16 | Mix(8) << 8 | Mix(0);
            }
        }
        return StickStops[^1].Color;
    }

    /// <summary>Picks an ink colour from the hand: its most common bright, saturated colour.</summary>
    public static uint Ink(Pixels p)
    {
        var best = (Score: -1.0, R: 0.53, G: 0.33, B: 0.95);
        var buckets = new Dictionary<int, (int N, double R, double G, double B)>();
        for (int y = 0; y < p.Height; y += 3)
        {
            for (int x = 0; x < p.Width; x += 3)
            {
                int i = (y * p.Width + x) * 4;
                double a = p.Data[i + 3];
                if (a <= 200)
                {
                    continue;
                }
                double r = p.Data[i + 2] / a;
                double g = p.Data[i + 1] / a;
                double b = p.Data[i] / a;
                int key = (int)(r * 5) * 36 + (int)(g * 5) * 6 + (int)(b * 5);
                buckets.TryGetValue(key, out var e);
                buckets[key] = (e.N + 1, e.R + r, e.G + g, e.B + b);
            }
        }
        foreach (var e in buckets.Values)
        {
            double r = e.R / e.N;
            double g = e.G / e.N;
            double b = e.B / e.N;
            double hi0 = Math.Max(r, Math.Max(g, b));
            double lo0 = Math.Min(r, Math.Min(g, b));
            double sat = hi0 == 0 ? 0 : (hi0 - lo0) / hi0;
            double lum = 0.3 * r + 0.59 * g + 0.11 * b;
            double score = e.N * (0.25 + sat) * (lum > 0.12 && lum < 0.85 ? 1 : 0.15);
            if (score > best.Score)
            {
                best = (score, r, g, b);
            }
        }
        double hi = Math.Max(best.R, Math.Max(best.G, best.B));
        double lo = Math.Min(best.R, Math.Min(best.G, best.B));
        double rr = best.R;
        double gg = best.G;
        double bb = best.B;
        if (hi - lo < 0.08)
        {
            rr = 0.42;
            gg = 0.45;
            bb = 0.5;
        }
        else if (hi > 0.82)
        {
            rr *= 0.82 / hi;
            gg *= 0.82 / hi;
            bb *= 0.82 / hi;
        }
        uint C(double v) => (uint)Math.Min(255, v * 255);
        return C(rr) << 16 | C(gg) << 8 | C(bb);
    }
}
