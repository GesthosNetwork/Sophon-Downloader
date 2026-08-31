using System.Text.Json;
using SophonDownloader.Models;
using SophonDownloader.Services;
using SophonDownloader.Utilities;

namespace SophonDownloader;

public partial class DownloadsView : UserControl
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();
    private readonly List<QueueItem> _items = [];
    private bool _historyLoaded;
    private bool _shuttingDown;
    private readonly object _schedulerLock = new();
    private int _activeOperations;
    private bool _schedulerPumpRunning;
    private Task? _historyLoadTask;
    private readonly System.Windows.Threading.DispatcherTimer _progressWatchdogTimer;

    private static readonly TimeSpan ProgressWaitThreshold = TimeSpan.FromSeconds(5);
    private const string ProgressWaitMessage = "Finalizing the segment and flushing the disk cache. Please wait a moment. Do not interrupt or close the application.";
    private readonly DownloadHistoryStore _historyStore = new(Path.Combine(AppContext.BaseDirectory, "sophon.db"));

    public DownloadsView()
    {
        InitializeComponent();
        _historyStore.Initialize();
        UpdateQueueSummary();

        _progressWatchdogTimer = new System.Windows.Threading.DispatcherTimer(
            System.Windows.Threading.DispatcherPriority.Background, Dispatcher)
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        _progressWatchdogTimer.Tick += (_, _) => RefreshProgressWaitNotices();
        _progressWatchdogTimer.Start();

        Loaded += DownloadsView_Loaded;
        Unloaded += DownloadsView_Unloaded;
    }

    private void DownloadsView_Unloaded(object sender, RoutedEventArgs e)
    {
        _progressWatchdogTimer.Stop();
    }

    private async void DownloadsView_Loaded(object sender, RoutedEventArgs e)
    {
        if (!_progressWatchdogTimer.IsEnabled && !_shuttingDown)
            _progressWatchdogTimer.Start();

        if (_historyLoaded || _shuttingDown) return;
        _historyLoaded = true;
        _historyLoadTask = LoadHistoryAsync();
        await _historyLoadTask;
    }

    public void HardStopDownloads()
    {
        _shuttingDown = true;

        QueueItem[] snapshot;
        lock (_schedulerLock)
        {
            snapshot = _items.ToArray();
            _schedulerPumpRunning = false;
        }

        foreach (QueueItem item in snapshot)
        {
            item.CancelRequested = true;
            try { item.CancellationSource?.Cancel(); } catch {}
            try { item.SophonOperationCancellationSource?.Cancel(); } catch {}
            try { item.LegacyService?.Cancel(); } catch {}
            try { item.LegacyExplorerService?.Cancel(); } catch {}
            try { item.SophonService?.Cancel(); } catch {}
        }

        try { KillRemainingAria2Processes(); } catch {}
    }

    private static void KillRemainingAria2Processes() => Aria2c.KillAllProcesses();

    private void RefreshProgressWaitNotices()
    {
        if (_shuttingDown) return;

        long now = Environment.TickCount64;
        foreach (QueueItem item in _items)
        {
            if (item.CancelRequested || item.StatusDetailText is null)
                continue;

            if (item.State is not (QueueItemState.Preparing or QueueItemState.Downloading))
                continue;

            bool paused = item.Type == QueueItemType.Legacy
                ? item.LegacyExplorerService?.IsPaused == true || item.LegacyService?.IsPaused == true
                : item.SophonService?.IsPaused == true;

            if (paused || item.LastProgressTick <= 0)
                continue;

            if (now - item.LastProgressTick >= (long)ProgressWaitThreshold.TotalMilliseconds)
            {
                if (!string.Equals(item.StatusDetailText.Text, ProgressWaitMessage, StringComparison.Ordinal))
                {
                    item.StatusText.Text = "PLEASE WAIT";
                    item.StatusDetailText.Text = ProgressWaitMessage;
                }
            }
        }
    }

    public void AddLegacyDownload(IReadOnlyList<string> urls, string destinationDirectory, string title, IReadOnlyList<LegacyContentOption> selectedContent)
    {
        if (_shuttingDown) return;

        if (urls is null || urls.Count == 0)
            throw new ArgumentException("At least one download URL is required.", nameof(urls));

        if (string.IsNullOrWhiteSpace(destinationDirectory))
            throw new ArgumentException("Destination directory cannot be empty.", nameof(destinationDirectory));

        ArgumentNullException.ThrowIfNull(selectedContent);

        var item = new QueueItem(
            QueueItemType.Legacy, title, destinationDirectory, urls,
            null, null, null, false);

        item.SelectedContentNames = selectedContent
            .Where(option => option.IsSelected && !string.IsNullOrWhiteSpace(option.Name))
            .Select(option => option.Name.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        AddItem(item);
        SchedulePendingDownloads();
    }

    public void AddLegacyExplorerDownload(
        IReadOnlyList<LegacyExplorerArchive> archives,
        IReadOnlyList<LegacyExplorerNode> selectedNodes,
        string destinationDirectory, string title)
    {
        ArgumentNullException.ThrowIfNull(archives);
        ArgumentNullException.ThrowIfNull(selectedNodes);
        if (_shuttingDown) return;

        if (archives.Count == 0)
            throw new ArgumentException("At least one archive is required.", nameof(archives));
        if (selectedNodes.Count == 0)
            throw new ArgumentException("At least one file must be selected.", nameof(selectedNodes));
        if (string.IsNullOrWhiteSpace(destinationDirectory))
            throw new ArgumentException("Destination directory cannot be empty.", nameof(destinationDirectory));

        var item = new QueueItem(
            QueueItemType.Legacy, title, destinationDirectory, [], null, null, null, false)
        {
            LegacyExplorerArchives = archives.ToList(),
            LegacyExplorerSelectedNodes = selectedNodes
                .Where(node => node is not null && !node.IsFolder && !string.IsNullOrWhiteSpace(node.FullPath))
                .Select(node => CloneLegacyExplorerNode(node))
                .GroupBy(node => node.FullPath, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .ToList(),
            LegacyExplorerSelectedPaths = selectedNodes
                .Where(node => node is not null && !node.IsFolder && !string.IsNullOrWhiteSpace(node.FullPath))
                .Select(node => node.FullPath)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList()
        };

        item.SelectedContentNames = archives
            .Select(archive => archive.Name)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        AddItem(item);
        SchedulePendingDownloads();
    }

    public void AddSophonExplorerDownload(
        GameInfo game, string version, string channel,
        IReadOnlyList<SophonContentOption> selectedContent,
        IReadOnlyList<string> selectedFilePaths,
        string destinationDirectory,
        ManifestConfig? manifest = null,
        string? patchFromVersion = null)
    {
        ArgumentNullException.ThrowIfNull(game);
        ArgumentNullException.ThrowIfNull(selectedContent);
        ArgumentNullException.ThrowIfNull(selectedFilePaths);
        if (_shuttingDown) return;

        if (string.IsNullOrWhiteSpace(version))
            throw new ArgumentException("Version cannot be empty.", nameof(version));
        if (string.IsNullOrWhiteSpace(destinationDirectory))
            throw new ArgumentException("Destination directory cannot be empty.", nameof(destinationDirectory));

        List<string> paths = selectedFilePaths
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(NormalizeExplorerPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (paths.Count == 0)
            throw new ArgumentException("At least one file must be selected.", nameof(selectedFilePaths));

        List<SophonContentOption> copiedContent = selectedContent
            .Select(option => new SophonContentOption(option.Category) { IsSelected = true })
            .ToList();

        if (copiedContent.Count == 0)
            throw new ArgumentException("At least one content category is required.", nameof(selectedContent));

        bool isPatch = !string.IsNullOrWhiteSpace(patchFromVersion);
        string title = isPatch
            ? $"{game.DisplayName} • {patchFromVersion} → {version}"
            : $"{game.DisplayName} {version}";

        var item = new QueueItem(
            QueueItemType.Sophon, title, destinationDirectory, [], game, version, channel, true)
        {
            SelectedContent = copiedContent,
            SelectedCategoryIds = copiedContent
                .Select(content => content.Category.category_id)
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList(),
            SophonSelectedFilePaths = paths,
            Manifest = manifest,
            PatchFromVersion = patchFromVersion
        };

        AddItem(item);
        SchedulePendingDownloads();
    }

    public void AddSophonDownload(
        GameInfo game, string version, string channel,
        IReadOnlyList<SophonContentOption> selectedContent,
        string destinationDirectory,
        bool deleteChunksAfterExtraction,
        ManifestConfig? manifest = null,
        string? patchFromVersion = null)
    {
        ArgumentNullException.ThrowIfNull(game);
        if (_shuttingDown) return;

        if (string.IsNullOrWhiteSpace(version))
            throw new ArgumentException("Version cannot be empty.", nameof(version));

        if (selectedContent is null || selectedContent.Count == 0)
            throw new ArgumentException("At least one content category is required.", nameof(selectedContent));

        if (string.IsNullOrWhiteSpace(destinationDirectory))
            throw new ArgumentException("Destination directory cannot be empty.", nameof(destinationDirectory));

        List<SophonContentOption> copiedContent = selectedContent
            .Select(option => new SophonContentOption(option.Category) { IsSelected = true })
            .ToList();

        bool isPatch = !string.IsNullOrWhiteSpace(patchFromVersion);
        string title = isPatch
            ? $"{game.DisplayName} • {patchFromVersion} → {version}"
            : $"{game.DisplayName} {version}";

        var item = new QueueItem(
            QueueItemType.Sophon, title, destinationDirectory, [], game, version, channel, deleteChunksAfterExtraction);

        item.SelectedContent = copiedContent;
        item.SelectedCategoryIds = copiedContent
            .Select(content => content.Category.category_id)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        item.Manifest = manifest;
        item.PatchFromVersion = patchFromVersion;

        AddItem(item);
        SchedulePendingDownloads();
    }

    public void RefreshScheduler() => SchedulePendingDownloads();

    private void SchedulePendingDownloads()
    {
        if (_shuttingDown || Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished)
            return;

        lock (_schedulerLock)
        {
            if (_schedulerPumpRunning) return;
            _schedulerPumpRunning = true;
        }

        PostUi(() => _ = PumpSchedulerAsync());
    }

    private async Task PumpSchedulerAsync()
    {
        try
        {
            while (!_shuttingDown)
            {
                int limit = GetQueueConcurrencyLimit();
                QueueItem? next = null;

                lock (_schedulerLock)
                {
                    if (_activeOperations >= limit)
                        break;

                    next = _items.FirstOrDefault(item =>
                        item.State == QueueItemState.Queued &&
                        !item.CancelRequested &&
                        !item.SchedulerRunning);

                    if (next is null)
                        break;

                    next.SchedulerRunning = true;
                    _activeOperations++;
                }

                Task runningTask = RunScheduledDownloadAsync(next);
                next.ScheduledTask = runningTask;

                await Task.Yield();
            }
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Unexpected error while pumping the download queue scheduler.");
        }
        finally
        {
            lock (_schedulerLock)
                _schedulerPumpRunning = false;

            if (!_shuttingDown)
                PostUi(UpdateQueueSummary);
        }
    }

    private void PostUi(Action action)
    {
        if (_shuttingDown || Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished)
            return;

        try
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (_shuttingDown || Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished)
                    return;

                try
                {
                    action();
                }
                catch (Exception ex)
                {
                    Logger.Warn(ex, "A queued UI update could not be applied.");
                }
            }), System.Windows.Threading.DispatcherPriority.Normal);
        }
        catch (InvalidOperationException) {}
    }

    private static int GetQueueConcurrencyLimit()
    {
        AppSettings settings = AppSettingsStore.Load();
        return string.Equals(settings.DownloadMode, "Sequential", StringComparison.OrdinalIgnoreCase)
            ? 1 : Math.Clamp(settings.MaxConcurrentDownloads, 1, 8);
    }

    private async Task RunScheduledDownloadAsync(QueueItem item)
    {
        try
        {
            item.SetState(QueueItemState.Preparing, "Starting queued download...");
            item.LastProgressTick = Environment.TickCount64;
            SetActiveControls(item);
            PersistHistory();

            if (item.Type == QueueItemType.Legacy)
                await RunLegacyDownloadAsync(item);
            else
                await RunSophonDownloadAsync(item);
        }
        finally
        {
            lock (_schedulerLock)
            {
                item.SchedulerRunning = false;
                item.ScheduledTask = null;
                _activeOperations = Math.Max(0, _activeOperations - 1);
            }

            if (!_shuttingDown)
                SchedulePendingDownloads();
        }
    }

    private void AddItem(QueueItem item, bool persist = true)
    {
        if (_shuttingDown) return;

        _items.Add(item);
        QueueItemsPanel.Children.Add(CreateQueueCard(item));
        UpdateQueueSummary();

        if (persist) PersistHistory();
        ScrollItemIntoView(item);
    }

    private Border CreateQueueCard(QueueItem item)
    {
        var card = new Border
        {
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(16),
            Margin = new Thickness(0, 0, 0, 10),
            Tag = item
        };
        card.SetResourceReference(Border.BackgroundProperty, "QueueCardBrush");
        card.SetResourceReference(Border.BorderBrushProperty, "QueueBorderBrush");

        var grid = new Grid();
        for (int i = 0; i < 5; i++)
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var headerGrid = new Grid();
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition
        {
            Width = new GridLength(1, GridUnitType.Star)
        });
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition
        {
            Width = GridLength.Auto
        });

        var titleStack = new StackPanel();

        var typeText = new TextBlock
        {
            Text = item.Type == QueueItemType.Sophon
                ? (item.IsPatch ? "SOPHON PATCH DOWNLOAD" : "SOPHON FULL DOWNLOAD")
                : (item.IsLegacyExplorer ? "LEGACY EXPLORE" : "LEGACY"),
            FontSize = 9,
            FontWeight = FontWeights.SemiBold
        };

        var titleText = new TextBlock
        {
            Text = item.Title,
            FontSize = 15,
            FontWeight = FontWeights.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(0, 3, 0, 0)
        };

        var contentText = new TextBlock
        {
            Text = GetSelectedContentText(item),
            FontSize = 10,
            FontWeight = FontWeights.Medium,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(0, 4, 0, 0)
        };

        var destinationText = new TextBlock
        {
            Text = item.DestinationDirectory,
            FontSize = 10,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(0, 2, 0, 0)
        };

        typeText.SetResourceReference(TextBlock.ForegroundProperty, "QueueTypeBrush");
        titleText.SetResourceReference(TextBlock.ForegroundProperty, "QueueTitleBrush");
        contentText.SetResourceReference(TextBlock.ForegroundProperty, "QueueSecondaryTextBrush");
        destinationText.SetResourceReference(TextBlock.ForegroundProperty, "QueueSecondaryTextBrush");
        item.SelectedContentText = contentText;
        titleStack.Children.Add(typeText);
        titleStack.Children.Add(titleText);
        titleStack.Children.Add(contentText);
        titleStack.Children.Add(destinationText);
        headerGrid.Children.Add(titleStack);

        var stateText = new TextBlock
        {
            Text = "QUEUED",
            FontSize = 11,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(20, 0, 0, 0),
            Tag = "Status"
        };

        Grid.SetColumn(stateText, 1);
        headerGrid.Children.Add(stateText);
        grid.Children.Add(headerGrid);

        var progressInfoGrid = new Grid
        {
            Margin = new Thickness(0, 14, 0, 0)
        };

        var progressText = new TextBlock
        {
            Text = item.Type == QueueItemType.Sophon ? "0 / 0 chunks" : "0 / 0 files",
            FontSize = 10,
            FontWeight = FontWeights.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Right,
            Tag = "ProgressText"
        };

        var statusText = new TextBlock
        {
            Text = "Waiting for an available queue slot...",
            FontSize = 10,
            TextWrapping = TextWrapping.Wrap,
            MaxHeight = 42,
            Margin = new Thickness(0, 0, 120, 0),
            Tag = "StatusDetail"
        };

        stateText.SetResourceReference(TextBlock.ForegroundProperty, "QueueTitleBrush");
        progressText.SetResourceReference(TextBlock.ForegroundProperty, "QueueTitleBrush");
        statusText.SetResourceReference(TextBlock.ForegroundProperty, "QueueSecondaryTextBrush");

        progressInfoGrid.Children.Add(statusText);
        progressInfoGrid.Children.Add(progressText);
        Grid.SetRow(progressInfoGrid, 1);
        grid.Children.Add(progressInfoGrid);

        var progressBar = new ProgressBar
        {
            Minimum = 0,
            Maximum = 100,
            Value = 0,
            Margin = new Thickness(0, 8, 0, 0),
            Tag = "ProgressBar"
        };

        Grid.SetRow(progressBar, 2);
        grid.Children.Add(progressBar);

        var statsGrid = new Grid
        {
            Margin = new Thickness(0, 14, 0, 0)
        };

        int statCount = item.Type == QueueItemType.Sophon ? 7 : 4;
        for (int i = 0; i < statCount; i++)
        {
            statsGrid.ColumnDefinitions.Add(new ColumnDefinition
            {
                Width = new GridLength(1, GridUnitType.Star)
            });
        }

        AddStat(statsGrid, 0, "FILES", "0", "Files");
        if (item.Type == QueueItemType.Sophon)
        {
            AddStat(statsGrid, 1, "UNIQUE CHUNKS", "0", "Chunks");
            AddStat(statsGrid, 2, "REQUIRED", "0 B", "Required");
            AddStat(statsGrid, 3, "CACHED", "0 B", "Cached");
            AddStat(statsGrid, 4, "PARTIAL", "0 B", "Partial");
            AddStat(statsGrid, 5, "SPEED", "--", "Speed");
            AddStat(statsGrid, 6, "ETA", "--", "Eta");
        }
        else
        {
            AddStat(statsGrid, 1, "ARCHIVES", item.LegacyUrls.Count.ToString("N0"), "Archives");
            AddStat(statsGrid, 2, "SPEED", "--", "Speed");
            AddStat(statsGrid, 3, "ETA", "--", "Eta");
        }

        Grid.SetRow(statsGrid, 3);
        grid.Children.Add(statsGrid);

        var actionGrid = new Grid
        {
            Margin = new Thickness(0, 14, 0, 0)
        };

        actionGrid.ColumnDefinitions.Add(new ColumnDefinition
        {
            Width = new GridLength(1, GridUnitType.Star)
        });
        actionGrid.ColumnDefinitions.Add(new ColumnDefinition
        {
            Width = GridLength.Auto
        });

        var deleteChunksCheckBox = new CheckBox
        {
            Content = "Delete chunks after extraction",
            IsChecked = item.Type == QueueItemType.Sophon && item.DeleteChunksAfterExtraction,
            Style = (Style)FindResource("QueueDeleteChunksCheckBoxStyle"),
            Tag = "DeleteChunks"
        };

        deleteChunksCheckBox.Click += (_, _) =>
        {
            if (item.Type != QueueItemType.Sophon) return;
            item.DeleteChunksAfterExtraction = deleteChunksCheckBox.IsChecked == true;
            PersistHistory();
        };

        Grid.SetColumn(deleteChunksCheckBox, 0);
        actionGrid.Children.Add(deleteChunksCheckBox);

        var buttonGrid = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right
        };

        var pauseButton = new Button
        {
            Content = "PAUSE",
            Style = (Style)FindResource("QueueSecondaryButtonStyle"),
            Margin = new Thickness(0, 0, 10, 0),
            Tag = "Pause"
        };
        pauseButton.Click += (_, _) => PauseResumeItem(item);

        var extractButton = new Button
        {
            Content = "EXTRACT",
            Style = (Style)FindResource("QueueSecondaryButtonStyle"),
            Visibility = Visibility.Collapsed,
            Margin = new Thickness(0, 0, 10, 0),
            Tag = "Extract"
        };
        extractButton.Click += (_, _) =>
        {
            if (item.RequiresSophonRepair)
                RepairSophonItem(item);
            else
                ExtractItem(item);
        };

        var cancelButton = new Button
        {
            Content = "CANCEL",
            Style = (Style)FindResource("QueueCancelButtonStyle"),
            Margin = new Thickness(0, 0, 0, 0),
            Tag = "Cancel"
        };
        cancelButton.Click += (_, _) => CancelItem(item);

        var removeButton = new Button
        {
            Width = 34,
            Height = 34,
            Style = (Style)FindResource("QueueRemoveButtonStyle"),
            Margin = new Thickness(8, 0, 0, 0),
            Visibility = Visibility.Collapsed,
            ToolTip = "Remove from queue",
            Tag = "Remove"
        };

        ImageSource? deleteIcon = ShellIconProvider.GetDeleteIcon();
        removeButton.Content = deleteIcon is not null
            ? new Image
            {
                Source = deleteIcon,
                Width = 16,
                Height = 16,
                Stretch = Stretch.Uniform
            }
            : "×";

        removeButton.Click += (_, _) => RemoveItem(item);

        buttonGrid.Children.Add(pauseButton);
        buttonGrid.Children.Add(extractButton);
        buttonGrid.Children.Add(cancelButton);
        buttonGrid.Children.Add(removeButton);

        Grid.SetColumn(buttonGrid, 1);
        actionGrid.Children.Add(buttonGrid);

        Grid.SetRow(actionGrid, 4);
        grid.Children.Add(actionGrid);

        card.Child = grid;

        item.Card = card;
        item.StatusText = stateText;
        item.StatusDetailText = statusText;
        item.ProgressText = progressText;
        item.ProgressBar = progressBar;
        item.PauseButton = pauseButton;
        item.CancelButton = cancelButton;
        item.ExtractButton = extractButton;
        item.RemoveButton = removeButton;
        item.DeleteChunksCheckBox = deleteChunksCheckBox;
        item.Stats = statsGrid;

        SetItemControls(item);
        SetQueuedControls(item);
        return card;
    }

    private static void AddStat(Grid grid, int column, string label, string value, string tag)
    {
        var stack = new StackPanel { Tag = tag };
        var labelText = new TextBlock
        {
            Text = label,
            FontSize = 8,
            FontWeight = FontWeights.SemiBold
        };
        labelText.SetResourceReference(TextBlock.ForegroundProperty, "QueueSecondaryTextBrush");
        stack.Children.Add(labelText);

        var valueText = new TextBlock
        {
            Text = value,
            FontSize = 11,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 3, 0, 0),
            Tag = $"{tag}Value"
        };
        valueText.SetResourceReference(TextBlock.ForegroundProperty, "QueueTitleBrush");
        stack.Children.Add(valueText);

        Grid.SetColumn(stack, column);
        grid.Children.Add(stack);
    }

    private static LegacyExplorerNode CloneLegacyExplorerNode(LegacyExplorerNode source)
    {
        return new LegacyExplorerNode
        {
            Name = source.Name,
            FullPath = source.FullPath,
            ArchiveCode = source.ArchiveCode,
            IsFolder = source.IsFolder,
            Size = source.Size,
            CompressedSize = source.CompressedSize,
            CompressionMethod = source.CompressionMethod,
            Md5 = source.Md5,
            IsSelected = source.IsSelected
        };
    }

    private static string NormalizeExplorerPath(string path) => path.Replace('\\', '/');

    private async Task RunLegacyExplorerDownloadAsync(QueueItem item)
    {
        item.SetState(QueueItemState.Downloading, "Preparing download...");
        item.LastProgressTick = Environment.TickCount64;
        item.SetTotalFiles(item.LegacyExplorerSelectedPaths.Count);
        PersistHistory();

        using var service = new LegacyExplorerService();
        service.SetLogContext(item.JobId, item.Title);
        using var cts = new CancellationTokenSource();
        item.LegacyExplorerService = service;
        item.CancellationSource = cts;

        try
        {
            List<LegacyExplorerNode> selectedNodes = item.LegacyExplorerSelectedNodes;

            if (selectedNodes.Count == 0 && item.LegacyExplorerSelectedPaths.Count > 0)
            {
                List<LegacyExplorerNode> loadedNodes = await service.LoadAsync(item.LegacyExplorerArchives, cts.Token);
                HashSet<string> wanted = item.LegacyExplorerSelectedPaths
                    .Select(NormalizeExplorerPath)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);

                selectedNodes = loadedNodes
                    .SelectMany(static node => FlattenLegacyNode(node))
                    .Where(node => !node.IsFolder && wanted.Contains(NormalizeExplorerPath(node.FullPath)))
                    .ToList();

                item.LegacyExplorerSelectedNodes = selectedNodes.Select(CloneLegacyExplorerNode).ToList();
            }

            if (selectedNodes.Count == 0)
                throw new InvalidOperationException("Unable to restore the selected Legacy Explorer files.");

            item.SetTotalFiles(selectedNodes.Count);
            item.LegacyExplorerMetrics.Reset(selectedNodes.Sum(static node => Math.Max(0, node.Size)));

            var progress = new Progress<LegacyExplorerDownloadProgress>(info =>
            {
                PostUi(() => UpdateLegacyExplorerProgress(item, info));
            });

            await service.DownloadSelectedAsync(
                item.LegacyExplorerArchives, selectedNodes, item.DestinationDirectory, progress, cts.Token);

            if (item.CancelRequested)
            {
                SetCancelled(item);
                return;
            }

            item.SetState(QueueItemState.Completed, "Download completed successfully.");
            item.ProgressBar.Value = 100;
            item.ProgressText.Text = $"{selectedNodes.Count:N0}/{selectedNodes.Count:N0} files";
            item.PauseButton.IsEnabled = false;
            item.CancelButton.IsEnabled = false;
            item.RemoveButton.Visibility = Visibility.Visible;
            item.RemoveButton.IsEnabled = true;
            PersistHistory();
            UpdateQueueSummary();
        }
        catch (OperationCanceledException)
        {
            SetCancelled(item);
        }
        catch (Exception ex)
        {
            if (item.CancelRequested)
            {
                SetCancelled(item);
                return;
            }

            SetFailed(item, ex.Message);
        }
        finally
        {
            item.LegacyExplorerService = null;
            item.CancellationSource = null;
        }
    }

    private static IEnumerable<LegacyExplorerNode> FlattenLegacyNode(LegacyExplorerNode node)
    {
        yield return node;
        foreach (LegacyExplorerNode child in FlattenLegacyNodes(node.Children))
            yield return child;
    }

    private static IEnumerable<LegacyExplorerNode> FlattenLegacyNodes(IEnumerable<LegacyExplorerNode> nodes)
    {
        foreach (LegacyExplorerNode node in nodes)
        {
            yield return node;
            foreach (LegacyExplorerNode child in FlattenLegacyNodes(node.Children))
                yield return child;
        }
    }

    private void UpdateLegacyExplorerProgress(QueueItem item, LegacyExplorerDownloadProgress progress)
    {
        if (item.CancelRequested) return;

        double percent = progress.TotalBytes > 0
            ? progress.CompletedBytes * 100d / progress.TotalBytes
            : progress.TotalFiles > 0
                ? progress.CompletedFiles * 100d / progress.TotalFiles
                : 0;

        item.LastProgressTick = Environment.TickCount64;
        item.ProgressBar.Value = Math.Clamp(percent, 0, 100);
        item.ProgressText.Text = $"{progress.CompletedFiles:N0} / {progress.TotalFiles:N0} files";
        item.StatusDetailText.Text = progress.StatusText;
        item.StatusText.Text = item.LegacyExplorerService?.IsPaused == true ? "PAUSED" : "DOWNLOADING";

        UnifiedTransferMetricsSnapshot metrics = item.LegacyExplorerMetrics.Update(
            availableBytes: progress.CompletedBytes,
            transferredBytes: progress.CompletedBytes);

        SetStatValue(item, "Files", $"{progress.CompletedFiles:N0}/{progress.TotalFiles:N0}");
        SetStatValue(item, "Speed", FormatSpeed(metrics.SpeedBytesPerSecond));
        SetStatValue(item, "Eta", FormatEta(metrics.Eta));
        item.PauseButton.Content = item.LegacyExplorerService?.IsPaused == true ? "RESUME" : "PAUSE";
    }

    private async Task RunLegacyDownloadAsync(QueueItem item)
    {
        if (item.IsLegacyExplorer)
        {
            await RunLegacyExplorerDownloadAsync(item);
            return;
        }

        item.SetState(QueueItemState.Downloading, "Preparing download...");
        item.LastProgressTick = Environment.TickCount64;
        PersistHistory();

        using var service = new DownloadService();
        service.SetLogContext(item.JobId, item.Title);
        using var cts = new CancellationTokenSource();

        item.LegacyService = service;
        item.CancellationSource = cts;

        try
        {
            item.SetTotalFiles(item.LegacyUrls.Count);

            var progress = new Progress<DownloadProgressInfo>(info =>
            {
                PostUi(() =>
                {
                    if (item.CancelRequested) return;
                    UpdateLegacyProgress(item, info);
                });
            });

            await service.DownloadAllAsync(
                item.LegacyUrls.ToList(),
                item.DestinationDirectory,
                progress,
                cts.Token);

            if (item.CancelRequested)
            {
                SetCancelled(item);
                return;
            }

            item.SetState(QueueItemState.Completed, "Download completed successfully.");
            item.ProgressBar.Value = 100;
            item.ProgressText.Text = $"{item.LegacyUrls.Count:N0}/{item.LegacyUrls.Count:N0} files";
            item.PauseButton.IsEnabled = false;
            item.CancelButton.IsEnabled = false;
            item.ExtractButton.Visibility = Visibility.Collapsed;
            item.RemoveButton.Visibility = Visibility.Collapsed;

            PersistHistory();
            UpdateQueueSummary();
        }
        catch (OperationCanceledException)
        {
            SetCancelled(item);
        }
        catch (Exception ex)
        {
            if (item.CancelRequested)
            {
                SetCancelled(item);
                return;
            }

            SetFailed(item, ex.Message);
        }
    }

    private async Task RunSophonDownloadAsync(QueueItem item)
    {
        long operationGeneration = ++item.OperationGeneration;

        item.SetState(QueueItemState.Preparing, "Loading Sophon manifest...");
        SetSophonPreparingControls(item);
        PersistHistory();

        item.SophonOperationCancellationSource?.Dispose();

        var operationCts = new CancellationTokenSource();
        item.SophonOperationCancellationSource = operationCts;

        CancellationToken cancellationToken = operationCts.Token;

        var service = new SophonDownloadService();
        service.SetLogContext(item.JobId, item.Title);
        item.SophonService = service;

        service.ChunkProgressCallback = progress =>
            PostUi(() =>
            {
                if (!IsCurrentSophonOperation(item, operationGeneration)) return;
                UpdateSophonChunkProgress(item, progress);
            });

        service.ExtractionProgressCallback = progress =>
            PostUi(() =>
            {
                if (!IsCurrentSophonOperation(item, operationGeneration)) return;
                UpdateSophonExtractionProgress(item, progress);
            });

        service.ChunkDownloadCompletedCallback = () =>
            PostUi(() =>
            {
                if (!IsCurrentSophonOperation(item, operationGeneration)) return;

                item.SophonChunksReady = true;
                item.SetState(
                    QueueItemState.ReadyToExtract,
                    item.IsPatch
                        ? "All required patch chunks are ready."
                        : "All required chunks are ready.");
                item.ProgressBar.Value = 100;
                item.ProgressText.Text = "100%";
                item.PauseButton.IsEnabled = false;
                item.PauseButton.Content = "PAUSE";
                item.CancelButton.IsEnabled = false;
                item.ExtractButton.Visibility = Visibility.Visible;
                item.ExtractButton.IsEnabled = true;
                item.RemoveButton.Visibility = Visibility.Collapsed;

                PersistHistory();
                UpdateQueueSummary();
            });

        service.DownloadCancelledCallback = () =>
            PostUi(() =>
            {
                if (!IsCurrentSophonOperation(item, operationGeneration)) return;
                SetCancelled(item);
            });

        service.ExtractionCompletedCallback = () =>
            PostUi(() =>
            {
                if (!IsCurrentSophonOperation(item, operationGeneration)) return;

                item.SetState(QueueItemState.Completed, "Extraction completed successfully.");
                item.ProgressBar.Value = 100;
                item.ExtractButton.IsEnabled = false;
                item.PauseButton.IsEnabled = false;
                item.CancelButton.IsEnabled = false;
                item.DeleteChunksCheckBox.IsEnabled = false;
                item.RemoveButton.Visibility = Visibility.Visible;
                item.RemoveButton.IsEnabled = true;
                item.ExtractButton.Visibility = Visibility.Collapsed;

                PersistHistory();
                UpdateQueueSummary();
            });

        service.ExtractionCancelledCallback = () =>
            PostUi(() =>
            {
                if (!item.CancelRequested) return;

                item.CancelRequested = false;
                item.ExtractRunning = false;
                item.SetState(QueueItemState.ReadyToExtract,
                    item.IsPatch
                        ? "Extraction cancelled. Patch chunks are still ready."
                        : "Extraction cancelled. Chunks are still ready.");
                item.ProgressBar.Value = 0;
                item.ProgressText.Text = item.IsPatch
                    ? "Patch chunks ready"
                    : "Chunks ready";
                item.PauseButton.IsEnabled = false;
                item.PauseButton.Content = "PAUSE";
                item.CancelButton.IsEnabled = false;
                item.ExtractButton.Visibility = Visibility.Visible;
                item.ExtractButton.IsEnabled = true;
                item.ExtractButton.Content = "EXTRACT";
                item.ExtractButton.Tag = "Extract";
                item.RemoveButton.Visibility = Visibility.Collapsed;
                item.RemoveButton.IsEnabled = false;
                item.DeleteChunksCheckBox.IsEnabled = true;

                PersistHistory();
                UpdateQueueSummary();
            });

        try
        {
            ManifestConfig manifest;

            if (item.Manifest is null ||
                !string.Equals(item.Manifest.data.tag, item.Version, StringComparison.OrdinalIgnoreCase))
            {
                cancellationToken.ThrowIfCancellationRequested();

                manifest = await service
                    .LoadManifestAsync(item.Game!, item.Version!, item.Channel!)
                    .WaitAsync(cancellationToken);

                if (!IsCurrentSophonOperation(item, operationGeneration)) return;
                item.Manifest = manifest;
            }
            else
            {
                manifest = item.Manifest;
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (!IsCurrentSophonOperation(item, operationGeneration)) return;

            item.SetState(QueueItemState.Preparing, "Loading content manifest...");
            SetSophonPreparingControls(item);
            PersistHistory();

            if (item.SelectedContent.Count == 0 && item.SelectedCategoryIds.Count > 0)
                item.SelectedContent = BuildSelectedHistoryContent(manifest, item.SelectedCategoryIds);

            if (item.SelectedContent.Count == 0)
            {
                throw new InvalidOperationException("No Sophon content was selected. The saved content selection could not be restored.");
            }

            cancellationToken.ThrowIfCancellationRequested();

            SophonContentSet content;
            if (item.IsPatch)
            {
                if (string.IsNullOrWhiteSpace(item.PatchFromVersion))
                    throw new InvalidOperationException("Patch source version is missing.");

                item.SetState(QueueItemState.Preparing, $"Loading patch manifest {item.PatchFromVersion} → {item.Version}...");
                ManifestConfig fromManifest = await service
                    .LoadManifestAsync(item.Game!, item.PatchFromVersion!, item.Channel!)
                    .WaitAsync(cancellationToken);

                content = await service
                    .LoadSelectedPatchContentAsync(item.Game!, fromManifest, manifest, item.SelectedContent, cancellationToken)
                    .WaitAsync(cancellationToken);
            }
            else
            {
                content = await service
                    .LoadSelectedContentAsync(manifest, item.SelectedContent, cancellationToken)
                    .WaitAsync(cancellationToken);
            }

            if (!IsCurrentSophonOperation(item, operationGeneration)) return;

            if (item.SophonSelectedFilePaths.Count > 0)
                content = FilterSophonContent(content, item.SophonSelectedFilePaths);

            item.SophonContent = content;
            SetSophonMetadata(item, content);

            item.SetState(QueueItemState.Downloading, "Downloading chunks...");
            item.LastProgressTick = Environment.TickCount64;
            SetSophonDownloadingControls(item);
            PersistHistory();

            cancellationToken.ThrowIfCancellationRequested();

            await service
                .StartChunkDownloadAsync(content, item.DestinationDirectory)
                .WaitAsync(cancellationToken);

            if (!IsCurrentSophonOperation(item, operationGeneration)) return;
            if (item.SophonChunksReady) return;

            item.SetState(QueueItemState.ReadyToExtract, "All required chunks are ready.");
            item.ExtractButton.Visibility = Visibility.Visible;
            item.ExtractButton.IsEnabled = true;
            item.PauseButton.IsEnabled = false;
            item.CancelButton.IsEnabled = false;

            PersistHistory();
        }
        catch (OperationCanceledException)
        {
            if (IsCurrentSophonOperation(item, operationGeneration) && item.CancelRequested)
                SetCancelled(item);
        }
        catch (Exception ex)
        {
            if (!IsCurrentSophonOperation(item, operationGeneration)) return;

            if (item.CancelRequested)
            {
                SetCancelled(item);
                return;
            }

            SetFailed(item, ex.Message);
        }
        finally
        {
            if (ReferenceEquals(item.SophonOperationCancellationSource, operationCts))
                item.SophonOperationCancellationSource = null;

            operationCts.Dispose();
        }
    }

    private void ExtractItem(QueueItem item)
    {
        if (_shuttingDown) return;
        if (item.Type != QueueItemType.Sophon) return;
        if (item.BackgroundOperationTask is not null && !item.BackgroundOperationTask.IsCompleted) return;
        item.BackgroundOperationTask = ExtractItemAsync(item);
    }

    private async Task ExtractItemAsync(QueueItem item)
    {

        SophonContentSet? content = item.SophonContent;
        if (content is null || item.ExtractRunning) return;

        item.ExtractRunning = true;
        item.CancelRequested = false;
        item.ExtractButton.IsEnabled = false;
        item.PauseButton.IsEnabled = true;
        item.CancelButton.IsEnabled = true;
        item.RemoveButton.Visibility = Visibility.Collapsed;
        item.DeleteChunksCheckBox.IsEnabled = false;

        item.SetState(QueueItemState.Extracting, "Extracting files...");
        PersistHistory();

        if (item.SophonService is null)
        {
            item.SophonService = new SophonDownloadService();
            item.SophonService.SetLogContext(item.JobId, item.Title);
            ConfigureSophonServiceCallbacks(item, item.SophonService);
        }

        try
        {
            SophonDownloadService service = item.SophonService;

            await service.StartExtractionAsync(
                content, item.DestinationDirectory,
                false, item.DeleteChunksAfterExtraction);

            if (item.CancelRequested) return;

            item.SetState(QueueItemState.Completed, "Extraction completed successfully.");
            item.ProgressBar.Value = 100;
            item.ExtractButton.IsEnabled = false;
            item.PauseButton.IsEnabled = false;
            item.CancelButton.IsEnabled = false;
            item.DeleteChunksCheckBox.IsEnabled = false;
            item.RemoveButton.Visibility = Visibility.Visible;
            item.RemoveButton.IsEnabled = true;
            item.ExtractButton.Visibility = Visibility.Collapsed;

            PersistHistory();
            UpdateQueueSummary();
        }
        catch (OperationCanceledException)
        {
            if (item.CancelRequested) SetCancelled(item);
        }
        catch (SophonChunkValidationException ex)
        {
            if (item.CancelRequested)
            {
                SetCancelled(item);
                return;
            }

            item.RequiresSophonRepair = true;
            item.SophonRepairChunkIds = [ex.ChunkId];
            item.SetState(QueueItemState.Failed, $"Corrupt patch chunk detected. Repair required: {ex.ChunkId}");
            item.ProgressBar.Value = 0;
            item.PauseButton.IsEnabled = false;
            item.CancelButton.IsEnabled = false;
            item.ExtractButton.Visibility = Visibility.Visible;
            item.ExtractButton.IsEnabled = true;
            item.ExtractButton.Content = "REPAIR";
            item.ExtractButton.Tag = "Repair";
            item.DeleteChunksCheckBox.IsEnabled = false;
            item.RemoveButton.Visibility = Visibility.Visible;
            item.RemoveButton.IsEnabled = true;
            PersistHistory();
            if (!_shuttingDown)
                UpdateQueueSummary();
        }
        catch (Exception ex)
        {
            if (item.CancelRequested)
            {
                SetCancelled(item);
                return;
            }

            SetFailed(item, ex.Message);
        }
        finally
        {
            item.ExtractRunning = false;
        }
    }

    private void RepairSophonItem(QueueItem item)
    {
        if (_shuttingDown) return;
        if (item.Type != QueueItemType.Sophon || item.SophonContent is null || item.SophonRepairChunkIds.Count == 0)
            return;
        if (item.BackgroundOperationTask is not null && !item.BackgroundOperationTask.IsCompleted) return;
        item.BackgroundOperationTask = RepairSophonItemAsync(item);
    }

    private async Task RepairSophonItemAsync(QueueItem item)
    {

        item.RequiresSophonRepair = false;
        item.ExtractRunning = false;
        item.CancelRequested = false;
        item.SetState(QueueItemState.Preparing, "Repairing corrupted chunks...");
        item.PauseButton.Visibility = Visibility.Visible;
        item.PauseButton.IsEnabled = false;
        item.PauseButton.Content = "RESUME";
        item.CancelButton.Visibility = Visibility.Visible;
        item.CancelButton.IsEnabled = true;
        item.ExtractButton.Visibility = Visibility.Collapsed;
        item.ExtractButton.IsEnabled = false;
        item.DeleteChunksCheckBox.IsEnabled = true;
        item.RemoveButton.Visibility = Visibility.Collapsed;

        try
        {
            var chunkStore = new Core.ChunkStore(item.DestinationDirectory);
            chunkStore.DeleteChunks(item.SophonRepairChunkIds);
        }
        catch (Exception ex)
        {
            SetFailed(item, $"Unable to remove corrupted patch chunks: {ex.Message}");
            return;
        }
        finally
        {
            item.SophonRepairChunkIds.Clear();
        }

        item.SetState(QueueItemState.Queued, "Waiting for an available queue slot...");
        SetQueuedControls(item);
        PersistHistory();
        UpdateQueueSummary();
        SchedulePendingDownloads();
    }

    private static SophonContentSet FilterSophonContent(
        SophonContentSet content, IReadOnlyCollection<string> selectedFilePaths)
    {
        HashSet<string> wanted = selectedFilePaths
            .Select(NormalizeExplorerPath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        List<SophonChunkFile> files = content.AllFiles
            .Where(file => wanted.Contains(NormalizeExplorerPath(file.File)))
            .ToList();

        Dictionary<string, string> manifest = content.FileManifest
            .Where(pair => wanted.Contains(NormalizeExplorerPath(pair.Key)))
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);

        if (files.Count == 0)
            throw new InvalidOperationException("The selected Sophon Explorer files could not be resolved from the content manifest.");

        return new SophonContentSet
        {
            AllFiles = files,
            FileManifest = manifest,
            SelectedContent = content.SelectedContent,
            IsPatch = content.IsPatch,
            IsLdiffPatch = content.IsLdiffPatch,
            PatchFromVersion = content.PatchFromVersion,
            PatchToVersion = content.PatchToVersion
        };
    }

    private void ConfigureSophonServiceCallbacks(QueueItem item, SophonDownloadService service)
    {
        service.ChunkProgressCallback = progress =>
            PostUi(() =>
            {
                if (item.CancelRequested) return;
                UpdateSophonChunkProgress(item, progress);
            });

        service.ExtractionProgressCallback = progress =>
            PostUi(() =>
            {
                if (item.CancelRequested) return;
                UpdateSophonExtractionProgress(item, progress);
            });

        service.ChunkDownloadCompletedCallback = () =>
            PostUi(() =>
            {
                if (item.CancelRequested) return;

                item.SophonChunksReady = true;
                item.SetState(QueueItemState.ReadyToExtract, item.IsPatch
                    ? "All required patch chunks are ready."
                    : "All required chunks are ready.");
                item.ProgressBar.Value = 100;
                item.ProgressText.Text = "100%";
                item.PauseButton.IsEnabled = false;
                item.CancelButton.IsEnabled = false;
                item.ExtractButton.Visibility = Visibility.Visible;
                item.ExtractButton.IsEnabled = true;
                item.ExtractButton.Content = "EXTRACT";
                item.ExtractButton.Tag = "Extract";
                item.RequiresSophonRepair = false;
                item.SophonRepairChunkIds.Clear();

                PersistHistory();
                UpdateQueueSummary();
            });

        service.DownloadCancelledCallback = () =>
            PostUi(() =>
            {
                if (item.CancelRequested) return;
                SetCancelled(item);
            });

        service.ExtractionCompletedCallback = () =>
            PostUi(() =>
            {
                if (item.CancelRequested) return;

                item.SetState(QueueItemState.Completed, "Extraction completed successfully.");
                item.ProgressBar.Value = 100;
                item.ExtractButton.IsEnabled = false;
                item.PauseButton.IsEnabled = false;
                item.CancelButton.IsEnabled = false;
                item.DeleteChunksCheckBox.IsEnabled = false;
                item.RemoveButton.Visibility = Visibility.Visible;
                item.RemoveButton.IsEnabled = true;
                item.ExtractButton.Visibility = Visibility.Collapsed;

                PersistHistory();
                UpdateQueueSummary();
            });

        service.ExtractionCancelledCallback = () =>
            PostUi(() =>
            {
                if (!item.CancelRequested) return;

                item.CancelRequested = false;
                item.ExtractRunning = false;
                item.SetState(QueueItemState.ReadyToExtract, item.IsPatch
                    ? "Extraction cancelled. Patch chunks are still ready."
                    : "Extraction cancelled. Chunks are still ready.");
                item.ProgressBar.Value = 0;
                item.ProgressText.Text = item.IsPatch
                    ? "Patch chunks ready"
                    : "Chunks ready";
                item.PauseButton.IsEnabled = false;
                item.PauseButton.Content = "PAUSE";
                item.CancelButton.IsEnabled = false;
                item.ExtractButton.Visibility = Visibility.Visible;
                item.ExtractButton.IsEnabled = true;
                item.ExtractButton.Content = "EXTRACT";
                item.ExtractButton.Tag = "Extract";
                item.RemoveButton.Visibility = Visibility.Collapsed;
                item.RemoveButton.IsEnabled = false;
                item.DeleteChunksCheckBox.IsEnabled = true;

                PersistHistory();
                UpdateQueueSummary();
            });
    }

    private void PauseResumeItem(QueueItem item)
    {
        if (_shuttingDown) return;
        if (item.State == QueueItemState.Queued) return;
        if (item.State == QueueItemState.Cancelled || item.State == QueueItemState.Failed)
        {
            ResumeItem(item);
            return;
        }

        if (item.Type == QueueItemType.Legacy)
        {
            if (item.IsLegacyExplorer)
            {
                LegacyExplorerService? explorerService = item.LegacyExplorerService;
                if (explorerService is null) return;

                if (explorerService.IsPaused)
                {
                    explorerService.Resume();
                    item.SetState(QueueItemState.Downloading, "Download resumed.");
                    item.LastProgressTick = Environment.TickCount64;
                    item.PauseButton.Content = "PAUSE";
                }
                else
                {
                    explorerService.Pause();
                    item.StatusText.Text = "PAUSED";
                    item.StatusDetailText.Text = "Download paused.";
                    item.PauseButton.Content = "RESUME";
                }

                PersistHistory();
                return;
            }

            DownloadService? service = item.LegacyService;
            if (service is null) return;

            if (service.IsPaused)
            {
                service.Resume();
                item.SetState(QueueItemState.Downloading, "Download resumed.");
                item.LastProgressTick = Environment.TickCount64;
                item.PauseButton.Content = "PAUSE";
            }
            else
            {
                service.Pause();
                item.StatusText.Text = "PAUSED";
                item.StatusDetailText.Text = "Download paused.";
                item.PauseButton.Content = "RESUME";
            }

            PersistHistory();
            return;
        }

        if (item.State == QueueItemState.Preparing) return;

        SophonDownloadService? service2 = item.SophonService;
        if (service2 is null) return;

        if (service2.IsPaused)
        {
            service2.TogglePause();
            item.PauseButton.Content = "PAUSE";
            item.StatusText.Text = item.ExtractRunning ? "EXTRACTING" : "DOWNLOADING";
            item.StatusDetailText.Text = item.ExtractRunning ? "Extraction resumed." : "Download resumed.";
        }
        else
        {
            service2.TogglePause();
            item.PauseButton.Content = "RESUME";
            item.StatusText.Text = "PAUSED";
            item.StatusDetailText.Text = item.ExtractRunning ? "Extraction paused." : "Download paused.";
        }

        PersistHistory();
    }

    private void ResumeItem(QueueItem item)
    {
        if (_shuttingDown) return;

        if (item.State is not QueueItemState.Completed &&
            item.State is not QueueItemState.Cancelled &&
            item.State is not QueueItemState.Failed)
        {
            return;
        }

        item.CancelRequested = false;
        item.ExtractRunning = false;
        item.SophonChunksReady = false;
        item.RemoveButton.Visibility = Visibility.Collapsed;
        item.RemoveButton.IsEnabled = false;
        item.PauseButton.Visibility = Visibility.Visible;
        item.PauseButton.IsEnabled = item.Type == QueueItemType.Legacy;
        item.PauseButton.Content = "RESUME";
        item.CancelButton.Visibility = Visibility.Visible;
        item.CancelButton.IsEnabled = true;
        item.ExtractButton.Visibility = Visibility.Collapsed;
        item.ExtractButton.IsEnabled = false;
        item.ProgressBar.Value = 0;
        item.ProgressText.Text = item.Type == QueueItemType.Sophon ? "0 / 0 chunks" : "0 / 0 files";
        item.SetState(QueueItemState.Preparing, "Resuming download...");

        if (item.Type == QueueItemType.Sophon)
            SetSophonPreparingControls(item);

        item.SetState(QueueItemState.Queued, "Waiting for an available queue slot...");
        SetQueuedControls(item);
        PersistHistory();
        UpdateQueueSummary();
        SchedulePendingDownloads();
    }

    private void CancelItem(QueueItem item)
    {
        if (_shuttingDown) return;

        if (item.CancelRequested ||
            item.State == QueueItemState.Completed ||
            item.State == QueueItemState.Cancelled ||
            item.State == QueueItemState.Failed)
        {
            return;
        }

        if (item.State == QueueItemState.Queued && !item.SchedulerRunning)
        {
            item.CancelRequested = false;
            SetCancelled(item);
            SchedulePendingDownloads();
            return;
        }

        item.CancelRequested = true;
        item.SetState(QueueItemState.Cancelling, item.Type == QueueItemType.Sophon && item.ExtractRunning
            ? "Cancelling extraction..."
            : "Stopping operation...");
        PersistHistory();

        if (item.Type == QueueItemType.Legacy)
        {
            item.PauseButton.IsEnabled = false;
            item.CancelButton.IsEnabled = false;
            item.ExtractButton.IsEnabled = false;
            item.DeleteChunksCheckBox.IsEnabled = false;
            TryCancel(item.CancellationSource);
            return;
        }

        if (item.ExtractRunning)
        {
            item.SophonService?.Cancel();
            return;
        }

        item.OperationGeneration++;
        TryCancel(item.SophonOperationCancellationSource);
        item.SophonService?.Cancel();

        SetCancelled(item);
    }

    private static void TryCancel(CancellationTokenSource? cancellationSource)
    {
        if (cancellationSource is null) return;

        try
        {
            cancellationSource.Cancel();
        }
        catch (ObjectDisposedException) {}
    }

    private void RemoveItem(QueueItem item)
    {
        if (_shuttingDown) return;

        if (item.State is not QueueItemState.Completed &&
            item.State is not QueueItemState.Cancelled &&
            item.State is not QueueItemState.Failed)
        {
            return;
        }

        TryCancel(item.CancellationSource);
        item.CancellationSource?.Dispose();
        item.CancellationSource = null;

        TryCancel(item.SophonOperationCancellationSource);
        item.SophonOperationCancellationSource?.Dispose();
        item.SophonOperationCancellationSource = null;

        item.OperationGeneration++;

        try { item.SophonService?.Dispose(); }
        catch {}

        item.SophonService = null;

        if (item.Card is not null)
            QueueItemsPanel.Children.Remove(item.Card);

        _items.Remove(item);
        UpdateQueueSummary();
        PersistHistory();
    }

    private static bool IsCurrentSophonOperation(QueueItem item, long operationGeneration)
    {
        return item.OperationGeneration == operationGeneration &&
               item.State != QueueItemState.Cancelled &&
               item.State != QueueItemState.Failed &&
               !item.CancelRequested;
    }

    private void UpdateLegacyProgress(QueueItem item, DownloadProgressInfo progress)
    {
        if (item.CancelRequested) return;
        item.LastProgressTick = Environment.TickCount64;

        double percent = progress.Percent ??
            (progress.TotalBytes is > 0
                ? progress.BytesReceived * 100d / progress.TotalBytes.Value
                : 0);

        item.ProgressBar.Value = Math.Clamp(percent, 0, 100);
        item.ProgressText.Text = progress.TotalBytes is > 0
            ? $"{FormatSize(progress.BytesReceived)} / {FormatSize(progress.TotalBytes.Value)}"
            : $"{progress.FileIndex:N0}/{progress.FileCount:N0} files";

        item.StatusDetailText.Text = string.IsNullOrWhiteSpace(progress.FileName)
            ? "Downloading..."
            : progress.FileName;

        SetStatValue(item, "Archives", $"{progress.FileIndex:N0}/{progress.FileCount:N0}");
        SetStatValue(item, "Required", FormatSize(progress.TotalBytes));
        SetStatValue(item, "Cached", FormatSize(progress.BytesReceived));
        SetStatValue(item, "Partial", "--");
        SetStatValue(item, "Speed", FormatSpeed(progress.SpeedBytesPerSecond));
        SetStatValue(item, "Eta", FormatEta(progress.Eta));
        item.StatusText.Text = "DOWNLOADING";
        item.PauseButton.Content = item.LegacyService?.IsPaused == true ? "RESUME" : "PAUSE";
    }

    private void UpdateSophonChunkProgress(QueueItem item, ChunkDownloadProgress progress)
    {
        if (item.CancelRequested) return;
        long availableBytes = Math.Clamp(progress.AvailableBytes, 0, progress.TotalBytes);

        double percent = progress.TotalBytes > 0
            ? availableBytes * 100d / progress.TotalBytes
            : progress.TotalChunks > 0
                ? progress.CompletedChunks * 100d / progress.TotalChunks
                : 0;

        item.LastProgressTick = Environment.TickCount64;
        item.ProgressBar.Value = Math.Clamp(percent, 0, 100);
        item.ProgressText.Text = item.IsPatch
            ? $"{progress.CompletedChunks:N0} / {progress.TotalChunks:N0} patch chunks"
            : $"{progress.CompletedChunks:N0} / {progress.TotalChunks:N0} chunks";
        item.StatusDetailText.Text = progress.StatusText ?? "Downloading chunks...";
        SetStatValue(item, "Files", item.SophonContent?.FileCount.ToString("N0") ?? "0");
        SetStatValue(item, "Chunks", progress.TotalChunks.ToString("N0"));
        SetStatValue(item, "Required", FormatSize(progress.TotalBytes));
        SetStatValue(item, "Cached", FormatSize(progress.CachedBytes));
        SetStatValue(item, "Partial", FormatSize(progress.PartialCacheBytes));
        SetStatValue(item, "Speed", FormatSpeed(progress.AggregateSpeedBytesPerSecond));
        SetStatValue(item, "Eta", FormatEta(progress.AggregateEta));
        item.StatusText.Text = item.SophonService?.IsPaused == true ? "PAUSED" : "DOWNLOADING";
        item.PauseButton.Content = item.SophonService?.IsPaused == true ? "RESUME" : "PAUSE";
    }

    private void UpdateSophonExtractionProgress(QueueItem item, ExtractionProgress progress)
    {
        if (item.CancelRequested) return;

        double percent = progress.TotalBytes > 0
            ? progress.ExtractedBytes * 100d / progress.TotalBytes
            : progress.TotalFiles > 0
                ? progress.CompletedFiles * 100d / progress.TotalFiles
                : 0;

        item.ProgressBar.Value = Math.Clamp(percent, 0, 100);
        item.ProgressText.Text = $"{progress.CompletedFiles:N0} / {progress.TotalFiles:N0} files";
        item.StatusDetailText.Text = progress.StatusText ?? "Extracting files...";
        item.StatusText.Text = item.SophonService?.IsPaused == true ? "PAUSED" : "EXTRACTING";
        item.PauseButton.Content = item.SophonService?.IsPaused == true ? "RESUME" : "PAUSE";
        SetStatValue(item, "Files", $"{progress.CompletedFiles:N0}/{progress.TotalFiles:N0}");
        SetStatValue(item, "Speed", "--");
        SetStatValue(item, "Eta", "--");
    }

    private static string GetSelectedContentText(QueueItem item)
    {
        IEnumerable<string> names = item.Type == QueueItemType.Legacy
            ? item.SelectedContentNames
            : item.SelectedContent
                .Where(option => option.IsSelected)
                .Select(option => option.Name)
                .Where(name => !string.IsNullOrWhiteSpace(name));

        string result = string.Join(", ", names);

        if (!string.IsNullOrWhiteSpace(result))
            return $"CONTENT  •  {result}";

        if (item.SelectedCategoryIds.Count == 0 && item.SelectedContentNames.Count == 0)
            return "CONTENT  •  None";

        return $"CONTENT  •  {string.Join(", ", item.SelectedCategoryIds)}";
    }

    private static void UpdateSelectedContentText(QueueItem item)
    {
        if (item.SelectedContentText is not null)
            item.SelectedContentText.Text = GetSelectedContentText(item);
    }

    private void SetSophonMetadata(QueueItem item, SophonContentSet content)
    {
        UpdateSelectedContentText(item);
        SetStatValue(item, "Files", content.FileCount.ToString("N0"));
        SetStatValue(item, "Chunks", content.UniqueChunkCount.ToString("N0"));
        SetStatValue(item, "Required", FormatSize(content.UniqueCompressedSize));
        SetStatValue(item, "Cached", "0 B");
        SetStatValue(item, "Partial", "0 B");
        SetStatValue(item, "Speed", "--");
        SetStatValue(item, "Eta", "--");
        item.ProgressText.Text = $"0 / {content.UniqueChunkCount:N0} chunks";
    }

    private void SetStatValue(QueueItem item, string tag, string value)
    {
        if (item.Stats is null) return;

        foreach (StackPanel stack in item.Stats.Children.OfType<StackPanel>())
        {
            if (!string.Equals(stack.Tag as string, tag, StringComparison.OrdinalIgnoreCase))
                continue;

            TextBlock? target = stack.Children.OfType<TextBlock>().FirstOrDefault(text =>
                string.Equals(text.Tag as string, $"{tag}Value", StringComparison.OrdinalIgnoreCase));

            if (target is not null)
                target.Text = value;

            break;
        }
    }

    private static void SetQueuedControls(QueueItem item)
    {
        item.PauseButton.Visibility = Visibility.Visible;
        item.PauseButton.IsEnabled = false;
        item.PauseButton.Content = "PAUSE";
        item.CancelButton.Visibility = Visibility.Visible;
        item.CancelButton.IsEnabled = true;
        item.ExtractButton.Visibility = Visibility.Collapsed;
        item.ExtractButton.IsEnabled = false;
        item.RemoveButton.Visibility = Visibility.Collapsed;
    }

    private static void SetActiveControls(QueueItem item)
    {
        item.PauseButton.Visibility = Visibility.Visible;
        item.PauseButton.IsEnabled = true;
        item.PauseButton.Content = "PAUSE";
        item.CancelButton.Visibility = Visibility.Visible;
        item.CancelButton.IsEnabled = true;
        item.ExtractButton.Visibility = Visibility.Collapsed;
        item.ExtractButton.IsEnabled = false;
        item.RemoveButton.Visibility = Visibility.Collapsed;
    }

    private void SetItemControls(QueueItem item)
    {
        item.ExtractButton.Visibility = Visibility.Collapsed;
        item.RemoveButton.Visibility = Visibility.Collapsed;
        item.PauseButton.Visibility = Visibility.Visible;
        item.CancelButton.Visibility = Visibility.Visible;
        item.PauseButton.IsEnabled = true;
        item.CancelButton.IsEnabled = true;
        item.DeleteChunksCheckBox.Visibility = item.Type == QueueItemType.Sophon
            ? Visibility.Visible
            : Visibility.Collapsed;

        item.DeleteChunksCheckBox.IsEnabled = item.Type == QueueItemType.Sophon;
    }

    private static void SetSophonPreparingControls(QueueItem item)
    {
        item.PauseButton.Visibility = Visibility.Visible;
        item.PauseButton.IsEnabled = false;
        item.PauseButton.Content = "RESUME";
        item.CancelButton.Visibility = Visibility.Visible;
        item.CancelButton.IsEnabled = true;
        item.ExtractButton.Visibility = Visibility.Collapsed;
        item.ExtractButton.IsEnabled = false;
        item.ExtractButton.Content = "EXTRACT";
        item.ExtractButton.Tag = "Extract";
        item.RemoveButton.Visibility = Visibility.Collapsed;
        item.DeleteChunksCheckBox.IsEnabled = true;
    }

    private static void SetSophonDownloadingControls(QueueItem item)
    {
        item.PauseButton.Visibility = Visibility.Visible;
        item.PauseButton.IsEnabled = true;
        item.PauseButton.Content = "PAUSE";
        item.CancelButton.Visibility = Visibility.Visible;
        item.CancelButton.IsEnabled = true;
        item.ExtractButton.Visibility = Visibility.Collapsed;
        item.ExtractButton.IsEnabled = false;
        item.ExtractButton.Content = "EXTRACT";
        item.ExtractButton.Tag = "Extract";
        item.RemoveButton.Visibility = Visibility.Collapsed;
    }

    private void SetCancelled(QueueItem item)
    {
        item.CancelRequested = false;
        TryCancel(item.SophonOperationCancellationSource);
        item.SetState(QueueItemState.Cancelled, "Operation cancelled. Partial data has been kept.");
        item.ProgressBar.Value = 0;
        item.PauseButton.Visibility = Visibility.Visible;
        item.PauseButton.IsEnabled = true;
        item.PauseButton.Content = "RESUME";
        item.CancelButton.Visibility = Visibility.Collapsed;
        item.CancelButton.IsEnabled = false;
        item.ExtractButton.Visibility = Visibility.Collapsed;
        item.ExtractButton.IsEnabled = false;
        item.DeleteChunksCheckBox.IsEnabled = false;
        item.RemoveButton.Visibility = Visibility.Visible;
        item.RemoveButton.IsEnabled = true;

        if (!_shuttingDown)
        {
            PersistHistory();
            UpdateQueueSummary();
        }
    }

    private void SetFailed(QueueItem item, string message)
    {
        item.CancelRequested = false;
        item.SetState(QueueItemState.Failed, string.IsNullOrWhiteSpace(message) ? "Download failed." : message);
        item.PauseButton.Visibility = Visibility.Visible;
        item.PauseButton.IsEnabled = true;
        item.PauseButton.Content = "RESUME";
        item.CancelButton.Visibility = Visibility.Collapsed;
        item.CancelButton.IsEnabled = false;
        item.ExtractButton.Visibility = Visibility.Collapsed;
        item.ExtractButton.IsEnabled = false;
        item.DeleteChunksCheckBox.IsEnabled = false;
        item.RemoveButton.Visibility = Visibility.Visible;
        item.RemoveButton.IsEnabled = true;

        if (!_shuttingDown)
        {
            PersistHistory();
            UpdateQueueSummary();
        }
    }

    private void ScrollItemIntoView(QueueItem item)
    {
        if (item.Card is null) return;
        item.Card.BringIntoView();
    }

    private void UpdateQueueSummary()
    {
        int active = _items.Count(item => item.State is
            QueueItemState.Preparing or
            QueueItemState.Downloading or
            QueueItemState.Extracting or
            QueueItemState.Cancelling);

        int completed = _items.Count(item => item.State == QueueItemState.Completed);
        int cancelled = _items.Count(item => item.State == QueueItemState.Cancelled);
        int failed = _items.Count(item => item.State == QueueItemState.Failed);
        int queued = _items.Count(item => item.State == QueueItemState.Queued);

        bool empty = _items.Count == 0;
        EmptyQueuePanel.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;

        if (empty)
        {
            QueueSummaryText.Text = "No queued jobs.";
            return;
        }

        QueueSummaryText.Text =
            $"{_items.Count:N0} job(s) • {active:N0} active • {queued:N0} queued • " +
            $"{completed:N0} completed • {cancelled:N0} cancelled • {failed:N0} failed";
    }

    private async Task LoadHistoryAsync()
    {
        try
        {
            foreach (DownloadHistoryEntry entry in _historyStore.LoadEntries())
            {
                if (_shuttingDown)
                    break;

                try
                {
                    await RestoreHistoryEntryAsync(entry);
                }
                catch (Exception ex)
                {
                    Logger.Warn(ex, "Failed to restore a download history entry.");
                }
            }

            if (!_shuttingDown)
                UpdateQueueSummary();
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Failed to load download history.");
        }
    }

    private void PersistHistory()
    {
        if (_shuttingDown) return;

        try
        {
            _historyStore.Save(_items.Select(CreateHistoryEntry));
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Failed to persist download history.");
        }
    }

    private DownloadHistoryEntry CreateHistoryEntry(QueueItem item) => new()
    {
        Type = (int)item.Type,
        Title = item.Title,
        DestinationDirectory = item.DestinationDirectory,
        LegacyUrls = item.LegacyUrls.ToList(),
        GameId = item.Game?.GameId,
        GameDisplayName = item.Game?.DisplayName,
        GameRegion = item.Game?.Region,
        Version = item.Version,
        Channel = item.Channel,
        DeleteChunksAfterExtraction = item.DeleteChunksAfterExtraction,
        SelectedCategoryIds = item.SelectedCategoryIds
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList(),
        SelectedContentNames = item.SelectedContentNames
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList(),
        PatchFromVersion = item.PatchFromVersion,
        State = (int)item.State,
        StatusMessage = item.StatusDetailText?.Text,
        LegacyExplorerArchivesJson = item.IsLegacyExplorer
            ? JsonSerializer.Serialize(item.LegacyExplorerArchives.Select(archive => new LegacyExplorerArchiveHistory
            {
                Code = archive.Code,
                Name = archive.Name,
                Urls = archive.Urls.ToList()
            }))
            : null,
        LegacyExplorerSelectedPaths = item.IsLegacyExplorer
            ? item.LegacyExplorerSelectedPaths.ToList()
            : [],
        SophonSelectedFilePaths = item.SophonSelectedFilePaths.ToList()
    };

    private async Task RestoreHistoryEntryAsync(DownloadHistoryEntry entry)
    {
        if (string.IsNullOrWhiteSpace(entry.Title) ||
            string.IsNullOrWhiteSpace(entry.DestinationDirectory))
        {
            return;
        }

        QueueItemType entryType = (QueueItemType)entry.Type;
        QueueItemState entryState = (QueueItemState)entry.State;

        if (entryType == QueueItemType.Legacy)
        {
            if (!string.IsNullOrWhiteSpace(entry.LegacyExplorerArchivesJson))
            {
                List<LegacyExplorerArchiveHistory>? archiveHistory =
                    JsonSerializer.Deserialize<List<LegacyExplorerArchiveHistory>>(entry.LegacyExplorerArchivesJson);

                if (archiveHistory is null || archiveHistory.Count == 0 || entry.LegacyExplorerSelectedPaths.Count == 0)
                    return;

                var explorerItem = new QueueItem(
                    QueueItemType.Legacy, entry.Title, entry.DestinationDirectory, [], null, null, null, false)
                {
                    LegacyExplorerArchives = archiveHistory
                        .Select(archive => new LegacyExplorerArchive(archive.Code, archive.Name, archive.Urls))
                        .ToList(),
                    LegacyExplorerSelectedPaths = entry.LegacyExplorerSelectedPaths
                        .Select(NormalizeExplorerPath)
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToList(),
                    SelectedContentNames = entry.SelectedContentNames.ToList()
                };

                AddItem(explorerItem, false);

                if (IsInterruptedState(entryState))
                {
                    explorerItem.SetState(QueueItemState.Cancelled, "Download interrupted by application close. Ready to resume.");
                    explorerItem.PauseButton.Content = "RESUME";
                    explorerItem.PauseButton.IsEnabled = true;
                    explorerItem.CancelButton.IsEnabled = false;
                    explorerItem.CancelButton.Visibility = Visibility.Collapsed;
                    explorerItem.RemoveButton.Visibility = Visibility.Visible;
                    explorerItem.RemoveButton.IsEnabled = true;
                }
                else
                {
                    explorerItem.SetState(entryState, entry.StatusMessage ?? GetDefaultStateMessage(entryState));
                    ApplyRestoredStateControls(explorerItem);
                }

                return;
            }

            if (entry.LegacyUrls.Count == 0) return;

            var legacyItem = new QueueItem(
                QueueItemType.Legacy, entry.Title, entry.DestinationDirectory, entry.LegacyUrls, null, null, null, false);

            legacyItem.SelectedContentNames = entry.SelectedContentNames
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            AddItem(legacyItem, false);

            if (IsInterruptedState(entryState))
            {
                legacyItem.SetState(QueueItemState.Cancelled, "Download interrupted by application close. Ready to resume.");
                legacyItem.PauseButton.Content = "RESUME";
                legacyItem.PauseButton.IsEnabled = true;
                legacyItem.CancelButton.IsEnabled = false;
                legacyItem.CancelButton.Visibility = Visibility.Collapsed;
                legacyItem.RemoveButton.Visibility = Visibility.Visible;
                legacyItem.RemoveButton.IsEnabled = true;
            }
            else
            {
                legacyItem.SetState(entryState, entry.StatusMessage ?? GetDefaultStateMessage(entryState));
                ApplyRestoredStateControls(legacyItem);
            }

            return;
        }

        if (entryType != QueueItemType.Sophon) return;

        if (string.IsNullOrWhiteSpace(entry.GameId) ||
            string.IsNullOrWhiteSpace(entry.Version))
        {
            return;
        }

        List<GameInfo> supportedGames = SophonGameService.GetSupportedGames() ?? [];

        GameInfo? game = supportedGames.FirstOrDefault(candidate =>
            string.Equals(candidate.GameId, entry.GameId, StringComparison.OrdinalIgnoreCase));

        if (game is null) return;

        string channel = string.IsNullOrWhiteSpace(entry.Channel) ? "main" : entry.Channel;

        var sophonItem = new QueueItem(
            QueueItemType.Sophon, entry.Title, entry.DestinationDirectory, [],
            game, entry.Version, channel, entry.DeleteChunksAfterExtraction);

        sophonItem.PatchFromVersion = entry.PatchFromVersion;
        sophonItem.SophonSelectedFilePaths = entry.SophonSelectedFilePaths
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(NormalizeExplorerPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        sophonItem.SelectedCategoryIds = entry.SelectedCategoryIds?
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList() ?? [];

        AddItem(sophonItem, false);

        sophonItem.DeleteChunksCheckBox.IsChecked = entry.DeleteChunksAfterExtraction;

        if (entryState == QueueItemState.Completed)
        {
            sophonItem.SetState(QueueItemState.Completed, entry.StatusMessage ?? "Completed.");
            sophonItem.PauseButton.IsEnabled = false;
            sophonItem.PauseButton.Visibility = Visibility.Collapsed;
            sophonItem.CancelButton.IsEnabled = false;
            sophonItem.CancelButton.Visibility = Visibility.Collapsed;
            sophonItem.RemoveButton.Visibility = Visibility.Visible;
            sophonItem.RemoveButton.IsEnabled = true;
            sophonItem.ExtractButton.Visibility = Visibility.Collapsed;
            sophonItem.ExtractButton.IsEnabled = false;
            sophonItem.DeleteChunksCheckBox.IsEnabled = false;

            return;
        }

        if (entryState == QueueItemState.ReadyToExtract)
        {
            try
            {
                ManifestConfig manifest = await LoadSophonHistoryManifestAsync(sophonItem);
                sophonItem.Manifest = manifest;

                List<SophonContentOption> contentOptions = BuildSelectedHistoryContent(
                    manifest, sophonItem.SelectedCategoryIds);

                if (contentOptions.Count > 0)
                {
                    sophonItem.SelectedContent = contentOptions;

                    SophonDownloadService? service = sophonItem.SophonService;
                    if (service is null) return;

                    SophonContentSet content = await service.LoadSelectedContentAsync(
                        manifest, contentOptions);

                    if (sophonItem.SophonSelectedFilePaths.Count > 0)
                        content = FilterSophonContent(content, sophonItem.SophonSelectedFilePaths);

                    sophonItem.SophonContent = content;
                    SetSophonMetadata(sophonItem, content);
                    sophonItem.SetState(QueueItemState.ReadyToExtract, entry.StatusMessage ?? "All required chunks are ready.");
                    sophonItem.PauseButton.IsEnabled = false;
                    sophonItem.PauseButton.Visibility = Visibility.Collapsed;
                    sophonItem.CancelButton.IsEnabled = false;
                    sophonItem.CancelButton.Visibility = Visibility.Collapsed;
                    sophonItem.ExtractButton.Visibility = Visibility.Visible;
                    sophonItem.ExtractButton.IsEnabled = true;
                    sophonItem.ExtractButton.Content = "EXTRACT";
                    sophonItem.ExtractButton.Tag = "Extract";
                    sophonItem.RemoveButton.Visibility = Visibility.Collapsed;
                    sophonItem.DeleteChunksCheckBox.IsEnabled = true;

                    return;
                }
            }
            catch (Exception ex)
            {
                Logger.Warn(ex, "Failed to restore Sophon history entry.");
            }
        }

        if (entryState == QueueItemState.Failed &&
            (entry.StatusMessage?.Contains("Repair required", StringComparison.OrdinalIgnoreCase) == true ||
             entry.StatusMessage?.Contains("Corrupt patch chunk", StringComparison.OrdinalIgnoreCase) == true))
        {
            sophonItem.SetState(QueueItemState.Failed, entry.StatusMessage ?? "Repair required.");
            sophonItem.RequiresSophonRepair = true;
            sophonItem.ExtractButton.Visibility = Visibility.Visible;
            sophonItem.ExtractButton.IsEnabled = true;
            sophonItem.ExtractButton.Content = "REPAIR";
            sophonItem.ExtractButton.Tag = "Repair";
            sophonItem.PauseButton.IsEnabled = false;
            sophonItem.CancelButton.IsEnabled = false;
            sophonItem.RemoveButton.Visibility = Visibility.Visible;
            sophonItem.RemoveButton.IsEnabled = true;
            return;
        }

        sophonItem.SetState(QueueItemState.Cancelled, "Download history restored. Ready to resume.");
        sophonItem.PauseButton.Content = "RESUME";
        sophonItem.PauseButton.IsEnabled = true;
        sophonItem.PauseButton.Visibility = Visibility.Visible;
        sophonItem.CancelButton.IsEnabled = false;
        sophonItem.CancelButton.Visibility = Visibility.Collapsed;
        sophonItem.RemoveButton.Visibility = Visibility.Visible;
        sophonItem.RemoveButton.IsEnabled = true;
        sophonItem.DeleteChunksCheckBox.IsEnabled = false;
    }

    private async Task<ManifestConfig> LoadSophonHistoryManifestAsync(QueueItem item)
    {
        if (item.Game is null || string.IsNullOrWhiteSpace(item.Version))
            throw new InvalidOperationException("Sophon history entry is incomplete.");

        item.SophonService = new SophonDownloadService();

        return await item.SophonService.LoadManifestAsync(
            item.Game, item.Version!, item.Channel ?? "main");
    }

    private static List<SophonContentOption> BuildSelectedHistoryContent(
        ManifestConfig manifest,
        IReadOnlyList<string> selectedIds)
    {
        HashSet<string> ids = selectedIds
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (ids.Count == 0) return [];

        return manifest.data.manifests
            .Where(category => ids.Contains(category.category_id))
            .Select(category => new SophonContentOption(category) { IsSelected = true })
            .ToList();
    }

    private static bool IsInterruptedState(QueueItemState state) =>
        state is QueueItemState.Preparing or
            QueueItemState.Downloading or
            QueueItemState.Extracting or
            QueueItemState.Cancelling;

    private static string GetDefaultStateMessage(QueueItemState state) =>
        state switch
        {
            QueueItemState.Completed => "Completed.",
            QueueItemState.Cancelled => "Operation cancelled.",
            QueueItemState.Failed => "Download failed.",
            QueueItemState.ReadyToExtract => "Ready to extract.",
            QueueItemState.Extracting => "Extracting files...",
            QueueItemState.Downloading => "Downloading...",
            QueueItemState.Queued => "Waiting for an available queue slot...",
            QueueItemState.Preparing => "Preparing...",
            QueueItemState.Cancelling => "Stopping operation...",
            _ => "Ready."
        };

    private void ApplyRestoredStateControls(QueueItem item)
    {
        switch (item.State)
        {
            case QueueItemState.Completed:
                item.PauseButton.IsEnabled = false;
                item.PauseButton.Visibility = Visibility.Collapsed;
                item.CancelButton.IsEnabled = false;
                item.CancelButton.Visibility = Visibility.Collapsed;
                item.ExtractButton.IsEnabled = false;
                item.ExtractButton.Visibility = Visibility.Collapsed;
                item.RemoveButton.Visibility = Visibility.Visible;
                item.RemoveButton.IsEnabled = true;
                item.DeleteChunksCheckBox.IsEnabled = false;
                break;

            case QueueItemState.Cancelled:
            case QueueItemState.Failed:
                item.PauseButton.Content = "RESUME";
                item.PauseButton.IsEnabled = true;
                item.PauseButton.Visibility = Visibility.Visible;
                item.CancelButton.IsEnabled = false;
                item.CancelButton.Visibility = Visibility.Collapsed;
                item.ExtractButton.Visibility = Visibility.Collapsed;
                item.RemoveButton.Visibility = Visibility.Visible;
                item.RemoveButton.IsEnabled = true;
                item.DeleteChunksCheckBox.IsEnabled = false;
                break;

            default:
                item.SetState(QueueItemState.Cancelled, "Download history restored. Ready to resume.");
                item.PauseButton.Content = "RESUME";
                item.PauseButton.IsEnabled = true;
                item.PauseButton.Visibility = Visibility.Visible;
                item.CancelButton.IsEnabled = false;
                item.CancelButton.Visibility = Visibility.Collapsed;
                item.RemoveButton.Visibility = Visibility.Visible;
                item.RemoveButton.IsEnabled = true;
                break;
        }
    }

    private static string FormatSize(long bytes) =>
        Utility.FormatCompactFileSize(bytes);

    private static string FormatSize(long? bytes)
    {
        return bytes is null ? "--" : FormatSize(bytes.Value);
    }

    private static string FormatSpeed(long? bytesPerSecond)
    {
        return bytesPerSecond is not > 0
            ? "--" : $"{FormatSize(bytesPerSecond.Value)}/s";
    }

    private static string FormatSpeed(double bytesPerSecond)
    {
        return bytesPerSecond <= 0
            ? "--" : $"{FormatSize((long)bytesPerSecond)}/s";
    }

    private static string FormatEta(TimeSpan? eta)
    {
        if (eta is null || eta < TimeSpan.Zero) return "--";
        if (eta.Value <= TimeSpan.Zero) return "00:00";

        if (eta.Value >= TimeSpan.FromDays(1))
        {
            int days = (int)eta.Value.TotalDays;
            TimeSpan remainder = eta.Value - TimeSpan.FromDays(days);

            return days == 1
                ? $"1 day {remainder:hh\\:mm\\:ss}"
                : $"{days:N0} days {remainder:hh\\:mm\\:ss}";
        }

        if (eta.Value >= TimeSpan.FromHours(1))
            return eta.Value.ToString(@"hh\:mm\:ss");

        return eta.Value.ToString(@"mm\:ss");
    }

    private enum QueueItemType
    {
        Legacy,
        Sophon
    }

    private enum QueueItemState
    {
        Queued,
        Preparing,
        Downloading,
        ReadyToExtract,
        Extracting,
        Cancelling,
        Completed,
        Cancelled,
        Failed
    }

    private sealed class LegacyExplorerArchiveHistory
    {
        public string Code { get; set; } = "";
        public string Name { get; set; } = "";
        public List<string> Urls { get; set; } = [];
    }

    private sealed class QueueItem
    {
        public string JobId { get; }
        public QueueItemType Type { get; }
        public string Title { get; }
        public string DestinationDirectory { get; }
        public IReadOnlyList<string> LegacyUrls { get; }
        public GameInfo? Game { get; }
        public string? Version { get; }
        public string? Channel { get; }
        public string? PatchFromVersion { get; set; }
        public bool IsPatch => Type == QueueItemType.Sophon && !string.IsNullOrWhiteSpace(PatchFromVersion);

        public bool DeleteChunksAfterExtraction { get; set; }
        public List<SophonContentOption> SelectedContent { get; set; } = [];
        public List<string> SelectedContentNames { get; set; } = [];
        public List<string> SelectedCategoryIds { get; set; } = [];
        public ManifestConfig? Manifest { get; set; }
        public SophonContentSet? SophonContent { get; set; }
        public SophonDownloadService? SophonService { get; set; }
        public CancellationTokenSource? SophonOperationCancellationSource { get; set; }
        public long OperationGeneration { get; set; }
        public DownloadService? LegacyService { get; set; }
        public LegacyExplorerService? LegacyExplorerService { get; set; }
        public UnifiedTransferMetrics LegacyExplorerMetrics { get; } = new();
        public List<LegacyExplorerArchive> LegacyExplorerArchives { get; set; } = [];
        public List<LegacyExplorerNode> LegacyExplorerSelectedNodes { get; set; } = [];
        public List<string> LegacyExplorerSelectedPaths { get; set; } = [];
        public List<string> SophonSelectedFilePaths { get; set; } = [];
        public bool IsLegacyExplorer => LegacyExplorerArchives.Count > 0 && LegacyExplorerSelectedPaths.Count > 0;
        public CancellationTokenSource? CancellationSource { get; set; }
        public Border? Card { get; set; }
        public TextBlock SelectedContentText { get; set; } = null!;
        public TextBlock StatusText { get; set; } = null!;
        public TextBlock StatusDetailText { get; set; } = null!;
        public TextBlock ProgressText { get; set; } = null!;
        public ProgressBar ProgressBar { get; set; } = null!;
        public Button PauseButton { get; set; } = null!;
        public Button CancelButton { get; set; } = null!;
        public Button ExtractButton { get; set; } = null!;
        public Button RemoveButton { get; set; } = null!;
        public CheckBox DeleteChunksCheckBox { get; set; } = null!;
        public Grid Stats { get; set; } = null!;
        public bool CancelRequested { get; set; }
        public long LastProgressTick { get; set; }
        public bool SophonChunksReady { get; set; }
        public bool ExtractRunning { get; set; }
        public bool RequiresSophonRepair { get; set; }
        public List<string> SophonRepairChunkIds { get; set; } = [];
        public bool SchedulerRunning { get; set; }
        public Task? ScheduledTask { get; set; }
        public Task? BackgroundOperationTask { get; set; }
        public QueueItemState State { get; private set; }

        public QueueItem(
            QueueItemType type, string title, string destinationDirectory,
            IReadOnlyList<string> legacyUrls, GameInfo? game, string? version, string? channel,
            bool deleteChunksAfterExtraction)
        {
            JobId = $"JOB-{Guid.NewGuid():N}"[..12].ToUpperInvariant();
            Type = type;
            Title = title;
            DestinationDirectory = Path.GetFullPath(destinationDirectory);
            LegacyUrls = legacyUrls;
            Game = game;
            Version = version;
            Channel = channel;
            DeleteChunksAfterExtraction = deleteChunksAfterExtraction;
            State = QueueItemState.Queued;
        }

        public void SetState(QueueItemState state, string message)
        {
            State = state;

            if (StatusText is not null)
            {
                StatusText.Text = state switch
                {
                    QueueItemState.Queued => "QUEUED",
                    QueueItemState.Preparing => "PREPARING",
                    QueueItemState.Downloading => "DOWNLOADING",
                    QueueItemState.ReadyToExtract => "READY",
                    QueueItemState.Extracting => "EXTRACTING",
                    QueueItemState.Cancelling => "CANCELLING",
                    QueueItemState.Completed => "COMPLETED",
                    QueueItemState.Cancelled => "CANCELLED",
                    QueueItemState.Failed => "FAILED",
                    _ => "READY"
                };
            }

            if (StatusDetailText is not null)
                StatusDetailText.Text = message;
        }

        public void SetTotalFiles(int count)
        {
            if (Stats is null) return;

            foreach (StackPanel stack in Stats.Children.OfType<StackPanel>())
            {
                if (!string.Equals(
                    stack.Tag as string,
                    "Files",
                    StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                TextBlock? target = stack.Children.OfType<TextBlock>().FirstOrDefault(text =>
                    string.Equals(text.Tag as string, "FilesValue", StringComparison.OrdinalIgnoreCase));

                if (target is not null)
                    target.Text = $"0/{count:N0}";

                return;
            }
        }
    }
}
