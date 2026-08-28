using System.Security.Cryptography;

namespace SophonDownloader.Utilities;

public static class Utility
{
    internal static string GetApplicationDirectory()
    {
        string? processPath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(processPath))
            throw new InvalidOperationException("Unable to determine the application executable path.");

        string? directory = Path.GetDirectoryName(processPath);
        if (string.IsNullOrWhiteSpace(directory))
            throw new InvalidOperationException("Unable to determine the application directory.");

        return Path.GetFullPath(directory);
    }

    public static async Task<string> CalculateMd5Async(Stream stream)
    {
        using var md5 = MD5.Create();
        var hash = await md5.ComputeHashAsync(stream);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    public static string FormatCompactFileSize(long bytes)
    {
        if (bytes <= 0) return "0 B";

        string[] units = ["B", "KB", "MB", "GB"];
        double size = bytes;
        int unit = 0;

        while (size >= 1024 && unit < units.Length - 1)
        {
            size /= 1024;
            unit++;
        }

        return $"{size:0.##} {units[unit]}";
    }

    public static string FormatFileSize(long bytes, int decimalPlaces = 2)
    {
        if (bytes < 0)
            throw new ArgumentException("File size cannot be negative.", nameof(bytes));

        if (bytes == 0)
            return "0 Bytes";

        string[] units = ["Bytes", "KB", "MB", "GB"];
        int unitIndex = 0;
        double size = bytes;

        while (size >= 1024 && unitIndex < units.Length - 1)
        {
            size /= 1024;
            unitIndex++;
        }

        return unitIndex == 0
            ? $"{size} {units[unitIndex]}"
            : $"{Math.Round(size, decimalPlaces)} {units[unitIndex]}";
    }

    public static string EnsureTrailingSlash(string url) =>
        url.EndsWith("/", StringComparison.Ordinal) ? url : url + "/";

    public static string FormatSpeed(double bytesPerSecond)
    {
        if (bytesPerSecond < 1024)
            return $"{bytesPerSecond:F0} B/s";

        if (bytesPerSecond < 1024 * 1024)
            return $"{bytesPerSecond / 1024:F1} KB/s";

        return $"{bytesPerSecond / (1024 * 1024):F1} MB/s";
    }

    public static int GetRecommendedDownloadConcurrency()
    {
        int logicalProcessors = Math.Max(1, Environment.ProcessorCount);

        return logicalProcessors switch
        {
            1 => 2,
            2 => 4,
            3 or 4 => 6,
            5 or 6 => 8,
            7 or 8 => 12,
            9 or 10 or 11 or 12 => 16,
            13 or 14 or 15 or 16 => 20,
            _ => 24
        };
    }
}
