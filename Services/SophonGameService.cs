using SophonDownloader.Models;

namespace SophonDownloader.Services;

public static class SophonGameService
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

    private static readonly HttpClient HttpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(10)
    };

    private static readonly Dictionary<string,
        (string ApiBase, string SophonBase, string LauncherId, string PlatApp)> RegionConfigs = new()
    {
        ["OSREL"] =
        (
            "https://sg-hyp-api.hoyoverse.com/hyp/hyp-connect/api/getGameBranches",
            "https://sg-public-api.hoyoverse.com/downloader/sophon_chunk/api/getBuild",
            "VYTpXlbWo8", "ddxf6vlr1reo"
        ),

        ["CNREL"] =
        (
            "https://hyp-api.mihoyo.com/hyp/hyp-connect/api/getGameBranches",
            "https://api-takumi.mihoyo.com/downloader/sophon_chunk/api/getBuild",
            "jGHBHlcOq1", "ddxf5qt290cg"
        ),

        ["BILIBILIYS"] =
        (
            "https://hyp-api.mihoyo.com/hyp/hyp-connect/api/getGameBranches",
            "https://downloader-api.mihoyo.com/downloader/sophon_chunk/api/getBuild",
            "umfgRO5gh5", "ddxf5qt290cg"
        ),

        ["BILIBILISR"] =
        (
            "https://hyp-api.mihoyo.com/hyp/hyp-connect/api/getGameBranches",
            "https://downloader-api.mihoyo.com/downloader/sophon_chunk/api/getBuild",
            "6P5gHMNyK3", "ddxf5qt290cg"
        ),

        ["BILIBILIJQL"] =
        (
            "https://hyp-api.mihoyo.com/hyp/hyp-connect/api/getGameBranches",
            "https://downloader-api.mihoyo.com/downloader/sophon_chunk/api/getBuild",
            "xV0f4r1GT0", "ddxf5qt290cg"
        )
    };

    private static readonly Dictionary<
        (string GameId, string Region), string> RelGameIdMapping = new()
    {
        [("nap_global", "OSREL")] = "U5hbdsT9W7",
        [("nap_cn", "CNREL")] = "x6znKlJ0xK",
        [("hkrpg_global", "OSREL")] = "4ziysqXOQ8",
        [("hkrpg_cn", "CNREL")] = "64kMb5iAWu",
        [("hk4e_global", "OSREL")] = "gopR6Cufr3",
        [("hk4e_cn", "CNREL")] = "1Z8W5NHUQb",
        [("bh3_cn", "CNREL")] = "osvnlOc0S8",
        [("bh3_global", "OSREL")] = "5TIVvvcwtM",
        [("bh3_jp", "OSREL")] = "g0mMIvshDb",
        [("bh3_kr", "OSREL")] = "uxB4MC7nzC",
        [("bh3_tw", "OSREL")] = "wkE5P5WsIf",
        [("bh3_sea", "OSREL")] = "bxPTXSET5t",
        [("nap_cn", "BILIBILIJQL")] = "HXAFlmYa17",
        [("hkrpg_cn", "BILIBILISR")] = "EdtUqXfCHh",
        [("hk4e_cn", "BILIBILIYS")] = "T2S0Gz4Dr2"
    };

    public static List<GameInfo> GetSupportedGames() =>
    [
        new("Genshin Impact", "hk4e_global", "OSREL"),
        new("YuanShen", "hk4e_cn", "CNREL"),
        new("Honkai: Star Rail", "hkrpg_global", "OSREL"),
        new("Honkai: Star Rail (CN)", "hkrpg_cn", "CNREL"),
        new("Zenless Zone Zero", "nap_global", "OSREL"),
        new("Zenless Zone Zero (CN)", "nap_cn", "CNREL"),
        new("Honkai Impact 3rd (CN)", "bh3_cn", "CNREL"),
        new("Honkai Impact 3rd", "bh3_global", "OSREL"),
        new("Honkai Impact 3rd (JP)", "bh3_jp", "OSREL"),
        new("Honkai Impact 3rd (KR)", "bh3_kr", "OSREL"),
        new("Honkai Impact 3rd (TW)", "bh3_tw", "OSREL"),
        new("Honkai Impact 3rd (SEA)", "bh3_sea", "OSREL"),
        new("Genshin Impact (Bilibili)", "hk4e_cn", "BILIBILIYS"),
        new("Honkai: Star Rail (Bilibili)", "hkrpg_cn", "BILIBILISR"),
        new("Zenless Zone Zero (Bilibili)", "nap_cn", "BILIBILIJQL")
    ];

    public static async Task<BranchesGameBranch> GetGameBranches(string gameId, string region)
    {
        var config = GetRegionConfig(region);
        string relGameId = GetRelGameId(gameId, region);

        string url = BuildQueryUrl(config.ApiBase, ("game_ids[]", relGameId), ("launcher_id", config.LauncherId));
        Logger.Debug($"Request: {url}");

        string json = await HttpClient.GetStringAsync(url);
        BranchesRoot? response = Deserialize<BranchesRoot>(json);

        if (response is null)
            throw new InvalidOperationException("The branches API returned an empty response.");

        if (response.retcode != 0)
            throw new InvalidOperationException($"Failed to get branch information: {response.message}");

        BranchesGameBranch? branch = response.data.game_branches
            .FirstOrDefault(x => x.game.id == relGameId);

        if (branch is null)
            throw new InvalidOperationException($"Could not find branch information for game {gameId} in region {region}");

        return branch;
    }

    public static string BuildGetBuildUrl(string gameId, string region, string packageId, string password, string? version, string branch = "main")
    {
        var config = GetRegionConfig(region);
        var builder = new StringBuilder(config.SophonBase)
            .Append("?branch=").Append(E(branch))
            .Append("&package_id=").Append(E(packageId))
            .Append("&password=").Append(E(password))
            .Append("&plat_app=").Append(E(config.PlatApp));

        if (branch != "predownload" || !string.IsNullOrEmpty(version))
            builder.Append("&tag=").Append(E(version ?? ""));

        return builder.ToString();
    }

    public static async Task<List<string>> GetHistoricalVersionsAsync(
        GameInfo game,
        CancellationToken cancellationToken = default)
    {
        BranchesGameBranch branches = await GetGameBranches(game.GameId, game.Region);
        BranchesMain? branch = branches.main;

        if (branch is null) return [];

        if (!TryParseVersion(branch.tag,
            out int currentMajor, out int currentMinor, out int currentPatch))
        {
            return [branch.tag];
        }

        if (string.IsNullOrWhiteSpace(branch.package_id) ||
            string.IsNullOrWhiteSpace(branch.password))
        {
            return [branch.tag];
        }

        var found = new ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase);
        var bases = new ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase);
        found.TryAdd(branch.tag, 0);

        using var semaphore = new SemaphoreSlim(12);
        var tasks = new List<Task>();

        for (int major = currentMajor; major >= 1; major--)
        {
            int maxMinor = major == currentMajor ? currentMinor : 20;

            for (int minor = maxMinor; minor >= 0; minor--)
            {
                tasks.Add(CheckVersionAsync (game, branch.package_id, branch.password,
                    $"{major}.{minor}.0", semaphore, found, bases, true, cancellationToken));
            }
        }

        await Task.WhenAll(tasks);
        tasks.Clear();

        foreach (string baseVersion in bases.Keys)
        {
            if (!TryParseVersion(baseVersion, out int major, out int minor, out _))
            { continue; }

            int maxPatch =
                major == currentMajor && minor == currentMinor
                    ? currentPatch : 9;

            for (int patch = 1; patch <= maxPatch; patch++)
            {
                tasks.Add(CheckVersionAsync (game, branch.package_id, branch.password,
                    $"{major}.{minor}.{patch}", semaphore, found, null, false, cancellationToken));
            }
        }

        await Task.WhenAll(tasks);

        return found.Keys
            .Where(v => TryParseVersion(v, out _, out _, out _))
            .OrderByDescending(v => v, Comparer<string>.Create(CompareVersions))
            .ToList();
    }

    private static async Task CheckVersionAsync(
        GameInfo game, string packageId, string password,
        string version, SemaphoreSlim semaphore,
        ConcurrentDictionary<string, byte> found,
        ConcurrentDictionary<string, byte>? bases,
        bool isBase, CancellationToken ct)
    {
        await semaphore.WaitAsync(ct);

        try
        {
            bool exists = await CheckHistoricalVersionAsync(game, packageId, password, version, found, ct);

            if (exists && isBase)
                bases!.TryAdd(version, 0);
        }
        finally { semaphore.Release(); }
    }

    private static async Task<bool> CheckHistoricalVersionAsync(
        GameInfo game, string packageId, string password, string version,
        ConcurrentDictionary<string, byte> found, CancellationToken ct)
    {
        string url = BuildGetBuildUrl(game.GameId, game.Region, packageId, password, version);

        try
        {
            using HttpResponseMessage response =
                await HttpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);

            if (response.StatusCode == HttpStatusCode.NotFound ||
                !response.IsSuccessStatusCode)
            {
                return false;
            }

            string json = await response.Content.ReadAsStringAsync(ct);
            ManifestConfig? config = Deserialize<ManifestConfig>(json);

            if (config is null ||
                config.retcode != 0 ||
                string.IsNullOrWhiteSpace(config.data.tag) ||
                !TryParseVersion(config.data.tag, out _, out _, out _))
            {
                return false;
            }

            found.TryAdd(config.data.tag, 0);
            return true;
        }
        catch (OperationCanceledException) { throw; }
        catch { return false; }
    }

    public static int CompareVersions(string a, string b)
    {
        TryParseVersion(a, out int aMajor, out int aMinor, out int aPatch);
        TryParseVersion(b, out int bMajor, out int bMinor, out int bPatch);

        int result = aMajor.CompareTo(bMajor);

        if (result != 0)
            return result;

        result = aMinor.CompareTo(bMinor);

        return result != 0 ? result : aPatch.CompareTo(bPatch);
    }

    private static bool TryParseVersion(string version, out int major, out int minor, out int patch)
    {
        major = minor = patch = 0;
        string[] parts = version.Split('.');

        return parts.Length == 3 &&
               int.TryParse(parts[0], out major) &&
               int.TryParse(parts[1], out minor) &&
               int.TryParse(parts[2], out patch);
    }

    private static (
        string ApiBase, string SophonBase, string LauncherId,
        string PlatApp) GetRegionConfig(string region) =>
        RegionConfigs.TryGetValue(region, out var config)
            ? config : throw new ArgumentException($"Unsupported region: {region}");

    private static string GetRelGameId(string gameId, string region) =>
        RelGameIdMapping.TryGetValue((gameId, region), out string? id)
            ? id : gameId;

    private static string BuildQueryUrl(
        string baseUrl, params (string Name, string Value)[] parameters)
    {
        var builder = new StringBuilder(baseUrl);
        builder.Append('?');

        for (int i = 0; i < parameters.Length; i++)
        {
            if (i > 0) builder.Append('&');
            builder.Append(E(parameters[i].Name))
                .Append('=').Append(E(parameters[i].Value));
        }

        return builder.ToString();
    }

    private static string E(string value)
        => Uri.EscapeDataString(value);

    private static T? Deserialize<T>(string json)
        => JsonSerializer.Deserialize<T>(json);
}
