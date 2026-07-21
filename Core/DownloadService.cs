using System;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;

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
            string regionText = args[1];
            string matchingField = args[2];
            string updateFrom = args[3];
            string updateTo = string.Empty;
            string outputDir;

            if (action.Equals("update", StringComparison.OrdinalIgnoreCase))
            {
                if (args.Length < 6)
                {
                    Logger.Error("Invalid arguments for update mode.");
                    return;
                }

                updateTo = args[4];
                outputDir = args[5];
            }
            else
            {
                outputDir = args[4];
            }

            string encoded = "SEs0RSBTb3Bob24gRG93bmxvYWRlciBDb3B5cmlnaHQgKEMpIDIwMjYgR2VzdGhvc05ldHdvcms=";
            Console.WriteLine(Encoding.UTF8.GetString(Convert.FromBase64String(encoded)));

            if (!Enum.TryParse(regionText, true, out Region region))
            {
                Logger.Warning($"Invalid region '{regionText}', defaulting to OSREL.");
                region = Region.OSREL;
            }

            Game.GameType gameType = region == Region.CNREL
                ? Game.GameType.hk4e_cn
                : Game.GameType.hk4e_global;

            Game game = new(gameType.ToString());

            BranchType branch = action.Equals("predownload", StringComparison.OrdinalIgnoreCase)
                ? BranchType.PreDownload
                : BranchType.Main;

            SophonUrl sophon = new(game, branch);
            updateFrom = NormalizeVersion(updateFrom);
            updateTo = NormalizeVersion(updateTo);

            if (action.Equals("predownload", StringComparison.OrdinalIgnoreCase) &&
                string.IsNullOrWhiteSpace(updateFrom))
            {
                updateFrom = "Latest";
            }

            Logger.Info("Initializing game and branch info...");

            try
            {
                await sophon.GetBuildData();
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

            string prevManifest = sophon.GetBuildUrl(updateFrom, false);
            string newManifest = action.Equals("update", StringComparison.OrdinalIgnoreCase)
                ? sophon.GetBuildUrl(updateTo, true)
                : string.Empty;

            if (action.Equals("update", StringComparison.OrdinalIgnoreCase))
            {
                Logger.Debug("Previous manifest: {0}", prevManifest);
                Logger.Debug("New manifest: {0}", newManifest);
            }
            else
            {
                Logger.Debug("Manifest: {0}", prevManifest);
            }

            await Downloader.StartDownload(prevManifest, newManifest, outputDir, matchingField);
        }

        private static string NormalizeVersion(string version)
        {
            if (string.IsNullOrWhiteSpace(version))
                return string.Empty;

            if (version.Equals("Latest", StringComparison.OrdinalIgnoreCase))
                return "Latest";

            if (version.Count(c => c == '.') == 1)
                return version + ".0";

            return version;
        }
    }
}
