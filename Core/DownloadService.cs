using System;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Net.Http;

namespace Core
{
    public static class DownloadService
    {
        public static async Task RunDownload(string[] args)
        {
            if (args.Length < 5)
            {
                Logger.Error("Invalid arguments.");
                return;
            }

            string action = args[0];
            string gameId = args[1];
            string matchingField = args[2];
            string updateFrom = args[3];

            string updateTo = args.Length >= 6
                    ? args[4]
                    : "";

            string outputDir = args[^1];
            string encoded = "SEs0RSBTb3Bob24gRG93bmxvYWRlciBDb3B5cmlnaHQgKEMpIDIwMjYgR2VzdGhvc05ldHdvcms=";
            Console.WriteLine(Encoding.UTF8.GetString(Convert.FromBase64String(encoded)));

            if (!Enum.TryParse(AppConfig.Config.Region, out Region region))
            {
                region = Region.OSREL;
            }

            BranchType branch = Enum.TryParse(AppConfig.Config.Branch, true, out BranchType parsedBranch)
                    ? parsedBranch
                    : BranchType.Main;

            Game game = new(region, gameId);

            SophonUrl urlPrev = new(
                    region,
                    game.GetGameId(),
                    BranchType.Main,
                    AppConfig.Config.LauncherId,
                    AppConfig.Config.PlatApp
                );

            SophonUrl urlNew = new(
                    region,
                    game.GetGameId(),
                    branch,
                    AppConfig.Config.LauncherId,
                    AppConfig.Config.PlatApp
                );

            updateFrom = NormalizeVersion(updateFrom);
            updateTo = NormalizeVersion(updateTo);
            Logger.Info("Initializing region, branch, and game info...");

            try
            {
                await urlPrev.GetBuildData();
                await urlNew.GetBuildData();
            }
            catch (HttpRequestException)
            {
                Logger.Error("Unable to connect to the internet.");
                Console.WriteLine("Press any key to exit...");
                Console.ReadKey();
                return;
            }
            catch (Exception ex)
            {
                Logger.Error("Unexpected error: {0}", ex.Message);
                Console.WriteLine("Press any key to exit...");
                Console.ReadKey();
                return;
            }

            string prevManifest = urlPrev.GetBuildUrl(updateFrom, false);
            string newManifest = action.Equals("update", StringComparison.OrdinalIgnoreCase)
                    ? urlNew.GetBuildUrl(updateTo, true)
                    : "";

            if (action.Equals("update", StringComparison.OrdinalIgnoreCase))
            {
                Logger.Debug("Previous manifest: {0}", prevManifest);
                Logger.Debug("New manifest: {0}", newManifest);
            }
            else
            {
                Logger.Debug("Manifest: {0}", prevManifest);
            }

            await Downloader.StartDownload(
                prevManifest,
                newManifest,
                outputDir,
                matchingField
            );
        }

        private static string NormalizeVersion(
            string version)
        {
            if (string.IsNullOrWhiteSpace(version))
                return string.Empty;

            if (version.Equals("Latest", StringComparison.OrdinalIgnoreCase))
            {
                return "Latest";
            }

            if (version.Count(c => c == '.') == 1)
            {
                return version + ".0";
            }

            return version;
        }
    }
}
