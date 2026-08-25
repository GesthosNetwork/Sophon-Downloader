using System.IO;
using System.Net.Http;
using System.Text.Json;
using SophonDownloader.Models;
using NLog;

namespace SophonDownloader.Services;

public sealed class LegacyManifestService
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

    private static readonly HttpClient HttpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(30)
    };

    private static readonly LegacyManifestSource[] Sources =
    [
        new("hk4e_global", "Genshin Impact",
            "https://gitlab.com/GesthosNetwork/hoyo-files/-/raw/main/data/hk4e_global.json"),
        new("hkrpg_global", "Honkai: Star Rail",
            "https://gitlab.com/GesthosNetwork/hoyo-files/-/raw/main/data/hkrpg_global.json"),
        new("nap_global", "Zenless Zone Zero",
            "https://gitlab.com/GesthosNetwork/hoyo-files/-/raw/main/data/nap_global.json"),
        new("bh3_global", "Honkai Impact 3",
            "https://gitlab.com/GesthosNetwork/hoyo-files/-/raw/main/data/bh3_global.json"),
        new("hk4e_cn", "YuanShen",
            "https://gitlab.com/GesthosNetwork/hoyo-files/-/raw/main/data/hk4e_cn.json"),
        new("hkrpg_cn", "Honkai: Star Rail (CN)",
            "https://gitlab.com/GesthosNetwork/hoyo-files/-/raw/main/data/hkrpg_cn.json"),
        new("nap_cn", "Zenless Zone Zero (CN)",
            "https://gitlab.com/GesthosNetwork/hoyo-files/-/raw/main/data/nap_cn.json"),
        new("bh3_cn", "Honkai Impact 3 (CN)",
            "https://gitlab.com/GesthosNetwork/hoyo-files/-/raw/main/data/bh3_cn.json")
    ];

    public async Task<List<GameOption>> LoadGamesAsync(
        CancellationToken cancellationToken = default)
    {
        var games = new List<GameOption>();

        foreach (LegacyManifestSource source in Sources)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Logger.Info($"Loading online Legacy manifest: {source.Name} <- {source.Url}");

            try
            {
                using HttpResponseMessage response = await HttpClient.GetAsync(
                    source.Url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

                response.EnsureSuccessStatusCode();
                string json = await response.Content.ReadAsStringAsync(cancellationToken);
                LegacyManifest? manifest = JsonSerializer.Deserialize<LegacyManifest>(json);

                if (manifest is null || manifest.Count == 0)
                    throw new InvalidDataException($"Legacy manifest for {source.Name} is empty.");

                games.Add(new GameOption
                {
                    Code = source.Code,
                    Name = source.Name,
                    Manifest = manifest
                });

                Logger.Info($"Online Legacy manifest loaded: {source.Name}, versions={manifest.Count:N0}");
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                Logger.Error(ex, $"Failed to load online Legacy manifest: {source.Name}");
                throw new InvalidOperationException($"Unable to load the online manifest for {source.Name}.", ex);
            }
        }

        return games;
    }

    private sealed record LegacyManifestSource(string Code, string Name, string Url);
}
