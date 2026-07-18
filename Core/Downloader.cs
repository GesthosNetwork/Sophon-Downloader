using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Sophon;

namespace Core
{
    internal class Downloader
    {
        private const string StatusMessage = "Downloading...";

        public static async Task<int> StartDownload(
            string prevManifestUrl,
            string newManifestUrl,
            string outputDir,
            string matchingField)
        {
            bool isFirstRun = true;

            using var httpCheckClient = new HttpClient
            {
                Timeout = TimeSpan.FromSeconds(3)
            };

            async Task<bool> HasInternet()
            {
                try
                {
                    using var response = await httpCheckClient.GetAsync("https://example.com");
                    return response.IsSuccessStatusCode;
                }
                catch
                {
                    return false;
                }
            }

            async Task WaitInternet()
            {
                while (!await HasInternet())
                {
                    Logger.WarningRefresh("Waiting for internet connection...");
                    await Task.Delay(1000);
                }

                Logger.ClearProgress();
            }

            HttpClient CreateHttpClient()
            {
                var handler = new HttpClientHandler
                {
                    MaxConnectionsPerServer = AppConfig.Config.MaxHttpHandle
                };

                return new HttpClient(handler)
                {
                    Timeout = TimeSpan.FromSeconds(15),
                    DefaultRequestVersion = HttpVersion.Version30,
                    DefaultVersionPolicy = HttpVersionPolicy.RequestVersionOrLower
                };
            }

            while (true)
            {
                Logger.ClearProgress();

                using var tokenSource = new CancellationTokenSource();
                using var httpClient = CreateHttpClient();

                Logger.Info("Fetching assets...");

                var result = await Assets.GetAssetsFromManifests(
                    httpClient,
                    matchingField,
                    prevManifestUrl,
                    newManifestUrl,
                    tokenSource
                );

                if (result?.Item1 == null)
                    return Error("Failed to fetch manifest.");

                var assets = result.Item1;
                int total = assets.Count;
                int done = 0;
                int downloading = 0;
                long currentRead = 0;

                if (isFirstRun)
                {
                    Logger.Info("Found {0} assets", total);
                    Logger.Info("Total download size is {0}", Utils.FormatSize(result.Item2));
                    Logger.Info("Download mode: {0}", AppConfig.Config.DownloadMode.Equals("Parallel", StringComparison.OrdinalIgnoreCase)
                        ? $"Parallel ({AppConfig.Config.Threads} threads)"
                        : "Sequential");

                    int promptLine = Console.CursorTop;

                    while (true)
                    {
                        Console.SetCursorPosition(0, promptLine);
                        Console.Write(new string(' ', Console.WindowWidth - 1));
                        Console.SetCursorPosition(0, promptLine);
                        Console.Write("Continue? (yes/no): ");

                        var input = Console.ReadLine()?.Trim().ToLower();
                        if (input == "y" || input == "yes")
                        {
                            Directory.CreateDirectory(outputDir);
                            isFirstRun = false;
                            break;
                        }

                        if (input == "n" || input == "no")
                        {
                            return 0;
                        }
                    }
                }

                var stopwatch = Stopwatch.StartNew();
                bool restartFetch = false;

                void Render()
                {
                    double seconds = Math.Max(1, stopwatch.Elapsed.TotalSeconds);
                    long safeRead = Math.Max(0, currentRead);
                    string speed = $"{Utils.FormatSize((long)(safeRead / seconds))}/s";

                    if (AppConfig.Config.DownloadMode.Equals("Parallel", StringComparison.OrdinalIgnoreCase))
                    {
                        Logger.WriteProgress($"{StatusMessage} | {done}/{total} completed | In progress: {downloading} | {speed}");
                    }
                    else
                    {
                        Logger.WriteProgress($"{StatusMessage} | {done}/{total} files ({speed})");
                    }
                }

                async Task DownloadAsset(SophonAsset asset)
                {
                    string path = Path.Combine(outputDir, asset.AssetName);

                    if (File.Exists(path))
                    {
                        Interlocked.Increment(ref done);
                        return;
                    }

                    if (AppConfig.Config.DownloadMode.Equals("Parallel", StringComparison.OrdinalIgnoreCase))
                    {
                        Interlocked.Increment(ref downloading);
                        Render();
                    }

                    try
                    {
                        await asset.WriteUpdateAsync(httpClient, outputDir, outputDir, outputDir,
                            false, read =>
                            {
                                if (read > 0)
                                {
                                    Interlocked.Add(ref currentRead, read);
                                }
                                Render();
                            },
                            null, null, tokenSource.Token
                        );

                        string tempPath = path + "_tempUpdate";

                        if (File.Exists(tempPath))
                        {
                            File.Move(tempPath, path, true);
                        }

                        if (AppConfig.Config.DownloadMode.Equals("Parallel", StringComparison.OrdinalIgnoreCase))
                        {
                            Interlocked.Decrement(ref downloading);
                        }

                        Interlocked.Increment(ref done);
                        Render();
                    }
                    catch (Exception ex) when (
                        ex is HttpRequestException ||
                        ex is TaskCanceledException ||
                        ex is IOException)
                    {
                        string tempPath = path + "_tempUpdate";

                        try
                        {
                            if (File.Exists(tempPath))
                            {
                                File.Delete(tempPath);
                            }
                        }
                        catch {}

                        if (AppConfig.Config.DownloadMode.Equals("Parallel", StringComparison.OrdinalIgnoreCase))
                        {
                            Interlocked.Decrement(ref downloading);
                            Render();
                        }

                        throw;
                    }
                }

                async Task DownloadSequential()
                {
                    foreach (var asset in assets)
                    {
                        try
                        {
                            await DownloadAsset(asset);
                        }
                        catch
                        {
                            restartFetch = true;
                            break;
                        }
                    }
                }

                async Task DownloadParallel()
                {
                    using var semaphore = new SemaphoreSlim(AppConfig.Config.Threads);

                    var tasks = assets.Select(async asset =>
                        {
                            await semaphore.WaitAsync();

                            try
                            {
                                await DownloadAsset(asset);
                            }
                            finally
                            {
                                semaphore.Release();
                            }
                        });

                    try
                    {
                        await Task.WhenAll(tasks);
                    }
                    catch
                    {
                        restartFetch = true;
                    }
                }

                if (AppConfig.Config.DownloadMode.Equals("Parallel", StringComparison.OrdinalIgnoreCase))
                {
                    await DownloadParallel();
                }
                else
                {
                    await DownloadSequential();
                }

                if (restartFetch)
                {
                    Logger.ClearProgress();
                    Logger.Warning("Download interrupted. Refreshing assets...");
                    await WaitInternet();
                    continue;
                }

                if (AppConfig.Config.DownloadMode.Equals("Parallel", StringComparison.OrdinalIgnoreCase))
                {
                    Logger.Info("Download completed | {0}/{1} files", total, total, Utils.FormatSize((long)(Math.Max(0, currentRead) / Math.Max(1, stopwatch.Elapsed.TotalSeconds))));
                }
                else
                {
                    Logger.Info($"{StatusMessage} | {total}/{total} files");
                }

                Logger.Info("Elapsed time: {0}", stopwatch.Elapsed.ToString(@"hh\:mm\:ss"));
                Logger.ClearProgress();

                while (Console.KeyAvailable)
                {
                    Console.ReadKey(true);
                }

                Console.ReadKey(true);
                return 0;
            }
        }

        private static int Error(string msg)
        {
            Logger.Error(msg);
            Console.ReadKey(true);
            return 1;
        }
    }
}
