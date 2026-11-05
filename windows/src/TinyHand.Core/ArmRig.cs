namespace TinyHand.Core;

/// <summary>
/// The pose of the hand on its stick, in screen pixels with y pointing up from the bottom of the screen,
/// the same space the macOS ArmView works in.
/// </summary>
public readonly record struct HandPose(
    double Rotation,
    double RodLength,
    double HandLength,
    double Width,
    double GripX,
    double GripY,
    double AnchorX,
    double Lift,
    double Press,
    double ShadowDx,
    double ShadowDy,
    double ShadowRadius,
    double ShadowOpacity);

public readonly record struct LaserPose(double X, double Y, double Radius, double GlowRadius, double CoreRadius);

/// <summary>
/// The motion of the arm: where the hand grips, how it eases after the cursor and how a tap presses it.
/// A port of ArmView.aim in the macOS app. Easing runs on time, not frames, so it feels the same at any refresh rate.
/// </summary>
public sealed class ArmRig
{
    public const double MinRodFrac = 0.05;
    public const double GripLeanFrac = 0.30;
    public const double GripDepthFrac = 0.12;
    public const double GripFollow = 0.08;
    public const double PressDecay = 0.18;
    public const double LiftFrac = 0.06;
    private const double ReferenceRate = 120;

    public double HandLenFrac { get; set; } = 0.13;
    public bool ShowShadow { get; set; } = true;
    public bool Held { get; set; }
    public double PressAmount { get; private set; }

    private double? gripX;
    private double? gripTarget;
    private double gripY;

    /// <summary>Pixels per macOS point, so fixed sizes in the original stay the same on screen.</summary>
    public double Unit { get; set; } = 1;

    public void Press() => PressAmount = 1;

    public void Reset()
    {
        gripX = null;
        gripTarget = null;
        PressAmount = 0;
        Held = false;
    }

    public bool IsSettling
    {
        get
        {
            if (PressAmount > 0.002)
            {
                return true;
            }
            if (gripX is not double gx || gripTarget is not double target)
            {
                return false;
            }
            return Math.Abs(target - gx) > 0.5 * Unit;
        }
    }

    /// <summary>Moves the grip toward the cursor and lets a released press spring back.</summary>
    public void Step(double seconds, double cursorX, double width, double height)
    {
        double frames = Math.Clamp(seconds, 0, 0.25) * ReferenceRate;
        double targetX = Math.Min(Math.Max(cursorX + GripLeanFrac * width, width * 0.15), width * 1.05);
        double follow = 1 - Math.Pow(1 - GripFollow, frames);
        double gx = gripX is double current ? current + (targetX - current) * follow : targetX;
        gripX = gx;
        gripTarget = targetX;
        gripY = -GripDepthFrac * height;
        if (!Held)
        {
            PressAmount *= Math.Pow(1 - PressDecay, frames);
            if (PressAmount < 1e-4)
            {
                PressAmount = 0;
            }
        }
    }

    public HandPose? AimHand(double cursorX, double cursorY, double height, HandMetrics a)
    {
        if (gripX is not double gx)
        {
            return null;
        }
        double press = PressAmount;
        double dx = cursorX - gx;
        double dy = cursorY - gripY;
        double reach = Math.Sqrt(dx * dx + dy * dy);
        double aimAngle = Math.Atan2(dy, dx);

        double handPts = HandLenFrac * height * (1 - 0.06 * press);
        double s = handPts / (a.Wrist - a.HandTop);
        double widthPts = s * a.Aspect;
        double handTipPts = (a.Wrist - a.Tip) * s;
        double tipOffX = (a.TipX - a.RodX) * widthPts;

        double along = Math.Sqrt(Math.Max(0, reach * reach - tipOffX * tipOffX));
        double rodLen = Math.Max(MinRodFrac * height, along - handTipPts);
        double tipAngle = Math.Atan2(rodLen + handTipPts, tipOffX);
        double rotation = aimAngle - tipAngle;

        double lift = LiftFrac * height * (1 - 0.8 * press);
        return new HandPose(
            Rotation: rotation,
            RodLength: rodLen,
            HandLength: handPts,
            Width: widthPts,
            GripX: gx,
            GripY: gripY,
            AnchorX: a.RodX * widthPts,
            Lift: lift,
            Press: press,
            ShadowDx: lift * 0.6,
            ShadowDy: -lift,
            ShadowRadius: Math.Max(1 * Unit, lift * 0.35) + 5 * Unit * press,
            ShadowOpacity: ShowShadow ? 0.26 + 0.02 * press : 0);
    }

    public LaserPose AimLaser(double cursorX, double cursorY, double height)
    {
        double press = PressAmount;
        double r = Math.Max(4 * Unit, height * 0.0038 * HandLenFrac / 0.13) * (1 + 0.2 * press);
        return new LaserPose(cursorX, cursorY, r, r * (2.2 + 1.0 * press), r * 0.45);
    }

    /// <summary>
    /// The transform that places the stick on a y-down screen. In its local space the stick stands on the
    /// origin and grows upward: the rod covers y from -RodLength to 0 and the hand sits above it.
    /// </summary>
    public static System.Numerics.Matrix3x2 LayerMatrix(HandPose pose, double screenHeight) =>
        System.Numerics.Matrix3x2.CreateTranslation((float)-pose.AnchorX, 0)
        * System.Numerics.Matrix3x2.CreateRotation((float)-pose.Rotation)
        * System.Numerics.Matrix3x2.CreateTranslation((float)pose.GripX, (float)(screenHeight - pose.GripY));

    /// <summary>A coarse key of a pose so frames that would look the same are skipped, as the macOS app does.</summary>
    public static long PoseKey(params double[] values)
    {
        long hash = 17;
        foreach (var v in values)
        {
            hash = hash * 31 + (long)Math.Round(v * 100);
        }
        return hash;
    }
}
