using System.Windows.Controls.Primitives;
using SophonDownloader.Models;
using SophonDownloader.Services;
using SophonDownloader.Utilities;

namespace SophonDownloader;

public partial class LegacyExplorerView : UserControl
{
    private readonly LegacyExplorerService _service = new();
    private readonly DownloadsView _downloadsView;
    private readonly MainWindow _mainWindow;
    private readonly Action _showDownloadQueue;
    private readonly IReadOnlyList<LegacyExplorerArchive> _archives;
    private readonly string _destinationDirectory;

    private bool _selectionUpdating;
    private bool _loaded;

    public LegacyExplorerView(
        MainWindow owner, DownloadsView downloadsView, Action showDownloadQueue, string title,
        IReadOnlyList<LegacyExplorerArchive> archives, string destinationDirectory)
    {
        InitializeComponent();
        _mainWindow = owner ?? throw new ArgumentNullException(nameof(owner));
        _downloadsView = downloadsView ?? throw new ArgumentNullException(nameof(downloadsView));
        _showDownloadQueue = showDownloadQueue ?? throw new ArgumentNullException(nameof(showDownloadQueue));
        _archives = archives ?? throw new ArgumentNullException(nameof(archives));
        _destinationDirectory = string.IsNullOrWhiteSpace(destinationDirectory)
            ? Environment.CurrentDirectory : Path.GetFullPath(destinationDirectory);

        TitleText.Text = title;
        Loaded += LegacyExplorerView_Loaded;
    }

    private async void LegacyExplorerView_Loaded(object sender, RoutedEventArgs e)
    {
        if (_loaded) return;
        _loaded = true;
        SetBusyState(true);
        SetLoadingState();

        try
        {
            var nodes = await _service.LoadAsync(_archives);
            ExplorerTreeView.ItemsSource = nodes;
            SummaryText.Text = $"{CountFiles(nodes):N0} files • {Utility.FormatCompactFileSize(SumFileSize(nodes))}";
            StatusText.Text = "Archive indexed. Ready.";
            ProgressText.Text = "";
            UpdateSelectionSummary();
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = "Operation cancelled.";
            RequestClose();
            return;
        }
        catch (Exception ex)
        {
            StatusText.Text = "Unable to read archive.";
            MessageBox.Show($"Unable to open the Legacy archive.\n\n{ex.Message}", "Legacy Explore", MessageBoxButton.OK, MessageBoxImage.Error);
            RequestClose();
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
        SummaryText.Text = "Loading remote archive index...";
        StatusText.Text = "Reading remote archive...";
        ProgressText.Text = "";
    }

    private void SetBusyState(bool busy)
    {
        bool enabled = !busy;
        SelectAllButton.IsEnabled = enabled;
        ClearSelectionButton.IsEnabled = enabled;
        DownloadSelectedButton.IsEnabled = enabled;
        ExplorerTreeView.IsEnabled = enabled;
    }

    private void NodeCheckBox_Checked(object sender, RoutedEventArgs e) => SetNodeSelectionFromEvent(sender, true);
    private void NodeCheckBox_Unchecked(object sender, RoutedEventArgs e) => SetNodeSelectionFromEvent(sender, false);

    private void SetNodeSelectionFromEvent(object sender, bool selected)
    {
        if (_selectionUpdating || sender is not CheckBox { DataContext: LegacyExplorerNode node })
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
        if (ExplorerTreeView.ItemsSource is not IEnumerable<LegacyExplorerNode> nodes)
            return;

        _selectionUpdating = true;
        try
        {
            foreach (var node in nodes) SetNodeSelection(node, selected);
        }
        finally { _selectionUpdating = false; }

        UpdateSelectionSummary();
    }

    private void DownloadSelectedButton_Click(object sender, RoutedEventArgs e)
    {
        var selected = GetAllNodes()
            .Where(node => node.IsSelected && !node.IsFolder)
            .ToList();

        if (selected.Count == 0)
        {
            MessageBox.Show("Please select at least one file.", "Legacy Explore", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        try
        {
            _downloadsView.AddLegacyExplorerDownload(_archives, selected, _destinationDirectory, TitleText.Text);
            _showDownloadQueue();
            RequestClose();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Unable to add the selected assets to Download Queue:\n\n{ex.Message}", "Download Queue", MessageBoxButton.OK, MessageBoxImage.Error);
        }
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
}
