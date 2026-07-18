using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Mono.Options;

namespace Core
{
    public static class CliHandler
    {
        private static string EnsureNotEmpty(string v, string name)
        {
            if (string.IsNullOrWhiteSpace(v))
                throw new OptionException($"Missing value for --{name}", name);

            return v.Trim();
        }

        public static void ParseArgsAndSetConfig(string[] args)
        {
            var options = new OptionSet
            {
                {
                    "region=", "Region: OSREL or CNREL", v =>
                    {
                        var region = EnsureNotEmpty(v, "region").ToUpperInvariant();

                        if (region != "OSREL" && region != "CNREL")
                        {
                            throw new OptionException("Invalid value for --region", "region");
                        }

                        AppConfig.Config.Region = region;
                    }
                },

                {
                    "branch=", "Branch: main or predownload", v =>
                    {
                        var branch = EnsureNotEmpty(v, "branch").ToLowerInvariant();

                        if (branch != "main" && branch != "predownload")
                        {
                            throw new OptionException("Invalid value for --branch", "branch");
                        }

                        AppConfig.Config.Branch = branch;
                    }
                },

                {
                    "launcherId=", "Launcher ID override", v =>
                    {
                        AppConfig.Config.LauncherId = EnsureNotEmpty(v, "launcherId");
                    }
                },

                {
                    "platApp=", "Platform App ID override", v =>
                    {
                        AppConfig.Config.PlatApp = EnsureNotEmpty(v, "platApp");
                    }
                },

                {
                    "threads=", "Threads to use", v =>
                    {
                        if (!int.TryParse(v, out int value) || value <= 0)
                        {
                            throw new OptionException("Invalid value for --threads", "threads");
                        }

                        AppConfig.Config.Threads = value;
                    }
                },

                {
                    "handles=", "Maximum HTTP handles", v =>
                    {
                        if (!int.TryParse(v, out int value) || value <= 0)
                        {
                            throw new OptionException("Invalid value for --handles", "handles");
                        }

                        AppConfig.Config.MaxHttpHandle = value;
                    }
                },

                {
                    "downloadMode=", "Download mode: Parallel or Sequential", v =>
                    {
                        var mode = EnsureNotEmpty(v, "downloadMode");
                
                        if (!mode.Equals("Parallel", StringComparison.OrdinalIgnoreCase) &&
                            !mode.Equals("Sequential", StringComparison.OrdinalIgnoreCase))
                        {
                            throw new OptionException("Invalid value for --downloadMode. Use Parallel or Sequential", "downloadMode");
                        }
                
                        AppConfig.Config.DownloadMode = mode;
                    }
                },

                {
                    "CNREL", "Switch to CN region", _ =>
                    {
                        AppConfig.Config.Region = "CNREL";
                        AppConfig.Config.LauncherId = "jGHBHlcOq1";
                        AppConfig.Config.PlatApp = "ddxf5qt290cg";
                    }
                },

                {
                    "OSREL", "Switch to OS region", _ =>
                    {
                        AppConfig.Config.Region = "OSREL";
                        AppConfig.Config.LauncherId = "VYTpXlbWo8";
                        AppConfig.Config.PlatApp = "ddxf6vlr1reo";
                    }
                },

                {
                    "main", "Use main branch", _ =>
                    {
                        AppConfig.Config.Branch = "main";
                    }
                },

                {
                    "predownload", "Use predownload branch", _ =>
                    {
                        AppConfig.Config.Branch = "predownload";
                    }
                },

                {
                    "h|help", "Show help", _ =>
                    {
                    }
                }
            };

            options.Parse(args);
            AppConfig.Config.SetPasswordByBranch();
        }


        public static async Task<int> RunWithArgs(string[] args)
        {
            bool showHelp = false;

            string action = "",
                   gameId = "",
                   updateFrom = "",
                   updateTo = "",
                   outputDir = "",
                   matchingField = "";

            try
            {
                List<string> extra = new OptionSet().Parse(args);

                int count = extra.Count;

                action = count > 0
                    ? extra[0].ToLowerInvariant()
                    : "";

                if (action == "full" && count >= 5)
                {
                    gameId = extra[1];
                    matchingField = extra[2];
                    updateFrom = extra[3];
                    outputDir = extra[4];
                }
                else if (action == "update" && count >= 6)
                {
                    gameId = extra[1];
                    matchingField = extra[2];
                    updateFrom = extra[3];
                    updateTo = extra[4];
                    outputDir = extra[5];
                }
                else
                {
                    showHelp = true;
                }

                if (!showHelp)
                {
                    string fullPath = Path.GetFullPath(outputDir);
                    Directory.CreateDirectory(fullPath);
                }
            }
            catch (OptionException e)
            {
                Logger.Error("CLI error: {0}", e.Message);
                Logger.Info("Use --help to see usage information.");

                return 1;
            }

            if (showHelp)
            {
                Console.WriteLine("""
                    Sophon Downloader - Command Line Interface

                    Usage:
                      Sophon.Downloader.exe full   <gameId> <package> <version> <outputDir> [options]
                      Sophon.Downloader.exe update <gameId> <package> <fromVer> <toVer> <outputDir> [options]


                    Examples:

                      Sophon.Downloader.exe full hk4e game 6.5 Downloads\Game_6.5.0

                      Sophon.Downloader.exe update hk4e game 6.5 6.6 Downloads\Game_6.5.0_6.6.0 --predownload --OSREL --threads=4 --handles=32


                    Options:

                      --region=<OSREL|CNREL>
                      --branch=<main|predownload>
                      --launcherId=<id>
                      --platApp=<id>
                      --threads=<number>
                      --handles=<number>
                      --downloadMode=<Parallel|Sequential>

                      --OSREL
                      --CNREL
                      --main
                      --predownload

                      -h, --help
                    """);

                return 0;
            }


            var preparedArgs = action == "full"
                ? new[]
                {
                    action,
                    gameId,
                    matchingField,
                    updateFrom,
                    outputDir
                }
                : new[]
                {
                    action,
                    gameId,
                    matchingField,
                    updateFrom,
                    updateTo,
                    outputDir
                };


            await DownloadService.RunDownload(preparedArgs);

            return 0;
        }
    }
}
