using SophonDownloader.Models;

namespace SophonDownloader.Services;

public interface ILdiffMetadataProvider
{
    Task<LdiffSource?> ResolveAsync(
        GameInfo game,
        string targetVersion,
        string matchingField,
        string fromVersion,
        CancellationToken cancellationToken = default);
}

public sealed record LdiffSource(
    string DiffListUrl,
    string DiffManifestUrl,
    string? Language);

public sealed class LdiffMetadataProvider : ILdiffMetadataProvider
{
    private static readonly HttpClient HttpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(30)
    };

    public async Task<LdiffSource?> ResolveAsync(
        GameInfo game,
        string targetVersion,
        string matchingField,
        string fromVersion,
        CancellationToken cancellationToken = default)
    {
        using JsonDocument repository =
            await LoadRepositoryManifestAsync(game.GameId, cancellationToken);

        JsonElement versionEntry = repository.RootElement
            .GetProperty("game_versions")
            .EnumerateArray()
            .FirstOrDefault(x =>
                string.Equals(
                    x.GetProperty("metadata").GetProperty("version").GetString(),
                    targetVersion, StringComparison.OrdinalIgnoreCase));

        if (versionEntry.ValueKind == JsonValueKind.Undefined)
            return null;

        string? categoryUrlKey = ResolveUrlKey(matchingField, versionEntry);
        if (string.IsNullOrWhiteSpace(categoryUrlKey))
            return null;

        JsonElement diffListUrls = versionEntry
            .GetProperty("metadata")
            .GetProperty("diff_list_url");

        if (!diffListUrls.TryGetProperty(
                categoryUrlKey,
                out JsonElement listUrlElement))
            return null;

        string? diffListUrl = listUrlElement.GetString();
        if (string.IsNullOrWhiteSpace(diffListUrl))
            return null;

        JsonElement diffContainer =
            ResolveDiffContainer(versionEntry, categoryUrlKey);

        JsonElement diffEntry = diffContainer
            .GetProperty("diff")
            .EnumerateArray()
            .FirstOrDefault(x =>
                string.Equals(
                    x.GetProperty("original_version").GetString(),
                    fromVersion, StringComparison.OrdinalIgnoreCase) &&
                (!x.TryGetProperty(
                    "language", out JsonElement language) ||
                 string.Equals(
                    language.GetString(),
                    ResolveLanguage(matchingField),
                    StringComparison.OrdinalIgnoreCase)));

        if (diffEntry.ValueKind == JsonValueKind.Undefined ||
            !diffEntry.TryGetProperty(
                "file_url", out JsonElement manifestUrlElement))
            return null;

        string? diffManifestUrl = manifestUrlElement.GetString();
        if (string.IsNullOrWhiteSpace(diffManifestUrl))
            return null;

        return new LdiffSource(diffListUrl, diffManifestUrl, ResolveLanguage(matchingField));
    }

    private static async Task<JsonDocument> LoadRepositoryManifestAsync(
        string gameId, CancellationToken cancellationToken)
    {
        string url = $"https://gitlab.com/GesthosNetwork/game-manifests/-/raw/main/{gameId}.json";
        return await LoadJsonAsync(url, cancellationToken);
    }

    private static string? ResolveUrlKey(
        string matchingField, JsonElement versionEntry)
    {
        string normalized = matchingField.Trim().ToLowerInvariant();

        if (normalized is "game" or "en-us" or "zh-cn" or "ja-jp" or "ko-kr")
            return normalized.Replace('-', '_');

        if (normalized.Contains("en"))
            return "en_us";

        if (normalized.Contains("zh"))
            return "zh_cn";

        if (normalized.Contains("ja"))
            return "ja_jp";

        if (normalized.Contains("ko"))
            return "ko_kr";

        if (normalized.Contains("game"))
            return "game";

        if (versionEntry
            .GetProperty("metadata")
            .GetProperty("diff_list_url")
            .TryGetProperty(normalized, out _))
            return normalized;

        return null;
    }

    private static JsonElement ResolveDiffContainer(
        JsonElement versionEntry,
        string categoryKey)
    {
        if (categoryKey == "game")
            return versionEntry.GetProperty("game");

        return versionEntry.GetProperty("audio");
    }

    private static string? ResolveLanguage(string matchingField) =>
        matchingField.Trim().ToLowerInvariant() switch
        {
            "en-us" or "en_us" => "en-us",
            "zh-cn" or "zh_cn" => "zh-cn",
            "ja-jp" or "ja_jp" => "ja-jp",
            "ko-kr" or "ko_kr" => "ko-kr",
            _ => null
        };

    private static async Task<JsonDocument> LoadJsonAsync(
        string url, CancellationToken cancellationToken)
    {
        using HttpResponseMessage response = await HttpClient.GetAsync(url, cancellationToken);
        response.EnsureSuccessStatusCode();
        byte[] bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        return JsonDocument.Parse(bytes);
    }
}
