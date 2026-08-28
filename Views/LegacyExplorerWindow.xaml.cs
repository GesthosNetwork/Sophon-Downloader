using SophonDownloader.Models;
using SophonDownloader.Services;
using SophonDownloader.Utilities;

namespace SophonDownloader;

public partial class LegacyExplorerWindow : Window
{
    private readonly LegacyExplorerService _service = new();
    private readonly LegacyVersion _version;
    private readonly IReadOnlyList<LegacyExplorerArchive> _archives;
    private readonly string _destinationDirectory;

    private bool _selectionUpdating;
    private bool _loaded;
    private bool _downloadRunning;

    public LegacyExplorerWindow(
        Window owner, string title, LegacyVersion version,
        IReadOnlyList<LegacyExplorerArchive> archives, string destinationDirectory)
    {
        InitializeComponent();
        ThemeManager.Apply(this, AppSettingsStore.Load());
        Owner = owner;
        _version = version ?? throw new ArgumentNullException(nameof(version));
        _archives = archives ?? throw new ArgumentNullException(nameof(archives));
        _destinationDirectory = string.IsNullOrWhiteSpace(destinationDirectory)
            ? Environment.CurrentDirectory : Path.GetFullPath(destinationDirectory);

        TitleText.Text = title;
        Loaded += LegacyExplorerWindow_Loaded;
    }

    private async void LegacyExplorerWindow_Loaded(object sender, RoutedEventArgs e)
    {
        if (_loaded) return;
        _loaded = true;
        SetBusyState(true);
        SetLoadingState();

        try
        {
            var nodes = await _service.LoadAsync(_archives);
            ExplorerTreeView.ItemsSource = nodes;
            ArchiveText.Text = $"{CountFiles(nodes):N0} files • {Utility.FormatCompactFileSize(SumFileSize(nodes))}";
            StatusText.Text = "Archive indexed. Ready.";
            ProgressText.Text = "";
            UpdateSelectionSummary();
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = "Operation cancelled.";
            Close();
            return;
        }
        catch (Exception ex)
        {
            StatusText.Text = "Unable to read archive.";
            MessageBox.Show(
                $"Unable to open the Legacy archive.\n\n{ex.Message}",
                "Legacy Explore", MessageBoxButton.OK, MessageBoxImage.Error);
            Close();
            return;
        }
        finally
        {
            LoadingProgressBar.IsIndeterminate = false;
            LoadingOverlay.Visibility = Visibility.Collapsed;
            SetBusyState(false);
        }
    }

    private void SetLoadingState()
    {
        LoadingOverlay.Visibility = Visibility.Visible;
        LoadingTitleText.Text = "LOADING ARCHIVE";
        LoadingStatusText.Text = "Reading remote archive...";
        LoadingProgressBar.IsIndeterminate = true;
        ArchiveText.Text = "Loading remote archive index...";
        StatusText.Text = "Reading remote archive...";
        ProgressText.Text = "";
    }

    private void SetBusyState(bool busy)
    {
        bool enabled = !busy && !_downloadRunning;
        SelectAllButton.IsEnabled = enabled;
        ClearSelectionButton.IsEnabled = enabled;
        DownloadSelectedButton.IsEnabled = enabled;
        ExplorerTreeView.IsEnabled = enabled;
    }

    private void NodeCheckBox_Checked(object sender, RoutedEventArgs e) => SetNodeSelectionFromEvent(sender, true);
    private void NodeCheckBox_Unchecked(object sender, RoutedEventArgs e) => SetNodeSelectionFromEvent(sender, false);

    private void SetNodeSelectionFromEvent(object sender, bool selected)
    {
        if (_selectionUpdating || _downloadRunning ||
            sender is not CheckBox { DataContext: LegacyExplorerNode node })
            return;

        _selectionUpdating = true;
        try { SetNodeSelection(node, selected); }
        finally { _selectionUpdating = false; }

        UpdateSelectionSummary();
    }

    private static void SetNodeSelection(LegacyExplorerNode node, bool selected)
    {
        node.IsSelected = selected;
        foreach (var child in node.Children) SetNodeSelection(child, selected);
    }

    private void SelectAllButton_Click(object sender, RoutedEventArgs e) => SetAllNodesSelected(true);
    private void ClearSelectionButton_Click(object sender, RoutedEventArgs e) => SetAllNodesSelected(false);

    private void SetAllNodesSelected(bool selected)
    {
        if (_downloadRunning || ExplorerTreeView.ItemsSource is not IEnumerable<LegacyExplorerNode> nodes)
            return;

        _selectionUpdating = true;
        try
        {
            foreach (var node in nodes) SetNodeSelection(node, selected);
        }
        finally { _selectionUpdating = false; }

        UpdateSelectionSummary();
    }

    private async void DownloadSelectedButton_Click(object sender, RoutedEventArgs e)
    {
        if (_downloadRunning) return;

        var selected = GetAllNodes().Where(node => node.IsSelected && !node.IsFolder).ToList();
        if (selected.Count == 0)
        {
            MessageBox.Show(
                "Please select at least one file.",
                "Legacy Explore",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        _downloadRunning = true;
        SetBusyState(true);
        PrepareDownload(selected);

        try
        {
            var progress = new Progress<LegacyExplorerDownloadProgress>(UpdateDownloadProgress);
            StatusText.Text = "Downloading selected assets...";
            CurrentFileText.Text = "Preparing download...";
            DownloadPauseButton.IsEnabled = true;

            await _service.DownloadSelectedAsync(
                _archives, selected, _destinationDirectory, progress);

            CompleteDownload(selected.Count, selected.Sum(node => node.Size));

            MessageBox.Show(
                $"Successfully downloaded {selected.Count:N0} selected file(s) to:\n\n{_destinationDirectory}",
                "Legacy Explore",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
        catch (OperationCanceledException)
        {
            ResetDownloadDisplay();
        }
        catch (Exception ex)
        {
            ResetDownloadDisplay();
            MessageBox.Show(
                $"Failed to download selected assets.\n\n{ex.Message}",
                "Legacy Explore",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        finally
        {
            _service.Resume();
            DownloadPauseButton.Content = "PAUSE";
            DownloadPauseButton.IsEnabled = false;
            DownloadCancelButton.IsEnabled = false;
            DownloadPauseButton.Visibility = Visibility.Collapsed;
            DownloadCancelButton.Visibility = Visibility.Collapsed;
            _downloadRunning = false;
            SetBusyState(false);
        }
    }

    private void PrepareDownload(IReadOnlyCollection<LegacyExplorerNode> selected)
    {
        long totalSize = selected.Sum(node => node.Size);

        DownloadProgressArea.Visibility = Visibility.Visible;
        StatusText.Text = "Preparing selected assets...";
        CurrentFileText.Text = "Preparing download...";
        DownloadPauseButton.Visibility = Visibility.Visible;
        DownloadCancelButton.Visibility = Visibility.Visible;
        DownloadPauseButton.IsEnabled = false;
        DownloadPauseButton.Content = "PAUSE";
        DownloadCancelButton.IsEnabled = true;
        LegacyDownloadProgressBar.Value = 0;
        StatusText.Text = "Preparing download...";
        ProgressText.Text = $"0 / {selected.Count:N0}";
        DownloadedBytesText.Text = "0 B";
        TotalBytesText.Text = Utility.FormatCompactFileSize(totalSize);
        CurrentFileText.Text = "Preparing download...";
    }

    private void CompleteDownload(int count, long totalSize)
    {
        LegacyDownloadProgressBar.Value = 100;
        StatusText.Text = "Download completed.";
        CurrentFileText.Text = "Download completed.";
        ProgressText.Text = $"{count:N0} / {count:N0}";
        DownloadedBytesText.Text = Utility.FormatCompactFileSize(totalSize);
        DownloadProgressArea.Visibility = Visibility.Collapsed;
        DownloadPauseButton.Visibility = Visibility.Collapsed;
        DownloadCancelButton.Visibility = Visibility.Collapsed;
        StatusText.Text = "Archive indexed. Ready.";
        ProgressText.Text = "";
    }

    private void ResetDownloadDisplay()
    {
        DownloadProgressArea.Visibility = Visibility.Collapsed;
        DownloadPauseButton.Visibility = Visibility.Collapsed;
        DownloadCancelButton.Visibility = Visibility.Collapsed;
        LegacyDownloadProgressBar.Value = 0;
        StatusText.Text = "Archive indexed. Ready.";
        ProgressText.Text = "";
        CurrentFileText.Text = "";
    }

    private void UpdateDownloadProgress(LegacyExplorerDownloadProgress progress)
    {
        if (!_downloadRunning) return;

        double percent = progress.TotalBytes > 0
            ? progress.CompletedBytes * 100d / progress.TotalBytes
            : progress.TotalFiles > 0
                ? progress.CompletedFiles * 100d / progress.TotalFiles
                : 0;

        LegacyDownloadProgressBar.Value = Math.Clamp(percent, 0, 100);
        StatusText.Text = progress.StatusText;
        CurrentFileText.Text = progress.CurrentFile;
        ProgressText.Text = $"{progress.CompletedFiles:N0} / {progress.TotalFiles:N0}";
        DownloadedBytesText.Text = Utility.FormatCompactFileSize(progress.CompletedBytes);
        TotalBytesText.Text = Utility.FormatCompactFileSize(progress.TotalBytes);

        if (_service.IsPaused)
        {
            StatusText.Text = "Download paused.";
            CurrentFileText.Text = "Download is paused.";
            DownloadPauseButton.Content = "RESUME";
            return;
        }

        CurrentFileText.Text = progress.CurrentFile;
        DownloadPauseButton.Content = "PAUSE";
    }

    private void DownloadPauseButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_downloadRunning) return;

        if (_service.IsPaused)
        {
            _service.Resume();
            DownloadPauseButton.Content = "PAUSE";
            CurrentFileText.Text = "Resuming download...";
            StatusText.Text = "Resuming download...";
            return;
        }

        _service.Pause();
        DownloadPauseButton.Content = "RESUME";
        CurrentFileText.Text = "Download is paused.";
        StatusText.Text = "Download paused.";
    }

    private void DownloadCancelButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_downloadRunning) return;

        DownloadCancelButton.IsEnabled = false;
        DownloadPauseButton.IsEnabled = false;
        CurrentFileText.Text = "Stopping download...";
        StatusText.Text = "Stopping download...";
        _service.Cancel();
    }

    private async void ExplorerTreeView_SelectedItemChanged(
        object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (e.NewValue is not LegacyExplorerNode node)
        {
            ClearInformation();
            return;
        }

        InfoNameText.Text = node.Name;
        InfoPathText.Text = node.FullPath;
        InfoTypeText.Text = node.TypeText;
        InfoSizeText.Text = node.IsFolder ? "--" : Utility.FormatCompactFileSize(node.Size);
        InfoCompressedSizeText.Text = node.IsFolder ? "--" : Utility.FormatCompactFileSize(node.CompressedSize);
        InfoCompressionText.Text = node.IsFolder ? "--" : node.CompressionMethod;
        InfoMd5Text.Text = node.IsFolder ? "--" : "Calculating...";

        if (node.IsFolder) return;

        try { InfoMd5Text.Text = await _service.CalculateMd5Async(_archives, node); }
        catch { InfoMd5Text.Text = "--"; }
    }

    private void ClearInformation()
    {
        InfoNameText.Text = "Nothing selected";
        InfoPathText.Text = "";
        InfoTypeText.Text = "--";
        InfoSizeText.Text = "--";
        InfoCompressedSizeText.Text = "--";
        InfoCompressionText.Text = "--";
        InfoMd5Text.Text = "--";
    }

    private void UpdateSelectionSummary()
    {
        var selected = GetAllNodes().Where(node => node.IsSelected && !node.IsFolder).ToList();

        SelectionSummaryText.Text = selected.Count == 0
            ? "0 files selected"
            : $"{selected.Count:N0} files selected • {Utility.FormatCompactFileSize(selected.Sum(node => node.Size))}";
    }

    private IEnumerable<LegacyExplorerNode> GetAllNodes()
    {
        if (ExplorerTreeView.ItemsSource is not IEnumerable<LegacyExplorerNode> roots)
            return [];

        return roots.SelectMany(EnumerateNodes);
    }

    private static IEnumerable<LegacyExplorerNode> EnumerateNodes(LegacyExplorerNode node)
    {
        yield return node;
        foreach (var child in node.Children)
            foreach (var descendant in EnumerateNodes(child))
                yield return descendant;
    }

    private static int CountFiles(IEnumerable<LegacyExplorerNode> nodes) =>
        nodes.Sum(node => node.IsFolder ? CountFiles(node.Children) : 1);

    private static long SumFileSize(IEnumerable<LegacyExplorerNode> nodes) =>
        nodes.Sum(node => node.IsFolder ? SumFileSize(node.Children) : node.Size);

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed) DragMove();
    }

    private void MinimizeButton_Click(object sender, RoutedEventArgs e) =>
        WindowState = WindowState.Minimized;

    private void CloseButton_Click(object sender, RoutedEventArgs e) =>
        Close();

    protected override void OnClosed(EventArgs e)
    {
        _service.Cancel();
        base.OnClosed(e);
    }
}
