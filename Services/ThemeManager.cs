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
        Palette p = ResolvePalette(settings.ThemePalette, settings.ThemeMode, settings.CustomAccentHex);
        FontFamily appFont = new(string.IsNullOrWhiteSpace(settings.FontFamily) ? "Segoe UI Variable" : settings.FontFamily);
        window.Resources["AppFontFamily"] = appFont;
        window.FontFamily = appFont;

        bool darkMode = string.Equals(settings.ThemeMode, "Dark", StringComparison.OrdinalIgnoreCase);
        string readableSecondary = darkMode ? p.Muted : p.Text;

        SetBrush(window.Resources, "WhiteBrush", p.Text);
        SetBrush(window.Resources, "InverseTextBrush", "#FFFFFF");
        SetBrush(window.Resources, "PrimaryTextBrush", p.Text);
        SetBrush(window.Resources, "SecondaryTextBrush", readableSecondary);
        SetBrush(window.Resources, "MutedTextBrush", readableSecondary);
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
        SetBrush(window.Resources, "TitleBarBackgroundBrush", darkMode ? "#CC111827" : "#DDF8FAFC");
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
        SetBrush(window.Resources, "HeaderForegroundBrush", darkMode ? "#FFFFFF" : p.Text);
        SetBrush(window.Resources, "HeaderMutedBrush", darkMode ? "#FFFFFF" : p.Text);
        SetBrush(window.Resources, "HeaderHoverBrush", p.PrimaryHover);
        SetBrush(window.Resources, "GameTextPanelBrush", darkMode ? "#B8000000" : "#55FFFFFF");
        bool hasBackgroundImage = !string.IsNullOrWhiteSpace(settings.BackgroundImagePath) && File.Exists(settings.BackgroundImagePath);
        SetBrush(window.Resources, "BackgroundScrimBrush", hasBackgroundImage ? (darkMode ? "#99000000" : "#18000000") : "#00000000");

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
        SetBrush(window.Resources, "QueueSecondaryTextBrush", readableSecondary);

        SetBrush(window.Resources, "TextBrush", p.Text);
        SetBrush(window.Resources, "MutedBrush", readableSecondary);
        SetBrush(window.Resources, "InputBrush", p.InputBackground);

        if (window is MainWindow main)
        {
            main.ApplyThemeSurfaces(p.Background, p.Surface, settings.BackgroundImagePath);
        }
    }

    private static void SetBrush(ResourceDictionary resources, string key, string hex)
    {
        resources[key] = new SolidColorBrush(Parse(hex));
    }

    private static Color Parse(string value) =>
        (Color)ColorConverter.ConvertFromString(value);

    private static Palette ResolvePalette(string theme, string mode, string customAccentHex)
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
            "orange" => dark
                ? new Palette("#1f1208", "#2f1b0b", "#43250f", "#3b200b", "#9a3412", "#ffedd5", "#c7a98b", "#241407", "#2f1b0b", "#f97316", "#fb923c", "#f87171", "#35161b", "#4a1b21", "#4ade80", "#BF3A1706", "#9a3412", "#ffedd5")
                : new Palette("#fff3e8", "#ffffff", "#fff7ed", "#ffedd5", "#fed7aa", "#7c2d12", "#7c6f67", "#ffffff", "#ffffff", "#ea580c", "#c2410c", "#dc2626", "#fef0f1", "#fde0e3", "#16a34a", "#593B1706", "#fed7aa", "#ffffff"),
            "yellow" => dark
                ? new Palette("#1a1603", "#2b2405", "#3d3308", "#352d06", "#a16207", "#fef9c3", "#c8bb7a", "#211b04", "#2b2405", "#eab308", "#facc15", "#f87171", "#35161b", "#4a1b21", "#4ade80", "#BF5A4A0B", "#a16207", "#fef9c3")
                : new Palette("#fffbea", "#ffffff", "#fff8d6", "#fff3b0", "#f7d774", "#5f4b08", "#7a6a1f", "#ffffff", "#ffffff", "#facc15", "#eab308", "#dc2626", "#fef0f1", "#fde0e3", "#16a34a", "#59FFF1A8", "#f7d774", "#ffffff"),
            "custom" => BuildCustomPalette(customAccentHex, dark),
            _ => dark
                ? new Palette("#111827", "#1f2937", "#273449", "#182230", "#374151", "#f3f4f6", "#9ca3af", "#111827", "#1f2937", "#94a3b8", "#cbd5e1", "#ef4444", "#3d171a", "#572024", "#22c55e", "#A6000000", "#374151", "#f3f4f6")
                : new Palette("#e9edf1", "#ffffff", "#f8fafc", "#fafafa", "#dfe3e8", "#202124", "#6b7280", "#ffffff", "#ffffff", "#475569", "#334155", "#dc2626", "#fef0f1", "#fde0e3", "#16a34a", "#59000000", "#dfe3e8", "#ffffff")
        };
    }
    private static Palette BuildCustomPalette(string hex, bool dark)
    {
        Color accent;
        try
        {
            string normalized = string.IsNullOrWhiteSpace(hex) ? "#FF7A00" : hex.Trim();
            if (!normalized.StartsWith("#", StringComparison.Ordinal)) normalized = "#" + normalized;
            accent = Parse(normalized);
        }
        catch
        {
            accent = Parse("#FF7A00");
        }

        string Hex(Color color) => color.ToString();
        Color Mix(Color a, Color b, double amount)
        {
            byte mix(byte x, byte y) => (byte)Math.Clamp(Math.Round(x + (y - x) * amount), 0, 255);
            return Color.FromRgb(mix(a.R, b.R), mix(a.G, b.G), mix(a.B, b.B));
        }
        Color Darken(Color c, double amount) => Mix(c, Colors.Black, amount);
        Color Lighten(Color c, double amount) => Mix(c, Colors.White, amount);

        if (dark)
        {
            Color background = Mix(accent, Colors.Black, 0.88);
            Color surface = Mix(accent, Colors.Black, 0.76);
            Color hover = Mix(accent, Colors.Black, 0.64);
            Color title = Mix(accent, Colors.Black, 0.70);
            Color border = Mix(accent, Colors.Black, 0.42);
            Color input = Mix(accent, Colors.Black, 0.82);
            Color modal = Mix(accent, Colors.Black, 0.84);
            Color primaryHover = Lighten(accent, 0.18);
            Color danger = Parse("#f87171");
            return new Palette(
                Hex(background), Hex(surface), Hex(hover), Hex(title), Hex(border),
                "#F8FAFC", "#B6C2D1", Hex(input), Hex(surface), Hex(accent), Hex(primaryHover),
                Hex(danger), "#35161B", "#4A1B21", "#4ADE80", Hex(Mix(background, Colors.Black, 0.12)),
                Hex(border), "#F8FAFC");
        }

        Color backgroundLight = Lighten(accent, 0.94);
        Color surfaceLight = Colors.White;
        Color hoverLight = Lighten(accent, 0.88);
        Color titleLight = Lighten(accent, 0.82);
        Color borderLight = Lighten(accent, 0.66);
        Color inputLight = Colors.White;
        Color primaryHoverLight = Darken(accent, 0.15);
        Color text = Darken(accent, 0.86);
        return new Palette(
            Hex(backgroundLight), Hex(surfaceLight), Hex(hoverLight), Hex(titleLight), Hex(borderLight),
            Hex(text), "#64748B", Hex(inputLight), Hex(surfaceLight), Hex(accent), Hex(primaryHoverLight),
            "#DC2626", "#FEF0F1", "#FDE0E3", "#16A34A", Hex(Mix(accent, Colors.White, 0.55)),
            Hex(borderLight), "#FFFFFF");
    }

}
