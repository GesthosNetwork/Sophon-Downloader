using System.Diagnostics.CodeAnalysis;
using System.Reflection;

namespace SophonDownloader;

internal static class DistributionIntegrityGuard
{
    private const string ExpectedNoticeMarker = "SophonDownloader";
    private const string ExpectedOfficialMarker = "GesthosNetwork";
    private const string ExpectedFreeMarker = "free software";

    internal static void Verify()
    {
        try
        {
            Type? licenseViewType = typeof(DistributionIntegrityGuard).Assembly
                .GetType("SophonDownloader.LicenseView", throwOnError: false);

            MethodInfo? decoder = licenseViewType?.GetMethod(
                "DecodeDistributionNotice", BindingFlags.Static | BindingFlags.NonPublic);

            if (decoder is null)
                FailFast("Distribution notice integrity check failed.");

            string? notice = decoder.Invoke(null, null) as string;

            if (string.IsNullOrWhiteSpace(notice) ||
                !notice.Contains(ExpectedNoticeMarker, StringComparison.Ordinal) ||
                !notice.Contains(ExpectedOfficialMarker, StringComparison.Ordinal) ||
                !notice.Contains(ExpectedFreeMarker, StringComparison.OrdinalIgnoreCase))
            {
                FailFast("Distribution notice integrity check failed.");
            }
        }
        catch (TargetInvocationException ex) when (ex.InnerException is not null)
        {
            FailFast("Distribution notice integrity check failed.", ex.InnerException);
        }
        catch (Exception ex)
        {
            FailFast("Distribution notice integrity check failed.", ex);
        }
    }

    [DoesNotReturn]
    private static void FailFast(string message, Exception? exception = null)
    {
        Environment.FailFast(message, exception);
    }
}
