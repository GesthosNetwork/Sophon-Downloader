using SophonDownloader.Models;

namespace SophonDownloader.Services;

public sealed class LegacyManifestService
{
    private const string DataApiUrl = "https://gitlab.com/api/v4/projects/GesthosNetwork%2Fhoyo-files/repository/tree";
    private const string RawBaseUrl = "https://gitlab.com/GesthosNetwork/hoyo-files/-/raw/main/data";
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

    private static readonly HttpClient HttpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(30)
    };

    public async Task<List<GameOption>> LoadGamesAsync(
        CancellationToken cancellationToken = default)
    {
        List<string> manifestFiles = await LoadManifestFilesAsync(cancellationToken);

        var games = new List<GameOption>();

        foreach (string fileName in manifestFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string url = $"{RawBaseUrl}/{fileName}";
            Logger.Info($"Loading online Legacy manifest: {fileName} <- {url}");

            try
            {
                using HttpResponseMessage response = await HttpClient.GetAsync(
                    url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

                response.EnsureSuccessStatusCode();

                string json = await response.Content.ReadAsStringAsync(cancellationToken);

                using JsonDocument document = JsonDocument.Parse(json);

                if (!document.RootElement.TryGetProperty("_metadata", out JsonElement metadata))
                {
                    throw new InvalidDataException($"Legacy manifest '{fileName}' does not contain '_metadata'.");
                }

                string gameId = metadata.GetProperty("gameId").GetString()
                    ?? throw new InvalidDataException($"Legacy manifest '{fileName}' has an invalid 'gameId'.");

                string name = metadata.GetProperty("name").GetString()
                    ?? throw new InvalidDataException($"Legacy manifest '{fileName}' has an invalid 'name'.");

                LegacyManifest? manifest =
                    JsonSerializer.Deserialize<LegacyManifest>(json);

                if (manifest is null || manifest.Count == 0)
                    throw new InvalidDataException($"Legacy manifest for {name} is empty.");

                games.Add(new GameOption
                {
                    Code = gameId,
                    Name = name,
                    Manifest = manifest
                });

                Logger.Info($"Online Legacy manifest loaded: {name}, versions={manifest.Count:N0}");
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                Logger.Error(ex, $"Failed to load online Legacy manifest: {fileName}");
                throw new InvalidOperationException($"Unable to load the online manifest '{fileName}'.", ex);
            }
        }

        return games;
    }

    private static async Task<List<string>> LoadManifestFilesAsync(
        CancellationToken cancellationToken)
    {
        string url = $"{DataApiUrl}?path=data&ref=main&per_page=100";
        using HttpResponseMessage response = await HttpClient.GetAsync(url, cancellationToken);
        response.EnsureSuccessStatusCode();

        string json = await response.Content.ReadAsStringAsync(cancellationToken);
        using JsonDocument document = JsonDocument.Parse(json);

        return document.RootElement
            .EnumerateArray().Where(x =>
                x.GetProperty("type").GetString() == "blob" &&
                x.GetProperty("name").GetString()?
                    .EndsWith(".json", StringComparison.OrdinalIgnoreCase) == true)
            .Select(x => x.GetProperty("name").GetString()!)
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}
