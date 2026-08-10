using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Sophon;

namespace Core
{
    public static class Download
    {
        static readonly StringComparison OI = StringComparison.OrdinalIgnoreCase;

        public static async Task RunDownload(string[] a)
        {
            if (a.Length < 5)
            {
                Logger.Error("Invalid arguments.");
                return;
            }

            string act = a[0], regTxt = a[1], field = a[2], from = a[3], to = "";
            string outDir;
            string? onlyAsset = null;

            bool upd = act.Equals("update", OI);
            bool pre = act.Equals("predownload", OI);
            bool single = act.Equals("single", OI);

            if (upd)
            {
                if (a.Length < 6)
                {
                    Logger.Error("Invalid arguments for update mode.");
                    return;
                }

                to = a[4];
                outDir = a[5];

                if (a.Length > 6)
                    onlyAsset = a[6];
            }
            else if (single)
            {
                if (a.Length < 6)
                {
                    Logger.Error("Invalid arguments for single-asset mode.");
                    return;
                }

                outDir = a[4];
                onlyAsset = a[5];
            }
            else
            {
                outDir = a[4];

                if (a.Length > 5)
                    onlyAsset = a[5];
            }

            Console.WriteLine(Encoding.UTF8.GetString(
                Convert.FromBase64String("SEs0RSBTb3Bob24gRG93bmxvYWRlciBDb3B5cmlnaHQgKEMpIDIwMjYgR2VzdGhvc05ldHdvcms=")));

            if (!Enum.TryParse(regTxt, true, out Region region))
            {
                Logger.Warning($"Invalid region '{regTxt}', defaulting to OSREL.");
                region = Region.OSREL;
            }

            var game = new Game(
                (region == Region.CNREL
                    ? Game.GameType.hk4e_cn
                    : Game.GameType.hk4e_global).ToString()
            );

            var sophon = new Sophon(
                game, pre ? BranchType.PreDownload : BranchType.Main
            );

            from = NormalizeManifestName(from);
            to = NormalizeManifestName(to);
            onlyAsset = NormalizeAssetQuery(onlyAsset);

            if (pre && string.IsNullOrWhiteSpace(from))
                from = "Latest";

            Logger.Info("Initializing game and branch info...");

            try
            {
                await sophon.GetBuildData();
            }
            catch (HttpRequestException)
            {
                Logger.Error("Unable to connect to the internet.");
                Console.ReadKey();
                return;
            }
            catch (Exception ex)
            {
                Logger.Error("Unexpected error: {0}", ex.Message);
                Console.ReadKey();
                return;
            }

            if (pre)
            {
                try
                {
                    string? preVersion = await sophon.GetLatestVersion();

                    if (string.IsNullOrWhiteSpace(preVersion))
                    {
                        Logger.Error("Unable to determine PreDownload version.");
                        return;
                    }

                    Logger.Info("PreDownload version: {0}", preVersion);
                }
                catch (HttpRequestException)
                {
                    Logger.Error("Unable to fetch PreDownload version.");
                    Console.ReadKey();
                    return;
                }
                catch (Exception ex)
                {
                    Logger.Error("Unexpected error while fetching PreDownload version: {0}", ex.Message);
                    Console.ReadKey();
                    return;
                }
            }

            string prev = sophon.GetBuildUrl(from, false);
            string next = upd ? sophon.GetBuildUrl(to, true) : "";

            if (upd)
            {
                Logger.Info("Previous manifest: {0}", prev);
                Logger.Info("New manifest: {0}", next);
            }
            else
            {
                Logger.Info("Manifest: {0}", prev);
            }

            if (!string.IsNullOrWhiteSpace(onlyAsset))
                Logger.Info("Asset filter: {0}", onlyAsset);

            await Downloader.StartDownload(prev, next, outDir, field, onlyAsset, single);
        }

        static string NormalizeManifestName(string v)
        {
            if (string.IsNullOrWhiteSpace(v))
                return "";

            if (v.Equals("Latest", OI))
                return "Latest";

            return v.Count(c => c == '.') == 1 ? v + ".0" : v;
        }

        internal static string? NormalizeAssetQuery(string? value)
            => string.IsNullOrWhiteSpace(value) ? null : value.Trim().Replace('\\', '/');
    }

    internal class Downloader
    {
        sealed class SpeedTracker
        {
            readonly Queue<(long Timestamp, long Bytes)> samples = new();
            readonly object sync = new();
            const double WindowSeconds = 5.0;

            public double GetSpeed(long totalBytes)
            {
                lock (sync)
                {
                    long now = Stopwatch.GetTimestamp();
                    samples.Enqueue((now, totalBytes));

                    long threshold = now - (long)(WindowSeconds * Stopwatch.Frequency);

                    while (samples.Count > 1
                        && samples.Peek().Timestamp < threshold)
                    {
                        samples.Dequeue();
                    }

                    if (samples.Count < 2)
                        return 0;

                    var first = samples.Peek();
                    var last = samples.Last();

                    double seconds = (last.Timestamp - first.Timestamp) / (double)Stopwatch.Frequency;

                    if (seconds <= 0)
                        return 0;

                    return Math.Max(0, last.Bytes - first.Bytes) / seconds;
                }
            }
        }

        static readonly StringComparison OI = StringComparison.OrdinalIgnoreCase;
        const string DownloadingLabel = "Downloading...";
        const string ResumingLabel = "Resuming...";
        const int ProgressUpdateIntervalMs = 200;

        static readonly ManualResetEventSlim PauseEvent = new(true);
        static CancellationTokenSource? _downloadCancel;
        static volatile bool _isPaused;

        public static Func<int, long, bool, bool>? ConfirmPrompt;
        public static Func<List<SophonAsset>, int>? AssetPicker;

        public static async Task<int> StartDownload(
            string prevManifestUrl,
            string newManifestUrl,
            string outputDir,
            string matchingField,
            string? onlyAsset = null,
            bool singleMode = false)
        {
            bool first = true;
            bool selectedFromList = false;

            _downloadCancel?.Dispose();
            _downloadCancel = new CancellationTokenSource();
            PauseEvent.Set();
            _isPaused = false;

            ConsoleCancelEventHandler onCancel = (_, e) =>
            {
                e.Cancel = true;
                TryCancelDownload();
            };

            Console.CancelKeyPress += onCancel;

            HttpClient httpClient = CreateClient();

            try
            {
                async Task<bool> HasInternetAsync(CancellationToken token)
                {
                    string[] probes =
                    {
                        "https://www.gstatic.com/generate_204",
                        "https://www.msftconnecttest.com/connecttest.txt",
                        "https://cp.cloudflare.com",
                        "https://detectportal.firefox.com/success.txt",
                        "https://example.com"
                    };

                    foreach (string url in probes)
                    {
                        try
                        {
                            using var checkCts = CancellationTokenSource.CreateLinkedTokenSource(token);
                            checkCts.CancelAfter(TimeSpan.FromSeconds(3));

                            using var req = new HttpRequestMessage(HttpMethod.Get, url);
                            using var res = await httpClient.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, checkCts.Token);

                            if (res.IsSuccessStatusCode)
                                return true;

                            if ((int)res.StatusCode >= 200 && (int)res.StatusCode < 500)
                                return true;
                        }
                        catch {}
                    }

                    return false;
                }

                async Task WaitInternetAsync(CancellationToken token)
                {
                    while (!await HasInternetAsync(token))
                    {
                        Logger.WarningRefresh("Waiting for internet connection...");
                        await Task.Delay(1000, token);
                    }

                    Logger.ClearProgress();
                }

                while (true)
                {
                    if (_downloadCancel?.IsCancellationRequested == true)
                    {
                        Logger.ClearStatus();
                        Logger.Warning("Download cancelled.");
                        return 0;
                    }

                    using var linked = CancellationTokenSource.CreateLinkedTokenSource(_downloadCancel!.Token);
                    using var operationCts = CancellationTokenSource.CreateLinkedTokenSource(linked.Token);
                    CancellationToken opToken = operationCts.Token;

                    Logger.Info("Fetching assets...");

                    var res = await Assets.GetAssetsFromManifests(httpClient, matchingField, prevManifestUrl, newManifestUrl, linked);

                    if (res == null || res.Item1 == null)
                    {
                        Logger.Warning("Manifest fetch failed, retrying with a fresh connection...");

                        var staleClient = httpClient;
                        httpClient = CreateClient();
                        staleClient.Dispose();

                        res = await Assets.GetAssetsFromManifests(httpClient, matchingField, prevManifestUrl, newManifestUrl, linked);

                        if (res == null || res.Item1 == null)
                            return Err("Failed to fetch manifest.");
                    }

                    var assets = res.Item1;

                    if (singleMode && !string.IsNullOrWhiteSpace(onlyAsset))
                    {
                        var selection = SelectAssetsByQuery(assets, onlyAsset);
                        assets = selection.Assets;
                        selectedFromList = selection.SelectedFromList;

                        if (selection.Cancelled)
                        {
                            Logger.Warning("Download cancelled.");
                            return 0;
                        }

                        if (assets.Count == 0)
                        {
                            Logger.Warning("Asset not found: {0}", onlyAsset);
                            return 1;
                        }
                    }

                    int total = assets.Count;
                    int done = 0;
                    int downloading = 0;
                    long currentRead = 0;

                    bool parallel = Config.DownloadMode.Equals("Parallel", OI);

                    int assetSlots = parallel
                        ? Math.Max(1, Math.Min(Config.Threads, Config.MaxHttpHandle))
                        : 1;

                    int maxHttpHandle = Math.Max(1, Config.MaxHttpHandle);
                    int maxChunkBudgetPerAsset = Math.Max(1, maxHttpHandle / assetSlots);
                    long totalSize = assets.Sum(x => x.AssetSize);

                    var resumingAssets = new ConcurrentDictionary<string, byte>(
                        StringComparer.OrdinalIgnoreCase
                    );

                    if (Directory.Exists(outputDir))
                    {
                        foreach (var a in assets)
                        {
                            if (File.Exists(Path.Combine(outputDir, a.AssetName + "_tempUpdate")))
                                resumingAssets.TryAdd(a.AssetName, 0);
                        }
                    }

                    int resumingRemaining = resumingAssets.Count;
                    bool isResuming = resumingRemaining > 0;

                    void MarkAssetSettled(SophonAsset asset)
                    {
                        if (resumingAssets.TryRemove(asset.AssetName, out _))
                            Interlocked.Decrement(ref resumingRemaining);
                    }

                    string CurrentLabel() => Volatile.Read(ref resumingRemaining) > 0
                        ? ResumingLabel
                        : DownloadingLabel;

                    if (first)
                    {
                        bool needConfirm = !singleMode || !selectedFromList;

                        if (needConfirm)
                        {
                            Logger.Info("Found {0} asset(s)", total);
                            Logger.Info("Total download size is {0}", Utils.FormatSize(totalSize));
                            Logger.Info("Download mode: {0}", parallel
                                ? $"Parallel ({assetSlots} threads)"
                                : "Sequential"
                            );

                            if (isResuming)
                                Logger.Info("Detected an interrupted download, resuming...");
                        }

                        if (singleMode && selectedFromList)
                        {
                            Directory.CreateDirectory(outputDir);
                            first = false;

                            Logger.SetStatus("[P] Pause    [R] Resume    [C] Cancel");
                            StartControlListener();
                        }
                        else
                        {
                            bool proceed = ConfirmPrompt?.Invoke(total, totalSize, isResuming)
                                ?? true;

                            if (!proceed)
                            {
                                TryCancelDownload();
                                Logger.ClearStatus();
                                return 0;
                            }

                            Directory.CreateDirectory(outputDir);
                            first = false;

                            Logger.SetStatus("[P] Pause    [R] Resume    [C] Cancel");
                            StartControlListener();
                        }
                    }

                    var sw = Stopwatch.StartNew();
                    int restartFetch = 0;
                    int cancelled = 0;
                    long lastRenderTicks = 0;
                    int rendering = 0;
                    var speedTracker = new SpeedTracker();

                    void ReportInterruption(string message)
                    {
                        if (Interlocked.CompareExchange(ref restartFetch, 1, 0) == 0)
                            Logger.Warning(message);
                    }

                    void Render(bool force = false)
                    {
                        long nowCheck = Stopwatch.GetTimestamp();

                        if (!force)
                        {
                            long observed = Volatile.Read(ref lastRenderTicks);

                            if (observed != 0)
                            {
                                long elapsedMs = (nowCheck - observed) * 1000 / Stopwatch.Frequency;

                                if (elapsedMs < ProgressUpdateIntervalMs)
                                    return;
                            }
                        }

                        if (Interlocked.Exchange(ref rendering, 1) == 1)
                            return;

                        try
                        {
                            long now = Stopwatch.GetTimestamp();
                            long previous = Volatile.Read(ref lastRenderTicks);

                            if (!force && previous != 0)
                            {
                                long elapsedMs = (now - previous) * 1000 / Stopwatch.Frequency;

                                if (elapsedMs < ProgressUpdateIntervalMs)
                                    return;
                            }

                            Volatile.Write(ref lastRenderTicks, now);

                            long read = Math.Max(0, Interlocked.Read(ref currentRead));

                            int d = Volatile.Read(ref done);
                            int dn = Volatile.Read(ref downloading);
                            double speedBps = speedTracker.GetSpeed(read);
                            string speed = $"{Utils.FormatSize((long)speedBps)}/s";
                            string label = CurrentLabel();

                            Logger.WriteProgress(parallel
                                ? $"{label} | {d}/{total} completed | In progress: {dn} | {speed}"
                                : $"{label} | {d}/{total} files ({speed})"
                            );
                        }
                        finally
                        {
                            Volatile.Write(ref rendering, 0);
                        }
                    }

                    async Task DownloadAsset(SophonAsset asset)
                    {
                        opToken.ThrowIfCancellationRequested();
                        WaitIfPaused(opToken);

                        string path = Path.Combine(outputDir, asset.AssetName);

                        if (IsCompleteFile(path, asset.AssetSize))
                        {
                            MarkAssetSettled(asset);
                            Interlocked.Increment(ref done);
                            Render();
                            return;
                        }

                        bool counted = false;

                        if (parallel)
                        {
                            Interlocked.Increment(ref downloading);
                            counted = true;
                            Render();
                        }

                        try
                        {
                            WaitIfPaused(opToken);

                            int chunkThreads = GetChunkThreadsForAsset(
                                asset.AssetSize,
                                Config.ChunkThreads,
                                maxChunkBudgetPerAsset
                            );

                            var perAssetParallelOptions = new ParallelOptions
                            {
                                CancellationToken = opToken,
                                MaxDegreeOfParallelism = chunkThreads
                            };

                            await asset.WriteUpdateAsync(
                                client: httpClient,
                                oldInputDir: outputDir,
                                newOutputDir: outputDir,
                                chunkDir: outputDir,
                                removeChunkAfterApply: false,
                                parallelOptions: perAssetParallelOptions,
                                writeInfoDelegate: read =>
                                {
                                    WaitIfPaused(opToken);

                                    if (read > 0)
                                        Interlocked.Add(ref currentRead, read);

                                    Render();
                                },
                                downloadInfoDelegate: null,
                                downloadCompleteDelegate: null
                            );

                            string temp = path + "_tempUpdate";

                            if (File.Exists(temp))
                                MoveFileWithRetry(temp, path);

                            MarkAssetSettled(asset);
                            Interlocked.Increment(ref done);
                            Render();
                        }
                        catch (OperationCanceledException)
                        {
                            if (_downloadCancel?.IsCancellationRequested == true)
                                Interlocked.Exchange(ref cancelled, 1);
                            else
                                ReportInterruption("Network connection interrupted or unstable. Attempting to reconnect...");

                            try { operationCts.Cancel(); } catch {}
                        }
                        catch (HttpRequestException)
                        {
                            ReportInterruption("Network connection interrupted or unstable. Attempting to reconnect...");
                            try { operationCts.Cancel(); } catch {}
                        }
                        catch (IOException)
                        {
                            ReportInterruption("Network connection interrupted or unstable. Attempting to reconnect...");
                            try { operationCts.Cancel(); } catch {}
                        }
                        catch (Exception ex)
                        {
                            if (Interlocked.CompareExchange(ref restartFetch, 1, 0) == 0)
                                Logger.Error("Unexpected download error: {0}", ex.Message);

                            try { operationCts.Cancel(); } catch {}
                        }
                        finally
                        {
                            if (counted)
                            {
                                Interlocked.Decrement(ref downloading);
                                Render();
                            }
                        }
                    }

                    async Task DownloadWorkerAsync(ConcurrentQueue<SophonAsset> queue)
                    {
                        while (true)
                        {
                            if (opToken.IsCancellationRequested)
                                break;

                            if (!queue.TryDequeue(out var asset))
                                break;

                            try
                            {
                                await DownloadAsset(asset);
                            }
                            catch (OperationCanceledException)
                            {
                                if (_downloadCancel?.IsCancellationRequested == true)
                                    Interlocked.Exchange(ref cancelled, 1);
                                else
                                    Interlocked.Exchange(ref restartFetch, 1);

                                try { operationCts.Cancel(); } catch {}
                                break;
                            }
                            catch (Exception ex)
                            {
                                if (Interlocked.CompareExchange(ref restartFetch, 1, 0) == 0)
                                    Logger.Error("Unexpected download error: {0}", ex.Message);

                                try { operationCts.Cancel(); } catch {}
                                break;
                            }

                            if (restartFetch != 0 || cancelled != 0)
                                break;
                        }
                    }

                    var queue = new ConcurrentQueue<SophonAsset>(assets);
                    int workerCount = assetSlots;
                    var workers = new List<Task>(workerCount);

                    for (int i = 0; i < workerCount; i++)
                        workers.Add(DownloadWorkerAsync(queue));

                    try
                    {
                        await Task.WhenAll(workers);
                    }
                    catch (OperationCanceledException)
                    {
                        if (_downloadCancel?.IsCancellationRequested == true)
                            cancelled = 1;
                        else
                            restartFetch = 1;
                    }
                    catch (Exception ex)
                    {
                        if (Interlocked.CompareExchange(ref restartFetch, 1, 0) == 0)
                            Logger.Error("Unexpected download error: {0}", ex.Message);
                    }

                    if (cancelled != 0
                        || _downloadCancel?.IsCancellationRequested == true
                        || linked.Token.IsCancellationRequested)
                    {
                        Logger.ClearAll();
                        return 0;
                    }

                    if (restartFetch != 0)
                    {
                        Logger.ClearProgress();
                        await WaitInternetAsync(linked.Token);

                        var oldClient = httpClient;
                        httpClient = CreateClient();
                        oldClient.Dispose();
                        continue;
                    }

                    double sec2 = Math.Max(1, sw.Elapsed.TotalSeconds);
                    string finalSpeed = $"{Utils.FormatSize((long)(Math.Max(0, currentRead) / sec2))}/s";

                    if (parallel)
                        Logger.Info("Download completed | {0}/{1} files | {2}", total, total, finalSpeed);
                    else
                        Logger.Info("{0} | {1}/{2} files", CurrentLabel(), total, total);

                    Logger.Info("Elapsed time: {0}", sw.Elapsed.ToString(@"hh\:mm\:ss"));
                    Logger.ClearAll();
                    return 0;
                }
            }
            finally
            {
                Console.CancelKeyPress -= onCancel;

                try { _downloadCancel?.Cancel(); }
                catch {}

                _downloadCancel?.Dispose();
                _downloadCancel = null;
                PauseEvent.Set();
                _isPaused = false;
                httpClient.Dispose();
            }
        }

        static HttpClient CreateClient()
        {
            var handler = new SocketsHttpHandler
            {
                MaxConnectionsPerServer = Math.Max(1, Config.MaxHttpHandle),
                AutomaticDecompression =
                    DecompressionMethods.GZip |
                    DecompressionMethods.Deflate |
                    DecompressionMethods.Brotli,
                PooledConnectionLifetime = TimeSpan.FromMinutes(10),
                PooledConnectionIdleTimeout = TimeSpan.FromMinutes(5),
                ConnectTimeout = TimeSpan.FromSeconds(10),
                EnableMultipleHttp2Connections = true
            };

            return new HttpClient(handler, disposeHandler: true)
            {
                Timeout = Timeout.InfiniteTimeSpan,
                DefaultRequestVersion = HttpVersion.Version20,
                DefaultVersionPolicy = HttpVersionPolicy.RequestVersionOrHigher
            };
        }

        static int GetChunkThreadsForAsset(
            long assetSize,
            int configuredMax,
            int globalBudgetPerAsset)
        {
            int preferred = assetSize switch
            {
                <= 16L * 1024 * 1024 => 1,
                <= 128L * 1024 * 1024 => 2,
                <= 512L * 1024 * 1024 => 4,
                _ => 8
            };

            int cap = Math.Max(1, Math.Min(configuredMax, globalBudgetPerAsset));
            return Math.Clamp(preferred, 1, cap);
        }

        static (
            List<SophonAsset> Assets,
            bool SelectedFromList,
            bool Cancelled) SelectAssetsByQuery(
                List<SophonAsset> assets,
                string query)
        {
            query = Download.NormalizeAssetQuery(query) ?? "";

            if (string.IsNullOrWhiteSpace(query))
                return (new List<SophonAsset>(), false, false);

            var exact = assets
                .Where(x => string.Equals(
                    Download.NormalizeAssetQuery(x.AssetName),
                    query,
                    StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (exact.Count == 1)
                return (exact, false, false);

            var matches = SearchAssets(assets, query).ToList();

            if (matches.Count == 0)
                return (new List<SophonAsset>(), false, false);

            if (matches.Count == 1)
                return (matches, false, false);

            int index = AssetPicker != null ? AssetPicker(matches) : -1;

            if (index < 0)
                return (new List<SophonAsset>(), false, true);

            return (new List<SophonAsset> { matches[index] }, true, false);
        }

        static IEnumerable<SophonAsset> SearchAssets(
            List<SophonAsset> assets,
            string query)
        {
            if (string.IsNullOrWhiteSpace(query))
                return Enumerable.Empty<SophonAsset>();

            query = Download.NormalizeAssetQuery(query) ?? "";
            bool hasWildcard = query.Contains('*') || query.Contains('?');

            if (hasWildcard)
            {
                string regex = "^"
                    + Regex.Escape(query)
                        .Replace("\\*", ".*")
                        .Replace("\\?", ".")
                    + "$";

                var rx = new Regex(
                    regex,
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant
                );

                return assets.Where(x =>
                {
                    string name = Download.NormalizeAssetQuery(x.AssetName)
                        ?? string.Empty;

                    string file = Path.GetFileName(name);

                    return rx.IsMatch(name) || rx.IsMatch(file);
                });
            }

            return assets.Where(x =>
            {
                string name = Download.NormalizeAssetQuery(x.AssetName)
                    ?? string.Empty;

                string file = Path.GetFileName(name);

                return name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                       file.Contains(query, StringComparison.OrdinalIgnoreCase);
            });
        }

        static void StartControlListener()
        {
            _ = Task.Run(() =>
            {
                while (_downloadCancel != null && !_downloadCancel.IsCancellationRequested)
                {
                    try
                    {
                        switch (Console.ReadKey(true).Key)
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
                    catch { break; }
                }
            });
        }

        static void TryCancelDownload()
        {
            try { _downloadCancel?.Cancel(); }
            catch {}

            PauseEvent.Set();
            _isPaused = false;
        }

        static void WaitIfPaused(CancellationToken token)
        {
            PauseEvent.Wait(token);
        }

        static bool IsCompleteFile(string path, long expectedSize)
        {
            try
            {
                if (!File.Exists(path))
                    return false;

                if (expectedSize <= 0)
                    return true;

                return new FileInfo(path).Length == expectedSize;
            }
            catch { return false; }
        }

        static void MoveFileWithRetry(
            string source,
            string destination,
            int retries = 5,
            int delayMs = 200)
        {
            Exception? last = null;

            for (int i = 0; i < retries; i++)
            {
                try
                {
                    File.Move(source, destination, true);
                    return;
                }
                catch (Exception ex) when (
                    ex is IOException ||
                    ex is UnauthorizedAccessException)
                {
                    last = ex;
                    Thread.Sleep(delayMs);
                }
            }

            throw new IOException(
                $"Failed to move '{source}' to '{destination}'.",
                last
            );
        }

        static int Err(string msg)
        {
            Logger.Error(msg);
            return 1;
        }
    }
}
