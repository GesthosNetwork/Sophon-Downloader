using Microsoft.Win32;
using System.Net.NetworkInformation;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using SophonDownloader.Models;
using SophonDownloader.Services;
using SophonDownloader.Utilities;

namespace SophonDownloader;

public partial class MainWindow : Window
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();
    private Brush AccentBrush => (Brush)Resources["AccentBrush"];
    private Brush SideNavInactiveBrush => (Brush)Resources["PanelBrush"];
    private Brush SideNavActiveBrush => (Brush)Resources["AccentBrush"];

    private enum DownloaderMode { Legacy, Sophon }
    private enum LegacyDownloadMode { Full, PatchDownload }
    private enum SophonDownloadMode { Full, Patch }
    private enum MainPage { Dashboard, Downloads, Settings, Updates, About, License }

    private readonly LegacyManifestService _legacyManifestService = new();
    private readonly SophonDownloadService _sophonDownloadService = new();
    private readonly HttpClient _connectivityClient = new() { Timeout = TimeSpan.FromSeconds(3) };
    private readonly DispatcherTimer _connectivityTimer;
    private readonly DispatcherTimer _distributionNoticeTimer;

    private CancellationTokenSource? _connectivityCts;
    private CancellationTokenSource? _legacyManifestCts;
    private CancellationTokenSource? _sophonManifestCts;

    private bool _distributionNoticeShowing;
    private int _distributionNoticeMarqueeRuns;

    private List<GameOption> _legacyGames = [];
    private LegacyManifest _legacyManifest = new();
    private DownloaderMode _mode = DownloaderMode.Sophon;
    private LegacyDownloadMode _legacyDownloadMode = LegacyDownloadMode.Full;
    private SophonDownloadMode _sophonDownloadMode = SophonDownloadMode.Full;
    private List<string> _sophonVersions = [];
    private MainPage _currentPage = MainPage.Dashboard;
    private GameInfo? _currentSophonGame;
    private List<SophonContentOption> _sophonContentOptions = [];
    private List<LegacyContentOption> _legacyContentOptions = [];

    private AppSettings? _lastSettingsSnapshot;
    private string? _lastAppliedLogLevel;
    private bool? _lastAppliedShowConsole;

    private bool _initializing;
    private bool _legacyManifestLoading;
    private bool _sophonDestinationCustomized;
    private bool _legacyContentUpdating;
    private bool _exploreActive;
    private MainPage _pageBeforeExplore = MainPage.Dashboard;

    private string DefaultDownloadDirectory => Utility.GetApplicationDirectory();

    public string GetSophonDestinationDirectory() => SophonDestinationTextBox.Text.Trim();

    private static string DecodeDistributionMarqueeNotice()
    {
        const string encoded = "U29waG9uIERvd25sb2FkZXIgaXMgZnJlZSBzb2Z0d2FyZS4gV2UgZG9uJ3Qgc2VsbCBpdC4gSWYgc29tZW9uZSBjaGFyZ2VzIHlvdSBmb3IgaXQsIGl0J3MgYSBzY2FtISBCZXdhcmUh";
        return System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(encoded));
    }

    public MainWindow()
    {
        InitializeComponent();
        DistributionNoticeMarqueeText.Text = DecodeDistributionMarqueeNotice();

        AppVersionText.Text = $"• v{App.Version}";
        SidePanelVersionText.Text = $"v{App.Version}";
        CopyrightTextBlock.Text = App.Copyright;

        _connectivityTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(10) };
        _connectivityTimer.Tick += ConnectivityTimer_Tick;

        _distributionNoticeTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(10) };
        _distributionNoticeTimer.Tick += DistributionNoticeTimer_Tick;
        NetworkChange.NetworkAvailabilityChanged += NetworkAvailabilityChanged;
        Loaded += MainWindow_Loaded;
        Closing += MainWindow_Closing;
        SizeChanged += MainWindow_SizeChanged;

        SettingsView.SettingsChanged += SettingsView_SettingsChanged;
        AppSettings initialSettings = AppSettingsStore.Load();
        ApplySettingsTheme(initialSettings);
        _lastAppliedLogLevel = initialSettings.LogLevel;
        _lastAppliedShowConsole = initialSettings.ShowConsole;
        _lastSettingsSnapshot = initialSettings;

        Logger.Info("MainWindow initialized.");
    }

    private void SettingsView_SettingsChanged(AppSettings settings)
    {
        ApplySettingsTheme(settings);
        StatusFromSettings(settings);
        DownloadsView.RefreshScheduler();
    }

    private void StatusFromSettings(AppSettings settings)
    {
        AppSettings? previous = _lastSettingsSnapshot;

        if (previous is null || !string.Equals(previous.ThemePalette, settings.ThemePalette, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(previous.ThemeMode, settings.ThemeMode, StringComparison.OrdinalIgnoreCase))
        {
            Logger.Debug($"Theme changed to {settings.ThemePalette}/{settings.ThemeMode}.");
        }

        if (previous is null || !string.Equals(previous.FontFamily, settings.FontFamily, StringComparison.OrdinalIgnoreCase))
            Logger.Debug("Font changed.");

        if (previous is null || previous.UseAria2c != settings.UseAria2c)
            Logger.Debug($"Aria2c changed to {(settings.UseAria2c ? "Enabled" : "Disabled")}.");

        if (previous is null || !string.Equals(previous.DownloadMode, settings.DownloadMode, StringComparison.OrdinalIgnoreCase))
            Logger.Debug($"Download mode changed to {settings.DownloadMode}.");

        if (previous is null || previous.Threads != settings.Threads)
            Logger.Debug($"Threads changed to {settings.Threads}.");

        if (previous is null || previous.MaxHttpHandle != settings.MaxHttpHandle)
            Logger.Debug($"HTTP connection limit changed to {settings.MaxHttpHandle}.");

        if (previous is null || previous.SpeedLimitKbps != settings.SpeedLimitKbps)
            Logger.Debug($"Speed limit changed to {(settings.SpeedLimitKbps > 0 ? $"{settings.SpeedLimitKbps} KB/s" : "Unlimited")}.");

        if (previous is null || !string.Equals(previous.ProxyMode, settings.ProxyMode, StringComparison.OrdinalIgnoreCase))
            Logger.Debug($"Proxy mode changed to {settings.ProxyMode}.");

        if (previous is null || !string.Equals(previous.ProxyHost, settings.ProxyHost, StringComparison.Ordinal))
            Logger.Debug($"Proxy host changed to \"{settings.ProxyHost}\".");

        if (previous is null || previous.ProxyPort != settings.ProxyPort)
            Logger.Debug($"Proxy port changed to \"{settings.ProxyPort}\".");

        if (previous is null || !string.Equals(previous.Dns, settings.Dns, StringComparison.Ordinal))
            Logger.Debug($"DNS server changed to \"{settings.Dns}\".");

        if (previous is null || !string.Equals(previous.BackgroundImagePath, settings.BackgroundImagePath, StringComparison.Ordinal))
            Logger.Debug($"Background image {(string.IsNullOrWhiteSpace(settings.BackgroundImagePath) ? "cleared" : "changed")}.");

        if (previous is null || previous.ShowConsole != settings.ShowConsole)
        {
            App.ApplyConsoleSetting(settings.ShowConsole);
            Logger.Info($"Show Console changed to {(settings.ShowConsole ? "On" : "Off")}.");
            _lastAppliedShowConsole = settings.ShowConsole;
        }

        if (previous is null || !string.Equals(previous.LogLevel, settings.LogLevel, StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                LogManager.GlobalThreshold = NLog.LogLevel.Debug;
                Logger.Info($"Log level changed to \"{settings.LogLevel}\".");
            }
            catch (Exception ex)
            {
                try { Logger.Warn(ex, "Failed to record log-level change event."); } catch {}
            }

            App.ApplyLogLevel(settings.LogLevel);
            _lastAppliedLogLevel = settings.LogLevel;
        }

        _lastSettingsSnapshot = settings;
    }

    private void ApplySettingsTheme(AppSettings settings)
    {
        ThemeManager.Apply(this, settings);
        ApplyModeButtonVisuals();
        SetMainPage(_currentPage);
    }

    internal void ApplyThemeSurfaces(string background, string surface, string? backgroundImagePath = null)
    {
        Brush backgroundBrush = CreateBackgroundBrush(background, backgroundImagePath);
        FullWindowBackgroundBorder.Background = backgroundBrush;
        RootContentBorder.Background = Brushes.Transparent;
        SidebarBorder.Background = Brushes.Transparent;
    }

    private static Brush CreateBackgroundBrush(string background, string? imagePath)
    {
        if (!string.IsNullOrWhiteSpace(imagePath) && File.Exists(imagePath))
        {
            try
            {
                var image = new BitmapImage();
                image.BeginInit();
                image.UriSource = new Uri(imagePath, UriKind.Absolute);
                image.CacheOption = BitmapCacheOption.OnLoad;
                image.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
                image.EndInit();
                image.Freeze();
                var brush = new ImageBrush(image)
                {
                    Stretch = Stretch.UniformToFill,
                    AlignmentX = AlignmentX.Center,
                    AlignmentY = AlignmentY.Center
                };
                brush.Freeze();
                return brush;
            }
            catch (Exception ex)
            {
                Logger.Warn(ex, $"Unable to load background image: {imagePath}");
            }
        }
        return new SolidColorBrush((Color)ColorConverter.ConvertFromString(background));
    }

    
    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        _initializing = true;
        UpdateRootClip();

        string directory = DefaultDownloadDirectory;
        LegacyDestinationTextBox.Text = directory;
        SophonDestinationTextBox.Text = directory;
        _sophonDestinationCustomized = false;

        LegacyPatchSourcePanel.Visibility = Visibility.Collapsed;
        LegacyExplorerButton.Visibility = Visibility.Collapsed;
        SophonExplorerButton.Visibility = Visibility.Collapsed;

        _connectivityTimer.Start();

        StartupLoadingStatus.Text = "Checking network connection...";
        await UpdateInternetStatusAsync();

        StartupLoadingStatus.Text = "Loading remote game manifest...";
        await LoadLegacyGamesAsync();

        StartupLoadingStatus.Text = "Starting interface...";
        await Task.Delay(250);

        _initializing = false;
        SetMode(DownloaderMode.Sophon);
        SetMainPage(MainPage.Dashboard);
        StartupOverlay.Visibility = Visibility.Collapsed;

        ShowDistributionNoticeMarquee();
    }

    private void MainWindow_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        UpdateRootClip();
    }

    private void DistributionNoticeTimer_Tick(object? sender, EventArgs e)
    {
        _distributionNoticeTimer.Stop();

        if (_shutdownCleanupStarted)
            return;

        ShowDistributionNoticeMarquee();
    }

    private void ShowDistributionNoticeMarquee()
    {
        if (_shutdownCleanupStarted || _distributionNoticeShowing)
            return;

        _distributionNoticeShowing = true;
        _distributionNoticeMarqueeRuns = 0;
        DistributionNoticeRow.Height = new GridLength(28);
        DistributionNoticeMarqueeBorder.Visibility = Visibility.Visible;

        Dispatcher.BeginInvoke(DispatcherPriority.Render, new Action(StartDistributionNoticeMarqueeRun));
    }

    private void StartDistributionNoticeMarqueeRun()
    {
        if (_shutdownCleanupStarted || !_distributionNoticeShowing)
            return;

        double viewportWidth = DistributionNoticeMarqueeBorder.ActualWidth;
        double textWidth = DistributionNoticeMarqueeText.ActualWidth;

        if (viewportWidth <= 0 || textWidth <= 0)
        {
            Dispatcher.BeginInvoke(DispatcherPriority.Render, new Action(StartDistributionNoticeMarqueeRun));
            return;
        }

        DistributionNoticeMarqueeTransform.BeginAnimation(TranslateTransform.XProperty, null);
        DistributionNoticeMarqueeTransform.X = viewportWidth;

        double travelDistance = viewportWidth + textWidth;
        double pixelsPerSecond = 90.0;
        double durationSeconds = Math.Clamp(travelDistance / pixelsPerSecond, 8.0, 28.0);

        var animation = new DoubleAnimation
        {
            From = viewportWidth,
            To = -textWidth,
            Duration = TimeSpan.FromSeconds(durationSeconds),
            AutoReverse = false
        };

        animation.Completed += DistributionNoticeMarqueeRunCompleted;
        DistributionNoticeMarqueeTransform.BeginAnimation(
            TranslateTransform.XProperty,
            animation,
            HandoffBehavior.SnapshotAndReplace);
    }

    private void DistributionNoticeMarqueeRunCompleted(object? sender, EventArgs e)
    {
        if (sender is AnimationClock clock)
            clock.Completed -= DistributionNoticeMarqueeRunCompleted;

        if (_shutdownCleanupStarted || !_distributionNoticeShowing)
            return;

        _distributionNoticeMarqueeRuns++;

        if (_distributionNoticeMarqueeRuns < 3)
        {
            Dispatcher.BeginInvoke(
                DispatcherPriority.Render,
                new Action(StartDistributionNoticeMarqueeRun));
            return;
        }

        DistributionNoticeMarqueeTransform.BeginAnimation(TranslateTransform.XProperty, null);
        DistributionNoticeMarqueeTransform.X = 0;
        _distributionNoticeShowing = false;
        DistributionNoticeMarqueeBorder.Visibility = Visibility.Collapsed;
        DistributionNoticeRow.Height = new GridLength(0);
        _distributionNoticeTimer.Start();
    }

    private void UpdateRootClip()
    {
        if (RootContentBorder.ActualWidth <= 0 || RootContentBorder.ActualHeight <= 0)
            return;

        const double radius = 13;
        RootContentBorder.Clip = new RectangleGeometry(
            new Rect(0, 0, RootContentBorder.ActualWidth, RootContentBorder.ActualHeight), radius, radius);
    }

    private bool _shutdownCleanupStarted;
    internal bool IsShuttingDown => _shutdownCleanupStarted;

    private void MainWindow_Closing(object? sender, CancelEventArgs e)
    {
        if (_shutdownCleanupStarted)
            return;

        _shutdownCleanupStarted = true;
        Logger.Info("MainWindow closing. Performing hard shutdown.");

        try { SettingsView.FlushPendingAutoSave(); }
        catch (Exception ex)
        {
            Logger.Error(ex, "Failed to flush pending settings during hard shutdown.");
        }

        try
        {
            NetworkChange.NetworkAvailabilityChanged -= NetworkAvailabilityChanged;
            _connectivityTimer.Stop();
            _distributionNoticeTimer.Stop();
            DistributionNoticeMarqueeTransform.BeginAnimation(TranslateTransform.XProperty, null);
            _distributionNoticeShowing = false;
        }
        catch {}

        try { DownloadsView.HardStopDownloads(); } catch {}

        CancelDispose(ref _connectivityCts);
        CancelDispose(ref _legacyManifestCts);
        CancelDispose(ref _sophonManifestCts);

        try { _sophonDownloadService.Dispose(); } catch {}
        try { _connectivityClient.Dispose(); } catch {}
        try { KillAria2Processes(); } catch {}

        Environment.Exit(0);
    }

    private static void KillAria2Processes() => Aria2c.KillAllProcesses();

    private static void CancelDispose(ref CancellationTokenSource? cts)
    {
        cts?.Cancel();
        cts?.Dispose();
        cts = null;
    }

    private void DashboardNavButton_Click(object sender, RoutedEventArgs e) => SetMainPage(MainPage.Dashboard);
    private void DownloadsNavButton_Click(object sender, RoutedEventArgs e) => SetMainPage(MainPage.Downloads);
    private void SettingsNavButton_Click(object sender, RoutedEventArgs e) => SetMainPage(MainPage.Settings);

    private void CheckForUpdatesButton_Click(object sender, RoutedEventArgs e) => SetMainPage(MainPage.Updates);
    private void AboutButton_Click(object sender, RoutedEventArgs e) => SetMainPage(MainPage.About);
    private void LicenseButton_Click(object sender, RoutedEventArgs e) => SetMainPage(MainPage.License);

    private void ApplyNavigationState(Button button, bool active)
    {
        button.Background = active ? SideNavActiveBrush : SideNavInactiveBrush;
        button.Foreground = active
            ? (Brush)Resources["HeaderForegroundBrush"]
            : (Brush)Resources["SecondaryTextBrush"];
    }
    private void EnterExplore(UserControl explorer)
    {
        ArgumentNullException.ThrowIfNull(explorer);
        _pageBeforeExplore = _currentPage;
        _exploreActive = true;
        ExploreHost.Child = explorer;
        ExploreHost.Visibility = Visibility.Visible;
    }

    internal void CloseExplore()
    {
        if (!_exploreActive)
            return;

        MainPage returnPage = _currentPage;
        _exploreActive = false;
        ExploreHost.Child = null;
        ExploreHost.Visibility = Visibility.Collapsed;
        SetMainPage(returnPage);
    }

    private void SetMainPage(MainPage page)
    {
        _currentPage = page;
        bool dashboard = page == MainPage.Dashboard;

        DownloadsView.Visibility = page == MainPage.Downloads ? Visibility.Visible : Visibility.Collapsed;
        SettingsView.Visibility = page == MainPage.Settings ? Visibility.Visible : Visibility.Collapsed;
        AboutView.Visibility = page == MainPage.About ? Visibility.Visible : Visibility.Collapsed;
        LicenseView.Visibility = page == MainPage.License ? Visibility.Visible : Visibility.Collapsed;
        UpdateView.Visibility = page == MainPage.Updates ? Visibility.Visible : Visibility.Collapsed;
        LegacyView.Visibility = dashboard && _mode == DownloaderMode.Legacy ? Visibility.Visible : Visibility.Collapsed;
        SophonView.Visibility = dashboard && _mode == DownloaderMode.Sophon ? Visibility.Visible : Visibility.Collapsed;

        ApplyNavigationState(DashboardNavButton, dashboard);
        ApplyNavigationState(DownloadsNavButton, page == MainPage.Downloads);
        ApplyNavigationState(SettingsNavButton, page == MainPage.Settings);
        ApplyNavigationState(UpdatesNavButton, page == MainPage.Updates);
        ApplyNavigationState(AboutNavButton, page == MainPage.About);
        ApplyNavigationState(LicenseNavButton, page == MainPage.License);
    }

    private void ReloadButton_Click(object sender, RoutedEventArgs e) => _ = ReloadCurrentModeAsync();

    private async Task ShowReloadOverlayAsync()
    {
        StartupLoadingTitle.Text = "Reloading SophonDownloader...";
        StartupLoadingStatus.Text = "Refreshing remote game manifest...";

        StartupOverlay.Visibility = Visibility.Visible;
        StartupOverlay.BeginAnimation(UIElement.OpacityProperty, null);
        StartupLoadingScaleTransform.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        StartupLoadingScaleTransform.BeginAnimation(ScaleTransform.ScaleYProperty, null);

        await Dispatcher.InvokeAsync(() => {}, System.Windows.Threading.DispatcherPriority.Render);

        StartupOverlay.Opacity = 0.0;
        StartupLoadingScaleTransform.ScaleX = 0.96;
        StartupLoadingScaleTransform.ScaleY = 0.96;

        var easing = new CubicEase { EasingMode = EasingMode.EaseOut };
        var fadeIn = new DoubleAnimation(0.0, 0.96, TimeSpan.FromMilliseconds(260))
        {
            EasingFunction = easing,
            FillBehavior = FillBehavior.HoldEnd
        };
        var scaleInX = new DoubleAnimation(0.96, 1.0, TimeSpan.FromMilliseconds(320))
        {
            EasingFunction = easing,
            FillBehavior = FillBehavior.HoldEnd
        };
        var scaleInY = new DoubleAnimation(0.96, 1.0, TimeSpan.FromMilliseconds(320))
        {
            EasingFunction = easing,
            FillBehavior = FillBehavior.HoldEnd
        };

        StartupOverlay.BeginAnimation(UIElement.OpacityProperty, fadeIn);
        StartupLoadingScaleTransform.BeginAnimation(ScaleTransform.ScaleXProperty, scaleInX);
        StartupLoadingScaleTransform.BeginAnimation(ScaleTransform.ScaleYProperty, scaleInY);
        await Task.Delay(330);
    }

    private async Task HideReloadOverlayAsync()
    {
        var easing = new CubicEase { EasingMode = EasingMode.EaseIn };
        var fadeOut = new DoubleAnimation(0.96, 0.0, TimeSpan.FromMilliseconds(180))
        {
            EasingFunction = easing
        };

        StartupOverlay.BeginAnimation(UIElement.OpacityProperty, fadeOut);
        await Task.Delay(190);
        StartupOverlay.BeginAnimation(UIElement.OpacityProperty, null);
        StartupOverlay.Opacity = 0.96;
        StartupOverlay.Visibility = Visibility.Collapsed;
        StartupLoadingTitle.Text = "Initializing SophonDownloader...";
        StartupLoadingStatus.Text = "Preparing application startup...";
    }

    private async Task ReloadCurrentModeAsync()
    {
        ReloadButton.IsEnabled = false;
        await ShowReloadOverlayAsync();

        try
        {
            if (_mode == DownloaderMode.Legacy)
            {
                string? selectedGameCode = GameComboBox.SelectedItem is GameOption selectedGame ? selectedGame.Code : null;
                string? selectedVersion = VersionComboBox.SelectedItem as string;

                await LoadLegacyGamesAsync(true, selectedGameCode, selectedVersion);
                RefreshLegacyVersion();
            }
            else
            {
                CancelDispose(ref _sophonManifestCts);
                await RefreshSophonSelectionAsync();
            }
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Manifest reload failed.");
            Error($"Unable to reload manifest data.\n\n{ex.Message}", "Reload Error");
        }
        finally
        {
            await HideReloadOverlayAsync();
            ReloadButton.IsEnabled = !_legacyManifestLoading;
        }
    }

    private void SophonModeButton_Click(object sender, RoutedEventArgs e)
    {
        SetMainPage(MainPage.Dashboard);
        SetMode(DownloaderMode.Sophon);
    }

    private void LegacyModeButton_Click(object sender, RoutedEventArgs e)
    {
        if (_legacyManifestLoading)
            return;

        SetMainPage(MainPage.Dashboard);
        SetMode(DownloaderMode.Legacy);
    }

    private void ApplyModeButtonVisuals()
    {
        bool legacy = _mode == DownloaderMode.Legacy;

        AppTitleText.Text = "SOPHON DOWNLOADER";

        SophonModeButton.Background = legacy
            ? (Brush)Resources["ModeButtonBackgroundBrush"]
            : (Brush)Resources["PrimaryBrush"];
        LegacyModeButton.Background = legacy
            ? (Brush)Resources["PrimaryBrush"]
            : (Brush)Resources["ModeButtonBackgroundBrush"];

        SophonModeButton.Foreground = legacy
            ? (Brush)Resources["PrimaryTextBrush"]
            : (Brush)Resources["InverseTextBrush"];
        LegacyModeButton.Foreground = legacy
            ? (Brush)Resources["InverseTextBrush"]
            : (Brush)Resources["PrimaryTextBrush"];
    }

    private void SetMode(DownloaderMode mode)
    {
        _mode = mode;
        bool legacy = mode == DownloaderMode.Legacy;

        if (_currentPage == MainPage.Dashboard)
        {
            LegacyView.Visibility = legacy ? Visibility.Visible : Visibility.Collapsed;
            SophonView.Visibility = legacy ? Visibility.Collapsed : Visibility.Visible;
        }

        ApplyModeButtonVisuals();

        if (_initializing)
            return;

        if (legacy)
        {
            RefreshLegacyGame();
        }
        else
        {
            RefreshSophonGames();
            SophonExplorerButton.Visibility = _sophonContentOptions.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        ReloadButton.IsEnabled = !_legacyManifestLoading;
    }

    private async void ConnectivityTimer_Tick(object? sender, EventArgs e) => await UpdateInternetStatusAsync();

    private async void NetworkAvailabilityChanged(object? sender, NetworkAvailabilityEventArgs e) =>
        await Dispatcher.InvokeAsync(UpdateInternetStatusAsync);

    private async Task UpdateInternetStatusAsync()
    {
        CancelDispose(ref _connectivityCts);
        _connectivityCts = new CancellationTokenSource();

        CancellationToken ct = _connectivityCts.Token;
        bool available = NetworkInterface.GetIsNetworkAvailable() && await HasInternetAccessAsync(ct);

        if (ct.IsCancellationRequested)
            return;

        Brush brush = FindResource(available ? "OnlineBrush" : "OfflineBrush") as Brush ?? Brushes.Green;
        SidebarConnectionIndicator.Fill = brush;
        SidebarConnectionText.Text = available ? "Online" : "Offline";
    }

    private async Task<bool> HasInternetAccessAsync(CancellationToken ct)
    {
        string[] urls =
        [
            "https://www.msftconnecttest.com/connecttest.txt",
            "https://clients3.google.com/generate_204"
        ];

        foreach (string url in urls)
        {
            try
            {
                using HttpResponseMessage response = await _connectivityClient.GetAsync(
                    url, HttpCompletionOption.ResponseHeadersRead, ct);

                if ((int)response.StatusCode is >= 200 and < 400)
                    return true;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return false;
            }
            catch {}
        }

        return false;
    }

    private async Task LoadLegacyGamesAsync(bool preserveSelection = false, string? selectedGameCode = null, string? selectedVersion = null)
    {
        CancelDispose(ref _legacyManifestCts);
        _legacyManifestCts = new CancellationTokenSource();

        CancellationToken ct = _legacyManifestCts.Token;
        _legacyManifestLoading = true;
        ReloadButton.IsEnabled = false;

        try
        {
            List<GameOption> loadedGames = await _legacyManifestService.LoadGamesAsync(ct);
            _legacyGames = loadedGames ?? [];

            if (ct.IsCancellationRequested)
                return;

            GameComboBox.ItemsSource = _legacyGames;

            if (_legacyGames.Count == 0)
            {
                VersionComboBox.ItemsSource = null;
                LegacyContentItemsControl.ItemsSource = null;
                LegacyExplorerButton.Visibility = Visibility.Collapsed;
                return;
            }

            int gameIndex = 0;

            if (preserveSelection && !string.IsNullOrWhiteSpace(selectedGameCode))
            {
                int foundIndex = _legacyGames.FindIndex(game =>
                    string.Equals(game.Code, selectedGameCode, StringComparison.OrdinalIgnoreCase));

                if (foundIndex >= 0)
                    gameIndex = foundIndex;
            }

            GameComboBox.SelectedIndex = gameIndex;

            if (preserveSelection && !string.IsNullOrWhiteSpace(selectedVersion))
                VersionComboBox.SelectedItem = selectedVersion;
        }
        catch (OperationCanceledException) {}
        catch (Exception ex)
        {
            Logger.Error(ex, "Failed to load online Legacy games.");
            GameComboBox.ItemsSource = null;
            VersionComboBox.ItemsSource = null;
            LegacyContentItemsControl.ItemsSource = null;
            LegacyExplorerButton.Visibility = Visibility.Collapsed;

            Error($"Unable to load the online Legacy manifest.\n\n{ex.Message}", "Manifest Error");
        }
        finally
        {
            _legacyManifestLoading = false;
            ReloadButton.IsEnabled = true;
        }
    }

    private void GameComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (IsInitialized && _mode == DownloaderMode.Legacy)
            RefreshLegacyGame();
    }

    private void RefreshLegacyGame()
    {
        if (GameComboBox.SelectedItem is not GameOption game)
        {
            _legacyManifest = new LegacyManifest();
            VersionComboBox.ItemsSource = null;
            LegacyContentItemsControl.ItemsSource = null;
            LegacyExplorerButton.Visibility = Visibility.Collapsed;
            LegacyGameTitleText.Text = string.Empty;
            LegacyContentSummaryText.Text = "No content available";
            return;
        }

        _legacyManifest = game.Manifest;
        LegacyGameTitleText.Text = game.Name ?? string.Empty;

        List<string> versions = ManifestResolver.GetSortedVersions(_legacyManifest) ?? [];
        VersionComboBox.ItemsSource = versions;

        if (versions.Count > 0)
            VersionComboBox.SelectedIndex = 0;
        else
        {
            LegacyContentItemsControl.ItemsSource = null;
            LegacyExplorerButton.Visibility = Visibility.Collapsed;
            LegacyContentSummaryText.Text = "No content available";
        }
    }

    private void VersionComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (IsInitialized && _mode == DownloaderMode.Legacy)
            RefreshLegacyVersion();
    }

    private void RefreshLegacyVersion()
    {
        if (VersionComboBox.SelectedItem is not string version)
        {
            ClearLegacyVersionContent();
            UpdateLegacyAvailabilityUI();
            UpdateLegacyPatchSourceVersions();
            return;
        }

        LegacyVersion? entry = ManifestResolver.GetVersion(_legacyManifest, version);

        if (entry is null)
        {
            ClearLegacyVersionContent();
            UpdateLegacyAvailabilityUI();
            UpdateLegacyPatchSourceVersions();
            return;
        }

        UpdateLegacyContentOptions(entry);
        UpdateLegacyAvailabilityUI();
        UpdateLegacyPatchSourceVersions();
        UpdateLegacyExplorerVisibility();
    }

    private void ClearLegacyVersionContent()
    {
        LegacyContentItemsControl.ItemsSource = null;
        LegacyExplorerButton.Visibility = Visibility.Collapsed;
        LegacyContentSummaryText.Text = "No content available";
    }

    private void UpdateLegacyContentOptions(LegacyVersion version)
    {
        _legacyContentUpdating = true;

        try
        {
            var options = new List<LegacyContentOption>();
            bool full = _legacyDownloadMode == LegacyDownloadMode.Full;

            if (full)
            {
                if (ManifestResolver.HasGameFull(version))
                {
                    options.Add(new LegacyContentOption
                    {
                        Code = "game",
                        Name = "Game",
                        IsGame = true,
                        IsSelected = _legacyContentOptions.Any(x => x.IsGame && x.IsSelected)
                    });
                }
            }
            else if (ManifestResolver.HasGameUpdate(version))
            {
                options.Add(new LegacyContentOption
                {
                    Code = "game",
                    Name = "Game",
                    IsGame = true,
                    IsSelected = _legacyContentOptions.Any(x => x.IsGame && x.IsSelected)
                });
            }

            foreach (VoiceOption voice in ManifestResolver.BuildVoiceOptions(version) ?? [])
            {
                string voiceCode = voice.Code ?? string.Empty;

                if (string.IsNullOrWhiteSpace(voiceCode))
                    continue;

                bool valid;

                if (full)
                {
                    try
                    {
                        string? url = ManifestResolver.BuildFullVoiceUrl(version, voiceCode);
                        valid = ManifestResolver.HasValidUrl(url);
                    }
                    catch { valid = false; }
                }
                else
                {
                    valid = version.Update.Values.Any(update =>
                        update.Voice.TryGetValue(voiceCode, out LegacyPackage? package) &&
                        package is not null && ManifestResolver.HasValidUrl(package.Url));
                }

                if (!valid)
                    continue;

                string displayName = NormalizeLegacyVoiceName(voice);
                bool wasSelected = _legacyContentOptions.Any(option =>
                    !option.IsGame &&
                    string.Equals(option.Code, voiceCode, StringComparison.OrdinalIgnoreCase) &&
                    option.IsSelected);

                options.Add(new LegacyContentOption
                {
                    Code = voiceCode,
                    Name = displayName,
                    IsGame = false,
                    IsSelected = wasSelected
                });
            }

            if (options.Count > 0 && !options.Any(x => x.IsSelected))
                options[0].IsSelected = true;

            _legacyContentOptions = options;
            LegacyContentItemsControl.ItemsSource = _legacyContentOptions;
            UpdateLegacyContentSummary();
        }
        finally
        {
            _legacyContentUpdating = false;
        }
    }

    private static string NormalizeLegacyVoiceName(VoiceOption voice)
    {
        string name = voice.Name?.Trim() ?? string.Empty;
        string code = voice.Code ?? string.Empty;

        if (name.EndsWith(".7z", StringComparison.OrdinalIgnoreCase))
            name = name[..^3].TrimEnd();

        if (name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            name = name[..^4].TrimEnd();

        return code.Equals("zh-tw", StringComparison.OrdinalIgnoreCase) ? "Chinese (TW)" : name;
    }

    private void LegacyContentCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsInitialized || _legacyContentUpdating)
            return;

        UpdateLegacyContentSummary();
        UpdateLegacyPatchSourceVersions();
        UpdateLegacyExplorerVisibility();
    }

    private void UpdateLegacyContentSummary()
    {
        List<LegacyContentOption> selected = _legacyContentOptions.Where(x => x.IsSelected).ToList();

        LegacyContentSummaryText.Text = selected.Count switch
        {
            0 => "No content selected",
            1 => $"{selected[0].Name} selected",
            _ => $"{selected.Count} content items selected"
        };
    }

    private List<LegacyContentOption> GetSelectedLegacyContentOptions() =>
        _legacyContentOptions.Where(x => x.IsSelected).ToList();

    private void LegacyDownloadModeComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsInitialized)
            return;

        _legacyDownloadMode = LegacyDownloadModeComboBox.SelectedIndex == 1
            ? LegacyDownloadMode.PatchDownload
            : LegacyDownloadMode.Full;

        if (_mode == DownloaderMode.Legacy && VersionComboBox.SelectedItem is string selectedVersion)
        {
            LegacyVersion? version = ManifestResolver.GetVersion(_legacyManifest, selectedVersion);

            if (version is not null)
                UpdateLegacyContentOptions(version);

            UpdateLegacyAvailabilityUI();
            UpdateLegacyPatchSourceVersions();
            UpdateLegacyExplorerVisibility();
        }
    }

    private void UpdateLegacyAvailabilityUI()
    {
        ComboBoxItem? fullDownloadItem = LegacyDownloadModeComboBox.Items.Count > 0
            ? LegacyDownloadModeComboBox.Items[0] as ComboBoxItem
            : null;

        ComboBoxItem? patchDownloadItem = LegacyDownloadModeComboBox.Items.Count > 1
            ? LegacyDownloadModeComboBox.Items[1] as ComboBoxItem
            : null;

        LegacyVersion? version = VersionComboBox.SelectedItem is string selectedVersion
            ? ManifestResolver.GetVersion(_legacyManifest, selectedVersion)
            : null;

        bool fullAvailable = version is not null && ManifestResolver.HasAnyFullDownload(version);
        bool patchAvailable = version is not null && ManifestResolver.HasAnyUpdateDownload(version);

        if (fullDownloadItem is not null)
            fullDownloadItem.Visibility = fullAvailable ? Visibility.Visible : Visibility.Collapsed;

        if (patchDownloadItem is not null)
            patchDownloadItem.Visibility = patchAvailable ? Visibility.Visible : Visibility.Collapsed;

        EnsureValidLegacyDownloadModeSelection(fullAvailable, patchAvailable);
    }

    private void EnsureValidLegacyDownloadModeSelection(bool fullAvailable, bool patchAvailable)
    {
        bool currentFull = LegacyDownloadModeComboBox.SelectedIndex == 0;
        bool currentPatch = LegacyDownloadModeComboBox.SelectedIndex == 1;

        if ((currentFull && fullAvailable) || (currentPatch && patchAvailable))
            return;

        if (fullAvailable)
        {
            LegacyDownloadModeComboBox.SelectedIndex = 0;
            return;
        }

        if (patchAvailable)
        {
            LegacyDownloadModeComboBox.SelectedIndex = 1;
            return;
        }

        LegacyDownloadModeComboBox.SelectedIndex = -1;
        LegacyPatchSourcePanel.Visibility = Visibility.Collapsed;
        LegacyExplorerButton.Visibility = Visibility.Collapsed;
    }

    private void UpdateLegacyPatchSourceVersions()
    {
        if (!IsInitialized)
            return;

        LegacyFromVersionComboBox.ItemsSource = null;
        LegacyFromVersionComboBox.SelectedIndex = -1;

        if (_legacyDownloadMode != LegacyDownloadMode.PatchDownload)
        {
            LegacyPatchSourcePanel.Visibility = Visibility.Collapsed;
            return;
        }

        LegacyVersion? target = VersionComboBox.SelectedItem is string targetVersion
            ? ManifestResolver.GetVersion(_legacyManifest, targetVersion) : null;

        if (target is null)
        {
            LegacyPatchSourcePanel.Visibility = Visibility.Collapsed;
            return;
        }

        List<LegacyContentOption> selected = GetSelectedLegacyContentOptions();

        if (selected.Count == 0)
        {
            LegacyPatchSourcePanel.Visibility = Visibility.Collapsed;
            return;
        }

        HashSet<string>? commonVersions = null;

        foreach (LegacyContentOption option in selected)
        {
            IEnumerable<string> sourceVersions = option.IsGame
                ? ManifestResolver.GetGameUpdateSourceVersions(target)
                : GetVoiceUpdateSourceVersions(target, option.Code);

            var set = new HashSet<string>(sourceVersions, StringComparer.OrdinalIgnoreCase);

            if (commonVersions is null)
                commonVersions = set;
            else
                commonVersions.IntersectWith(set);
        }

        List<string> versions = commonVersions?
            .OrderByDescending(
                value => value,
                Comparer<string>.Create(ManifestResolver.CompareVersions))
            .ToList() ?? [];

        LegacyFromVersionComboBox.ItemsSource = versions;
        LegacyPatchSourcePanel.Visibility = versions.Count > 0
            ? Visibility.Visible
            : Visibility.Collapsed;

        if (versions.Count > 0)
            LegacyFromVersionComboBox.SelectedIndex = 0;
    }

    private static IEnumerable<string> GetVoiceUpdateSourceVersions(LegacyVersion version, string voiceCode) =>
        version.Update
            .Where(pair =>
                pair.Value.Voice.TryGetValue(voiceCode, out LegacyPackage? package) &&
                package is not null && ManifestResolver.HasValidUrl(package.Url))
            .Select(pair => pair.Key);

    private void UpdateLegacyExplorerVisibility()
    {
        if (!IsInitialized || _mode != DownloaderMode.Legacy)
        {
            LegacyExplorerButton.Visibility = Visibility.Collapsed;
            return;
        }

        LegacyVersion? version = VersionComboBox.SelectedItem is string selectedVersion
            ? ManifestResolver.GetVersion(_legacyManifest, selectedVersion) : null;

        bool visible = version is not null && GetSelectedLegacyContentOptions().Count > 0;

        if (_legacyDownloadMode == LegacyDownloadMode.PatchDownload)
            visible &= LegacyFromVersionComboBox.SelectedItem is string sourceVersion && !string.IsNullOrWhiteSpace(sourceVersion);

        LegacyExplorerButton.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
    }

    private void LegacyDownloadButton_Click(object sender, RoutedEventArgs e)
    {
        if (!TryGetLegacySelection(out LegacyVersion version, out string? fromVersion))
            return;

        string destination = LegacyDestinationTextBox.Text.Trim();

        if (string.IsNullOrWhiteSpace(destination))
        {
            Warn("Please choose a download folder.", "Download");
            return;
        }

        List<string> urls;

        try
        {
            urls = BuildLegacyUrls(version, fromVersion);
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Failed to build Legacy download URLs.");
            Error($"Unable to prepare the Legacy download:\n\n{ex.Message}", "Download");
            return;
        }

        if (urls.Count == 0)
        {
            Error("No valid download URLs were resolved.", "Download");
            return;
        }

        string gameName = GameComboBox.SelectedItem is GameOption game ? game.Name : "Legacy";
        string selectedVersion = VersionComboBox.SelectedItem as string ?? string.Empty;
        string title = string.IsNullOrWhiteSpace(selectedVersion) ? gameName : $"{gameName} {selectedVersion}";
        IReadOnlyList<LegacyContentOption> selectedContent = GetSelectedLegacyContentOptions();

        try
        {
            DownloadsView.AddLegacyDownload(urls, destination, title, selectedContent);
            SetMainPage(MainPage.Downloads);
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Failed to add Legacy download to queue.");
            Error($"Unable to add the Legacy download to the queue:\n\n{ex.Message}", "Download Queue");
        }
    }

    private bool TryGetLegacySelection(out LegacyVersion version, out string? fromVersion)
    {
        fromVersion = null;

        if (GameComboBox.SelectedItem is not GameOption)
        {
            version = null!;
            Warn("Please select a game.", "Download");
            return false;
        }

        if (VersionComboBox.SelectedItem is not string selectedVersion)
        {
            version = null!;
            Warn("Please select a version.", "Download");
            return false;
        }

        LegacyVersion? selectedEntry = ManifestResolver.GetVersion(_legacyManifest, selectedVersion);

        if (selectedEntry is null)
        {
            version = null!;
            Error($"Version {selectedVersion} was not found.", "Download");
            return false;
        }

        List<LegacyContentOption> selectedContent = GetSelectedLegacyContentOptions();

        if (selectedContent.Count == 0)
        {
            version = null!;
            Warn("Please select at least one content item.", "Content");
            return false;
        }

        if (_legacyDownloadMode == LegacyDownloadMode.PatchDownload)
        {
            if (LegacyFromVersionComboBox.SelectedItem is not string sourceVersion)
            {
                version = null!;
                Warn("Please select the source version.", "Patch Download");
                return false;
            }

            if (ManifestResolver.CompareVersions(sourceVersion, selectedVersion) >= 0)
            {
                version = null!;
                Warn("The source version must be older than the target version.", "Patch Download");
                return false;
            }

            foreach (LegacyContentOption option in selectedContent)
            {
                if (option.IsGame)
                {
                    if (!selectedEntry.Update.TryGetValue(sourceVersion, out LegacyUpdate? update) ||
                        update is null ||
                        !ManifestResolver.HasValidUrl(update.Game?.Url))
                    {
                        version = null!;
                        Warn($"No valid game update is available from {sourceVersion} to {selectedVersion}.", "Patch Download");
                        return false;
                    }
                }
                else
                {
                    if (!selectedEntry.Update.TryGetValue(sourceVersion, out LegacyUpdate? update) ||
                        update is null ||
                        !update.Voice.TryGetValue(option.Code, out LegacyPackage? package) ||
                        package is null ||
                        !ManifestResolver.HasValidUrl(package.Url))
                    {
                        version = null!;
                        Warn($"No valid {option.Name} voice update is available from {sourceVersion} to {selectedVersion}.", "Patch Download");
                        return false;
                    }
                }
            }

            fromVersion = sourceVersion;
        }
        else
        {
            foreach (LegacyContentOption option in selectedContent)
            {
                if (option.IsGame)
                {
                    if (!ManifestResolver.HasGameFull(selectedEntry))
                    {
                        version = null!;
                        Warn("The selected game does not have a valid full download.", "Download");
                        return false;
                    }
                }
                else
                {
                    string? url;

                    try { url = ManifestResolver.BuildFullVoiceUrl(selectedEntry, option.Code); }
                    catch { url = null; }

                    if (!ManifestResolver.HasValidUrl(url))
                    {
                        version = null!;
                        Warn($"The selected {option.Name} voice pack does not have a valid full download.", "Download");
                        return false;
                    }
                }
            }
        }

        version = selectedEntry;
        return true;
    }

    private List<string> BuildLegacyUrls(LegacyVersion version, string? fromVersion)
    {
        List<LegacyContentOption> selected = GetSelectedLegacyContentOptions();
        var urls = new List<string>();

        foreach (LegacyContentOption option in selected)
        {
            if (_legacyDownloadMode == LegacyDownloadMode.Full)
            {
                if (option.IsGame)
                {
                    IEnumerable<string> gameUrls = ManifestResolver.GetGameFullUrls(version) ?? [];
                    urls.AddRange(gameUrls.Where(ManifestResolver.HasValidUrl));
                }
                else
                {
                    string? url = ManifestResolver.BuildFullVoiceUrl(version, option.Code);

                    if (ManifestResolver.HasValidUrl(url))
                        urls.Add(url);
                }
            }
            else
            {
                if (string.IsNullOrWhiteSpace(fromVersion))
                    throw new InvalidOperationException("Patch source version is missing.");

                if (option.IsGame)
                {
                    string? url = ManifestResolver.BuildGameUpdateUrl(version, fromVersion);

                    if (ManifestResolver.HasValidUrl(url))
                        urls.Add(url);
                }
                else if (version.Update.TryGetValue(fromVersion, out LegacyUpdate? update) &&
                         update is not null && update.Voice.TryGetValue(option.Code, out LegacyPackage? package) &&
                         package is not null && ManifestResolver.HasValidUrl(package.Url))
                {
                    urls.Add(package.Url);
                }
            }
        }

        if (urls.Count == 0)
            throw new InvalidOperationException("No valid download URLs were found for the selected content.");

        return urls;
    }

    private void LegacyExplorerButton_Click(object sender, RoutedEventArgs e)
    {
        if (GameComboBox.SelectedItem is not GameOption game)
        {
            Warn("Please select a game.", "Legacy Explore");
            return;
        }

        if (VersionComboBox.SelectedItem is not string versionText)
        {
            Warn("Please select a version.", "Legacy Explore");
            return;
        }

        LegacyVersion? version = ManifestResolver.GetVersion(_legacyManifest, versionText);

        if (version is null)
        {
            Warn("Please select a valid version.", "Legacy Explore");
            return;
        }

        List<LegacyContentOption> selectedContent = GetSelectedLegacyContentOptions();

        if (selectedContent.Count == 0)
        {
            Warn("Please select at least one content item.", "Legacy Explore");
            return;
        }

        string? fromVersion = null;

        if (_legacyDownloadMode == LegacyDownloadMode.PatchDownload)
        {
            if (LegacyFromVersionComboBox.SelectedItem is not string sourceVersion ||
                string.IsNullOrWhiteSpace(sourceVersion))
            {
                Warn("Please select the source version.", "Legacy Explore");
                return;
            }

            if (ManifestResolver.CompareVersions(sourceVersion, versionText) >= 0)
            {
                Warn("The source version must be older than the target version.", "Legacy Explore");
                return;
            }

            fromVersion = sourceVersion;
        }

        var archives = new List<LegacyExplorerArchive>();
        string? archiveUrl = null;

        foreach (LegacyContentOption option in selectedContent)
        {
            if (option.IsGame)
            {
                List<string> urls;

                if (_legacyDownloadMode == LegacyDownloadMode.PatchDownload)
                {
                    string url = ManifestResolver.BuildGameUpdateUrl(version, fromVersion!);
                    urls = ManifestResolver.HasValidUrl(url) ? [url] : [];
                }
                else
                {
                    urls = (ManifestResolver.GetGameFullUrls(version) ?? [])
                        .Where(ManifestResolver.HasValidUrl)
                        .ToList();
                }

                if (urls.Count > 0)
                {
                    archives.Add(new LegacyExplorerArchive("game", "Game", urls));
                    archiveUrl ??= urls.FirstOrDefault();
                }

                continue;
            }

            string? voiceUrl = null;

            try
            {
                if (_legacyDownloadMode == LegacyDownloadMode.PatchDownload)
                {
                    if (version.Update.TryGetValue(fromVersion!, out LegacyUpdate? update) &&
                        update is not null &&
                        update.Voice.TryGetValue(option.Code, out LegacyPackage? package) &&
                        package is not null)
                    {
                        voiceUrl = package.Url;
                    }
                }
                else
                {
                    voiceUrl = ManifestResolver.BuildFullVoiceUrl(version, option.Code);
                }
            }
            catch
            {
                voiceUrl = null;
            }

            if (!ManifestResolver.HasValidUrl(voiceUrl))
                continue;

            archives.Add(new LegacyExplorerArchive($"voice:{option.Code}", option.Name, [voiceUrl]));
            archiveUrl ??= voiceUrl;
        }

        if (archives.Count == 0)
        {
            Warn("No valid archives are available for the selected content.", "Legacy Explore");
            return;
        }

        string baseDestination = LegacyDestinationTextBox.Text.Trim();

        if (string.IsNullOrWhiteSpace(baseDestination))
        {
            Warn("Please choose a save folder.", "Legacy Explore");
            return;
        }

        string fallbackFolderName = _legacyDownloadMode == LegacyDownloadMode.PatchDownload && !string.IsNullOrWhiteSpace(fromVersion)
            ? $"{GetLegacyFolderGameName(game)}_{fromVersion}-{versionText}"
            : $"{GetLegacyFolderGameName(game)}_{versionText}";

        string archiveFolderName = GetLegacyExploreFolderName(archiveUrl, fallbackFolderName);
        string archiveDirectory = Path.Combine(baseDestination, archiveFolderName);
        string title = _legacyDownloadMode == LegacyDownloadMode.PatchDownload && !string.IsNullOrWhiteSpace(fromVersion)
            ? $"{game.Name ?? string.Empty} {fromVersion} → {versionText} (Patch Download)"
            : $"{game.Name ?? string.Empty} {versionText}";

        try
        {
            ReloadButton.IsEnabled = false;

            var explorer = new LegacyExplorerView(
                this, DownloadsView, () => SetMainPage(MainPage.Downloads), title, archives, archiveDirectory);

            EnterExplore(explorer);
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Failed to open Legacy Explorer.");
            Error($"Unable to open the Legacy archive explorer:\n\n{ex.Message}", "Legacy Explore");
        }
        finally
        {
            ReloadButton.IsEnabled = !_legacyManifestLoading;
        }
    }

    private static string GetLegacyExploreFolderName(string? url, string fallback)
    {
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url, UriKind.Absolute, out Uri? uri))
            return fallback;

        string fileName = uri.Segments
            .LastOrDefault(segment => !string.IsNullOrWhiteSpace(segment.Trim('/')))
            ?.Trim('/') ?? string.Empty;

        if (string.IsNullOrWhiteSpace(fileName))
            return fallback;

        fileName = Uri.UnescapeDataString(fileName);
        fileName = Path.GetFileNameWithoutExtension(fileName);

        string remainingExtension = Path.GetExtension(fileName);

        if (remainingExtension.Equals(".zip", StringComparison.OrdinalIgnoreCase) ||
            remainingExtension.Equals(".7z", StringComparison.OrdinalIgnoreCase))
            fileName = Path.GetFileNameWithoutExtension(fileName);

        return string.IsNullOrWhiteSpace(fileName) ? fallback : SanitizeFolderName(fileName);
    }

    private static string GetLegacyFolderGameName(GameOption game)
    {
        string name = game.Name ?? "Game";

        name = name
            .Replace(" ", "", StringComparison.Ordinal)
            .Replace(":", "", StringComparison.Ordinal);

        return SanitizeFolderName(name);
    }

    private static string GetSophonFolderGameName(GameInfo game)
    {
        string gameId = game.GameId ?? string.Empty;
        string displayName = game.DisplayName ?? "Game";

        return SophonFolderNames.GetValueOrDefault(
            gameId, SanitizeFolderName(displayName));
    }

    private static readonly Dictionary<string, string> SophonFolderNames = new()
    {
        ["hk4e_global"] = "GenshinImpact",
        ["hk4e_cn"] = "YuanShen",
        ["hkrpg_global"] = "StarRail",
        ["hkrpg_cn"] = "StarRail",
        ["nap_global"] = "ZenlessZoneZero",
        ["nap_cn"] = "ZenlessZoneZero",
        ["bh3_global"] = "Hi3Global",
        ["bh3_sea"] = "Hi3SEA",
        ["bh3_cn"] = "Hi3CN",
        ["bh3_jp"] = "Hi3JP",
        ["bh3_kr"] = "Hi3KR",
        ["bh3_tw"] = "Hi3TW"
    };

    private static string SanitizeFolderName(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "Game";

        char[] invalid = Path.GetInvalidFileNameChars();
        return new string(value.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim();
    }

    private string GetSophonChannel() =>
        (SophonChannelComboBox.SelectedItem as ComboBoxItem)?.Tag as string ?? "main";

    private async Task RefreshSophonManifestAsync()
    {
        if (!IsInitialized ||
            _mode != DownloaderMode.Sophon || _currentSophonGame is null ||
            SophonVersionComboBox.SelectedItem is not string version)
            return;

        CancelDispose(ref _sophonManifestCts);
        _sophonManifestCts = new CancellationTokenSource();

        CancellationToken ct = _sophonManifestCts.Token;

        try
        {
            SophonContentSummaryText.Text = "Loading manifest...";

            ManifestConfig manifest = await _sophonDownloadService.LoadManifestAsync(
                _currentSophonGame, version, GetSophonChannel(), ct);

            ct.ThrowIfCancellationRequested();

            _sophonContentOptions = _sophonDownloadService.BuildContentOptions(manifest)?.ToList() ?? [];
            SophonContentItemsControl.ItemsSource = _sophonContentOptions;
            SophonExplorerButton.Visibility = _sophonContentOptions.Count > 0
                ? Visibility.Visible
                : Visibility.Collapsed;

            UpdateSophonContentSummary();
        }
        catch (OperationCanceledException) {}
        catch (Exception ex)
        {
            Logger.Error(ex, "Failed to load Sophon manifest.");
            SophonContentItemsControl.ItemsSource = null;
            SophonExplorerButton.Visibility = Visibility.Collapsed;
            SophonContentSummaryText.Text = "Unable to load manifest.";
        }
    }

    private void RefreshSophonGames()
    {
        if (!IsInitialized)
            return;

        List<GameInfo> games = SophonGameService.GetSupportedGames() ?? [];
        SophonGameComboBox.ItemsSource = games;

        if (games.Count > 0 && SophonGameComboBox.SelectedIndex < 0)
            SophonGameComboBox.SelectedIndex = 0;
    }

    private async void SophonGameComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (IsInitialized && _mode == DownloaderMode.Sophon)
        {
            _sophonDestinationCustomized = false;
            await RefreshSophonSelectionAsync();
        }
    }

    private async void SophonChannelComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (IsInitialized && _mode == DownloaderMode.Sophon)
            await RefreshSophonSelectionAsync();
    }

    private async void SophonDownloadModeComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsInitialized || _mode != DownloaderMode.Sophon) return;
        _sophonDownloadMode = SophonDownloadModeComboBox.SelectedIndex == 1
            ? SophonDownloadMode.Patch
            : SophonDownloadMode.Full;
        UpdateSophonVersionModeFilter();
        UpdateSophonPatchSourceVersions();
        UpdateSophonDefaultDestination();
        await RefreshSophonManifestAsync();
    }

    private void SophonFromVersionComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsInitialized || _mode != DownloaderMode.Sophon) return;
        UpdateSophonDefaultDestination();
    }

    private void UpdateSophonPatchSourceVersions()
    {
        if (_sophonDownloadMode != SophonDownloadMode.Patch ||
            _currentSophonGame is null ||
            SophonVersionComboBox.SelectedItem is not string target ||
            !IsSophonPatchTargetAllowed(_currentSophonGame, target))
        {
            SophonFromVersionComboBox.ItemsSource = null;
            SophonFromVersionComboBox.SelectedIndex = -1;
            SophonPatchSourcePanel.Visibility = Visibility.Collapsed;
            return;
        }

        int limit = GetSophonPatchSourceVersionLimit(_currentSophonGame);
        List<string> candidates = _sophonVersions
            .Where(v => !string.Equals(v, target, StringComparison.OrdinalIgnoreCase))
            .Where(v => ManifestResolver.CompareVersions(v, target) < 0)
            .OrderByDescending(v => v, Comparer<string>.Create(ManifestResolver.CompareVersions))
            .Take(limit)
            .ToList();

        SophonFromVersionComboBox.ItemsSource = candidates;
        SophonFromVersionComboBox.SelectedIndex = candidates.Count > 0 ? 0 : -1;
        SophonPatchSourcePanel.Visibility = candidates.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void UpdateSophonVersionModeFilter()
    {
        List<string> visible = _sophonVersions;
        string? selected = SophonVersionComboBox.SelectedItem as string;

        if (_sophonDownloadMode == SophonDownloadMode.Patch && _currentSophonGame is not null)
        {
            visible = _sophonVersions
                .Where(v => IsSophonPatchTargetAllowed(_currentSophonGame, v))
                .ToList();
        }

        SophonVersionComboBox.ItemsSource = visible;

        if (selected is not null && visible.Contains(selected, StringComparer.OrdinalIgnoreCase))
            SophonVersionComboBox.SelectedItem = selected;
        else
            SophonVersionComboBox.SelectedIndex = visible.Count > 0 ? 0 : -1;
    }

    private static int GetSophonPatchSourceVersionLimit(GameInfo game)
    {
        return game.GameId.StartsWith("hkrpg_", StringComparison.OrdinalIgnoreCase) ? 1 : 2;
    }

    private bool IsSophonPatchTargetAllowed(GameInfo game, string targetVersion)
    {
        List<string> fullVersions = _sophonVersions
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(v => v, Comparer<string>.Create(ManifestResolver.CompareVersions))
            .ToList();

        if (fullVersions.Count < 2)
            return false;

        string minimumPatchTarget = fullVersions[1];
        return ManifestResolver.CompareVersions(targetVersion, minimumPatchTarget) >= 0;
    }

    private async void SophonVersionComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsInitialized || _mode != DownloaderMode.Sophon)
            return;

        UpdateSophonPatchSourceVersions();
        UpdateSophonDefaultDestination();
        await RefreshSophonManifestAsync();
    }

    private async Task RefreshSophonSelectionAsync()
    {
        if (!IsInitialized ||
            _mode != DownloaderMode.Sophon ||
            SophonGameComboBox.SelectedItem is not GameInfo game)
            return;

        _currentSophonGame = game;
        SophonGameTitleText.Text = game.DisplayName ?? string.Empty;
        SophonVersionComboBox.ItemsSource = null;
        SophonContentItemsControl.ItemsSource = null;
        SophonContentSummaryText.Text = "Loading versions...";
        SophonExplorerButton.Visibility = Visibility.Collapsed;

        ComboBoxItem? preDownloadItem = SophonChannelComboBox.Items
            .OfType<ComboBoxItem>()
            .FirstOrDefault(item => string.Equals(item.Tag as string, "predownload", StringComparison.OrdinalIgnoreCase));

        if (preDownloadItem is not null)
            preDownloadItem.IsEnabled = false;

        string channel = GetSophonChannel();
        List<string> versions = [];

        try
        {
            BranchesGameBranch branch = await SophonGameService.GetGameBranches(game.GameId, game.Region, "MainWindow.RefreshSophonSelectionAsync");
            string? preDownloadTag = branch.pre_download?.tag;
            bool preDownloadAvailable = !string.IsNullOrWhiteSpace(preDownloadTag);

            if (preDownloadItem is not null)
                preDownloadItem.IsEnabled = preDownloadAvailable;

            if (channel.Equals("predownload", StringComparison.OrdinalIgnoreCase))
            {
                if (!preDownloadAvailable)
                {
                    ComboBoxItem? mainItem = SophonChannelComboBox.Items
                        .OfType<ComboBoxItem>()
                        .FirstOrDefault(item => string.Equals(item.Tag as string, "main", StringComparison.OrdinalIgnoreCase));

                    if (mainItem is not null)
                        SophonChannelComboBox.SelectedItem = mainItem;

                    channel = "main";
                }
                else
                {
                    versions = [preDownloadTag!];
                }
            }

            if (channel.Equals("main", StringComparison.OrdinalIgnoreCase))
                versions = await SophonGameService.GetHistoricalVersionsAsync(game) ?? [];
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Failed to load Sophon versions.");

            if (preDownloadItem is not null)
                preDownloadItem.IsEnabled = false;

            if (channel.Equals("predownload", StringComparison.OrdinalIgnoreCase))
            {
                ComboBoxItem? mainItem = SophonChannelComboBox.Items
                    .OfType<ComboBoxItem>()
                    .FirstOrDefault(item => string.Equals(item.Tag as string, "main", StringComparison.OrdinalIgnoreCase));

                if (mainItem is not null)
                    SophonChannelComboBox.SelectedItem = mainItem;

                channel = "main";

                try
                {
                    versions = await SophonGameService.GetHistoricalVersionsAsync(game) ?? [];
                }
                catch (Exception fallbackEx)
                {
                    Logger.Error(fallbackEx, "Failed to load Sophon main versions after pre-download lookup failed.");
                }
            }
        }

        if (!IsInitialized || _mode != DownloaderMode.Sophon)
            return;

        _sophonVersions = versions.ToList();
        UpdateSophonVersionModeFilter();
        UpdateSophonPatchSourceVersions();

        if (_sophonVersions.Count == 0 || SophonVersionComboBox.Items.Count == 0)
        {
            SophonContentSummaryText.Text = _sophonDownloadMode == SophonDownloadMode.Patch
                ? "No eligible patch target version available."
                : "No version available.";
            return;
        }

        UpdateSophonDefaultDestination();
        await RefreshSophonManifestAsync();
    }

    private void UpdateSophonDefaultDestination()
    {
        if (_sophonDestinationCustomized ||
            _currentSophonGame is null ||
            SophonVersionComboBox.SelectedItem is not string version ||
            string.IsNullOrWhiteSpace(version))
            return;

        string game = GetSophonFolderGameName(_currentSophonGame);
        string folderName = _sophonDownloadMode == SophonDownloadMode.Patch
            && SophonFromVersionComboBox.SelectedItem is string fromVersion
            && !string.IsNullOrWhiteSpace(fromVersion)
            ? $"{game}_{fromVersion}-{version}"
            : $"{game}_{version}";
        SophonDestinationTextBox.Text = Path.Combine(Utility.GetApplicationDirectory(), folderName);
    }

    private void SophonContentCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (IsInitialized)
            UpdateSophonContentSummary();
    }

    private void UpdateSophonContentSummary()
    {
        if (!IsInitialized)
            return;

        List<SophonContentOption> selected = _sophonContentOptions.Where(x => x.IsSelected).ToList();

        SophonContentSummaryText.Text = selected.Count switch
        {
            0 => "No content selected",
            1 => $"{selected[0].Name} selected",
            _ => $"{selected.Count} content items selected"
        };
    }

    private async void SophonExplorerButton_Click(object sender, RoutedEventArgs e)
    {
        if (_currentSophonGame is null)
        {
            Warn("Please select a Sophon game.", "Sophon Explorer");
            return;
        }

        if (SophonVersionComboBox.SelectedItem is not string version)
        {
            Warn("Please select a Sophon version.", "Sophon Explorer");
            return;
        }

        List<SophonContentOption> selected = _sophonContentOptions.Where(x => x.IsSelected).ToList();

        if (selected.Count == 0)
        {
            Warn("Please select at least one content category.", "Sophon Explorer");
            return;
        }

        try
        {
            SophonExplorerButton.IsEnabled = false;
            SophonDownloadButton.IsEnabled = false;
            ReloadButton.IsEnabled = false;

            string channel = GetSophonChannel();
            GameInfo game = _currentSophonGame;
            List<SophonContentOption> selectedContent = selected.ToList();

            string? patchFromVersion = _sophonDownloadMode == SophonDownloadMode.Patch
                ? SophonFromVersionComboBox.SelectedItem as string
                : null;

            if (_sophonDownloadMode == SophonDownloadMode.Patch && string.IsNullOrWhiteSpace(patchFromVersion))
            {
                Warn("Please select the source version for the patch download.", "Sophon Explorer");
                return;
            }

            var explorer = new SophonExplorerView(
                this, DownloadsView, () => SetMainPage(MainPage.Downloads), game, version, channel, patchFromVersion,
                _sophonDownloadMode == SophonDownloadMode.Patch
                    ? $"{game.DisplayName ?? string.Empty} {patchFromVersion} → {version} (Patch)"
                    : $"{game.DisplayName ?? string.Empty} {version}",
                async () =>
                {
                    ManifestConfig targetManifest = await ResolveSophonManifestAsync(game, version, channel);
                    if (_sophonDownloadMode != SophonDownloadMode.Patch)
                        return await _sophonDownloadService.LoadSelectedContentAsync(targetManifest, selectedContent);

                    ManifestConfig fromManifest = await _sophonDownloadService.LoadManifestAsync(game, patchFromVersion!, channel);
                    return await _sophonDownloadService.LoadSelectedPatchContentAsync(
                        game, fromManifest, targetManifest, selectedContent);
                });

            EnterExplore(explorer);
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Failed to open Sophon Explorer.");
            Error($"Unable to read the Sophon content manifest:\n\n{ex.Message}", "Sophon Explorer");
        }
        finally
        {
            SophonExplorerButton.IsEnabled = _sophonContentOptions.Count > 0;
            SophonDownloadButton.IsEnabled = true;
            ReloadButton.IsEnabled = !_legacyManifestLoading;
        }
    }

    private async Task<ManifestConfig> ResolveSophonManifestAsync(GameInfo game, string version, string channel)
    {
        ManifestConfig? manifest = _sophonDownloadService.CurrentManifest;

        if (manifest is null ||
            !string.Equals(manifest.data.tag, version, StringComparison.OrdinalIgnoreCase))
        {
            manifest = await _sophonDownloadService.LoadManifestAsync(game, version, channel);
        }

        return manifest;
    }

    private void SophonDownloadButton_Click(object sender, RoutedEventArgs e)
    {
        if (_currentSophonGame is null)
        {
            Warn("Please select a Sophon game.", "Sophon");
            return;
        }

        if (SophonVersionComboBox.SelectedItem is not string version)
        {
            Warn("Please select a Sophon version.", "Sophon");
            return;
        }

        List<SophonContentOption> selected = _sophonContentOptions.Where(x => x.IsSelected).ToList();

        if (selected.Count == 0)
        {
            Warn("Please select at least one content category.", "Sophon");
            return;
        }

        string destination = SophonDestinationTextBox.Text.Trim();

        if (string.IsNullOrWhiteSpace(destination))
        {
            Warn("Please choose a save folder.", "Sophon");
            return;
        }

        try
        {
            ManifestConfig? manifest = _sophonDownloadService.CurrentManifest;

            if (manifest is not null &&
                !string.Equals(manifest.data.tag, version, StringComparison.OrdinalIgnoreCase))
                manifest = null;

            string? patchFromVersion = null;
            if (_sophonDownloadMode == SophonDownloadMode.Patch)
            {
                if (!IsSophonPatchTargetAllowed(_currentSophonGame, version))
                {
                    Warn("Patch Download is not available for this target version.", "Sophon Patch");
                    return;
                }

                patchFromVersion = SophonFromVersionComboBox.SelectedItem as string;
                List<string> allowedSources = _sophonVersions
                    .Where(v => ManifestResolver.CompareVersions(v, version) < 0)
                    .OrderByDescending(v => v, Comparer<string>.Create(ManifestResolver.CompareVersions))
                    .Take(GetSophonPatchSourceVersionLimit(_currentSophonGame))
                    .ToList();

                if (string.IsNullOrWhiteSpace(patchFromVersion) ||
                    !allowedSources.Contains(patchFromVersion, StringComparer.OrdinalIgnoreCase))
                {
                    Warn("Please select a valid source version for the patch download.", "Sophon Patch");
                    return;
                }
            }

            DownloadsView.AddSophonDownload(
                _currentSophonGame, version, GetSophonChannel(), selected, destination,
                true, manifest, patchFromVersion);

            SetMainPage(MainPage.Downloads);
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Failed to add Sophon download to queue.");
            Error($"Unable to add the Sophon download to the queue:\n\n{ex.Message}", "Download Queue");
        }
    }

    private void LegacyBrowseButton_Click(object sender, RoutedEventArgs e) =>
        OpenFolder(LegacyDestinationTextBox, "Choose Legacy save folder");

    private void SophonBrowseButton_Click(object sender, RoutedEventArgs e)
    {
        if (OpenFolder(SophonDestinationTextBox, "Choose Sophon save folder"))
            _sophonDestinationCustomized = true;
    }

    private static bool OpenFolder(TextBox target, string title)
    {
        var dialog = new OpenFolderDialog { Title = title };

        if (dialog.ShowDialog() != true)
            return false;

        string folderName = dialog.FolderName ?? string.Empty;
        target.Text = folderName;
        return !string.IsNullOrWhiteSpace(folderName);
    }
    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed)
            DragMove();
    }

    private void MinimizeButton_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();
    private static void Warn(string message, string title) => MessageBox.Show(message, title, MessageBoxButton.OK, MessageBoxImage.Warning);
    private static void Error(string message, string title) => MessageBox.Show(message, title, MessageBoxButton.OK, MessageBoxImage.Error);
}
