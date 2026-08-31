using System.Windows.Threading;
using Microsoft.Win32;

namespace SophonDownloader;

public partial class SettingsView : UserControl
{
    private string _backgroundImagePath = string.Empty;
    private bool _isLoadingSettings = true;
    private readonly DispatcherTimer _autoSaveTimer;
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

    public event Action<AppSettings>? SettingsChanged;

    public SettingsView()
    {
        InitializeComponent();

        FontFamilyComboBox.ItemsSource = Fonts.SystemFontFamilies
            .OrderBy(font => font.Source, StringComparer.OrdinalIgnoreCase)
            .ToList();

        _autoSaveTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(350)
        };
        _autoSaveTimer.Tick += AutoSaveTimer_Tick;

        _isLoadingSettings = true;
        LoadSettings();
        _isLoadingSettings = false;
        UpdateNetworkControlState();
    }

    public AppSettings CurrentSettings => new()
    {
        ThemePalette = (ThemePaletteComboBox.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "Default",
        ThemeMode = (ThemeModeComboBox.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "Light",
        CustomAccentHex = string.IsNullOrWhiteSpace(CustomAccentHexTextBox.Text) ? "#FF7A00" : CustomAccentHexTextBox.Text.Trim(),
        FontFamily = (FontFamilyComboBox.SelectedItem as FontFamily)?.Source ?? "Segoe UI Variable",
        UseAria2c = UseAria2cCheckBox.IsChecked == true,
        DownloadMode = (DownloadModeComboBox.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "Parallel",
        MaxConcurrentDownloads = ParseInt(MaxConcurrentDownloadsTextBox.Text, ConcurrencyDefaults.DefaultMaxConcurrentDownloads, 1, 8),
        Threads = ParseInt(ThreadsTextBox.Text, ConcurrencyDefaults.Threads, 1, 64),
        MaxHttpHandle = ParseInt(MaxHttpHandleTextBox.Text, ConcurrencyDefaults.MaxHttpConnections, 1, 256),
        SpeedLimitKbps = ParseInt(SpeedLimitTextBox.Text, 0, 0, 1024 * 1024),
        ProxyMode = (ProxyModeComboBox.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "System",
        ProxyHost = ProxyHostTextBox.Text.Trim(),
        ProxyPort = ParseInt(ProxyPortTextBox.Text, 8080, 1, 65535),
        Dns = DnsTextBox.Text.Trim(),
        BackgroundImagePath = _backgroundImagePath.Trim(),
        ShowConsole = ShowConsoleCheckBox.IsChecked == true,
        LogLevel = (LogLevelComboBox.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "Debug"
    };

    private void LoadSettings()
    {
        AppSettings settings = AppSettingsStore.Load();
        SelectItem(ThemePaletteComboBox, settings.ThemePalette, "Default");
        SelectItem(ThemeModeComboBox, settings.ThemeMode, "Light");

        CustomAccentHexTextBox.Text = settings.CustomAccentHex;
        SetCustomAccentControls(settings.CustomAccentHex);
        UpdateCustomAccentPanel();

        SelectFontFamily(settings.FontFamily);
        UseAria2cCheckBox.IsChecked = settings.UseAria2c;
        SelectItem(DownloadModeComboBox, settings.DownloadMode, "Parallel");

        MaxConcurrentDownloadsTextBox.Text = settings.MaxConcurrentDownloads.ToString();
        ThreadsTextBox.Text = settings.Threads.ToString();

        MaxHttpHandleTextBox.Text = settings.MaxHttpHandle.ToString();

        SpeedLimitTextBox.Text = settings.SpeedLimitKbps > 0
            ? settings.SpeedLimitKbps.ToString()
            : string.Empty;

        SelectItem(ProxyModeComboBox, settings.ProxyMode, "System");
        ProxyHostTextBox.Text = settings.ProxyHost;
        ProxyPortTextBox.Text = settings.ProxyPort.ToString();
        DnsTextBox.Text = settings.Dns;

        _backgroundImagePath = settings.BackgroundImagePath ?? string.Empty;
        BackgroundImageTextBox.Text = _backgroundImagePath;
        ShowConsoleCheckBox.IsChecked = settings.ShowConsole;

        SelectItem(LogLevelComboBox, settings.LogLevel, "Debug");
    }

    private void UpdateCustomAccentPanel()
    {
        bool isCustom = string.Equals(
            (ThemePaletteComboBox.SelectedItem as ComboBoxItem)?.Content?.ToString(),
            "Custom", StringComparison.OrdinalIgnoreCase);
        CustomAccentPanel.Visibility = isCustom ? Visibility.Visible : Visibility.Collapsed;
    }

    private void CustomAccentHexTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!IsLoaded || _isLoadingSettings || !TryParseHexColor(CustomAccentHexTextBox.Text, out Color color))
            return;

        SetColorControls(color);
        CustomAccentPreview.Background = new SolidColorBrush(color);
        if (string.Equals((ThemePaletteComboBox.SelectedItem as ComboBoxItem)?.Content?.ToString(), "Custom", StringComparison.OrdinalIgnoreCase))
            QueueAutoSave();
    }

    private void CustomColorSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!IsLoaded || _isLoadingSettings)
            return;

        Color color = Color.FromRgb(
            (byte)Math.Round(CustomRedSlider.Value),
            (byte)Math.Round(CustomGreenSlider.Value),
            (byte)Math.Round(CustomBlueSlider.Value));
        CustomAccentHexTextBox.Text = $"#{color.R:X2}{color.G:X2}{color.B:X2}";
        CustomAccentPreview.Background = new SolidColorBrush(color);
        UpdateCustomValueLabels();
        if (string.Equals((ThemePaletteComboBox.SelectedItem as ComboBoxItem)?.Content?.ToString(), "Custom", StringComparison.OrdinalIgnoreCase))
            QueueAutoSave();
    }

    private void SetColorControls(Color color)
    {
        CustomRedSlider.Value = color.R;
        CustomGreenSlider.Value = color.G;
        CustomBlueSlider.Value = color.B;
        UpdateCustomValueLabels();
    }

    private void SetCustomAccentControls(string hex)
    {
        if (TryParseHexColor(hex, out Color color))
        {
            SetColorControls(color);
            CustomAccentPreview.Background = new SolidColorBrush(color);
        }
    }

    private void UpdateCustomValueLabels()
    {
        CustomRedValueText.Text = ((int)Math.Round(CustomRedSlider.Value)).ToString();
        CustomGreenValueText.Text = ((int)Math.Round(CustomGreenSlider.Value)).ToString();
        CustomBlueValueText.Text = ((int)Math.Round(CustomBlueSlider.Value)).ToString();
    }

    private static bool TryParseHexColor(string value, out Color color)
    {
        color = default;
        string text = (value ?? string.Empty).Trim();
        if (!text.StartsWith("#", StringComparison.Ordinal))
            text = "#" + text;
        if (text.Length != 7 || !text.Skip(1).All(Uri.IsHexDigit))
            return false;
        try
        {
            color = (Color)ColorConverter.ConvertFromString(text);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private void QueueAutoSave()
    {
        if (_isLoadingSettings || !IsLoaded || !CanAutoSaveCurrentSettings())
            return;
        _autoSaveTimer.Stop();
        _autoSaveTimer.Start();
    }

    private void FontFamilyComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded || FontFamilyComboBox.SelectedItem is null)
        {
            return;
        }

        ApplyThemeImmediately("Font updated");
    }

    private void SelectFontFamily(string value)
    {
        string target = string.IsNullOrWhiteSpace(value)
            ? "Segoe UI Variable" : value;

        object? match =
            FontFamilyComboBox.Items
                .Cast<FontFamily>()
                .FirstOrDefault(item =>
                    string.Equals(item.Source, target, StringComparison.OrdinalIgnoreCase));

        FontFamilyComboBox.SelectedItem = match
            ?? FontFamilyComboBox.Items
                .Cast<object>()
                .FirstOrDefault(item =>
                    string.Equals(item?.ToString(), "Segoe UI", StringComparison.OrdinalIgnoreCase))
            ?? FontFamilyComboBox.Items
                .Cast<FontFamily>()
                .FirstOrDefault();
    }

    private void ThemePaletteComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded)
            return;

        UpdateCustomAccentPanel();
        ApplyThemeImmediately("Theme updated");
    }

    private void ThemeModeComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded)
            return;

        UpdateCustomAccentPanel();
        ApplyThemeImmediately("Theme updated");
    }

    private void ApplyThemeImmediately(string status = "Settings updated")
    {
        if (_isLoadingSettings)
            return;

        SaveCurrentSettings(status);
    }

    private void SaveCurrentSettings(string status = "Settings auto-saved")
    {
        try
        {
            if (!CanAutoSaveCurrentSettings())
                return;

            AppSettings settings = CurrentSettings;
            AppSettingsStore.Save(settings);
            SettingsChanged?.Invoke(settings);
            UpdateNetworkControlState();
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Auto-save failed.");
        }
    }

    private bool CanAutoSaveCurrentSettings()
    {
        return
            IsInRange(MaxConcurrentDownloadsTextBox.Text, 1, 8) &&
            IsInRange(ThreadsTextBox.Text, 1, 64) &&
            IsInRange(MaxHttpHandleTextBox.Text, 1, 256) &&
            IsInRange(SpeedLimitTextBox.Text, 0, 1024 * 1024, allowEmpty: true) &&
            IsInRange(ProxyPortTextBox.Text, 1, 65535) &&
            TryParseHexColor(CustomAccentHexTextBox.Text, out _);
    }

    private static bool IsInRange(
        string text, int min, int max, bool allowEmpty = false)
    {
        string value = text.Trim();

        if (allowEmpty && value.Length == 0)
            return true;

        return int.TryParse(value, out int parsed) &&
               parsed >= min &&
               parsed <= max;
    }

    public void FlushPendingAutoSave()
    {
        if (_isLoadingSettings)
            return;

        _autoSaveTimer.Stop();
        SaveCurrentSettings("Settings saved before shutdown");
    }

    private void AutoSaveTimer_Tick(object? sender, EventArgs e)
    {
        _autoSaveTimer.Stop();
        SaveCurrentSettings();
    }

    private void BrowseBackgroundButton_Click(
        object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Choose background image",
            Filter = "Image files|*.png;*.jpg;*.jpeg;*.bmp;*.gif|All files|*.*",
            CheckFileExists = true,
            Multiselect = false
        };

        if (dialog.ShowDialog() != true)
            return;

        _backgroundImagePath = dialog.FileName;
        BackgroundImageTextBox.Text = _backgroundImagePath;
        ApplyThemeImmediately("Background updated");
    }

    private void ClearBackgroundButton_Click(
        object sender, RoutedEventArgs e)
    {
        _backgroundImagePath = string.Empty;
        BackgroundImageTextBox.Clear();
        ApplyThemeImmediately("Background cleared");
    }

    private void UseAria2cCheckBox_Changed(
        object sender, RoutedEventArgs e)
    {
        UpdateNetworkControlState();
        if (!_isLoadingSettings && IsLoaded)
            SaveCurrentSettings();
    }

    private void DownloadModeComboBox_SelectionChanged(
        object sender, SelectionChangedEventArgs e)
    {
        if (!_isLoadingSettings && IsLoaded)
            SaveCurrentSettings();
    }

    private void ProxyModeComboBox_SelectionChanged(
        object sender, SelectionChangedEventArgs e)
    {
        UpdateNetworkControlState();
        if (!_isLoadingSettings && IsLoaded)
            SaveCurrentSettings();
    }

    private void LogLevelComboBox_SelectionChanged(
        object sender, SelectionChangedEventArgs e)
    {
        if (!_isLoadingSettings && IsLoaded)
            SaveCurrentSettings();
    }

    private void ShowConsoleCheckBox_Changed(
        object sender, RoutedEventArgs e)
    {
        if (!_isLoadingSettings && IsLoaded)
            SaveCurrentSettings();
    }

    private void AutoSaveTextBox_TextChanged(
        object sender, TextChangedEventArgs e)
    {
        QueueAutoSave();
    }

    private void UpdateNetworkControlState()
    {
        if (ProxyModeComboBox is null || DnsTextBox is null)
        {
            return;
        }

        bool ariaEnabled = UseAria2cCheckBox.IsChecked == true;
        bool customProxy = string.Equals(
            (ProxyModeComboBox.SelectedItem as ComboBoxItem)
                ?.Content ?.ToString(), "Custom", StringComparison.OrdinalIgnoreCase);

        ProxyHostTextBox.IsEnabled = customProxy;
        ProxyPortTextBox.IsEnabled = customProxy;
        DnsTextBox.IsEnabled = ariaEnabled;
        LogLevelComboBox.IsEnabled = true;
    }

    private static void SelectItem(ComboBox comboBox, string value, string fallback)
    {
        string target = string.IsNullOrWhiteSpace(value)
            ? fallback
            : value;

        foreach (object item in comboBox.Items)
        {
            if (item is ComboBoxItem comboItem &&
                string.Equals(comboItem.Content?.ToString(), target, StringComparison.OrdinalIgnoreCase))
            {
                comboBox.SelectedItem = comboItem;
                return;
            }
        }

        foreach (object item in comboBox.Items)
        {
            if (item is ComboBoxItem comboItem &&
                string.Equals(comboItem.Content?.ToString(), fallback, StringComparison.OrdinalIgnoreCase))
            {
                comboBox.SelectedItem = comboItem;
                return;
            }
        }
    }

    private static int ParseInt(string text, int fallback, int min, int max)
    {
        return int.TryParse(text.Trim(), out int value)
            ? Math.Clamp(value, min, max)
            : fallback;
    }
}
