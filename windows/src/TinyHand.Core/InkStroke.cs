namespace TinyHand.Core;

public readonly record struct PathPoint(double X, double Y);

/// <summary>A path command: a straight line to <see cref="To"/>, or a quadratic curve through <see cref="Control"/>.</summary>
public readonly record struct PathSegment(PathPoint To, PathPoint? Control);

/// <summary>One drawn line. Points are smoothed into quadratic curves through their midpoints, as in the macOS Ink.</summary>
public sealed class InkStroke
{
    private readonly List<PathPoint> points = [];

    public uint Color { get; }
    public double Width { get; }
    public double MinStep { get; }
    public double? EndedAt { get; private set; }
    public int Version { get; private set; }

    public InkStroke(PathPoint start, uint color, double width, double minStep)
    {
        Color = color;
        Width = width;
        MinStep = minStep;
        points.Add(start);
    }

    public IReadOnlyList<PathPoint> Points => points;

    public bool Extend(PathPoint p)
    {
        if (EndedAt is not null)
        {
            return false;
        }
        var last = points[^1];
        if (Math.Sqrt((p.X - last.X) * (p.X - last.X) + (p.Y - last.Y) * (p.Y - last.Y)) < MinStep)
        {
            return false;
        }
        points.Add(p);
        Version++;
        return true;
    }

    public void End(double now) => EndedAt = now;

    public PathPoint Start => points[0];

    public List<PathSegment> Segments()
    {
        var result = new List<PathSegment>();
        var first = points[0];
        if (points.Count == 1)
        {
            result.Add(new PathSegment(first, null));
        }
        for (int i = 1; i < points.Count; i++)
        {
            var p = points[i];
            var prev = points[i - 1];
            var mid = new PathPoint((prev.X + p.X) / 2, (prev.Y + p.Y) / 2);
            result.Add(new PathSegment(mid, prev));
        }
        if (points.Count > 1)
        {
            result.Add(new PathSegment(points[^1], null));
        }
        return result;
    }

    /// <summary>Opacity of a finished line: holds for <paramref name="delay"/>, then eases out over <paramref name="duration"/>.</summary>
    public double Opacity(double now, double delay, double duration, bool keep)
    {
        if (keep || EndedAt is not double ended)
        {
            return 1;
        }
        double t = (now - ended - delay) / duration;
        if (t <= 0)
        {
            return 1;
        }
        if (t >= 1)
        {
            return 0;
        }
        return 1 - Easing.EaseIn(t);
    }
}

public static class Easing
{
    /// <summary>The cubic Bézier (0.42, 0, 1, 1) behind kCAMediaTimingFunctionEaseIn.</summary>
    public static double EaseIn(double x) => CubicBezier(0.42, 0, 1, 1, x);

    public static double EaseOut(double x) => CubicBezier(0, 0, 0.58, 1, x);

    public static double CubicBezier(double x1, double y1, double x2, double y2, double x)
    {
        if (x <= 0)
        {
            return 0;
        }
        if (x >= 1)
        {
            return 1;
        }
        double lo = 0;
        double hi = 1;
        double t = x;
        for (int i = 0; i < 30; i++)
        {
            t = (lo + hi) / 2;
            double bx = Bezier(x1, x2, t);
            if (Math.Abs(bx - x) < 1e-5)
            {
                break;
            }
            if (bx < x)
            {
                lo = t;
            }
            else
            {
                hi = t;
            }
        }
        return Bezier(y1, y2, t);
    }

    private static double Bezier(double p1, double p2, double t)
    {
        double u = 1 - t;
        return 3 * u * u * t * p1 + 3 * u * t * t * p2 + t * t * t;
    }
}
