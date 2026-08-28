using System.Runtime.CompilerServices;
using SophonDownloader.Utilities;

namespace SophonDownloader.Models;

public sealed class SophonExplorerNode : INotifyPropertyChanged
{
    private bool _isSelected;

    public string Name { get; set; } = "";
    public string FullPath { get; set; } = "";
    public bool IsFolder { get; set; }
    public long Size { get; set; }
    public string Md5 { get; set; } = "";

    public bool IsSelected
    {
        get => _isSelected; set
        {
            if (_isSelected == value)
                return;

            _isSelected = value;
            OnPropertyChanged();
        }
    }

    public List<SophonExplorerNode> Children { get; } = [];
    public List<SophonExplorerChunk> Chunks { get; set; } = [];

    public ImageSource? Icon => IsFolder
        ? ShellIconProvider.GetFolderIcon()
        : ShellIconProvider.GetFileIcon(Name);

    public string SizeText => IsFolder ? "" : Utility.FormatCompactFileSize(Size);
    public int ChunkCount => Chunks.Count;

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

public sealed class SophonExplorerChunk
{
    public int Index { get; init; }
    public string Id { get; init; } = "";
    public long CompressedSize { get; init; }
    public long UncompressedSize { get; init; }
    public string CompressedMd5 { get; init; } = "";
    public string UncompressedMd5 { get; init; } = "";

    public string CompressedSizeText => Utility.FormatCompactFileSize(CompressedSize);
    public string UncompressedSizeText => Utility.FormatCompactFileSize(UncompressedSize);
}
