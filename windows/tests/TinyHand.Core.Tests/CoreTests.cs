using StbImageSharp;
using TinyHand.Core;

namespace TinyHand.Core.Tests;

public class Assets
{
    public static string Root
    {
        get
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "assets", "hand_lavender.png")))
            {
                dir = dir.Parent;
            }
            return Path.Combine(dir?.FullName ?? throw new DirectoryNotFoundException("assets"), "assets");
        }
    }

    public static Pixels Load(string path)
    {
        using var stream = File.OpenRead(path);
        var image = ImageResult.FromStream(stream, ColorComponents.RedGreenBlueAlpha);
        return Pixels.FromStraightRgba(image.Width, image.Height, image.Data);
    }

    /// <summary>A plain hand shape: a fist with one finger up, on a transparent canvas.</summary>
    public static Pixels SyntheticHand(uint color = 0xE07040, int width = 600, int height = 900)
    {
        var p = new Pixels(width, height);
        void Fill(int x0, int y0, int x1, int y1)
        {
            for (int y = y0; y < y1; y++)
            {
                for (int x = x0; x < x1; x++)
                {
                    int i = (y * width + x) * 4;
                    p.Data[i] = (byte)(color & 0xFF);
                    p.Data[i + 1] = (byte)((color >> 8) & 0xFF);
                    p.Data[i + 2] = (byte)((color >> 16) & 0xFF);
                    p.Data[i + 3] = 255;
                }
            }
        }
        Fill(150, 400, 450, 800);
        Fill(170, 100, 240, 400);
        return p;
    }
}

public class HandMetricsTests
{
    public static IEnumerable<object[]> BuiltInHands() =>
        Pointers.BuiltInHands.Select(s => new object[] { s.Id });

    [Theory]
    [MemberData(nameof(BuiltInHands))]
    public void EveryBuiltInHandMeasures(string id)
    {
        var path = Path.Combine(Assets.Root, $"hand_{id}.png");
        Assert.True(File.Exists(path), path);
        var image = Assets.Load(path);
        var m = HandMetrics.Measure(image);
        Assert.NotNull(m);
        Assert.InRange(m!.Tip, 0.0, 0.3);
        Assert.True(m.HandTop <= m.Tip);
        Assert.True(m.Tip < m.Wrist, "tip above wrist");
        Assert.True(m.Wrist < m.RodEnd, "rod below wrist");
        Assert.InRange(m.RodX, 0.3, 0.7);
        Assert.InRange(m.TipX, 0.05, 0.95);
        Assert.True(m.HandMinX < m.HandMaxX);
        Assert.Equal(1024.0 / 1536.0, m.Aspect, 3);
    }

    [Fact]
    public void ThumbnailCropCoversTheHand()
    {
        var image = Assets.Load(Path.Combine(Assets.Root, "hand_lavender.png"));
        var m = HandMetrics.Measure(image)!;
        var (x, y, size) = m.ThumbnailCrop(image.Width, image.Height);
        Assert.True(size > 100);
        Assert.True(y < m.Tip * image.Height);
        Assert.True(y + size > m.Wrist * image.Height - 2);
        Assert.True(x < m.HandMinX * image.Width + 1);
    }
}

public class ArmRigTests
{
    private static HandMetrics Lavender() =>
        HandMetrics.Measure(Assets.Load(Path.Combine(Assets.Root, "hand_lavender.png")))!;

    [Theory]
    [InlineData(960, 540)]
    [InlineData(200, 900)]
    [InlineData(1700, 300)]
    [InlineData(960, 1000)]
    public void FingertipLandsOnTheCursor(double cx, double cyUp)
    {
        const double w = 1920, h = 1080;
        var m = Lavender();
        var rig = new ArmRig();
        rig.Step(0, cx, w, h);
        var pose = rig.AimHand(cx, cyUp, h, m)!.Value;

        double s = pose.HandLength / (m.Wrist - m.HandTop);
        double handTip = (m.Wrist - m.Tip) * s;
        var local = new System.Numerics.Vector2((float)(m.TipX * pose.Width), (float)-(pose.RodLength + handTip));
        var screen = System.Numerics.Vector2.Transform(local, ArmRig.LayerMatrix(pose, h));
        Assert.Equal(cx, screen.X, 0.5);
        Assert.Equal(h - cyUp, screen.Y, 0.5);
    }

    [Fact]
    public void PressSpringsBackOverTimeNotFrames()
    {
        var a = new ArmRig();
        var b = new ArmRig();
        a.Press();
        b.Press();
        a.Step(0.1, 500, 1000, 800);
        for (int i = 0; i < 6; i++)
        {
            b.Step(1 / 60.0, 500, 1000, 800);
        }
        Assert.Equal(a.PressAmount, b.PressAmount, 6);
        Assert.True(a.PressAmount < 0.2);
    }

    [Fact]
    public void HeldPressDoesNotRelease()
    {
        var rig = new ArmRig();
        rig.Press();
        rig.Held = true;
        rig.Step(0.2, 100, 1000, 800);
        Assert.Equal(1, rig.PressAmount);
        Assert.True(rig.IsSettling);
    }

    [Fact]
    public void LaserGrowsWhenPressed()
    {
        var rig = new ArmRig();
        var calm = rig.AimLaser(10, 10, 1080);
        rig.Press();
        var pressed = rig.AimLaser(10, 10, 1080);
        Assert.True(pressed.Radius > calm.Radius);
        Assert.Equal(calm.Radius * 0.45, calm.CoreRadius, 6);
    }
}

public class HandMakerTests
{
    [Fact]
    public void AcceptsAPlainHandAndPutsItOnAStick()
    {
        var (image, ink) = HandMaker.Make(Assets.SyntheticHand());
        Assert.Equal(HandMaker.CanvasWidth, image.Width);
        Assert.Equal(HandMaker.CanvasHeight, image.Height);
        var m = HandMetrics.Measure(image);
        Assert.NotNull(m);
        Assert.InRange(m!.RodX, 0.45, 0.55);
        Assert.InRange(m.Tip * image.Height, 140, 160);
        Assert.True(m.RodEnd * image.Height > 1400);
        Assert.Equal(0xD0u, ink >> 16 & 0xF0);
    }

    [Fact]
    public void RejectsAnOpaqueImage()
    {
        var p = new Pixels(200, 300);
        Array.Fill(p.Data, (byte)255);
        var problem = Assert.Throws<HandProblem>(() => HandMaker.Make(p));
        Assert.Contains("transparent background", problem.Message);
    }

    [Fact]
    public void RejectsTwoThings()
    {
        var p = Assets.SyntheticHand();
        for (int y = 820; y < 880; y++)
        {
            for (int x = 480; x < 560; x++)
            {
                p.Data[(y * p.Width + x) * 4 + 3] = 255;
            }
        }
        var problem = Assert.Throws<HandProblem>(() => HandMaker.Make(p));
        Assert.Contains("more than one thing", problem.Message);
    }

    [Fact]
    public void RejectsABuiltInHandOnItsStick()
    {
        var p = Assets.Load(Path.Combine(Assets.Root, "hand_lavender.png"));
        Assert.Throws<HandProblem>(() => HandMaker.Make(p));
    }

    [Fact]
    public void PromptFillsInTheIdea()
    {
        Assert.Contains("in this style: knitted wool", HandMaker.PromptFor("knitted wool"));
        Assert.Contains("a soft vinyl toy", HandMaker.PromptFor("  "));
    }
}

public class ResampleTests
{
    [Fact]
    public void ShrinkingKeepsTheAverageColour()
    {
        var p = new Pixels(64, 64);
        for (int i = 0; i < 64 * 64; i++)
        {
            bool on = (i % 2) == 0;
            p.Data[i * 4 + 2] = (byte)(on ? 255 : 0);
            p.Data[i * 4 + 3] = 255;
        }
        var small = Resample.Resize(p, 8, 8);
        for (int i = 0; i < 64; i++)
        {
            Assert.InRange(small.Data[i * 4 + 2], (byte)110, (byte)145);
            Assert.Equal(255, small.Data[i * 4 + 3]);
        }
    }

    [Fact]
    public void ColourNeverExceedsAlpha()
    {
        var hand = Assets.Load(Path.Combine(Assets.Root, "hand_neon.png"));
        var small = Resample.ResizeToHeight(hand, 300);
        Assert.Equal(300, small.Height);
        for (int i = 0; i < small.Data.Length; i += 4)
        {
            Assert.True(small.Data[i] <= small.Data[i + 3] && small.Data[i + 2] <= small.Data[i + 3]);
        }
    }
}

public class InkTests
{
    [Fact]
    public void SmoothsThroughMidpoints()
    {
        var s = new InkStroke(new PathPoint(0, 0), 0xFF0000, 4, 1.5);
        Assert.False(s.Extend(new PathPoint(1, 0)));
        Assert.True(s.Extend(new PathPoint(10, 0)));
        Assert.True(s.Extend(new PathPoint(10, 10)));
        var segs = s.Segments();
        Assert.Equal(3, segs.Count);
        Assert.Equal(new PathPoint(5, 0), segs[0].To);
        Assert.Equal(new PathPoint(0, 0), segs[0].Control);
        Assert.Equal(new PathPoint(10, 10), segs[^1].To);
        Assert.Null(segs[^1].Control);
    }

    [Fact]
    public void FadesAfterTheDelay()
    {
        var s = new InkStroke(new PathPoint(0, 0), 0, 4, 1.5);
        s.End(10);
        Assert.Equal(1, s.Opacity(12.9, 3, 0.6, keep: false));
        Assert.InRange(s.Opacity(13.3, 3, 0.6, keep: false), 0.01, 0.99);
        Assert.Equal(0, s.Opacity(13.7, 3, 0.6, keep: false));
        Assert.Equal(1, s.Opacity(99, 3, 0.6, keep: true));
    }

    [Fact]
    public void EaseInStartsSlow()
    {
        Assert.True(Easing.EaseIn(0.25) < 0.25);
        Assert.Equal(1, Easing.EaseIn(1));
        Assert.True(Easing.EaseIn(0.5) < Easing.EaseIn(0.6));
    }
}

public class PointerTests
{
    [Fact]
    public void ThirtySixHandsPlusTheLaser()
    {
        Assert.Equal(36, Pointers.BuiltInHands.Count);
        Assert.Equal(Pointers.Laser, Pointers.AllCases[^1]);
    }

    [Fact]
    public void ReadsOldNames()
    {
        Assert.Equal(Pointers.Laser, Pointers.Parse("dot"));
        Assert.Equal(Pointers.Coral, Pointers.Parse("sunny"));
        Assert.Null(Pointers.Parse("nope"));
    }

    [Fact]
    public void PinnedDropsUnknownsAndRepeats()
    {
        var pinned = Pointers.NormalizePinned(["coral", "nope", "coral", "laser"]);
        Assert.Equal([Pointers.Coral, Pointers.Laser], pinned);
        Assert.Equal(Pointers.DefaultPinned, Pointers.NormalizePinned([]));
    }

    [Fact]
    public void CustomHandsRoundTrip()
    {
        var dir = Path.Combine(Path.GetTempPath(), "th-hands-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new CustomHandStore(dir);
            Pointers.Custom = store;
            var style = store.Add([1, 2, 3], 0x123456, "  Woolly mitten hand  ");
            Assert.True(style.IsCustom);
            Assert.Equal("Woolly mitten ha", Pointers.Title(style));
            Assert.Equal(0x123456u, Pointers.InkColor(style));
            Assert.Contains(style, Pointers.AllCases);
            Assert.Equal(style.Id, Pointers.AssetName(style));
            var reread = new CustomHandStore(dir);
            Assert.Single(reread.All);
            store.Remove(style);
            Assert.Empty(new CustomHandStore(dir).All);
            Assert.Equal("Knit glove", store.SuggestedName("knit_glove-final.png"));
            Assert.Equal("My hand 1", store.SuggestedName("ChatGPT Image Oct 3.png"));
        }
        finally
        {
            Pointers.Custom = null;
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, true);
            }
        }
    }

    [Fact]
    public void InkChoicesResolve()
    {
        Assert.Equal(0xFF8A00u, InkChoice.Orange.Resolved(Pointers.Lavender));
        Assert.Equal(InkColors.Purple, InkChoice.Match.Resolved(Pointers.Lavender));
        Assert.Equal(InkColors.Red, InkChoice.Match.Resolved(Pointers.Laser));
        Assert.Equal("tap_thock.wav", TapSoundChoice.Thock.FileName());
        Assert.Equal(TapSoundChoice.Wood, Choices.ParseEnum<TapSoundChoice>("wood"));
    }
}
