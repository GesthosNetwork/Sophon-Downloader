using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Navigation;

namespace SophonDownloader;

public partial class AboutView : UserControl
{
    public AboutView()
    {
        InitializeComponent();

        string version = App.Version;
        VersionText.Text = $"Version {version}";
        DetailVersionText.Text = version;
        CopyrightText.Text = App.Copyright;
    }

    private void Hyperlink_RequestNavigate(object sender, RequestNavigateEventArgs e)
    {
        Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri)
        {
            UseShellExecute = true
        });

        e.Handled = true;
    }
}
