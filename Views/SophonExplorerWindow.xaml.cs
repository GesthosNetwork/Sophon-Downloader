using SophonDownloader.Models;
using SophonDownloader.Services;
using SophonDownloader.Utilities;

namespace SophonDownloader;

public partial class SophonExplorerWindow : Window
{
    private SophonContentSet? _content;
    private readonly List<SophonExplorerNode> _roots = [];
    private readonly SophonDownloadService _downloadService = new();
    private readonly Func<Task<SophonContentSet>> _contentLoader;

    private bool _downloadRunning;
    private bool _cancelRequested;
    private bool _explorerContentLoaded;
    private bool _loading;

    public SophonContentSet? LoadedContent => _content;

    public SophonExplorerWindow(Window owner, string title, Func<Task<SophonContentSet>> contentLoader)
    {
        InitializeComponent();
        ThemeManager.Apply(this, AppSettingsStore.Load());

        Owner = owner;
        _contentLoader = contentLoader ?? throw new ArgumentNullException(nameof(contentLoader));
        Title = $"Sophon Explorer - {title}";
        TitleText.Text = title;

        ConfigureDownloadCallbacks();
        Loaded += SophonExplorerWindow_Loaded;
        SetLoadingState();
    }

    private async void SophonExplorerWindow_Loaded(object sender, RoutedEventArgs e)
    {
        if (_explorerContentLoaded || _loading)
            return;

        _loading = true;

        try
        {
            SetLoadingState();

            SophonContentSet content = await _contentLoader() ??
                throw new InvalidOperationException("No Sophon content was loaded.");

            _content = content;
            _explorerContentLoaded = true;
            if (content.IsPatch)
            {
                DownloadSelectedButton.Content = "DOWNLOAD PATCH";
                ExplorerDeleteChunksCheckBox.Visibility = Visibility.Collapsed;
                SummaryText.Text = $"PATCH DOWNLOAD • {content.PatchFromVersion} → {content.PatchToVersion}";
            }

            BuildTree();
            UpdateSelectionSummary();

            LoadingOverlay.Visibility = Visibility.Collapsed;
            SetExplorerEnabled(true);
        }
        catch (OperationCanceledException)
        {
            LoadingOverlay.Visibility = Visibility.Collapsed;
            _loading = false;
            Close();
            return;
        }
        catch (Exception ex)
        {
            LoadingTitleText.Text = "UNABLE TO LOAD CONTENT";
            LoadingStatusText.Text = ex.Message;
            LoadingProgressBar.IsIndeterminate = false;
            SetExplorerEnabled(false);
            _loading = false;
            return;
        }

        _loading = false;
    }

    private void SetLoadingState()
    {
        LoadingOverlay.Visibility = Visibility.Visible;
        LoadingProgressBar.IsIndeterminate = true;
        SetExplorerEnabled(false);
        SetDownloadIdleState();

        SummaryText.Text = "";
        SelectionSummaryText.Text = "0 files selected";

        ClearDetails();
    }

    private void SetExplorerEnabled(bool enabled)
    {
        SelectAllButton.IsEnabled = enabled;
        ClearSelectionButton.IsEnabled = enabled;
        DownloadSelectedButton.IsEnabled = enabled && GetSelectedFiles().Count > 0;
        ExplorerTree.IsEnabled = enabled;
        ExplorerDeleteChunksCheckBox.IsEnabled = enabled && !_downloadRunning;
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
            DragMove();
    }

    private void MinimizeButton_Click(object sender, RoutedEventArgs e) =>
        WindowState = WindowState.Minimized;

    private void CloseButton_Click(object sender, RoutedEventArgs e) =>
        Close();

    private void ConfigureDownloadCallbacks()
    {
        _downloadService.ChunkProgressCallback = p =>
            Dispatcher.BeginInvoke(() => UpdateChunkProgress(p));

        _downloadService.ExtractionProgressCallback = p =>
            Dispatcher.BeginInvoke(() => UpdateExtractionProgress(p));

        _downloadService.ChunkDownloadCompletedCallback = () =>
            Dispatcher.BeginInvoke(OnChunkDownloadCompleted);

        _downloadService.ExtractionCompletedCallback = () =>
            Dispatcher.BeginInvoke(OnExtractionCompleted);

        _downloadService.DownloadCancelledCallback = () =>
            Dispatcher.BeginInvoke(OnDownloadCancelled);

        _downloadService.ExtractionCancelledCallback = () =>
            Dispatcher.BeginInvoke(OnExtractionCancelled);
    }

    private void BuildTree()
    {
        if (_content is null)
            return;

        _roots.Clear();

        foreach (SophonChunkFile file in _content.AllFiles.Where(x => !x.IsFolder))
            AddFile(_roots, file);

        SortNodes(_roots);
        ExplorerTree.ItemsSource = _roots;

        int fileCount = _content.AllFiles.Count(x => !x.IsFolder);
        int folderCount = CountFolders(_roots);
        int chunkCount = _content.AllFiles
            .SelectMany(x => x.Chunks)
            .Select(x => x.Id)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();

        SummaryText.Text = $"{fileCount:N0} files • {folderCount:N0} folders • {chunkCount:N0} unique chunks";
    }

    private static void AddFile(List<SophonExplorerNode> roots, SophonChunkFile file)
    {
        string[] parts = file.File.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);

        if (parts.Length == 0)
            return;

        List<SophonExplorerNode> collection = roots;
        string path = "";

        for (int i = 0; i < parts.Length; i++)
        {
            string part = parts[i];
            string newPath = string.IsNullOrWhiteSpace(path) ? part : $"{path}/{part}";
            bool last = i == parts.Length - 1;

            SophonExplorerNode? child = collection.FirstOrDefault(
                x => x.Name.Equals(part, StringComparison.OrdinalIgnoreCase));

            if (child is null)
            {
                child = new SophonExplorerNode
                {
                    Name = part,
                    FullPath = newPath,
                    IsFolder = !last
                };

                collection.Add(child);
            }

            if (last)
            {
                child.IsFolder = false;
                child.FullPath = newPath;
                child.Size = file.Size;
                child.Md5 = file.Md5;
                child.Chunks = file.Chunks
                    .Select((c, index) => new SophonExplorerChunk
                    {
                        Index = index + 1,
                        Id = c.Id,
                        CompressedSize = c.CompressedSize,
                        UncompressedSize = c.UncompressedSize,
                        CompressedMd5 = c.CompressedMd5,
                        UncompressedMd5 = c.UncompressedMd5
                    })
                    .ToList();
                child.IsSelected = false;
            }

            path = newPath;
            collection = child.Children;
        }
    }

    private static void SortNodes(List<SophonExplorerNode> nodes)
    {
        nodes.Sort((a, b) =>
            a.IsFolder != b.IsFolder
                ? a.IsFolder ? -1 : 1
                : StringComparer.OrdinalIgnoreCase.Compare(a.Name, b.Name));

        foreach (SophonExplorerNode node in nodes)
        {
            if (node.Children.Count > 0)
                SortNodes(node.Children);
        }
    }

    private static int CountFolders(IEnumerable<SophonExplorerNode> nodes)
    {
        int count = 0;

        foreach (SophonExplorerNode node in nodes)
        {
            if (node.IsFolder)
                count += 1 + CountFolders(node.Children);
        }

        return count;
    }

    private void NodeCheckBox_Click(object sender, RoutedEventArgs e)
    {
        if (!_explorerContentLoaded)
            return;

        if (sender is not CheckBox { Tag: SophonExplorerNode node } box)
            return;

        SetNodeSelection(node, box.IsChecked == true);
        UpdateSelectionSummary();
        e.Handled = true;
    }

    private static void SetNodeSelection(SophonExplorerNode node, bool selected)
    {
        node.IsSelected = selected;

        foreach (SophonExplorerNode child in node.Children)
            SetNodeSelection(child, selected);
    }

    private List<SophonExplorerNode> GetSelectedFiles() =>
        Flatten(_roots).Where(x => !x.IsFolder && x.IsSelected).ToList();

    private static IEnumerable<SophonExplorerNode> Flatten(IEnumerable<SophonExplorerNode> nodes)
    {
        foreach (SophonExplorerNode node in nodes)
        {
            yield return node;
            foreach (SophonExplorerNode child in Flatten(node.Children))
                yield return child;
        }
    }

    private void UpdateSelectionSummary()
    {
        List<SophonExplorerNode> selected = GetSelectedFiles();
        long bytes = selected.Sum(x => x.Size);

        SelectionSummaryText.Text = $"{selected.Count:N0} files selected • {Utility.FormatCompactFileSize(bytes)}";
        DownloadSelectedButton.IsEnabled = _explorerContentLoaded && !_downloadRunning && selected.Count > 0;
    }

    private void SelectAllButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_explorerContentLoaded || _downloadRunning)
            return;

        foreach (SophonExplorerNode node in _roots)
            SetNodeSelection(node, true);

        UpdateSelectionSummary();
    }

    private void ClearSelectionButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_explorerContentLoaded || _downloadRunning)
            return;

        foreach (SophonExplorerNode node in _roots)
            SetNodeSelection(node, false);

        UpdateSelectionSummary();
    }

    private void ExplorerTree_SelectedItemChanged(
        object sender,
        RoutedPropertyChangedEventArgs<object> e)
    {
        if (!_explorerContentLoaded)
            return;

        if (e.NewValue is not SophonExplorerNode node)
        {
            ClearDetails();
            return;
        }

        SelectedNameText.Text = node.Name;
        SelectedPathText.Text = string.IsNullOrWhiteSpace(node.FullPath) ? "-" : node.FullPath;

        if (node.IsFolder)
        {
            SelectedSizeText.Text = "-";
            SelectedChunkCountText.Text = "-";
            SelectedMd5Text.Text = "-";
            ChunkListView.ItemsSource = null;
            return;
        }

        SelectedSizeText.Text = node.SizeText;
        SelectedChunkCountText.Text = node.ChunkCount.ToString("N0");
        SelectedMd5Text.Text = string.IsNullOrWhiteSpace(node.Md5) ? "-" : node.Md5;
        ChunkListView.ItemsSource = node.Chunks;
    }

    private void ClearDetails()
    {
        SelectedNameText.Text = "-";
        SelectedPathText.Text = "-";
        SelectedSizeText.Text = "-";
        SelectedChunkCountText.Text = "-";
        SelectedMd5Text.Text = "-";
        ChunkListView.ItemsSource = null;
    }

    private async void DownloadSelectedButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_explorerContentLoaded || _content is null || _downloadRunning)
            return;

        List<SophonExplorerNode> selected = GetSelectedFiles();

        if (selected.Count == 0)
        {
            MessageBox.Show(
                "Please select at least one file.",
                "Sophon Explorer",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        string saveDirectory = GetExplorerSaveDirectory();

        if (string.IsNullOrWhiteSpace(saveDirectory))
        {
            MessageBox.Show("Please choose a Sophon save folder first.", "Sophon Explorer", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        List<SophonChunkFile> selectedFiles = selected
            .Select(node => _content.AllFiles.First(
                file => string.Equals(
                    NormalizePath(file.File), NormalizePath(node.FullPath),
                    StringComparison.OrdinalIgnoreCase)))
            .ToList();

        var fileManifest = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (SophonChunkFile file in selectedFiles)
        {
            if (_content.FileManifest.TryGetValue(file.File, out string? prefix))
                fileManifest[file.File] = prefix;
        }

        var selectedContent = new SophonContentSet
        {
            AllFiles = selectedFiles,
            FileManifest = fileManifest,
            SelectedContent = _content.SelectedContent
        };

        bool deleteChunksAfterExtraction = ExplorerDeleteChunksCheckBox.IsChecked == true;

        try
        {
            _downloadRunning = true;
            _cancelRequested = false;

            SetDownloadRunningState();

            DownloadStatusText.Text = "Preparing selected assets...";
            DownloadProgressBar.Value = 0;
            DownloadProgressText.Text = "0%";
            DownloadSpeedText.Text = "0 B/s";

            await _downloadService.StartChunkDownloadAsync(selectedContent, saveDirectory);

            if (_cancelRequested)
                return;

            if (_content.IsPatch)
            {
                DownloadProgressBar.Value = 100;
                DownloadProgressText.Text = "100%";
                DownloadSpeedText.Text = "--";
                DownloadStatusText.Text = "Patch chunks downloaded.";
                return;
            }

            DownloadStatusText.Text = "Reconstructing selected assets...";
            DownloadProgressBar.Value = 0;
            DownloadProgressText.Text = "0%";
            DownloadSpeedText.Text = "--";

            await _downloadService.StartExtractionAsync(
                selectedContent,
                saveDirectory,
                false,
                deleteChunksAfterExtraction);

            if (_cancelRequested)
                return;

            DownloadProgressBar.Value = 0;
            DownloadProgressText.Text = "";
            DownloadSpeedText.Text = "--";
            DownloadStatusText.Text = "Ready.";
        }
        catch (OperationCanceledException)
        {
            DownloadStatusText.Text = "Download cancelled.";
            ResetProgress();
        }
        catch (Exception ex)
        {
            DownloadStatusText.Text = "Download failed.";
            ResetProgress();

            if (!_cancelRequested)
            {
                MessageBox.Show($"Unable to download selected assets:\n\n{ex.Message}", "Sophon Explorer", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
        finally
        {
            _downloadRunning = false;
            _cancelRequested = false;
            SetDownloadIdleState();
            UpdateSelectionSummary();
        }
    }

    private string GetExplorerSaveDirectory()
    {
        if (Owner is MainWindow mainWindow)
            return mainWindow.GetSophonDestinationDirectory();

        return "";
    }

    private void ResetProgress()
    {
        DownloadProgressBar.Value = 0;
        DownloadProgressText.Text = "";
        DownloadSpeedText.Text = "--";
    }

    private void SetDownloadRunningState()
    {
        SelectAllButton.IsEnabled = false;
        ClearSelectionButton.IsEnabled = false;
        DownloadSelectedButton.IsEnabled = false;
        ExplorerTree.IsEnabled = false;
        ExplorerDeleteChunksCheckBox.IsEnabled = false;

        DownloadProgressBar.Visibility = Visibility.Visible;
        DownloadSpeedText.Visibility = Visibility.Visible;
        DownloadProgressText.Visibility = Visibility.Visible;
        PauseButton.Visibility = Visibility.Visible;
        CancelButton.Visibility = Visibility.Visible;

        PauseButton.IsEnabled = true;
        CancelButton.IsEnabled = true;
        PauseButton.Content = "PAUSE";
        CancelButton.Content = "CANCEL";
    }

    private void SetDownloadIdleState()
    {
        SelectAllButton.IsEnabled = _explorerContentLoaded;
        ClearSelectionButton.IsEnabled = _explorerContentLoaded;
        ExplorerTree.IsEnabled = _explorerContentLoaded;
        ExplorerDeleteChunksCheckBox.IsEnabled = _explorerContentLoaded;
        DownloadSelectedButton.IsEnabled = _explorerContentLoaded && !_downloadRunning && GetSelectedFiles().Count > 0;

        DownloadProgressBar.Visibility = Visibility.Collapsed;
        DownloadSpeedText.Visibility = Visibility.Collapsed;
        DownloadProgressText.Visibility = Visibility.Collapsed;
        PauseButton.Visibility = Visibility.Collapsed;
        CancelButton.Visibility = Visibility.Collapsed;

        PauseButton.IsEnabled = false;
        CancelButton.IsEnabled = false;
        PauseButton.Content = "PAUSE";
        CancelButton.Content = "CANCEL";
    }

    private void PauseButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_downloadRunning)
            return;

        try
        {
            _downloadService.TogglePause();
            PauseButton.Content = _downloadService.IsPaused ? "RESUME" : "PAUSE";
            DownloadStatusText.Text = _downloadService.IsPaused
                ? "Download paused."
                : "Download resumed.";
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Unable to change download state.\n\n{ex.Message}", "Sophon Explorer", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_downloadRunning)
            return;

        _cancelRequested = true;
        CancelButton.IsEnabled = false;
        PauseButton.IsEnabled = false;
        DownloadStatusText.Text = "Stopping operation...";
        DownloadSpeedText.Text = "--";

        try
        {
            _downloadService.Cancel();
        }
        catch {}
    }

    private void UpdateChunkProgress(ChunkDownloadProgress progress)
    {
        double percent = progress.TotalChunks > 0
            ? progress.CompletedChunks * 100d / progress.TotalChunks
            : 0;

        DownloadProgressBar.Value = Math.Clamp(percent, 0, 100);
        DownloadProgressText.Text = $"{progress.CompletedChunks:N0}/{progress.TotalChunks:N0} chunks";
        DownloadSpeedText.Text = progress.CurrentSpeed;
        DownloadStatusText.Text = progress.StatusText;
        PauseButton.Content = _downloadService.IsPaused ? "RESUME" : "PAUSE";
    }

    private void UpdateExtractionProgress(ExtractionProgress progress)
    {
        double percent = progress.TotalBytes > 0
            ? progress.ExtractedBytes * 100d / progress.TotalBytes
            : progress.TotalFiles > 0
                ? progress.CompletedFiles * 100d / progress.TotalFiles
                : 0;

        DownloadProgressBar.Value = Math.Clamp(percent, 0, 100);
        DownloadProgressText.Text = $"{progress.CompletedFiles:N0}/{progress.TotalFiles:N0} files";
        DownloadSpeedText.Text = "--";
        DownloadStatusText.Text = progress.StatusText;
        PauseButton.Content = _downloadService.IsPaused ? "RESUME" : "PAUSE";
    }

    private void OnChunkDownloadCompleted()
    {
        DownloadStatusText.Text = "All required chunks are ready.";
        DownloadProgressBar.Value = 100;
        DownloadProgressText.Text = "100%";
        DownloadSpeedText.Text = "0 B/s";
    }

    private void OnExtractionCompleted()
    {
        ResetProgress();
        DownloadStatusText.Text = "Ready.";
        PauseButton.Content = "PAUSE";
    }

    private void OnDownloadCancelled()
    {
        ResetProgress();
        DownloadStatusText.Text = "Download cancelled.";
        PauseButton.Content = "PAUSE";
    }

    private void OnExtractionCancelled()
    {
        ResetProgress();
        DownloadStatusText.Text = "Extraction cancelled.";
        PauseButton.Content = "PAUSE";
    }

    private static string NormalizePath(string path) => path.Replace('\\', '/');

    protected override void OnClosed(EventArgs e)
    {
        try
        {
            _downloadService.Dispose();
        }
        catch {}

        base.OnClosed(e);
    }
}
