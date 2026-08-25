using System.Windows;
using System.Windows.Media;

namespace SophonDownloader;

public static class ThemeManager
{
    private sealed record Palette(
        string Background,
        string Surface,
        string SurfaceHover,
        string CardTitle,
        string Border,
        string Text,
        string Muted,
        string InputBackground,
        string ButtonBackground,
        string Primary,
        string PrimaryHover,
        string Danger,
        string DangerSoft,
        string DangerSoftHover,
        string Active,
        string ModalBackground,
        string SwitchOff,
        string SwitchKnob);

    public static void Apply(Window window, AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(window);
        Palette p = ResolvePalette(settings.ThemePalette, settings.ThemeMode);
        FontFamily appFont = new(string.IsNullOrWhiteSpace(settings.FontFamily) ? "Segoe UI Variable" : settings.FontFamily);
        window.Resources["AppFontFamily"] = appFont;
        window.FontFamily = appFont;

        SetBrush(window.Resources, "WhiteBrush", p.Text);
        SetBrush(window.Resources, "InverseTextBrush", "#FFFFFF");
        SetBrush(window.Resources, "PrimaryTextBrush", p.Text);
        SetBrush(window.Resources, "SecondaryTextBrush", p.Muted);
        SetBrush(window.Resources, "MutedTextBrush", p.Muted);
        SetBrush(window.Resources, "LabelBrush", p.Text);
        SetBrush(window.Resources, "AccentBrush", p.Primary);
        SetBrush(window.Resources, "AccentHoverBrush", p.PrimaryHover);
        SetBrush(window.Resources, "PanelBrush", p.Surface);
        SetBrush(window.Resources, "PanelStrongBrush", p.CardTitle);
        SetBrush(window.Resources, "InputBackgroundBrush", p.InputBackground);
        SetBrush(window.Resources, "InputHoverBrush", p.SurfaceHover);
        SetBrush(window.Resources, "DropdownBackgroundBrush", p.InputBackground);
        SetBrush(window.Resources, "DropdownHoverBrush", p.SurfaceHover);
        SetBrush(window.Resources, "DropdownSelectedBrush", p.Primary);
        SetBrush(window.Resources, "ControlBorderBrush", p.Border);
        SetBrush(window.Resources, "ControlHoverBrush", p.SurfaceHover);
        SetBrush(window.Resources, "ControlFocusBrush", p.Primary);
        SetBrush(window.Resources, "OnlineBrush", p.Active);
        SetBrush(window.Resources, "OfflineBrush", p.Danger);

        SetBrush(window.Resources, "WindowBackgroundBrush", p.Background);
        SetBrush(window.Resources, "TitleBarBackgroundBrush", p.Primary);
        SetBrush(window.Resources, "PanelBackgroundBrush", p.Surface);
        SetBrush(window.Resources, "PanelStrongBackgroundBrush", p.CardTitle);
        SetBrush(window.Resources, "InputBackgroundGradientBrush", p.InputBackground);
        SetBrush(window.Resources, "DropdownBackgroundGradientBrush", p.InputBackground);
        SetBrush(window.Resources, "ModeButtonBackgroundBrush", p.SurfaceHover);
        SetBrush(window.Resources, "PrimaryButtonBackgroundBrush", p.Primary);
        SetBrush(window.Resources, "PrimaryButtonHoverBrush", p.PrimaryHover);
        SetBrush(window.Resources, "PrimaryButtonPressedBrush", p.PrimaryHover);
        SetBrush(window.Resources, "SecondaryButtonBackgroundBrush", p.SurfaceHover);
        SetBrush(window.Resources, "SecondaryButtonHoverBrush", p.CardTitle);
        SetBrush(window.Resources, "SecondaryButtonPressedBrush", p.Primary);
        SetBrush(window.Resources, "CancelButtonBackgroundBrush", p.Danger);
        SetBrush(window.Resources, "CancelButtonHoverBrush", p.Danger);
        SetBrush(window.Resources, "CancelButtonPressedBrush", p.Danger);
        SetBrush(window.Resources, "DangerSoftBackgroundBrush", p.DangerSoft);
        SetBrush(window.Resources, "DangerSoftHoverBrush", p.DangerSoftHover);
        SetBrush(window.Resources, "ReloadButtonBackgroundBrush", p.SurfaceHover);
        SetBrush(window.Resources, "LogoBackgroundBrush", p.Primary);
        SetBrush(window.Resources, "HeaderForegroundBrush", "#FFFFFF");
        SetBrush(window.Resources, "HeaderMutedBrush", "#FFFFFF");
        SetBrush(window.Resources, "HeaderHoverBrush", p.PrimaryHover);

        SetBrush(window.Resources, "BackgroundBrush", p.Background);
        SetBrush(window.Resources, "SurfaceBrush", p.Surface);
        SetBrush(window.Resources, "SurfaceHoverBrush", p.SurfaceHover);
        SetBrush(window.Resources, "CardTitleBrush", p.CardTitle);
        SetBrush(window.Resources, "ButtonBackgroundBrush", p.SurfaceHover);
        SetBrush(window.Resources, "PrimaryBrush", p.Primary);
        SetBrush(window.Resources, "PrimaryHoverBrush", p.PrimaryHover);
        SetBrush(window.Resources, "DangerBrush", p.Danger);
        SetBrush(window.Resources, "ModalBackgroundBrush", p.ModalBackground);
        SetBrush(window.Resources, "BorderBrush", p.Border);
        SetBrush(window.Resources, "QueueCardBrush", p.Surface);
        SetBrush(window.Resources, "QueueBorderBrush", p.Border);
        SetBrush(window.Resources, "QueueTypeBrush", p.Primary);
        SetBrush(window.Resources, "QueueTitleBrush", p.Text);
        SetBrush(window.Resources, "QueueSecondaryTextBrush", p.Muted);

        SetBrush(window.Resources, "TextBrush", p.Text);
        SetBrush(window.Resources, "MutedBrush", p.Muted);
        SetBrush(window.Resources, "InputBrush", p.InputBackground);

        if (window is MainWindow main)
        {
            main.ApplyThemeSurfaces(p.Background, p.Surface);
        }
    }

    private static void SetBrush(ResourceDictionary resources, string key, string hex)
    {
        resources[key] = new SolidColorBrush(Parse(hex));
    }

    private static Color Parse(string value) =>
        (Color)ColorConverter.ConvertFromString(value);

    private static Palette ResolvePalette(string theme, string mode)
    {
        string t = theme.Trim().ToLowerInvariant();
        bool dark = string.Equals(mode, "Dark", StringComparison.OrdinalIgnoreCase);

        return t switch
        {
            "blue" => dark
                ? new Palette("#0f172a", "#172554", "#1e3a5f", "#172f52", "#1e40af", "#dbeafe", "#93a4bd", "#0f1b32", "#172554", "#3b82f6", "#60a5fa", "#f87171", "#3a171b", "#551f24", "#22c55e", "#730F172A", "#1e40af", "#dbeafe")
                : new Palette("#e6f0ff", "#ffffff", "#f0f7ff", "#dbeafe", "#bfdbfe", "#172554", "#64748b", "#ffffff", "#ffffff", "#2563eb", "#1d4ed8", "#dc2626", "#fef0f1", "#fde0e3", "#16a34a", "#730F172A", "#bfdbfe", "#ffffff"),
            "green" => dark
                ? new Palette("#071a0d", "#10291a", "#173b24", "#123520", "#166534", "#dcfce7", "#91a89a", "#0a1f11", "#10291a", "#22c55e", "#4ade80", "#f87171", "#35161b", "#4a1b21", "#4ade80", "#BF052E16", "#166534", "#dcfce7")
                : new Palette("#e6f7eb", "#ffffff", "#f0fdf4", "#dcfce7", "#bbf7d0", "#14532d", "#64748b", "#ffffff", "#ffffff", "#16a34a", "#15803d", "#dc2626", "#fef0f1", "#fde0e3", "#16a34a", "#5914532D", "#bbf7d0", "#ffffff"),
            "purple" => dark
                ? new Palette("#160b21", "#241331", "#321b45", "#2d1740", "#6b21a8", "#f3e8ff", "#a99bb4", "#1b0d27", "#241331", "#a855f7", "#c084fc", "#f87171", "#35161b", "#4a1b21", "#4ade80", "#BF3B0764", "#6b21a8", "#f3e8ff")
                : new Palette("#f3eaf9", "#ffffff", "#faf5ff", "#f3e8ff", "#e9d5ff", "#3b0764", "#6b7280", "#ffffff", "#ffffff", "#9333ea", "#7e22ce", "#dc2626", "#fef0f1", "#fde0e3", "#16a34a", "#593B0764", "#e9d5ff", "#ffffff"),
            "red" => dark
                ? new Palette("#21090d", "#321117", "#461820", "#3d141b", "#991b1b", "#ffe4e6", "#b8a0a4", "#260b10", "#321117", "#ef4444", "#f87171", "#f87171", "#35161b", "#4a1b21", "#4ade80", "#BF4C0519", "#991b1b", "#ffe4e6")
                : new Palette("#fce7ea", "#ffffff", "#fff1f2", "#ffe4e6", "#fecdd3", "#4c0519", "#6b7280", "#ffffff", "#ffffff", "#dc2626", "#b91c1c", "#dc2626", "#fef0f1", "#fde0e3", "#16a34a", "#594C0519", "#fecdd3", "#ffffff"),
            _ => dark
                ? new Palette("#111827", "#1f2937", "#273449", "#182230", "#374151", "#f3f4f6", "#9ca3af", "#111827", "#1f2937", "#94a3b8", "#cbd5e1", "#ef4444", "#3d171a", "#572024", "#22c55e", "#A6000000", "#374151", "#f3f4f6")
                : new Palette("#e9edf1", "#ffffff", "#f8fafc", "#fafafa", "#dfe3e8", "#202124", "#6b7280", "#ffffff", "#ffffff", "#475569", "#334155", "#dc2626", "#fef0f1", "#fde0e3", "#16a34a", "#59000000", "#dfe3e8", "#ffffff")
        };
    }
}
