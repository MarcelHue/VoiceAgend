using Windows.UI;

namespace VoiceAgend.App.Themes;

/// <summary>
/// Theme-Color-Tokens, übernommen aus dem Design-Handoff (shared.jsx → VA_THEMES).
/// Wird vom ThemeManager als globale Resources im App.Resources-Dictionary publiziert.
/// </summary>
public sealed class VATheme
{
    public required Color Bg { get; init; }
    public required Color BgRaised { get; init; }
    public required Color Surface { get; init; }
    public required Color SurfaceHi { get; init; }
    public required Color Border { get; init; }
    public required Color BorderSoft { get; init; }
    public required Color Text { get; init; }
    public required Color TextDim { get; init; }
    public required Color TextMute { get; init; }
    public required Color Accent { get; init; }
    public required Color AccentDim { get; init; }
    public required Color Danger { get; init; }
    public required Color Ok { get; init; }
    public required Color Warn { get; init; }

    public static readonly VATheme Dark = new()
    {
        Bg = Hex(0x0c, 0x0d, 0x10),
        BgRaised = Hex(0x12, 0x14, 0x19),
        Surface = Hex(0x18, 0x1a, 0x20),
        SurfaceHi = Hex(0x1f, 0x22, 0x29),
        Border = Hex(0x26, 0x29, 0x32),
        BorderSoft = Hex(0x1d, 0x20, 0x27),
        Text = Hex(0xe8, 0xe9, 0xec),
        TextDim = Hex(0xa4, 0xa8, 0xb2),
        TextMute = Hex(0x6e, 0x72, 0x80),
        Accent = Hex(0x22, 0xb5, 0xb0),
        AccentDim = Hex(0x1a, 0x87, 0x84),
        Danger = Hex(0xe2, 0x6a, 0x5b),
        Ok = Hex(0x5c, 0xc8, 0x9e),
        Warn = Hex(0xe9, 0xc4, 0x68),
    };

    public static readonly VATheme Light = new()
    {
        Bg = Hex(0xf4, 0xf2, 0xec),
        BgRaised = Hex(0xff, 0xff, 0xff),
        Surface = Hex(0xff, 0xff, 0xff),
        SurfaceHi = Hex(0xfb, 0xfa, 0xf6),
        Border = Hex(0xe2, 0xdd, 0xd2),
        BorderSoft = Hex(0xec, 0xeb, 0xe4),
        Text = Hex(0x15, 0x16, 0x1a),
        TextDim = Hex(0x54, 0x56, 0x5d),
        TextMute = Hex(0x8b, 0x8c, 0x92),
        Accent = Hex(0x0f, 0x8a, 0x85),
        AccentDim = Hex(0x0c, 0x70, 0x6b),
        Danger = Hex(0xc8, 0x55, 0x3d),
        Ok = Hex(0x2f, 0x8f, 0x6a),
        Warn = Hex(0xb0, 0x7c, 0x1a),
    };

    private static Color Hex(byte r, byte g, byte b) => Color.FromArgb(0xFF, r, g, b);
    public static Color WithAlpha(Color c, byte a) => Color.FromArgb(a, c.R, c.G, c.B);
}
