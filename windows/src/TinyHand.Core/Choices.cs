namespace TinyHand.Core;

public enum InkChoice
{
    Match, Red, Orange, Yellow, Green, Blue, Purple, White, Black,
}

public enum TapSoundChoice
{
    Thock, Click, Pop, Wood, Boop, Squeak,
}

/// <summary>The key held to click for real. Windows has no Option or Command, so Alt, Ctrl and Shift stand in.</summary>
public enum InteractKey
{
    Alt, Ctrl, Shift, Off,
}

public static class Choices
{
    public static string Title(this InkChoice choice) => choice switch
    {
        InkChoice.Match => "Match the pointer",
        _ => choice.ToString(),
    };

    public static uint? Color(this InkChoice choice) => choice switch
    {
        InkChoice.Match => null,
        InkChoice.Red => InkColors.Red,
        InkChoice.Orange => 0xFF8A00,
        InkChoice.Yellow => 0xFFCC00,
        InkChoice.Green => 0x30C25A,
        InkChoice.Blue => 0x0A84FF,
        InkChoice.Purple => 0x8754F2,
        InkChoice.White => 0xFFFFFF,
        InkChoice.Black => 0x1D1D21,
        _ => null,
    };

    public static uint Resolved(this InkChoice choice, PointerStyle style) => choice.Color() ?? Pointers.InkColor(style);

    public static string Title(this TapSoundChoice choice) => choice switch
    {
        TapSoundChoice.Wood => "Wood block",
        _ => choice.ToString(),
    };

    public static string FileName(this TapSoundChoice choice) => $"tap_{choice.ToString().ToLowerInvariant()}.wav";

    public static string Title(this InteractKey key) => key.ToString();

    public static readonly IReadOnlyList<(string Title, double Seconds)> FadeChoices =
    [
        ("After 3 seconds", 3),
        ("After 6 seconds", 6),
        ("After 10 seconds", 10),
        ("Keep until cleared", 0),
    ];

    public static T? ParseEnum<T>(string? raw) where T : struct, Enum =>
        Enum.TryParse<T>(raw, ignoreCase: true, out var value) && Enum.IsDefined(value) ? value : null;
}
