using System.Windows.Navigation;

namespace SophonDownloader;

public partial class LicenseView : UserControl
{
    public LicenseView()
    {
        InitializeComponent();
    }

    private void Hyperlink_RequestNavigate(object sender, RequestNavigateEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = e.Uri.AbsoluteUri,
                UseShellExecute = true
            });
        }
        catch {}

        e.Handled = true;
    }
}
