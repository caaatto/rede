using Avalonia;
using Avalonia.Media;

namespace Rede.Desktop.Themes;

/// <summary>
/// Runtime theme switching. Replaces color/brush resources in Application.Current.Resources
/// so all DynamicResource bindings in RedeTheme.axaml update live without restart.
/// </summary>
public static class ThemeService
{
    public const string Dark = "dark";         // default — #0a0a0f near-black
    public const string Midnight = "midnight"; // deeper pure black, more contrast
    public const string Dim = "dim";           // softer dark gray, Discord-like
    public const string Light = "light";       // white/gray

    public record ThemePalette(
        string Background,
        string Surface,
        string SurfaceHover,
        string SurfaceActive,
        string Border,
        string BorderSubtle,
        string TextPrimary,
        string TextSecondary,
        string TextMuted);

    private static readonly ThemePalette DarkPalette = new(
        Background: "#0a0a0f",
        Surface: "#12121a",
        SurfaceHover: "#1a1a28",
        SurfaceActive: "#222236",
        Border: "#1e1e2e",
        BorderSubtle: "#16161f",
        TextPrimary: "#e0e0e8",
        TextSecondary: "#6b6b80",
        TextMuted: "#44445a");

    private static readonly ThemePalette MidnightPalette = new(
        Background: "#000000",
        Surface: "#07070c",
        SurfaceHover: "#0e0e16",
        SurfaceActive: "#16162a",
        Border: "#141420",
        BorderSubtle: "#0a0a12",
        TextPrimary: "#f0f0f8",
        TextSecondary: "#7a7a90",
        TextMuted: "#4a4a60");

    private static readonly ThemePalette DimPalette = new(
        Background: "#1a1b22",
        Surface: "#22232c",
        SurfaceHover: "#2a2b36",
        SurfaceActive: "#343541",
        Border: "#2d2e3a",
        BorderSubtle: "#242530",
        TextPrimary: "#e8e8f0",
        TextSecondary: "#8a8ba0",
        TextMuted: "#5a5b70");

    private static readonly ThemePalette LightPalette = new(
        Background: "#f5f5f8",
        Surface: "#ffffff",
        SurfaceHover: "#ebebf0",
        SurfaceActive: "#dcdce4",
        Border: "#d4d4de",
        BorderSubtle: "#e4e4ec",
        TextPrimary: "#1a1a24",
        TextSecondary: "#5a5b70",
        TextMuted: "#8a8ba0");

    /// <summary>
    /// Override the global accent brush (buttons, highlights, focus rings).
    /// Called on profile load and whenever the user changes their accent in
    /// Settings. Uses DynamicResource-backed keys so all views update live.
    /// </summary>
    public static void ApplyAccent(string? hex)
    {
        var app = Application.Current;
        if (app is null) return;
        var r = app.Resources;
        if (!Color.TryParse(hex, out var c)) c = Color.Parse("#8b5cf6");
        var dim = Dim20(c);
        var glow = Color.FromArgb(0x40, c.R, c.G, c.B);

        r["RedeAccentViolet"] = c;
        r["RedeAccentVioletDim"] = dim;
        r["RedeGlowViolet"] = glow;
        r["RedeAccentVioletBrush"] = new SolidColorBrush(c);
        r["RedeAccentVioletDimBrush"] = new SolidColorBrush(dim);
        r["RedeGlowVioletBrush"] = new SolidColorBrush(glow);
    }

    // Darken a color by ~20% for hover / dim variants.
    private static Color Dim20(Color c)
    {
        byte d(byte v) => (byte)(v * 0.8);
        return Color.FromArgb(c.A, d(c.R), d(c.G), d(c.B));
    }

    public static void Apply(string? variant)
    {
        var palette = (variant ?? Dark).ToLowerInvariant() switch
        {
            Midnight => MidnightPalette,
            Dim => DimPalette,
            Light => LightPalette,
            _ => DarkPalette,
        };

        var app = Application.Current;
        if (app is null) return;
        var r = app.Resources;

        Set(r, "RedeBackground", palette.Background);
        Set(r, "RedeSurface", palette.Surface);
        Set(r, "RedeSurfaceHover", palette.SurfaceHover);
        Set(r, "RedeSurfaceActive", palette.SurfaceActive);
        Set(r, "RedeBorder", palette.Border);
        Set(r, "RedeBorderSubtle", palette.BorderSubtle);
        Set(r, "RedeTextPrimary", palette.TextPrimary);
        Set(r, "RedeTextSecondary", palette.TextSecondary);
        Set(r, "RedeTextMuted", palette.TextMuted);

        SetBrush(r, "RedeBackgroundBrush", palette.Background);
        SetBrush(r, "RedeSurfaceBrush", palette.Surface);
        SetBrush(r, "RedeSurfaceHoverBrush", palette.SurfaceHover);
        SetBrush(r, "RedeSurfaceActiveBrush", palette.SurfaceActive);
        SetBrush(r, "RedeBorderBrush", palette.Border);
        SetBrush(r, "RedeBorderSubtleBrush", palette.BorderSubtle);
        SetBrush(r, "RedeTextPrimaryBrush", palette.TextPrimary);
        SetBrush(r, "RedeTextSecondaryBrush", palette.TextSecondary);
        SetBrush(r, "RedeTextMutedBrush", palette.TextMuted);
    }

    private static void Set(Avalonia.Controls.IResourceDictionary r, string key, string hex)
    {
        if (Color.TryParse(hex, out var c))
            r[key] = c;
    }

    private static void SetBrush(Avalonia.Controls.IResourceDictionary r, string key, string hex)
    {
        if (Color.TryParse(hex, out var c))
            r[key] = new SolidColorBrush(c);
    }
}
