using System.Windows.Navigation;

namespace SophonDownloader;

public partial class LicenseView : UserControl
{
    public LicenseView()
    {
        InitializeComponent();
        DistributionNoticeText.Text = DecodeDistributionNotice();
    }

    private static string DecodeDistributionNotice()
    {
        const string encoded = "U29waG9uRG93bmxvYWRlciBpcyBmcmVlIHNvZnR3YXJlIGFuZCBpcyBkaXN0cmlidXRlZCBieSBHZXN0aG9zTmV0d29yayBhdCBubyBjaGFyZ2UuIEdlc3Rob3NOZXR3b3JrIGRvZXMgbm90IHNlbGwgb2ZmaWNpYWwgbGljZW5zZXMsIGFjdGl2YXRpb24ga2V5cywgcHJlbWl1bSBlZGl0aW9ucywgb3IgcGFpZCBvZmZpY2lhbCBjb3BpZXMgb2YgU29waG9uRG93bmxvYWRlci4gQmUgY2F1dGlvdXMgb2Ygc2NhbW1lcnMgb2ZmZXJpbmcgU29waG9uRG93bmxvYWRlciBmb3IgcGF5bWVudCBvciBjbGFpbWluZyB0byByZXByZXNlbnQgdGhpcyBwcm9qZWN0LiBPZmZpY2lhbCByZWxlYXNlcyBhcmUgcHVibGlzaGVkIHRocm91Z2ggdGhlIHByb2plY3QgcmVwb3NpdG9yeS4=";
        return System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(encoded));
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
