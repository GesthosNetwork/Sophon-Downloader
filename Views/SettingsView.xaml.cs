using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
namespace SophonDownloader;
public partial class SettingsView : UserControl
{
    public event Action<AppSettings>? SettingsChanged;
    public SettingsView()
    {
        InitializeComponent();
        FontFamilyComboBox.ItemsSource = Fonts.SystemFontFamilies
            .OrderBy(font => font.Source, StringComparer.OrdinalIgnoreCase)
            .ToList();
        LoadSettings();
        UpdateNetworkControlState();
    }
    public AppSettings CurrentSettings => new()
    {
        ThemePalette = (ThemePaletteComboBox.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "Default",
        ThemeMode = (ThemeModeComboBox.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "Light",
        FontFamily = (FontFamilyComboBox.SelectedItem as FontFamily)?.Source ?? "Segoe UI Variable",
        UseAria2c = UseAria2cCheckBox.IsChecked == true,
        DownloadMode = (DownloadModeComboBox.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "Parallel",
        Threads = ParseInt(ThreadsTextBox.Text, 8, 1, 64),
        MaxHttpHandle = ParseInt(MaxHttpHandleTextBox.Text, 16, 1, 256),
        SpeedLimitKbps = ParseInt(SpeedLimitTextBox.Text, 0, 0, 1024 * 1024),
        ProxyMode = (ProxyModeComboBox.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "System",
        ProxyHost = ProxyHostTextBox.Text.Trim(),
        ProxyPort = ParseInt(ProxyPortTextBox.Text, 8080, 1, 65535),
        Dns = DnsTextBox.Text.Trim(),
        LogLevel = (LogLevelComboBox.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "Debug"
    };
    private void LoadSettings()
    {
        AppSettings settings = AppSettingsStore.Load();
        SelectItem(ThemePaletteComboBox, settings.ThemePalette, "Default");
        SelectItem(ThemeModeComboBox, settings.ThemeMode, "Light");
        SelectFontFamily(settings.FontFamily);
        UseAria2cCheckBox.IsChecked = settings.UseAria2c;
        SelectItem(DownloadModeComboBox, settings.DownloadMode, "Parallel");
        ThreadsTextBox.Text = settings.Threads.ToString();
        MaxHttpHandleTextBox.Text = settings.MaxHttpHandle.ToString();
        SpeedLimitTextBox.Text = settings.SpeedLimitKbps > 0 ? settings.SpeedLimitKbps.ToString() : string.Empty;
        SelectItem(ProxyModeComboBox, settings.ProxyMode, "System");
        ProxyHostTextBox.Text = settings.ProxyHost;
        ProxyPortTextBox.Text = settings.ProxyPort.ToString();
        DnsTextBox.Text = settings.Dns;
        SelectItem(LogLevelComboBox, settings.LogLevel, "Debug");
    }
    private void FontFamilyComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded || FontFamilyComboBox.SelectedItem is null) return;
        ApplyThemeImmediately("Font updated");
    }
    private void SelectFontFamily(string value)
    {
        string target = string.IsNullOrWhiteSpace(value) ? "Segoe UI Variable" : value;
        object? match = FontFamilyComboBox.Items
            .Cast<FontFamily>()
            .FirstOrDefault(item => string.Equals(item.Source, target, StringComparison.OrdinalIgnoreCase));
        FontFamilyComboBox.SelectedItem = match ?? FontFamilyComboBox.Items
            .Cast<object>()
            .FirstOrDefault(item => string.Equals(item?.ToString(), "Segoe UI", StringComparison.OrdinalIgnoreCase))
            ?? FontFamilyComboBox.Items.Cast<FontFamily>().FirstOrDefault();
    }
    private void ThemePaletteComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded) return;
        ApplyThemeImmediately("Theme updated");
    }
    private void ThemeModeComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded) return;
        ApplyThemeImmediately("Theme updated");
    }
    private void ApplyThemeImmediately(string status = "Theme updated")
    {
        try
        {
            AppSettings settings = CurrentSettings;
            AppSettingsStore.Save(settings);
            StatusTextBlock.Text = status;
            SettingsChanged?.Invoke(settings);
            UpdateNetworkControlState();
        }
        catch (Exception ex)
        {
            StatusTextBlock.Text = $"Theme update failed: {ex.Message}";
        }
    }
    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            AppSettings settings = CurrentSettings;
            if (string.Equals(settings.ProxyMode, "Custom", StringComparison.OrdinalIgnoreCase) &&
                string.IsNullOrWhiteSpace(settings.ProxyHost))
            {
                StatusTextBlock.Text = "Custom proxy selected: enter a proxy server first.";
                ProxyHostTextBox.Focus();
                return;
            }
            AppSettingsStore.Save(settings);
            StatusTextBlock.Text = "Saved to sophon.db";
            SettingsChanged?.Invoke(settings);
            UpdateNetworkControlState();
        }
        catch (Exception ex)
        {
            StatusTextBlock.Text = $"Save failed: {ex.Message}";
        }
    }
    private void UseAria2cCheckBox_Changed(object sender, RoutedEventArgs e) => UpdateNetworkControlState();
    private void ProxyModeComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateNetworkControlState();
    private void UpdateNetworkControlState()
    {
        if (ProxyModeComboBox is null || DnsTextBox is null)
            return;
        bool ariaEnabled = UseAria2cCheckBox.IsChecked == true;
        bool customProxy = string.Equals((ProxyModeComboBox.SelectedItem as ComboBoxItem)?.Content?.ToString(), "Custom", StringComparison.OrdinalIgnoreCase);
        ProxyHostTextBox.IsEnabled = customProxy;
        ProxyPortTextBox.IsEnabled = customProxy;
        DnsTextBox.IsEnabled = ariaEnabled;
        LogLevelComboBox.IsEnabled = ariaEnabled;
    }
    private static void SelectItem(ComboBox comboBox, string value, string fallback)
    {
        string target = string.IsNullOrWhiteSpace(value) ? fallback : value;
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
