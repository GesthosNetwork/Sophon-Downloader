using System.Diagnostics.CodeAnalysis;
using SophonDownloader.Models;

namespace SophonDownloader.Services;

public static class ManifestResolver
{
    public static int CompareVersions(string a, string b)
    {
        int[] pa = ParseVersion(a);
        int[] pb = ParseVersion(b);

        for (int i = 0; i < 3; i++)
        {
            int result = pa[i].CompareTo(pb[i]);
            if (result != 0) return result;
        }

        return 0;
    }

    public static List<string> GetSortedVersions(LegacyManifest manifest) =>
        manifest
            .Where(pair => HasAnyDownload(pair.Value))
            .Select(pair => pair.Key)
            .OrderByDescending(version => version, Comparer<string>.Create(CompareVersions))
            .ToList();

    public static LegacyVersion? GetVersion(
        LegacyManifest manifest, string version) =>
        manifest.TryGetValue(version, out LegacyVersion? entry)
            ? entry : null;

    public static List<VoiceOption> BuildVoiceOptions(LegacyVersion version) =>
        version.Voice
            .Where(pair => HasValidUrl(pair.Value.Url))
            .Select(pair => new VoiceOption
            {
                Code = pair.Key,
                Name = GetVoiceDisplayName(pair.Key, pair.Value.Name),
                IsSelected = false
            })
            .OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase).ToList();

    public static List<string> GetVoiceUpdateSourceVersions(
        LegacyVersion targetVersion) =>
        targetVersion.Update
            .Where(pair => pair.Value.Voice.Any(
                voice => HasValidUrl(voice.Value.Url)))
            .Select(pair => pair.Key)
            .OrderByDescending(version => version,
                Comparer<string>.Create(CompareVersions)).ToList();

    public static List<string> GetGameUpdateSourceVersions(
        LegacyVersion targetVersion) =>
        targetVersion.Update
            .Where(pair => HasValidUrl(pair.Value.Game?.Url))
            .Select(pair => pair.Key)
            .OrderByDescending(version => version,
                Comparer<string>.Create(CompareVersions)).ToList();

    public static bool HasGameFull(LegacyVersion version) =>
        GetGameFullUrls(version).Count > 0;

    public static bool HasVoiceFull(LegacyVersion version) =>
        version.Voice.Any(pair => HasValidUrl(pair.Value.Url));

    public static bool HasGameUpdate(LegacyVersion version) =>
        version.Update.Any(pair => HasValidUrl(pair.Value.Game?.Url));

    public static bool HasVoiceUpdate(LegacyVersion version) =>
        version.Update.Any(pair => pair.Value.Voice.Any(
            voice => HasValidUrl(voice.Value.Url)));

    public static bool HasAnyFullDownload(LegacyVersion version) =>
        HasGameFull(version) || HasVoiceFull(version);

    public static bool HasAnyUpdateDownload(LegacyVersion version) =>
        HasGameUpdate(version) || HasVoiceUpdate(version);

    public static bool HasAnyDownload(LegacyVersion version) =>
        HasAnyFullDownload(version) || HasAnyUpdateDownload(version);

    public static bool HasValidUrl([NotNullWhen(true)] string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) &&
        (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);

    public static List<string> GetGameFullUrls(LegacyVersion version)
    {
        List<string> segmentUrls = version.Game.Segments
            .Where(segment => HasValidUrl(segment.Url))
            .OrderBy(segment => GetSegmentOrder(segment.Name, version.Game.Segments.IndexOf(segment)))
            .Select(segment => segment.Url).ToList();

        if (segmentUrls.Count > 0)
            return segmentUrls;

        if (HasValidUrl(version.Game.Full?.Url))
            return [version.Game.Full!.Url];

        return [];
    }

    public static string BuildFullVoiceUrl(
        LegacyVersion version,
        string language)
    {
        if (!version.Voice.TryGetValue(language, out LegacyPackage? package) ||
            !HasValidUrl(package.Url))
        {
            throw new InvalidOperationException($"No valid full voice download is available for {language}.");
        }

        return package.Url;
    }

    public static string BuildGameUpdateUrl(
        LegacyVersion version,
        string fromVersion)
    {
        if (!version.Update.TryGetValue(fromVersion, out LegacyUpdate? update) ||
            !HasValidUrl(update.Game?.Url))
        {
            throw new InvalidOperationException($"No valid game update is available for {fromVersion} -> target version.");
        }

        return update.Game!.Url;
    }

    private static int GetSegmentOrder(string name, int fallbackIndex)
    {
        if (string.IsNullOrWhiteSpace(name))
            return fallbackIndex;

        int dotIndex = name.LastIndexOf('.');
        if (dotIndex < 0 || dotIndex == name.Length - 1)
            return fallbackIndex;

        string suffix = name[(dotIndex + 1)..];
        return int.TryParse(suffix, out int number) ? number : fallbackIndex;
    }

    private static int[] ParseVersion(string version)
    {
        string[] parts = version.Split('.');

        return
        [
            parts.Length > 0 && int.TryParse(parts[0], out int major) ? major : 0,
            parts.Length > 1 && int.TryParse(parts[1], out int minor) ? minor : 0,
            parts.Length > 2 && int.TryParse(parts[2], out int patch) ? patch : 0
        ];
    }

    private static string GetVoiceDisplayName(string code, string name) =>
        code.Equals("en-us", StringComparison.OrdinalIgnoreCase)
            ? "English (US)"
            : code.Equals("zh-cn", StringComparison.OrdinalIgnoreCase)
                ? "Chinese"
                : code.Equals("ja-jp", StringComparison.OrdinalIgnoreCase)
                    ? "Japanese"
                    : code.Equals("ko-kr", StringComparison.OrdinalIgnoreCase)
                        ? "Korean"
                        : string.IsNullOrWhiteSpace(name)
                            ? code
                            : name;
}
