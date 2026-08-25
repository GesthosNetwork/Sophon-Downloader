using System.ComponentModel;

namespace SophonDownloader.Models;

public sealed class LegacyContentOption : INotifyPropertyChanged
{
    private bool _isSelected;

    public string Code { get; init; } = "";
    public string Name { get; init; } = "";
    public bool IsGame { get; init; }

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected == value) return;
            _isSelected = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}
