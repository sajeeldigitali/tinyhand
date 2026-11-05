using System.Text.Json;
using System.Text.Json.Serialization;

namespace TinyHand.Core;

public readonly record struct PointerStyle(string Id)
{
    public bool IsLaser => Id == "laser";
    public bool IsCustom => Id.StartsWith("custom-", StringComparison.Ordinal);
    public override string ToString() => Id;
}

public static class InkColors
{
    public const uint Purple = 0x8754F2;
    public const uint Red = 0xFF291F;
}

/// <summary>Every pointer TinyHand can show: the built-in hands, the hands people make and the laser dot.</summary>
public static class Pointers
{
    public static readonly PointerStyle Lavender = new("lavender");
    public static readonly PointerStyle Glove = new("glove");
    public static readonly PointerStyle Coral = new("coral");
    public static readonly PointerStyle Plush = new("plush");
    public static readonly PointerStyle Robot = new("robot");
    public static readonly PointerStyle Laser = new("laser");

    public const int MaxPinned = 8;

    public static CustomHandStore? Custom { get; set; }

    private static readonly (string Id, string Title, uint Ink)[] BuiltIn =
    [
        ("lavender", "Lavender toy", InkColors.Purple),
        ("glove", "Cursor glove", InkColors.Purple),
        ("coral", "Coral vinyl", InkColors.Red),
        ("plush", "Plush", 0x2F86F0),
        ("robot", "Robot", 0x16A87A),
        ("pink", "Bubblegum", 0xEE4F98),
        ("blue", "Sky", 0x2F86F0),
        ("mint", "Mint", 0x16A87A),
        ("lemon", "Lemon", 0xF2B705),
        ("black", "Midnight", 0x1D1D21),
        ("chrome", "Chrome", 0x6B7380),
        ("holo", "Holo", InkColors.Purple),
        ("fuzzy", "Fuzzy", InkColors.Purple),
        ("wood", "Wood", 0x9A5E2C),
        ("marble", "Marble", 0x6B7380),
        ("terrazzo", "Terrazzo", 0xF0561D),
        ("glass", "Glass", 0x1597E5),
        ("jelly", "Jelly", 0xA33FE6),
        ("pastel", "Pastel", 0xEE4F98),
        ("pixel", "Pixel", 0x1D1D21),
        ("lowpoly", "Low poly", InkColors.Purple),
        ("knit", "Knit", InkColors.Purple),
        ("skeleton", "Skeleton", 0x9A5E2C),
        ("astro", "Astronaut", 0x2F86F0),
        ("space", "Space", InkColors.Red),
        ("monster", "Monster", 0x3E9E2E),
        ("water", "Water", 0x1597E5),
        ("lava", "Lava", 0xF0561D),
        ("grass", "Grass", 0x3E9E2E),
        ("cactus", "Cactus", 0x3E9E2E),
        ("choco", "Chocolate", 0x9A5E2C),
        ("cloud", "Cloud", 0x2F86F0),
        ("mech", "Mech", 0xF0561D),
        ("neon", "Neon", 0xE0309A),
        ("leaf", "Leaf", 0x3E9E2E),
        ("cookie", "Gingerbread", 0x9A5E2C),
    ];

    public static IReadOnlyList<PointerStyle> BuiltInHands => BuiltIn.Select(b => new PointerStyle(b.Id)).ToList();

    public static IReadOnlyList<PointerStyle> CustomHands =>
        (Custom?.All ?? []).Select(c => new PointerStyle(c.Id)).ToList();

    public static IReadOnlyList<PointerStyle> AllCases => [.. BuiltInHands, .. CustomHands, Laser];

    public static readonly IReadOnlyList<PointerStyle> DefaultPinned = [Lavender, Glove, Coral, Plush, Robot, Laser];

    public static bool Exists(string id) => AllCases.Any(s => s.Id == id);

    /// <summary>Reads a saved style, mapping the names older versions used.</summary>
    public static PointerStyle? Parse(string? raw)
    {
        if (raw is null)
        {
            return null;
        }
        if (raw == "dot")
        {
            return Laser;
        }
        if (raw == "sunny")
        {
            return Coral;
        }
        return Exists(raw) ? new PointerStyle(raw) : null;
    }

    public static string Title(PointerStyle style)
    {
        if (style.IsLaser)
        {
            return "Laser dot";
        }
        if (Custom?.Hand(style.Id) is CustomHand custom)
        {
            return custom.Name;
        }
        return BuiltIn.FirstOrDefault(b => b.Id == style.Id).Title ?? style.Id;
    }

    public static string? AssetName(PointerStyle style)
    {
        if (style.IsLaser)
        {
            return null;
        }
        return style.IsCustom ? style.Id : "hand_" + style.Id;
    }

    public static uint InkColor(PointerStyle style)
    {
        if (style.IsLaser)
        {
            return InkColors.Red;
        }
        if (Custom?.Hand(style.Id) is CustomHand custom)
        {
            return custom.Ink;
        }
        var match = BuiltIn.FirstOrDefault(b => b.Id == style.Id);
        return match.Id is null ? InkColors.Purple : match.Ink;
    }

    /// <summary>The pinned list as saved, without unknown or repeated pointers, falling back to the defaults.</summary>
    public static List<PointerStyle> NormalizePinned(IEnumerable<string>? raw)
    {
        if (raw is null)
        {
            return [.. DefaultPinned];
        }
        var seen = new HashSet<string>();
        var pinned = raw.Select(Parse).OfType<PointerStyle>().Where(s => seen.Add(s.Id)).ToList();
        return pinned.Count == 0 ? [.. DefaultPinned] : pinned;
    }
}

public sealed record CustomHand(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("ink")] uint Ink);

/// <summary>The hands people made, kept as PNGs next to a hands.json list. A port of CustomHands in the macOS app.</summary>
public sealed class CustomHandStore(string directory)
{
    public string Directory { get; } = directory;
    public event Action? Changed;

    private List<CustomHand>? loaded;

    private string ListPath => Path.Combine(Directory, "hands.json");

    public IReadOnlyList<CustomHand> All
    {
        get
        {
            if (loaded is not null)
            {
                return loaded;
            }
            try
            {
                loaded = File.Exists(ListPath)
                    ? JsonSerializer.Deserialize<List<CustomHand>>(File.ReadAllText(ListPath)) ?? []
                    : [];
            }
            catch (Exception)
            {
                loaded = [];
            }
            return loaded;
        }
    }

    public CustomHand? Hand(string id) =>
        id.StartsWith("custom-", StringComparison.Ordinal) ? All.FirstOrDefault(h => h.Id == id) : null;

    public string ImagePath(string id) => Path.Combine(Directory, id + ".png");

    public PointerStyle Add(byte[] png, uint ink, string name)
    {
        System.IO.Directory.CreateDirectory(Directory);
        var id = "custom-" + Guid.NewGuid().ToString("N")[..8];
        File.WriteAllBytes(ImagePath(id), png);
        var trimmed = name.Trim();
        var list = All.ToList();
        list.Add(new CustomHand(id, trimmed.Length == 0 ? $"My hand {list.Count + 1}" : trimmed[..Math.Min(16, trimmed.Length)], ink));
        Save(list);
        return new PointerStyle(id);
    }

    public void Remove(PointerStyle style)
    {
        if (!style.IsCustom)
        {
            return;
        }
        try
        {
            File.Delete(ImagePath(style.Id));
        }
        catch (IOException)
        {
        }
        Save(All.Where(h => h.Id != style.Id).ToList());
    }

    private void Save(List<CustomHand> list)
    {
        System.IO.Directory.CreateDirectory(Directory);
        var temp = ListPath + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(list));
        File.Move(temp, ListPath, overwrite: true);
        loaded = list;
        Changed?.Invoke();
    }

    public string SuggestedName(string path)
    {
        var name = Path.GetFileNameWithoutExtension(path).Replace('_', ' ').Replace('-', ' ').Trim();
        var words = string.Join(' ', name.Split(' ', StringSplitOptions.RemoveEmptyEntries).Take(2));
        if (words.Length == 0 || words.Length > 16 || words.StartsWith("chatgpt image", StringComparison.OrdinalIgnoreCase))
        {
            return $"My hand {All.Count + 1}";
        }
        return char.ToUpperInvariant(words[0]) + words[1..];
    }
}
