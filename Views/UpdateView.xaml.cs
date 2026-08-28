using SophonDownloader.Services;

namespace SophonDownloader;

public partial class UpdateView : UserControl
{
    private readonly ApplicationUpdateService _updateService = new();
    private ApplicationUpdateInfo? _update;
    private bool _checking;

    public UpdateView()
    {
        InitializeComponent();
        CurrentVersionText.Text = App.Version;
    }

    private async void ActionButton_Click(object sender, RoutedEventArgs e)
    {
        if (_checking)
            return;

        if (_update?.HasUpdate == true && !string.IsNullOrWhiteSpace(_update.DownloadUrl))
        {
            await InstallUpdateAsync();
            return;
        }

        _checking = true;
        ActionButton.IsEnabled = false;
        UpdateProgressBar.Visibility = Visibility.Collapsed;
        ReleaseNotesTextBox.Clear();
        StatusText.Text = "Checking for the latest version…";

        try
        {
            _update = await _updateService.CheckForUpdateAsync();
            if (_update is null)
            {
                StatusText.Text = "Unable to read the latest release information.";
                ActionButton.Content = "Check Again";
                return;
            }

            if (!_update.HasUpdate)
            {
                StatusText.Text = $"You are using the latest available version ({App.Version}).";
                ActionButton.Content = "Check Again";
                return;
            }

            StatusText.Text = $"Version {_update.Version} is available.";
            ReleaseNotesTextBox.Text = _update.ReleaseNotes?.Trim() ?? "No release notes were provided.";
            ActionButton.Content = string.IsNullOrWhiteSpace(_update.DownloadUrl) ? "Update Unavailable" : $"Update to {_update.Version}";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Update check failed: {ex.Message}";
            ActionButton.Content = "Try Again";
        }
        finally
        {
            _checking = false;
            ActionButton.IsEnabled = true;
        }
    }

    private async Task InstallUpdateAsync()
    {
        ActionButton.IsEnabled = false;
        UpdateProgressBar.Visibility = Visibility.Visible;
        UpdateProgressBar.Value = 0;
        StatusText.Text = $"Downloading and installing {_update!.Version}…";

        try
        {
            await _updateService.DownloadAndInstallAsync(
                _update,
                new Progress<double>(value => UpdateProgressBar.Value = value));
        }
        catch (Exception ex)
        {
            UpdateProgressBar.Visibility = Visibility.Collapsed;
            StatusText.Text = $"Update failed: {ex.Message}";
            ActionButton.Content = "Try Again";
            ActionButton.IsEnabled = true;
        }
    }
}
