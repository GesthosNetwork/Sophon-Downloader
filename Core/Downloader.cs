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
        private static readonly ManualResetEventSlim PauseEvent = new(true);
        private static CancellationTokenSource? _downloadCancel;
        private static volatile bool _isPaused;

        public static async Task<int> StartDownload(
            string prevManifestUrl,
            string newManifestUrl,
            string outputDir,
            string matchingField)
        {
            Utils.DisableQuickEdit();

            bool isFirstRun = true;

            _downloadCancel?.Dispose();
            _downloadCancel = new CancellationTokenSource();

            PauseEvent.Set();
            _isPaused = false;

            ConsoleCancelEventHandler cancelHandler = (_, e) =>
            {
                e.Cancel = true;
                TryCancelDownload();
            };

            Console.CancelKeyPress += cancelHandler;

            using var httpCheckClient = new HttpClient
            {
                Timeout = TimeSpan.FromSeconds(3)
            };

            try
            {
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
                    if (_downloadCancel.IsCancellationRequested)
                    {
                        Logger.ClearStatus();
                        Logger.Warning("Download cancelled.");
                        return 0;
                    }

                    using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(_downloadCancel.Token);
                    using var httpClient = CreateHttpClient();

                    Logger.Info("Fetching assets...");

                    var result = await Assets.GetAssetsFromManifests(
                        httpClient,
                        matchingField,
                        prevManifestUrl,
                        newManifestUrl,
                        linkedCts);

                    if (result?.Item1 == null)
                    {
                        return Error("Failed to fetch manifest.");
                    }

                    var assets = result.Item1;
                    int total = assets.Count;
                    int done = 0;
                    int downloading = 0;
                    long currentRead = 0;

                    bool parallelMode = AppConfig.Config.DownloadMode.Equals("Parallel", StringComparison.OrdinalIgnoreCase);

                    if (isFirstRun)
                    {
                        Logger.Info("Found {0} assets", total);
                        Logger.Info("Total download size is {0}", Utils.FormatSize(result.Item2));
                        Logger.Info("Download mode: {0}", parallelMode
                            ? $"Parallel ({AppConfig.Config.Threads} threads)"
                            : "Sequential");

                        int promptLine = Console.CursorTop;

                        while (true)
                        {
                            if (!Console.IsOutputRedirected)
                            {
                                Console.SetCursorPosition(0, promptLine);
                                Console.Write(new string(' ', Math.Max(1, Console.BufferWidth - 1)));
                                Console.SetCursorPosition(0, promptLine);
                            }

                            Console.Write("Continue? (yes/no): ");

                            var input = Console.ReadLine()?.Trim().ToLowerInvariant();

                            if (input == "y" || input == "yes")
                            {
                                Directory.CreateDirectory(outputDir);

                                isFirstRun = false;

                                if (!Console.IsOutputRedirected)
                                {
                                    Console.SetCursorPosition(0, promptLine);
                                    Console.Write(new string(' ', Math.Max(1, Console.BufferWidth - 1)));
                                    Console.SetCursorPosition(0, promptLine);
                                    Console.WriteLine();
                                }

                                Logger.SetStatus("[P] Pause    [R] Resume    [C] Cancel");
                                StartControlListener();
                                break;
                            }

                            if (input == "n" || input == "no")
                            {
                                TryCancelDownload();
                                Logger.ClearStatus();
                                return 0;
                            }
                        }
                    }

                    var stopwatch = Stopwatch.StartNew();
                    bool restartFetch = false;
                    bool cancelled = false;

                    void Render()
                    {
                        double seconds = Math.Max(1, stopwatch.Elapsed.TotalSeconds);
                        long safeRead = Math.Max(0, Interlocked.Read(ref currentRead));
                        int safeDone = Volatile.Read(ref done);
                        int safeDownloading = Volatile.Read(ref downloading);
                        string speed = $"{Utils.FormatSize((long)(safeRead / seconds))}/s";

                        if (parallelMode)
                        {
                            Logger.WriteProgress($"{StatusMessage} | {safeDone}/{total} completed | In progress: {safeDownloading} | {speed}");
                        }
                        else
                        {
                            Logger.WriteProgress($"{StatusMessage} | {safeDone}/{total} files ({speed})");
                        }
                    }

                    async Task DownloadAsset(SophonAsset asset)
                    {
                        linkedCts.Token.ThrowIfCancellationRequested();
                        WaitIfPaused(linkedCts.Token);

                        string path = Path.Combine(outputDir, asset.AssetName);

                        if (File.Exists(path))
                        {
                            Interlocked.Increment(ref done);
                            Render();
                            return;
                        }

                        bool countedDownloading = false;

                        if (parallelMode)
                        {
                            Interlocked.Increment(ref downloading);
                            countedDownloading = true;
                            Render();
                        }

                        try
                        {
                            WaitIfPaused(linkedCts.Token);

                            await asset.WriteUpdateAsync(httpClient, outputDir, outputDir, outputDir, false, read =>
                                {
                                    WaitIfPaused(linkedCts.Token);

                                    if (read > 0)
                                    {
                                        Interlocked.Add(ref currentRead, read);
                                    }

                                    Render();
                                },
                                null,
                                null,
                                linkedCts.Token);

                            string tempPath = path + "_tempUpdate";

                            if (File.Exists(tempPath))
                            {
                                File.Move(tempPath, path, true);
                            }

                            Interlocked.Increment(ref done);
                            Render();
                        }
                        catch (OperationCanceledException)
                        {
                            cancelled = true;
                            throw;
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

                            throw;
                        }
                        finally
                        {
                            if (countedDownloading)
                            {
                                Interlocked.Decrement(ref downloading);
                                Render();
                            }
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
                            catch (OperationCanceledException)
                            {
                                cancelled = true;
                                break;
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
                            await semaphore.WaitAsync(linkedCts.Token);

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
                        catch (OperationCanceledException)
                        {
                            cancelled = true;
                        }
                        catch
                        {
                            restartFetch = true;
                        }
                    }

                    if (parallelMode)
                    {
                        await DownloadParallel();
                    }
                    else
                    {
                        await DownloadSequential();
                    }

                    if (cancelled ||
                        _downloadCancel.IsCancellationRequested ||
                        linkedCts.Token.IsCancellationRequested)
                    {
                        Logger.ClearProgress();
                        Logger.ClearStatus();
                        Logger.Warning("Download cancelled.");
                        return 0;
                    }

                    if (restartFetch)
                    {
                        Logger.ClearProgress();
                        Logger.Warning("Download interrupted. Refreshing assets...");
                        await WaitInternet();
                        continue;
                    }

                    double seconds = Math.Max(1, stopwatch.Elapsed.TotalSeconds);
                    string finalSpeed = $"{Utils.FormatSize((long)(Math.Max(0, currentRead) / seconds))}/s";

                    if (parallelMode)
                    {
                        Logger.Info("Download completed | {0}/{1} files | {2}", total, total, finalSpeed);
                    }
                    else
                    {
                        Logger.Info("{0} | {1}/{2} files", StatusMessage, total, total);
                    }

                    Logger.Info("Elapsed time: {0}", stopwatch.Elapsed.ToString(@"hh\:mm\:ss"));
                    Logger.ClearProgress();
                    Logger.ClearStatus();

                    return 0;
                }
            }
            finally
            {
                Console.CancelKeyPress -= cancelHandler;

                try
                {
                    _downloadCancel?.Cancel();
                }
                catch {}

                _downloadCancel?.Dispose();
                _downloadCancel = null;

                PauseEvent.Set();
                _isPaused = false;
            }
        }

        private static void StartControlListener()
        {
            _ = Task.Run(() =>
            {
                while (_downloadCancel != null &&
                       !_downloadCancel.IsCancellationRequested)
                {
                    try
                    {
                        var key = Console.ReadKey(true).Key;

                        switch (key)
                        {
                            case ConsoleKey.P:
                                if (!_isPaused)
                                {
                                    _isPaused = true;
                                    PauseEvent.Reset();
                                    Logger.Warning("Download paused.");
                                }
                                break;

                            case ConsoleKey.R:
                                if (_isPaused)
                                {
                                    _isPaused = false;
                                    PauseEvent.Set();
                                    Logger.Info("Download resumed.");
                                }
                                break;

                            case ConsoleKey.C:
                                TryCancelDownload();
                                Logger.Warning("Download cancelled.");
                                break;
                        }
                    }
                    catch
                    {
                        break;
                    }
                }
            });
        }

        private static void TryCancelDownload()
        {
            try
            {
                _downloadCancel?.Cancel();
            }
            catch {}

            PauseEvent.Set();
            _isPaused = false;
        }

        private static void WaitIfPaused(
            CancellationToken token)
        {
            while (!PauseEvent.IsSet)
            {
                token.ThrowIfCancellationRequested();
                Thread.Sleep(100);
            }
        }

        private static int Error(
            string msg)
        {
            Logger.Error(msg);
            return 1;
        }
    }
}
