using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace Core
{
    public static class InteractiveMenu
    {
        private static VersionsConfig? _versionsCache;

        private static string?
            _cacheRegion,
            _cacheBranch,
            _cacheLauncherId,
            _cachePackageId,
            _cachePlatApp,
            _cachePassword;

        public static async Task<int> RunInteractiveMenu()
        {
            while (true)
            {
                Console.Clear();
                Console.WriteLine("=== Sophon Downloader ===\n");
                Console.WriteLine("[1] Full Download");
                Console.WriteLine("[2] Update Download");
                Console.WriteLine("[0] Exit");
                Console.Write("\nChoose: ");

                string input = Console.ReadLine()?.Trim() ?? "";

                if (input == "0")
                    return 0;

                if (input == "1")
                    await RunDownloadCategoryMenu("full");
                else if (input == "2")
                    await RunDownloadCategoryMenu("update");
            }
        }

        private static async Task RunDownloadCategoryMenu(string mode)
        {
            string[] langs =
            {
                "game",
                "en-us",
                "ja-jp",
                "zh-cn",
                "ko-kr"
            };

            while (true)
            {
                Console.Clear();
                Console.WriteLine($"=== {(mode == "full" ? "Full" : "Update")} Download ===\n");

                for (int i = 0; i < langs.Length; i++)
                    Console.WriteLine($"[{i + 1}] {langs[i]}");

                Console.WriteLine("[0] Back");
                Console.Write("\nChoose: ");

                string input = Console.ReadLine()?.Trim() ?? "";

                if (input == "0")
                    return;

                if (int.TryParse(input, out int c) &&
                    c >= 1 &&
                    c <= langs.Length)
                {
                    await RunVersionPickerMenu(mode, langs[c - 1]);
                }
            }
        }

        private static async Task<VersionsConfig> GetCachedVersionsAsync(SophonUrl sophon)
        {
            string currentRegion = AppConfig.Config.Region;
            string currentBranch = AppConfig.Config.Branch;
            string currentLauncherId = AppConfig.Config.LauncherId;
            string currentPackageId = AppConfig.Config.PackageId;
            string currentPlatApp = AppConfig.Config.PlatApp;
            string currentPassword = AppConfig.Config.Password;

            if (_versionsCache != null &&
                _cacheRegion == currentRegion &&
                _cacheBranch == currentBranch &&
                _cacheLauncherId == currentLauncherId &&
                _cachePackageId == currentPackageId &&
                _cachePlatApp == currentPlatApp &&
                _cachePassword == currentPassword)
            {
                return _versionsCache;
            }

            Logger.Info("Fetching version list...");

            await sophon.GetBuildData();

            _versionsCache = await sophon.GetVersionsAsync();

            _cacheRegion = currentRegion;
            _cacheBranch = currentBranch;
            _cacheLauncherId = currentLauncherId;
            _cachePackageId = currentPackageId;
            _cachePlatApp = currentPlatApp;
            _cachePassword = currentPassword;

            return _versionsCache;
        }

        private static async Task RunVersionPickerMenu(string mode, string lang)
        {
            Region region = Enum.TryParse(
                AppConfig.Config.Region,
                true,
                out Region parsedRegion)
                    ? parsedRegion
                    : Region.OSREL;

            string gameId = new Game(
                region,
                Game.GameType.hk4e.ToString()
            ).GetGameId();

            BranchType branch = Enum.TryParse(
                AppConfig.Config.Branch,
                true,
                out BranchType parsedBranch)
                    ? parsedBranch
                    : BranchType.Main;

            SophonUrl sophon = new SophonUrl(
                region,
                gameId,
                branch,
                AppConfig.Config.LauncherId,
                AppConfig.Config.PlatApp
            );

            VersionsConfig versions;

            try
            {
                Console.Clear();
                Console.WriteLine($"=== {(mode == "full" ? "Full" : "Update")} Download: {lang} ===\n");

                versions = await GetCachedVersionsAsync(sophon);
            }
            catch (Exception ex)
            {
                Logger.Error("Cannot get version list: {0}", ex.Message);
                Console.WriteLine("\nPress any key...");
                Console.ReadKey();
                return;
            }

            string[][] versionList = mode == "full"
                ? versions.Full
                    .Select(v => new[] { v })
                    .ToArray()
                : versions.Update
                    .Select(v => v.ToArray())
                    .ToArray();

            while (true)
            {
                Console.Clear();
                Console.WriteLine($"=== {(mode == "full" ? "Full" : "Update")} Download: {lang} ===\n");

                for (int i = 0; i < versionList.Length; i++)
                {
                    var v = versionList[i];

                    string label = mode == "full"
                        ? $"Version {v[0]}"
                        : $"From {v[0]} → {v[1]}";

                    Console.WriteLine($"[{i + 1}] {label}");
                }

                Console.WriteLine("[0] Back");
                Console.Write("\nChoose: ");

                string input = Console.ReadLine()?.Trim() ?? "";

                if (input == "0")
                    return;

                if (!int.TryParse(input, out int choice))
                    continue;

                if (choice < 1 || choice > versionList.Length)
                    continue;

                string[] selected = versionList[choice - 1];
                string v1 = NormalizeVersion(selected[0]);
                string outputDir;

                if (mode == "full")
                {
                    outputDir = Path.Combine("Downloads", $"{lang}_{v1}");

                    string[] args =
                    {
                        "full",
                        gameId,
                        lang,
                        v1,
                        outputDir
                    };

                    Console.Clear();
                    Console.WriteLine($"Executing:\nSophon.Downloader.exe {string.Join(" ", args)}\n");

                    await DownloadService.RunDownload(args);
                }
                else
                {
                    string v2 = NormalizeVersion(selected[1]);

                    outputDir = Path.Combine("Downloads", $"{lang}_{v1}_{v2}_diff");

                    string[] args =
                    {
                        "update",
                        gameId,
                        lang,
                        v1,
                        v2,
                        outputDir
                    };

                    Console.Clear();
                    Console.WriteLine($"Executing:\nSophon.Downloader.exe {string.Join(" ", args)}\n");

                    await DownloadService.RunDownload(args);
                }

                Console.WriteLine("\nPress any key to return...");
                Console.ReadKey();
            }
        }

        private static string NormalizeVersion(string version)
        {
            if (string.IsNullOrWhiteSpace(version))
                return string.Empty;

            var parts = version.Split('.');

            if (parts.Length == 1)
                return $"{parts[0]}.0.0";

            if (parts.Length == 2)
                return $"{parts[0]}.{parts[1]}.0";

            return version;
        }
    }
}
