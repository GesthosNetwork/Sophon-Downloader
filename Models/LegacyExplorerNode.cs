using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media;
using SophonDownloader.Utilities;

namespace SophonDownloader.Models;

public sealed class LegacyExplorerNode : INotifyPropertyChanged
{
    private bool _isSelected;
    private string _md5 = "";

    public string Name { get; set; } = "";
    public string FullPath { get; set; } = "";
    public string ArchiveCode { get; set; } = "";
    public bool IsFolder { get; set; }
    public long Size { get; set; }
    public long CompressedSize { get; set; }
    public string CompressionMethod { get; set; } = "";

    public string Md5
    {
        get => _md5; set
        {
            if (string.Equals(_md5, value, StringComparison.OrdinalIgnoreCase)) return;
            _md5 = value ?? "";
            OnPropertyChanged();
        }
    }

    public bool IsSelected
    {
        get => _isSelected; set
        {
            if (_isSelected == value) return;
            _isSelected = value;
            OnPropertyChanged();
        }
    }

    public List<LegacyExplorerNode> Children { get; } = [];

    public ImageSource? Icon => IsFolder
        ? ShellIconProvider.GetFolderIcon()
        : ShellIconProvider.GetFileIcon(Name);

    public string SizeText => IsFolder ? "" : Utility.FormatCompactFileSize(Size);
    public string CompressedSizeText => IsFolder ? "" : Utility.FormatCompactFileSize(CompressedSize);
    public string TypeText => IsFolder ? "Folder" : CompressionMethod;

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
