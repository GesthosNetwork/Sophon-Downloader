using System.Windows.Controls.Primitives;
using SophonDownloader.Models;
using SophonDownloader.Services;
using SophonDownloader.Utilities;

namespace SophonDownloader;

public partial class SophonExplorerView : UserControl
{
    private SophonContentSet? _content;
    private readonly List<SophonExplorerNode> _roots = [];
    private readonly Func<Task<SophonContentSet>> _contentLoader;
    private readonly DownloadsView _downloadsView;
    private readonly MainWindow _mainWindow;
    private readonly Action _showDownloadQueue;
    private readonly GameInfo _game;
    private readonly string _version;
    private readonly string _channel;
    private readonly string? _patchFromVersion;

    private bool _explorerContentLoaded;
    private bool _loading;

    public SophonContentSet? LoadedContent => _content;

    public SophonExplorerView(
        MainWindow owner,
        DownloadsView downloadsView,
        Action showDownloadQueue,
        GameInfo game,
        string version,
        string channel,
        string? patchFromVersion,
        string title,
        Func<Task<SophonContentSet>> contentLoader)
    {
        InitializeComponent();

        _mainWindow = owner ?? throw new ArgumentNullException(nameof(owner));
        _downloadsView = downloadsView ?? throw new ArgumentNullException(nameof(downloadsView));
        _showDownloadQueue = showDownloadQueue ?? throw new ArgumentNullException(nameof(showDownloadQueue));
        _game = game ?? throw new ArgumentNullException(nameof(game));
        _version = string.IsNullOrWhiteSpace(version) ? throw new ArgumentException("Version is required.", nameof(version)) : version;
        _channel = string.IsNullOrWhiteSpace(channel) ? "main" : channel;
        _patchFromVersion = patchFromVersion;
        _contentLoader = contentLoader ?? throw new ArgumentNullException(nameof(contentLoader));
        TitleText.Text = title;

        Loaded += SophonExplorerView_Loaded;
        SetLoadingState();
    }

    private async void SophonExplorerView_Loaded(object sender, RoutedEventArgs e)
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
                DownloadSelectedButton.Content = "DOWNLOAD";
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
            RequestClose();
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
    }

    private void ExplorerSurface_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || IsInteractiveElement(e.OriginalSource as DependencyObject))
            return;

        if (Window.GetWindow(this) is not { WindowState: WindowState.Normal } window)
            return;

        try
        {
            window.DragMove();
            e.Handled = true;
        }
        catch (InvalidOperationException) {}
    }

    private static bool IsInteractiveElement(DependencyObject? source)
    {
        while (source is not null)
        {
            if (source is Button
                or CheckBox
                or TextBox
                or ComboBox
                or ListBoxItem
                or TreeViewItem
                or ScrollBar
                or Thumb
                or Slider)
            {
                return true;
            }

            source = VisualTreeHelper.GetParent(source);
        }

        return false;
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => RequestClose();

    private void RequestClose() => _mainWindow.CloseExplore();

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
        DownloadSelectedButton.IsEnabled = _explorerContentLoaded && selected.Count > 0;
    }

    private void SelectAllButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_explorerContentLoaded)
            return;

        foreach (SophonExplorerNode node in _roots)
            SetNodeSelection(node, true);

        UpdateSelectionSummary();
    }

    private void ClearSelectionButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_explorerContentLoaded)
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

    private void DownloadSelectedButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_explorerContentLoaded || _content is null)
            return;

        List<SophonExplorerNode> selected = GetSelectedFiles();
        if (selected.Count == 0)
        {
            MessageBox.Show("Please select at least one file.", "Sophon Explorer", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        string saveDirectory = GetExplorerSaveDirectory();
        if (string.IsNullOrWhiteSpace(saveDirectory))
        {
            MessageBox.Show("Please choose a Sophon save folder first.", "Sophon Explorer", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        List<string> selectedPaths = selected
            .Select(node => NormalizePath(node.FullPath))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        try
        {
            _downloadsView.AddSophonExplorerDownload(
                _game, _version, _channel, _content.SelectedContent, selectedPaths, saveDirectory, patchFromVersion: _patchFromVersion);

            _showDownloadQueue();
            RequestClose();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Unable to add the selected assets to Download Queue:\n\n{ex.Message}", "Download Queue", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private string GetExplorerSaveDirectory()
    {
        return _mainWindow.GetSophonDestinationDirectory();
    }

    private static string NormalizePath(string path) => path.Replace('\\', '/');

}
