using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using System.Web;

namespace Core
{
    public class BranchesRoot
    {
        public int retcode {get; set;}
        public string? message {get; set;}
        public BranchesData? data {get; set;}
    }

    public class BranchesData
    {
        public List<BranchesGameBranch>? game_branches {get; set;}
    }

    public class BranchesGameBranch
    {
        public BranchesGame? game {get; set;}
        public BranchesMain? main {get; set;}
        public BranchesMain? pre_download {get; set;}
    }

    public class BranchesGame
    {
        public string? id {get; set;}
        public string? biz {get; set;}
    }

    public class BranchesMain
    {
        public string? package_id {get; set;}
        public string? branch {get; set;}
        public string? password {get; set;}
        public string? tag {get; set;}
        public List<string>? diff_tags {get; set;}
        public List<BranchesCategory>? categories {get; set;}
    }

    public class BranchesCategory
    {
        public string? category_id {get; set;}
        public string? matching_field {get; set;}
    }

    public class BuildRoot
    {
        public int retcode {get; set;}
        public string? message {get; set;}
        public BuildData? data {get; set;}
    }

    public class BuildData
    {
        public string? build_id {get; set;}
        public string? tag {get; set;}
    }

    public class VersionsConfig
    {
        public List<string> Full {get; set;} = new();
        public List<List<string>> Update {get; set;} = new();
    }

    public enum Region
    {
        OSREL, CNREL
    }

    public enum BranchType
    {
        Main, PreDownload
    }

    public class Sophon
    {
        private static readonly HttpClient Http = new()
        {
            Timeout = TimeSpan.FromSeconds(30)
        };

        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNameCaseInsensitive = true
        };

        private readonly Game game;
        private readonly string gameId;
        private readonly string launcherId;
        private readonly string platApp;
        private readonly BranchType branch;

        private string apiBase = "";
        private string sophonBase = "";
        private string gameBiz = "";
        private string packageId = "";
        private string password = "";

        private BranchesRoot branchBackup = new();
        private VersionsConfig? versionsCache;
        private string? latestVersionCache;

        public Sophon(Game game, BranchType branch = BranchType.Main)
        {
            this.game = game;
            this.branch = branch;

            gameId = game.GameId;
            launcherId = game.LauncherId;
            platApp = game.PlatApp;
            packageId = game.PackageId;
            password = game.GetPassword(branch);

            UpdateRegion(game.Region);
        }

        public void UpdateRegion(Region region)
        {
            (apiBase, sophonBase) = region switch
            {
                Region.OSREL =>
                (
                    "https://sg-hyp-api.hoyoverse.com/hyp/hyp-connect/api/getGameBranches",
                    "https://sg-public-api.hoyoverse.com/downloader/sophon_chunk/api/getBuild"
                ),

                Region.CNREL =>
                (
                    "https://hyp-api.mihoyo.com/hyp/hyp-connect/api/getGameBranches",
                    "https://api-takumi.mihoyo.com/downloader/sophon_chunk/api/getBuild"
                ),

                _ => throw new ArgumentOutOfRangeException(nameof(region))
            };
        }

        public async Task<int> GetBuildData()
        {
            var uri = new UriBuilder(apiBase);
            var query = HttpUtility.ParseQueryString(uri.Query);

            query["game_ids[]"] = gameId;
            query["launcher_id"] = launcherId;
            uri.Query = query.ToString();

            var json = await FetchUrl(uri.ToString());
            var obj = JsonSerializer.Deserialize<BranchesRoot>(json, JsonOptions);
            var result = ParseBuildData(obj, branch);

            if (!result.ok)
            {
                Logger.Debug($"Branch data unavailable: {result.err}");
                packageId = game.PackageId;
                password = game.GetPassword(branch);
                branchBackup = new BranchesRoot();
                return 0;
            }

            gameBiz = result.biz;

            if (!string.IsNullOrWhiteSpace(result.pkg))
            {
                packageId = result.pkg;
            }

            if (!string.IsNullOrWhiteSpace(result.pass))
            {
                password = result.pass;
            }

            branchBackup = obj ?? new BranchesRoot();
            return 0;
        }

        public async Task<string?> GetLatestVersion()
        {
            if (!string.IsNullOrWhiteSpace(latestVersionCache))
            {
                return latestVersionCache;
            }

            var json = await FetchUrl(BuildSophon());
            var obj = JsonSerializer.Deserialize<BuildRoot>(json, JsonOptions);

            if (obj?.retcode != 0 || string.IsNullOrWhiteSpace(obj.data?.tag))
            {
                return null;
            }

            latestVersionCache = obj.data.tag;
            return latestVersionCache;
        }

        public async Task<VersionsConfig> GetVersionsAsync()
        {
            if (versionsCache != null)
            {
                return versionsCache;
            }

            Logger.Debug($"Branch: {branch}");
            Logger.Debug($"Package ID: {packageId}");
            Logger.Debug($"Build API: {sophonBase}");

            if (branch == BranchType.PreDownload)
            {
                throw new Exception("Version list is not available for predownload branch.");
            }

            var latest = await GetLatestVersion();

            if (string.IsNullOrWhiteSpace(latest))
            {
                throw new Exception("Latest version unavailable.");
            }

            if (!TryParseTag(latest, out int latestMajor, out int latestMinor))
            {
                throw new Exception($"Invalid version format: {latest}");
            }

            var versions = new List<string>();
            const int minMajor = 4;
            const int minMinor = 2;

            for (int major = latestMajor; major >= minMajor; major--)
            {
                int startMinor = major == latestMajor
                    ? latestMinor
                    : 99;

                for (int minor = startMinor; minor >= 0; minor--)
                {
                    if (major == minMajor && minor < minMinor)
                    {
                        break;
                    }

                    string tag = $"{major}.{minor}.0";

                    if (await IsValidBuildTagAsync(tag))
                    {
                        Logger.Debug($"Valid build: {tag}");
                        versions.Add($"{major}.{minor}");
                    }
                }
            }

            versions.Reverse();

            var result = new VersionsConfig();
            result.Full.AddRange(versions);

            for (int i = 0; i < result.Full.Count - 1; i++)
            {
                result.Update.Add(new List<string>
                {
                    result.Full[i],
                    result.Full[i + 1]
                });
            }

            Logger.Debug($"Version count: {result.Full.Count}");
            versionsCache = result;
            return result;
        }

        public string GetBuildUrl(string version, bool isUpdate = false)
        {
            if (branch == BranchType.PreDownload)
            {
                return BuildSophon();
            }

            string? tag = null;

            if (!string.Equals(version, "Latest", StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(version))
            {
                tag = version.Count(c => c == '.') == 1
                    ? $"{version}.0"
                    : version;
            }

            return BuildSophon(tag);
        }

        private string BuildSophon(
            string? tag = null)
        {
            var uri = new UriBuilder(sophonBase);
            var query = HttpUtility.ParseQueryString(uri.Query);

            query["branch"] = branch.ToString().ToLowerInvariant();
            query["package_id"] = packageId;
            query["password"] = password;
            query["plat_app"] = platApp;

            if (!string.IsNullOrWhiteSpace(tag))
            {
                query["tag"] = tag;
            }

            uri.Query = query.ToString();
            return uri.ToString();
        }

        private async Task<bool> IsValidBuildTagAsync(string tag)
        {
            var json = await FetchUrl(BuildSophon(tag));
            var obj = JsonSerializer.Deserialize<BuildRoot>(json, JsonOptions);

            return obj?.retcode == 0
                && string.Equals(obj.message, "OK", StringComparison.OrdinalIgnoreCase);
        }

        private static async Task<string> FetchUrl(string url)
        {
            return await Http.GetStringAsync(url);
        }

        private static bool TryParseTag(string tag, out int major, out int minor)
        {
            major = 0;
            minor = 0;

            var split = tag.Split('.');
            if (split.Length < 2)
            {
                return false;
            }

            return int.TryParse(split[0], out major)
                && int.TryParse(split[1], out minor);
        }

        private static BranchesMain? GetBranch(BranchesRoot obj, BranchType type)
        {
            var branch = obj.data?.game_branches?.FirstOrDefault();

            return type == BranchType.Main
                ? branch?.main
                : branch?.pre_download;
        }

        private static (bool ok, string biz, string pkg, string pass, string err)
            ParseBuildData(BranchesRoot? obj, BranchType type)
        {
            if (obj?.retcode != 0 ||
                !string.Equals(obj.message, "OK", StringComparison.OrdinalIgnoreCase))
            {
                return
                (
                    false, "", "", "", obj?.message ?? "Unknown error"
                );
            }

            var branch = GetBranch(obj, type);
            if (branch == null)
            {
                return
                (
                    false, "", "", "", $"Branch {type} not found"
                );
            }

            var game = obj.data?.game_branches?.FirstOrDefault()?.game;

            return
            (
                true,
                game?.biz ?? "",
                branch.package_id ?? "",
                branch.password ?? "",
                ""
            );
        }
    }
}
