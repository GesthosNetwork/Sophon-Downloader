using System.Net;
using System.Net.Http;

namespace SophonDownloader.Services;

internal static class NetworkClient
{
    public static SocketsHttpHandler CreateHandler(AppSettings settings)
    {
        var handler = new SocketsHttpHandler
        {
            MaxConnectionsPerServer = Math.Clamp(settings.MaxHttpHandle, 1, 256),
            UseProxy = !string.Equals(settings.ProxyMode, "Direct", StringComparison.OrdinalIgnoreCase)
        };

        if (string.Equals(settings.ProxyMode, "Custom", StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrWhiteSpace(settings.ProxyHost))
        {
            handler.Proxy = new WebProxy(new Uri(BuildProxyUri(settings.ProxyHost, settings.ProxyPort)));
        }

        return handler;
    }

    public static void AddAria2cProxyArguments(List<string> arguments, AppSettings settings, string url)
    {
        string mode = settings.ProxyMode?.Trim() ?? "System";

        if (string.Equals(mode, "Direct", StringComparison.OrdinalIgnoreCase))
        {
            arguments.Add("--all-proxy=");
            return;
        }

        if (string.Equals(mode, "Custom", StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrWhiteSpace(settings.ProxyHost))
        {
            arguments.Add($"--all-proxy={BuildProxyUri(settings.ProxyHost, settings.ProxyPort)}");
            return;
        }

        try
        {
            IWebProxy? proxy = WebRequest.DefaultWebProxy;
            if (proxy is null)
                return;

            Uri target = new(url);
            Uri? resolved = proxy.GetProxy(target);
            if (resolved is not null && resolved != target &&
                !string.Equals(resolved.Host, target.Host, StringComparison.OrdinalIgnoreCase))
            {
                arguments.Add($"--all-proxy={resolved.AbsoluteUri.TrimEnd('/')}");
            }
        }
        catch { }
    }

    private static string BuildProxyUri(string host, int port)
    {
        string normalizedHost = host.Trim();
        if (normalizedHost.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
            normalizedHost = normalizedHost[7..];
        else if (normalizedHost.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            normalizedHost = normalizedHost[8..];

        normalizedHost = normalizedHost.TrimEnd('/');
        return $"http://{normalizedHost}:{Math.Clamp(port, 1, 65535)}";
    }
}
