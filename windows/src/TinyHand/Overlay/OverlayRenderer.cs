using System.Numerics;
using TinyHand.Core;
using Vortice.Direct2D1;
using Vortice.Direct2D1.Effects;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DirectComposition;
using Vortice.DXGI;
using Vortice.Mathematics;
using D2DAlphaMode = Vortice.DCommon.AlphaMode;
using FeatureLevel = Vortice.Direct3D.FeatureLevel;
using InterpolationMode = Vortice.Direct2D1.InterpolationMode;
using D2DPixelFormat = Vortice.DCommon.PixelFormat;

namespace TinyHand.Overlay;

internal readonly record struct InkDraw(ID2D1PathGeometry Geometry, uint Color, float Width, float Opacity);

/// <summary>Everything one frame of the overlay shows, in pixels of the display it covers.</summary>
internal sealed class Frame
{
    public float Height;
    public float Unit = 1;
    public HandPose? Hand;
    public HandMetrics? Metrics;
    public LaserPose? Laser;
    public readonly List<InkDraw> Ink = [];
}

/// <summary>
/// Draws the hand, its stick and shadow, the laser dot and the ink with Direct2D into a swap chain that
/// DirectComposition shows in the overlay window. It plays the part of the Core Animation layers in ArmView.
/// </summary>
internal sealed class OverlayRenderer : IDisposable
{
    private static ID2D1Factory1? sharedFactory;

    public static ID2D1Factory1 Factory => sharedFactory ??= D2D1.D2D1CreateFactory<ID2D1Factory1>(FactoryType.SingleThreaded);

    private readonly ID3D11Device device;
    private readonly IDXGIDevice1 dxgi;
    private readonly ID2D1Device d2d;
    private readonly ID2D1DeviceContext dc;
    private readonly IntPtr hwnd;
    private IDXGISwapChain1? swap;
    private IDCompositionDevice? dcomp;
    private IDCompositionTarget? target;
    private IDCompositionVisual? visual;
    private ID2D1Bitmap1? targetBitmap;
    private readonly Shadow rodShadow;
    private readonly Shadow handShadow;
    private readonly Shadow glow;
    private readonly ID2D1SolidColorBrush brush;
    private readonly ID2D1StrokeStyle1 round;
    private ID2D1Bitmap1? hand;
    private string? handKey;

    public int Width { get; private set; }
    public int Height { get; private set; }
    public bool UsingWarp { get; }

    private static readonly D2DPixelFormat Format32 = new(Format.B8G8R8A8_UNorm, D2DAlphaMode.Premultiplied);

    private OverlayRenderer(IntPtr hwnd, int width, int height)
    {
        this.hwnd = hwnd;
        FeatureLevel[] levels = [FeatureLevel.Level_11_1, FeatureLevel.Level_11_0, FeatureLevel.Level_10_1, FeatureLevel.Level_10_0, FeatureLevel.Level_9_3];
        var result = D3D11.D3D11CreateDevice(IntPtr.Zero, DriverType.Hardware, DeviceCreationFlags.BgraSupport, levels, out var created);
        if (result.Failure || created is null)
        {
            result = D3D11.D3D11CreateDevice(IntPtr.Zero, DriverType.Warp, DeviceCreationFlags.BgraSupport, levels, out created);
            UsingWarp = true;
        }
        result.CheckError();
        device = created!;
        dxgi = device.QueryInterface<IDXGIDevice1>();
        dxgi.MaximumFrameLatency = 1;
        d2d = Factory.CreateDevice(dxgi);
        dc = d2d.CreateDeviceContext(DeviceContextOptions.None);
        rodShadow = new Shadow(dc);
        handShadow = new Shadow(dc);
        glow = new Shadow(dc);
        brush = dc.CreateSolidColorBrush(new Color4(0, 0, 0, 1));
        round = Factory.CreateStrokeStyle(new StrokeStyleProperties1
        {
            StartCap = CapStyle.Round,
            EndCap = CapStyle.Round,
            DashCap = CapStyle.Round,
            LineJoin = LineJoin.Round,
            MiterLimit = 10,
        });
        Resize(width, height);
    }

    /// <summary>A renderer that shows its frames in <paramref name="window"/>.</summary>
    public static OverlayRenderer ForWindow(IntPtr window, int width, int height) => new(window, width, height);

    /// <summary>A renderer that draws into memory, for self-tests.</summary>
    public static OverlayRenderer Offscreen(int width, int height) => new(IntPtr.Zero, width, height);

    public void Resize(int width, int height)
    {
        width = Math.Max(1, width);
        height = Math.Max(1, height);
        if (width == Width && height == Height && targetBitmap is not null)
        {
            return;
        }
        Width = width;
        Height = height;
        dc.Target = null;
        targetBitmap?.Dispose();
        targetBitmap = null;

        if (hwnd == IntPtr.Zero)
        {
            targetBitmap = dc.CreateBitmap(new SizeI(width, height), IntPtr.Zero, 0,
                new BitmapProperties1(Format32, 96, 96, BitmapOptions.Target));
            return;
        }

        if (swap is null)
        {
            var desc = new SwapChainDescription1
            {
                Width = (uint)width,
                Height = (uint)height,
                Format = Format.B8G8R8A8_UNorm,
                SampleDescription = new SampleDescription(1, 0),
                BufferUsage = Usage.RenderTargetOutput,
                BufferCount = 2,
                Scaling = Scaling.Stretch,
                SwapEffect = SwapEffect.FlipSequential,
                AlphaMode = Vortice.DXGI.AlphaMode.Premultiplied,
                Flags = SwapChainFlags.None,
            };
            using var adapter = dxgi.GetAdapter();
            using var factory = adapter.GetParent<IDXGIFactory2>();
            swap = factory.CreateSwapChainForComposition(device, desc);
            dcomp = DComp.DCompositionCreateDevice<IDCompositionDevice>(dxgi);
            dcomp.CreateTargetForHwnd(hwnd, true, out target).CheckError();
            visual = dcomp.CreateVisual();
            visual.SetContent(swap).CheckError();
            target!.SetRoot(visual).CheckError();
            dcomp.Commit().CheckError();
        }
        else
        {
            swap.ResizeBuffers(2, (uint)width, (uint)height, Format.Unknown, SwapChainFlags.None).CheckError();
        }
        using var surface = swap.GetBuffer<IDXGISurface>(0);
        targetBitmap = dc.CreateBitmapFromDxgiSurface(surface,
            new BitmapProperties1(Format32, 96, 96, BitmapOptions.Target | BitmapOptions.CannotDraw));
    }

    public string? HandKey => handKey;

    public unsafe void SetHand(string key, Pixels pixels)
    {
        if (key == handKey && hand is not null)
        {
            return;
        }
        hand?.Dispose();
        fixed (byte* data = pixels.Data)
        {
            hand = dc.CreateBitmap(new SizeI(pixels.Width, pixels.Height), (IntPtr)data, (uint)pixels.Stride,
                new BitmapProperties1(Format32, 96, 96, BitmapOptions.None));
        }
        handKey = key;
    }

    public static ID2D1PathGeometry BuildPath(InkStroke stroke)
    {
        var geometry = Factory.CreatePathGeometry();
        using var sink = geometry.Open();
        var start = stroke.Start;
        sink.BeginFigure(new Vector2((float)start.X, (float)start.Y), FigureBegin.Hollow);
        foreach (var seg in stroke.Segments())
        {
            var to = new Vector2((float)seg.To.X, (float)seg.To.Y);
            if (seg.Control is PathPoint c)
            {
                sink.AddQuadraticBezier(new QuadraticBezierSegment
                {
                    Point1 = new Vector2((float)c.X, (float)c.Y),
                    Point2 = to,
                });
            }
            else
            {
                sink.AddLine(to);
            }
        }
        sink.EndFigure(FigureEnd.Open);
        sink.Close();
        return geometry;
    }

    /// <summary>Draws and shows a frame. Returns false when the graphics device was lost and the renderer must be rebuilt.</summary>
    public bool Render(Frame frame)
    {
        if (targetBitmap is null)
        {
            return false;
        }
        var lists = new List<ID2D1CommandList>();
        dc.Target = targetBitmap;
        dc.BeginDraw();
        dc.Transform = Matrix3x2.Identity;
        dc.Clear(new Color4(0, 0, 0, 0));

        foreach (var ink in frame.Ink)
        {
            if (ink.Opacity <= 0)
            {
                continue;
            }
            brush.Color = new Color4(0, 0, 0, 0.18f * ink.Opacity);
            dc.Transform = Matrix3x2.CreateTranslation(1 * frame.Unit, 1.5f * frame.Unit);
            dc.DrawGeometry(ink.Geometry, brush, ink.Width, round);
            dc.Transform = Matrix3x2.Identity;
            brush.Color = Rgb.ToColor4(ink.Color, ink.Opacity);
            dc.DrawGeometry(ink.Geometry, brush, ink.Width, round);
        }

        if (frame.Hand is HandPose pose && frame.Metrics is HandMetrics m && hand is not null)
        {
            var size = hand.PixelSize;
            float iw = size.Width;
            float ih = size.Height;
            var layer = ArmRig.LayerMatrix(pose, frame.Height);
            var shadowShift = Matrix3x2.CreateTranslation((float)pose.ShadowDx, (float)-pose.ShadowDy);
            float rodLen = (float)pose.RodLength;
            float handLen = (float)pose.HandLength;
            float width = (float)pose.Width;
            DrawPart(new Rect(0, -rodLen, width, rodLen),
                new Rect(0, (float)(m.Wrist * ih), iw, (float)((m.RodEnd - m.Wrist) * ih)),
                rodShadow, pose, layer, shadowShift, lists);
            DrawPart(new Rect(0, -(rodLen + handLen), width, handLen),
                new Rect(0, (float)(m.HandTop * ih), iw, (float)((m.Wrist - m.HandTop) * ih)),
                handShadow, pose, layer, shadowShift, lists);
        }

        if (frame.Laser is LaserPose laser)
        {
            float cx = (float)laser.X;
            float cy = frame.Height - (float)laser.Y;
            float r = (float)laser.Radius;
            var dot = dc.CreateCommandList();
            lists.Add(dot);
            dc.Target = dot;
            dc.Transform = Matrix3x2.Identity;
            brush.Color = Rgb.ToColor4(InkColors.Red);
            dc.FillEllipse(new Ellipse(new Vector2(0, 0), r, r), brush);
            dot.Close();
            dc.Target = targetBitmap;
            glow.SetInput(0, dot, true);
            glow.BlurStandardDeviation = Math.Min(250, (float)laser.GlowRadius);
            glow.Color = new Vector4(1, 0.16f, 0.12f, 1);
            dc.Transform = Matrix3x2.CreateTranslation(cx, cy);
            dc.DrawImage(glow);
            dc.FillEllipse(new Ellipse(new Vector2(0, 0), r, r), brush);
            brush.Color = new Color4(1, 0.86f, 0.82f, 0.95f);
            float core = (float)laser.CoreRadius;
            dc.FillEllipse(new Ellipse(new Vector2(0, 0), core, core), brush);
        }

        var drawn = dc.EndDraw();
        foreach (var list in lists)
        {
            list.Dispose();
        }
        if (drawn.Failure)
        {
            Log.Write($"draw failed: 0x{drawn.Code:X8}");
            return false;
        }
        if (swap is not null)
        {
            var shown = swap.Present(0, PresentFlags.None);
            if (shown.Failure)
            {
                Log.Write($"present failed: 0x{shown.Code:X8}");
                return false;
            }
        }
        return true;
    }

    private void DrawPart(Rect dest, Rect source, Shadow shadow, HandPose pose, Matrix3x2 layer, Matrix3x2 shadowShift,
        List<ID2D1CommandList> lists)
    {
        if (hand is null)
        {
            return;
        }
        if (pose.ShadowOpacity > 0)
        {
            var part = dc.CreateCommandList();
            lists.Add(part);
            dc.Target = part;
            dc.Transform = Matrix3x2.Identity;
            dc.DrawBitmap(hand, dest, 1, InterpolationMode.Linear, source, null);
            part.Close();
            dc.Target = targetBitmap;
            shadow.SetInput(0, part, true);
            shadow.BlurStandardDeviation = Math.Min(250, (float)pose.ShadowRadius);
            shadow.Color = new Vector4(0, 0, 0, (float)pose.ShadowOpacity);
            dc.Transform = layer * shadowShift;
            dc.DrawImage(shadow);
        }
        dc.Transform = layer;
        dc.DrawBitmap(hand, dest, 1, InterpolationMode.Linear, source, null);
    }

    /// <summary>Copies the last frame back to memory. Only for offscreen renderers.</summary>
    public Pixels ReadBack()
    {
        using var cpu = dc.CreateBitmap(new SizeI(Width, Height), IntPtr.Zero, 0,
            new BitmapProperties1(Format32, 96, 96, BitmapOptions.CpuRead | BitmapOptions.CannotDraw));
        cpu.CopyFromBitmap(targetBitmap!).CheckError();
        var map = cpu.Map(MapOptions.Read);
        var result = new Pixels(Width, Height);
        try
        {
            for (int y = 0; y < Height; y++)
            {
                System.Runtime.InteropServices.Marshal.Copy(map.Bits + (nint)(y * map.Pitch), result.Data, y * result.Stride, result.Stride);
            }
        }
        finally
        {
            cpu.Unmap();
        }
        return result;
    }

    public void Dispose()
    {
        dc.Target = null;
        hand?.Dispose();
        targetBitmap?.Dispose();
        visual?.Dispose();
        target?.Dispose();
        dcomp?.Dispose();
        swap?.Dispose();
        round.Dispose();
        brush.Dispose();
        glow.Dispose();
        handShadow.Dispose();
        rodShadow.Dispose();
        dc.Dispose();
        d2d.Dispose();
        dxgi.Dispose();
        device.Dispose();
    }
}
