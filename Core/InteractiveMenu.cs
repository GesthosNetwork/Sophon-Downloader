using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace Core
{
    public static class InteractiveMenu
    {
        private sealed class VersionEntry
        {
            public string From {get;}
            public string To {get;}

            public VersionEntry(string from, string to = "")
                => (From, To) = (from, to);
        }

        private static VersionsConfig? _versionsCache;

        private static string?
            _cacheGameKey,
            _cacheBranch;

        public static async Task<int> RunInteractiveMenu()
        {
            while (true)
            {
                Console.Clear();
                Console.WriteLine("""
                === Select Game ===

                [1] OSREL - Global (Genshin Impact)
                [2] CNREL - China (YuanShen)
                [X] Exit
                """);

                switch (ReadInput())
                {
                    case "1": await SelectPackageMenu(Region.OSREL); break;
                    case "2": await SelectPackageMenu(Region.CNREL); break;
                    case "x": return 0;
                }
            }
        }

        private static async Task SelectPackageMenu(Region region)
        {
            while (true)
            {
                Console.Clear();
                Console.WriteLine($"""
                === {GetRegionTitle(region)} Package ===

                [1] Full
                [2] Update
                [3] Pre-download
                [0] Back
                [X] Exit
                """);

                switch (ReadInput())
                {
                    case "1": await RunLanguageMenu(region, "full"); break;
                    case "2": await RunLanguageMenu(region, "update"); break;
                    case "3": await RunLanguageMenu(region, "predownload"); break;
                    case "0": return;
                    case "x": return;
                }
            }
        }

        private static async Task RunLanguageMenu(Region region, string mode)
        {
            string[] languages =
            {
                "game", "en-us", "ja-jp", "zh-cn", "ko-kr"
            };

            while (true)
            {
                Console.Clear();
                Console.WriteLine($"=== {FormatMode(mode)} ===\n");

                for (int i = 0; i < languages.Length; i++)
                    Console.WriteLine($"[{i + 1}] {languages[i]}");

                Console.WriteLine("""
                
                [0] Back
                [X] Exit
                """);

                string input = ReadInput();

                if (input == "x") return;
                if (input == "0") return;

                if (!int.TryParse(input, out int choice))
                    continue;

                if ((uint)(choice - 1) >= languages.Length)
                    continue;

                string language = languages[choice - 1];

                await (mode == "predownload"
                    ? RunPreDownload(region, language)
                    : RunVersionPickerMenu(region, mode, language));
            }
        }

        private static async Task<VersionsConfig> GetCachedVersionsAsync(
            SophonUrl sophon,
            Game game,
            BranchType branch)
        {
            string gameKey = $"{game.Type}_{game.Region}_{game.GameId}";
            string branchKey = branch.ToString();

            if (_versionsCache != null &&
                _cacheGameKey == gameKey &&
                _cacheBranch == branchKey)
            {
                return _versionsCache;
            }

            Console.Clear();
            Logger.Info("Fetching version list...");

            await sophon.GetBuildData();

            _versionsCache = await sophon.GetVersionsAsync();
            _cacheGameKey = gameKey;
            _cacheBranch = branchKey;

            return _versionsCache;
        }

        private static async Task RunVersionPickerMenu(Region region, string mode, string lang)
        {
            Game game = CreateGame(region);
            SophonUrl sophon = new(game, BranchType.Main);
            VersionsConfig versions;

            try
            {
                versions = await GetCachedVersionsAsync(sophon, game, BranchType.Main);
            }
            catch (Exception ex)
            {
                Logger.Error("Cannot get version list: {0}", ex.Message);
                Pause(); return;
            }

            VersionEntry[] versionsList = mode == "full"
                ? versions.Full.Select(x => new VersionEntry(x)).ToArray()
                : versions.Update.Select(x => new VersionEntry(x[0], x[1])).ToArray();

            while (true)
            {
                Console.Clear();
                Console.WriteLine($"=== {FormatMode(mode)} Download: {lang} ===\n");

                for (int i = 0; i < versionsList.Length; i++)
                {
                    VersionEntry item = versionsList[i];
                    Console.WriteLine(mode == "full"
                        ? $"[{i + 1}] Version {item.From}"
                        : $"[{i + 1}] From {item.From} -> {item.To}");
                }

                Console.WriteLine("""
                
                [0] Back
                [X] Exit
                """);

                string input = ReadInput();

                if (input == "x") return;
                if (input == "0") return;

                if (!int.TryParse(input, out int choice) ||
                    choice < 1 ||
                    choice > versionsList.Length)
                    continue;

                VersionEntry selected = versionsList[choice - 1];
                string from = NormalizeVersion(selected.From);
                string[] args;

                bool isFull = mode == "full";
                string to = isFull
                    ? ""
                    : NormalizeVersion(selected.To);

                string output = Path.Combine("Downloads", isFull
                    ? $"{lang}_{from}"
                    : $"{lang}_{from}_{to}_diff");

                args = isFull
                    ? ["full", region.ToString(), lang, from, output]
                    : ["update", region.ToString(), lang, from, to, output];

                await StartDownload(args);
                return;
            }
        }

        private static async Task RunPreDownload(Region region, string lang)
        {
            SophonUrl sophon = new(CreateGame(region), BranchType.PreDownload);
            try
            {
                await sophon.GetBuildData();
            }
            catch (Exception ex)
            {
                Logger.Error("Cannot initialize pre-download: {0}", ex.Message);
                Pause(); return;
            }

            await StartDownload(
            [
                "predownload",
                region.ToString(),
                lang,
                NormalizeVersion(await sophon.GetLatestVersion() ?? "Latest"),
                Path.Combine("Downloads", $"{lang}_predownload")
            ]);
        }

        private static async Task StartDownload(string[] args)
        {
            Console.Clear();
            Console.WriteLine("Starting download...\n");
            await DownloadService.RunDownload(args);
            Pause();
        }

        private static Game CreateGame(Region region)
        {
            return new Game(region == Region.CNREL
                ? Game.GameType.hk4e_cn.ToString()
                : Game.GameType.hk4e_global.ToString());
        }

        private static string ReadInput()
        {
            Console.Write("Choose: ");
            return Console.ReadLine()?.Trim().ToLowerInvariant() ?? "";
        }

        private static void Pause()
        {
            Console.WriteLine("\nPress any key...");
            Console.ReadKey();
        }

        private static string FormatMode(string mode)
        {
            return mode switch
            {
                "full" => "Full",
                "update" => "Update",
                "predownload" => "Pre-download",
                _ => mode
            };
        }

        private static string GetRegionTitle(Region region)
        {
            return region switch
            {
                Region.OSREL => "OSREL - Global (Genshin Impact)",
                Region.CNREL => "CNREL - China (YuanShen)",
                _ => region.ToString()
            };
        }

        private static string NormalizeVersion(string version)
        {
            if (string.IsNullOrWhiteSpace(version))
                return string.Empty;

            string[] parts = version.Split('.');

            return parts.Length switch
            {
                1 => $"{parts[0]}.0.0",
                2 => $"{parts[0]}.{parts[1]}.0",
                _ => version
            };
        }
    }
}
