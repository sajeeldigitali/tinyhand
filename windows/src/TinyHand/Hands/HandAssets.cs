using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using TinyHand.Core;

namespace TinyHand.Hands;

/// <summary>Reads and writes images through Windows Imaging, as premultiplied BGRA <see cref="Pixels"/>.</summary>
internal static class ImageIO
{
    public static Pixels Decode(string path)
    {
        using var stream = File.OpenRead(path);
        return Decode(stream);
    }

    public static Pixels Decode(Stream stream)
    {
        var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        var frame = decoder.Frames[0];
        var converted = new FormatConvertedBitmap(frame, PixelFormats.Pbgra32, null, 0);
        int w = converted.PixelWidth;
        int h = converted.PixelHeight;
        var data = new byte[w * h * 4];
        converted.CopyPixels(data, w * 4, 0);
        return new Pixels(w, h, data);
    }

    public static BitmapSource ToBitmap(Pixels p, double dpi = 96)
    {
        var bitmap = BitmapSource.Create(p.Width, p.Height, dpi, dpi, PixelFormats.Pbgra32, null, p.Data, p.Stride);
        bitmap.Freeze();
        return bitmap;
    }

    public static byte[] EncodePng(Pixels p)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(ToBitmap(p)));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }

    public static void SavePng(BitmapSource bitmap, string path)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }
}

internal sealed record HandAsset(string Name, Pixels Image, HandMetrics Metrics);

/// <summary>
/// Loads hand images and measures them, keeps the last two in memory, and makes the small square
/// thumbnails the toolbar and Settings show. A port of HandAsset in the macOS app.
/// </summary>
internal static class HandAssets
{
    private const int CacheLimit = 2;
    private static readonly Dictionary<string, HandAsset> Cache = [];
    private static readonly List<string> Recent = [];
    private static readonly Dictionary<string, BitmapSource> Thumbnails = [];
    private static (string Key, Pixels Image)? scaled;

    public static string? PathFor(string name)
    {
        if (Pointers.Custom is CustomHandStore store)
        {
            var custom = store.ImagePath(name);
            if (File.Exists(custom))
            {
                return custom;
            }
        }
        var builtIn = Paths.HandAsset(name);
        return File.Exists(builtIn) ? builtIn : null;
    }

    public static HandAsset? Load(string name)
    {
        if (Cache.TryGetValue(name, out var hit))
        {
            Touch(name);
            return hit;
        }
        var asset = Read(name);
        if (asset is null)
        {
            return null;
        }
        Cache[name] = asset;
        Touch(name);
        return asset;
    }

    private static HandAsset? Read(string name)
    {
        try
        {
            if (PathFor(name) is not string path)
            {
                return null;
            }
            var image = ImageIO.Decode(path);
            return HandMetrics.Measure(image) is HandMetrics m ? new HandAsset(name, image, m) : null;
        }
        catch (Exception e)
        {
            Log.Write($"hand {name} unreadable: {e.Message}");
            return null;
        }
    }

    private static void Touch(string name)
    {
        Recent.Remove(name);
        Recent.Add(name);
        while (Recent.Count > CacheLimit)
        {
            Cache.Remove(Recent[0]);
            Recent.RemoveAt(0);
        }
    }

    public static void Forget(string name)
    {
        Cache.Remove(name);
        Recent.Remove(name);
        Thumbnails.Remove(name);
        scaled = null;
    }

    /// <summary>
    /// The hand image shrunk to about the size it is drawn, in steps of 64 pixels, as the macOS app does,
    /// so it looks sharp and takes little memory.
    /// </summary>
    public static (string Key, Pixels Image) Scaled(HandAsset asset, double handLengthPx)
    {
        var full = asset.Image;
        const int step = 64;
        double restPx = handLengthPx / (asset.Metrics.Wrist - asset.Metrics.HandTop);
        int needed = ((int)Math.Ceiling(restPx) + step - 1) / step * step;
        int height = needed >= full.Height * 9 / 10 ? full.Height : Math.Max(step, needed);
        var key = $"{asset.Name}@{height}";
        if (scaled is { } s && s.Key == key)
        {
            return s;
        }
        var image = height == full.Height ? full : Resample.ResizeToHeight(full, height);
        scaled = (key, image);
        return scaled.Value;
    }

    public static BitmapSource? CachedThumbnail(string name) => Thumbnails.GetValueOrDefault(name);

    public static BitmapSource? Thumbnail(string name)
    {
        if (Thumbnails.TryGetValue(name, out var hit))
        {
            return hit;
        }
        var asset = Cache.GetValueOrDefault(name) ?? Read(name);
        if (asset is null)
        {
            return null;
        }
        var thumb = MakeThumbnail(asset);
        Thumbnails[name] = thumb;
        return thumb;
    }

    private static BitmapSource MakeThumbnail(HandAsset asset)
    {
        var (x, y, size) = asset.Metrics.ThumbnailCrop(asset.Image.Width, asset.Image.Height);
        var cropped = asset.Image.Crop(x, y, size, size);
        var small = Resample.Resize(cropped, 128, 128);
        return ImageIO.ToBitmap(small, 192);
    }

    public static void PreloadThumbnails(IEnumerable<string> names, Action? done = null)
    {
        var missing = names.Where(n => !Thumbnails.ContainsKey(n)).Distinct().ToList();
        if (missing.Count == 0)
        {
            done?.Invoke();
            return;
        }
        var dispatcher = Dispatcher.CurrentDispatcher;
        Task.Run(() =>
        {
            var built = new List<(string, BitmapSource)>();
            foreach (var name in missing)
            {
                if (Read(name) is HandAsset asset)
                {
                    built.Add((name, MakeThumbnail(asset)));
                }
            }
            dispatcher.BeginInvoke(() =>
            {
                foreach (var (name, thumb) in built)
                {
                    Thumbnails.TryAdd(name, thumb);
                }
                done?.Invoke();
            });
        });
    }
}
