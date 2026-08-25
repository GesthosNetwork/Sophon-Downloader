using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SophonDownloader;
using SophonDownloader.Core;
using SophonDownloader.Models;
using SophonDownloader.Utilities;
using NLog;

namespace SophonDownloader.Services;

public sealed class ChunkDownloadProgress
{
    public int TotalChunks { get; init; }
    public int CompletedChunks { get; init; }
    public int ActiveChunks { get; init; }
    public long TotalBytes { get; init; }
    public long DownloadedBytes { get; init; }
    public long CachedBytes { get; init; }
    public long PartialCacheBytes { get; init; }
    public long AvailableBytes { get; init; }
    public double AggregateSpeedBytesPerSecond { get; init; }
    public TimeSpan? AggregateEta { get; init; }
    public string CurrentSpeed { get; init; } = "0 KB/s";
    public string StatusText { get; init; } = "";
}

public sealed class ChunkDownloader : IDisposable
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();
    private const int MaxRetryCount = 3;
    private const int BufferSize = 1024 * 1024;
    private const double SmoothingFactor = 0.20;
    private const long AggregateSpeedSampleIntervalMilliseconds = 250;
    private const long AggregateSpeedWindowMilliseconds = 5000;
    private const long AggregateSpeedResetMilliseconds = 3500;

    private readonly CancellationTokenSource _cancellationTokenSource = new();
    private readonly HttpClient _http;

    public ChunkDownloader()
    {
        AppSettings settings = AppSettingsStore.Load();
        _http = new HttpClient(NetworkClient.CreateHandler(settings))
        {
            Timeout = Timeout.InfiniteTimeSpan
        };
    }
    private readonly object _pauseLock = new();
    private readonly object _speedLock = new();
    private readonly Queue<(long Time, long Bytes)> _speedSamples = new();
    private TaskCompletionSource<bool> _resumeSignal = CreateCompletedSignal();
    private readonly ConcurrentDictionary<string, byte> _activeChunks = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, long> _partialChunkBytes = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, long> _aria2ChunkBytes = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, long> _aria2ChunkSpeeds = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, Aria2c> _aria2Downloads = new(StringComparer.OrdinalIgnoreCase);

    private bool _isPaused;
    private bool _disposed;
    private bool _deleteTemporaryFilesOnCancel;
    private ChunkStore? _chunkStore;
    private int _totalChunks;
    private int _completedChunks;
    private long _totalBytes;
    private long _downloadedBytes;
    private long _cachedBytes;
    private long _lastProgressPublishTime;
    private long _lastSpeedSampleTime;
    private double _smoothedSpeedBytesPerSecond;

    public Action<ChunkDownloadProgress>? ProgressUpdateCallback { get; set; }
    public Action<string>? StatusTextCallback { get; set; }
    public Action? DownloadCompletedCallback { get; set; }
    public Action? DownloadCancelledCallback { get; set; }
    public bool IsPaused => _isPaused;

    private static TaskCompletionSource<bool> CreateCompletedSignal()
    {
        var signal = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        signal.SetResult(true);
        return signal;
    }

    public void TogglePause()
    {
        ThrowIfDisposed();

        bool paused;

        lock (_pauseLock)
        {
            if (_isPaused)
            {
                _isPaused = false;
                _resumeSignal.TrySetResult(true);
                _resumeSignal = CreateCompletedSignal();
                paused = false;
                Logger.Info("Chunk download resumed.");
            }
            else
            {
                _isPaused = true;
                _resumeSignal = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                paused = true;
                Logger.Info("Chunk download paused.");
            }
        }

        if (paused)
        {
            foreach (Aria2c aria2c in _aria2Downloads.Values)
            {
                try { aria2c.Pause(); }
                catch (Exception ex) { Logger.Warn(ex, "Unable to pause aria2c process."); }
            }
        }

        PublishAggregateProgress(paused ? "Download paused." : "Download resumed.", true);
    }

    public void CancelDownload()
    {
        if (_disposed) return;

        Logger.Info("Chunk download cancellation requested.");
        _deleteTemporaryFilesOnCancel = true;

        try { _cancellationTokenSource.Cancel(); }
        catch { }

        foreach (Aria2c aria2c in _aria2Downloads.Values)
        {
            try { aria2c.Cancel(); }
            catch { }
        }

        lock (_pauseLock)
            _resumeSignal.TrySetResult(true);
    }

    public async Task StartDownload(List<SophonChunkFile> allFiles, Dictionary<string, string> fileManifest, string saveDirectory, int? maxConcurrency = null)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(allFiles);
        ArgumentNullException.ThrowIfNull(fileManifest);

        if (string.IsNullOrWhiteSpace(saveDirectory))
            throw new ArgumentException("Save directory cannot be empty.", nameof(saveDirectory));

        AppSettings settings = AppSettingsStore.Load();
        int requestedConcurrency = maxConcurrency ?? settings.Threads;
        int downloadConcurrency = string.Equals(settings.DownloadMode, "Sequential", StringComparison.OrdinalIgnoreCase)
            ? 1
            : Math.Clamp(requestedConcurrency, 1, 64);

        Logger.Info($"Download concurrency: {downloadConcurrency} workers (logical processors: {Environment.ProcessorCount}).");

        CancellationToken cancellationToken = _cancellationTokenSource.Token;
        cancellationToken.ThrowIfCancellationRequested();

        _chunkStore = new ChunkStore(saveDirectory);

        var uniqueChunks = BuildUniqueChunkMap(allFiles, fileManifest);

        _totalChunks = uniqueChunks.Count;
        _completedChunks = 0;
        _totalBytes = uniqueChunks.Values.Sum(item => item.Chunk.CompressedSize);
        _downloadedBytes = 0;
        _cachedBytes = 0;
        _lastProgressPublishTime = Environment.TickCount64;
        _lastSpeedSampleTime = _lastProgressPublishTime;
        _smoothedSpeedBytesPerSecond = 0;

        lock (_speedLock) _speedSamples.Clear();

        _activeChunks.Clear();
        _partialChunkBytes.Clear();
        _aria2ChunkBytes.Clear();
        _aria2ChunkSpeeds.Clear();

        if (_totalChunks == 0)
        {
            PublishAggregateProgress("No chunks are required.", true);
            DownloadCompletedCallback?.Invoke();
            return;
        }

        Logger.Info($"Preparing {_totalChunks:N0} unique chunks. Compressed size: {Utility.FormatFileSize(_totalBytes)}");
        PublishAggregateProgress("Checking local chunk cache...", true);

        var pendingChunks = new List<(SophonChunk Chunk, string UrlPrefix)>();
        int scannedChunks = 0;

        foreach (var item in uniqueChunks.Values)
        {
            cancellationToken.ThrowIfCancellationRequested();

            string completedPath = _chunkStore.GetChunkPath(item.Chunk.Id);
            string temporaryPath = completedPath + ".tmp";
            bool completeChunkFound = false;

            if (File.Exists(completedPath))
            {
                try
                {
                    long size = new FileInfo(completedPath).Length;

                    if (size == item.Chunk.CompressedSize)
                    {
                        completeChunkFound = true;
                        Interlocked.Add(ref _cachedBytes, size);
                        Interlocked.Increment(ref _completedChunks);
                    }
                    else TryDeleteFile(completedPath);
                }
                catch (Exception ex)
                {
                    Logger.Warn(ex, $"Unable to inspect cached chunk: {completedPath}");
                }
            }

            if (completeChunkFound)
            {
                scannedChunks++;
                PublishCacheScanProgress(scannedChunks);
                continue;
            }

            if (File.Exists(temporaryPath))
            {
                try
                {
                    long partialSize = new FileInfo(temporaryPath).Length;

                    if (partialSize > 0 && partialSize < item.Chunk.CompressedSize)
                    {
                        _partialChunkBytes[item.Chunk.Id] = partialSize;
                    }
                    else if (partialSize >= item.Chunk.CompressedSize)
                    {
                        bool valid = await ValidateTemporaryChunkAsync(item.Chunk, temporaryPath, cancellationToken);

                        if (valid)
                        {
                            File.Move(temporaryPath, completedPath, true);
                            Interlocked.Add(ref _cachedBytes, item.Chunk.CompressedSize);
                            Interlocked.Increment(ref _completedChunks);
                            scannedChunks++;
                            PublishCacheScanProgress(scannedChunks);
                            continue;
                        }

                        TryDeleteFile(temporaryPath);
                    }
                }
                catch (Exception ex)
                {
                    Logger.Warn(ex, $"Unable to inspect partial chunk: {temporaryPath}");
                }
            }

            pendingChunks.Add(item);
            scannedChunks++;
            PublishCacheScanProgress(scannedChunks);
        }

        cancellationToken.ThrowIfCancellationRequested();

        if (pendingChunks.Count == 0)
        {
            PublishAggregateProgress("All required chunks are already cached.", true);
            DownloadCompletedCallback?.Invoke();
            return;
        }

        PublishAggregateProgress("Downloading chunks...", true);

        using var semaphore = new SemaphoreSlim(downloadConcurrency, downloadConcurrency);

        try
        {
            var downloadTasks = new List<Task>(pendingChunks.Count);

            foreach (var item in pendingChunks)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await semaphore.WaitAsync(cancellationToken);
                downloadTasks.Add(DownloadChunkWorkerAsync(item.Chunk, item.UrlPrefix, semaphore));
            }

            await Task.WhenAll(downloadTasks);
            cancellationToken.ThrowIfCancellationRequested();

            PublishAggregateProgress("All required chunks are ready.", true);
            DownloadCompletedCallback?.Invoke();
        }
        catch (OperationCanceledException)
        {
            Logger.Info("Chunk download cancelled.");
            DownloadCancelledCallback?.Invoke();
        }
        finally
        {
            foreach (Aria2c aria2c in _aria2Downloads.Values)
            {
                try { aria2c.Cancel(); }
                catch { }
            }

            _aria2Downloads.Clear();
            _aria2ChunkBytes.Clear();
            _aria2ChunkSpeeds.Clear();
        }
    }

    private void PublishCacheScanProgress(int scannedChunks)
    {
        if (scannedChunks == 1 || scannedChunks % 100 == 0 || scannedChunks == _totalChunks)
            PublishAggregateProgress($"Checking local chunk cache... {scannedChunks:N0}/{_totalChunks:N0}", false);
    }

    private async Task DownloadChunkWorkerAsync(SophonChunk chunk, string urlPrefix, SemaphoreSlim semaphore)
    {
        try { await DownloadChunkAsync(chunk, urlPrefix, _cancellationTokenSource.Token); }
        finally { semaphore.Release(); }
    }

    private async Task DownloadChunkAsync(SophonChunk chunk, string urlPrefix, CancellationToken cancellationToken)
    {
        if (_chunkStore == null)
            throw new InvalidOperationException("Chunk store has not been initialized.");

        cancellationToken.ThrowIfCancellationRequested();
        await WaitIfPausedAsync(cancellationToken);

        if (_chunkStore.HasChunk(chunk.Id, chunk.CompressedSize))
        {
            Interlocked.Increment(ref _completedChunks);
            Interlocked.Add(ref _cachedBytes, chunk.CompressedSize);
            RemovePartialChunk(chunk.Id);
            PublishAggregateProgress("Downloading chunks...", false);
            return;
        }

        string url = Utility.EnsureTrailingSlash(urlPrefix) + chunk.Id;
        Exception? lastException = null;
        int attempt = 1;

        while (attempt <= MaxRetryCount)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                await WaitIfPausedAsync(cancellationToken);
                await DownloadChunkAttemptAsync(chunk, url, cancellationToken);

                RemovePartialChunk(chunk.Id);
                Interlocked.Increment(ref _completedChunks);
                Interlocked.Add(ref _cachedBytes, chunk.CompressedSize);
                PublishAggregateProgress("Downloading chunks...", false);
                return;
            }
            catch (Aria2PausedException)
            {
                await WaitIfPausedAsync(cancellationToken);
                continue;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (ChunkValidationException ex)
            {
                Logger.Warn(ex, $"Invalid data for chunk {chunk.Id}. Restarting.");
                RemovePartialChunk(chunk.Id);
                TryDeleteFile(_chunkStore.GetChunkPath(chunk.Id) + ".tmp");
                _aria2ChunkBytes.TryRemove(chunk.Id, out _);
                _aria2ChunkSpeeds.TryRemove(chunk.Id, out _);
                lastException = ex;
                attempt++;
            }
            catch (Exception ex)
            {
                lastException = ex;
                Logger.Warn(ex, $"Chunk download failed (attempt {attempt}/{MaxRetryCount}).");

                if (attempt < MaxRetryCount)
                {
                    int delayMilliseconds = (int)Math.Pow(2, attempt - 1) * 1000;
                    await WaitIfPausedAsync(cancellationToken);
                    await Task.Delay(delayMilliseconds, cancellationToken);
                }

                attempt++;
            }
        }

        throw new InvalidOperationException($"Failed to download chunk '{chunk.Id}' after {MaxRetryCount} attempts.", lastException);
    }

    private async Task DownloadChunkAttemptAsync(SophonChunk chunk, string url, CancellationToken cancellationToken)
    {
        if (_chunkStore == null)
            throw new InvalidOperationException("Chunk store has not been initialized.");

        if (AppSettingsStore.Load().UseAria2c)
        {
            await DownloadChunkWithAria2cAsync(chunk, url, cancellationToken);
            return;
        }

        await DownloadChunkWithHttpAsync(chunk, url, cancellationToken);
    }

    private async Task DownloadChunkWithAria2cAsync(SophonChunk chunk, string url, CancellationToken cancellationToken)
    {
        if (_chunkStore == null)
            throw new InvalidOperationException("Chunk store has not been initialized.");

        cancellationToken.ThrowIfCancellationRequested();
        await WaitIfPausedAsync(cancellationToken);

        string destinationPath = _chunkStore.GetChunkPath(chunk.Id);
        string temporaryPath = destinationPath + ".tmp";
        string? directory = Path.GetDirectoryName(destinationPath);

        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);

        long existingBytes = File.Exists(temporaryPath) ? new FileInfo(temporaryPath).Length : 0;
        if (existingBytes <= 0 || existingBytes > chunk.CompressedSize)
        {
            TryDeleteFile(temporaryPath);
            existingBytes = 0;
        }

        if (existingBytes == chunk.CompressedSize && await ValidateTemporaryChunkAsync(chunk, temporaryPath, cancellationToken))
        {
            File.Move(temporaryPath, destinationPath, true);
            return;
        }

        _partialChunkBytes[chunk.Id] = existingBytes;
        _aria2ChunkBytes[chunk.Id] = existingBytes;
        _aria2ChunkSpeeds[chunk.Id] = 0;
        _activeChunks.TryAdd(chunk.Id, 0);

        var aria2c = new Aria2c();
        _aria2Downloads[chunk.Id] = aria2c;

        try
        {
            int exitCode = await aria2c.RunAsync(
                url,
                directory ?? _chunkStore.ChunkDirectory,
                Path.GetFileName(temporaryPath),
                existingBytes,
                null,
                info =>
                {
                    long currentBytes = info.BytesReceived;
                    long previousBytes = _aria2ChunkBytes.GetValueOrDefault(chunk.Id);
                    long delta = currentBytes - previousBytes;
                    if (delta > 0) Interlocked.Add(ref _downloadedBytes, delta);

                    _aria2ChunkBytes[chunk.Id] = currentBytes;
                    _partialChunkBytes[chunk.Id] = currentBytes;
                    _aria2ChunkSpeeds[chunk.Id] = Math.Max(0, info.SpeedBytesPerSecond ?? 0);
                    PublishAria2Progress();
                },
                cancellationToken,
                Math.Clamp(AppSettingsStore.Load().MaxHttpHandle, 1, 256),
                Math.Clamp(AppSettingsStore.Load().Threads, 1, 64),
                GetPerWorkerSpeedLimitBytesPerSecond(),
                AppSettingsStore.Load().LogLevel,
                AppSettingsStore.Load().Dns,
                AppSettingsStore.Load().ProxyMode,
                AppSettingsStore.Load().ProxyHost,
                AppSettingsStore.Load().ProxyPort);

            _aria2ChunkSpeeds[chunk.Id] = 0;
            PublishAria2Progress();

            if (exitCode != 0)
            {
                if (_isPaused) throw new Aria2PausedException();
                throw new Aria2cDownloadException(exitCode, $"aria2c failed to download chunk '{chunk.Id}'. Exit code: {exitCode}.");
            }

            cancellationToken.ThrowIfCancellationRequested();
            await ValidateAndCommitChunkAsync(chunk, temporaryPath, destinationPath, cancellationToken, "aria2c");
            TryDeleteFile(temporaryPath + ".aria2");
        }
        catch (OperationCanceledException)
        {
            if (_deleteTemporaryFilesOnCancel)
            {
                RemovePartialChunk(chunk.Id);
                TryDeleteFile(temporaryPath);
            }
            throw;
        }
        catch (Aria2PausedException)
        {
            throw;
        }
        finally
        {
            _aria2ChunkSpeeds.TryRemove(chunk.Id, out _);
            _aria2Downloads.TryRemove(chunk.Id, out _);
            _activeChunks.TryRemove(chunk.Id, out _);
        }
    }

    private async Task DownloadChunkWithHttpAsync(SophonChunk chunk, string url, CancellationToken cancellationToken)
    {
        if (_chunkStore == null)
            throw new InvalidOperationException("Chunk store has not been initialized.");

        cancellationToken.ThrowIfCancellationRequested();
        await WaitIfPausedAsync(cancellationToken);

        string destinationPath = _chunkStore.GetChunkPath(chunk.Id);
        string temporaryPath = destinationPath + ".tmp";
        string? directory = Path.GetDirectoryName(destinationPath);

        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);

        long existingBytes = File.Exists(temporaryPath) ? new FileInfo(temporaryPath).Length : 0;
        if (existingBytes <= 0 || existingBytes > chunk.CompressedSize)
        {
            TryDeleteFile(temporaryPath);
            existingBytes = 0;
        }

        if (existingBytes == chunk.CompressedSize && await ValidateTemporaryChunkAsync(chunk, temporaryPath, cancellationToken))
        {
            File.Move(temporaryPath, destinationPath, true);
            return;
        }

        _partialChunkBytes[chunk.Id] = existingBytes;
        _activeChunks.TryAdd(chunk.Id, 0);

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            if (existingBytes > 0)
                request.Headers.Range = new RangeHeaderValue(existingBytes, null);

            using HttpResponseMessage response = await _http.SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

            if (!response.IsSuccessStatusCode && response.StatusCode != HttpStatusCode.PartialContent)
            {
                throw new IOException($"HTTP download failed for chunk '{chunk.Id}': {(int)response.StatusCode} {response.ReasonPhrase}");
            }

            bool resumeAccepted = existingBytes > 0 && response.StatusCode == HttpStatusCode.PartialContent;
            if (!resumeAccepted)
            {
                if (existingBytes > 0)
                {
                    TryDeleteFile(temporaryPath);
                    existingBytes = 0;
                    _partialChunkBytes[chunk.Id] = 0;
                }
            }

            long downloaded = existingBytes;
            DateTime lastSample = DateTime.UtcNow;
            long lastBytes = downloaded;

            await using Stream input = await response.Content.ReadAsStreamAsync(cancellationToken);
            await using var output = new FileStream(
                temporaryPath,
                resumeAccepted ? FileMode.Append : FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                BufferSize,
                FileOptions.Asynchronous | FileOptions.SequentialScan);

            byte[] buffer = new byte[BufferSize];
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await WaitIfPausedAsync(cancellationToken);

                int read = await input.ReadAsync(buffer, cancellationToken);
                if (read == 0) break;

                await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                downloaded += read;

                long speedLimit = GetPerWorkerSpeedLimitBytesPerSecond();
                if (speedLimit > 0)
                {
                    double targetSeconds = (downloaded - existingBytes) / (double)speedLimit;
                    double actualSeconds = (DateTime.UtcNow - lastSample).TotalSeconds;
                    double delaySeconds = targetSeconds - actualSeconds;
                    if (delaySeconds > 0)
                        await Task.Delay(TimeSpan.FromSeconds(Math.Min(delaySeconds, 5)), cancellationToken);
                }

                _partialChunkBytes[chunk.Id] = downloaded;
                Interlocked.Add(ref _downloadedBytes, read);

                DateTime now = DateTime.UtcNow;
                double elapsed = (now - lastSample).TotalSeconds;
                if (elapsed >= 0.25)
                {
                    double speed = (downloaded - lastBytes) / elapsed;
                    PublishAggregateProgress($"Downloading chunks ({Utility.FormatSpeed(Math.Max(0, speed))})...", false);
                    lastSample = now;
                    lastBytes = downloaded;
                }
            }

            await output.FlushAsync(cancellationToken);
            await ValidateAndCommitChunkAsync(chunk, temporaryPath, destinationPath, cancellationToken, "HTTP");
        }
        catch (OperationCanceledException)
        {
            if (_deleteTemporaryFilesOnCancel)
            {
                RemovePartialChunk(chunk.Id);
                TryDeleteFile(temporaryPath);
            }
            throw;
        }
        finally
        {
            _activeChunks.TryRemove(chunk.Id, out _);
        }
    }

    private long GetPerWorkerSpeedLimitBytesPerSecond()
    {
        AppSettings settings = AppSettingsStore.Load();
        long total = Math.Max(0, settings.SpeedLimitKbps) * 1024L;
        if (total <= 0)
            return 0;

        int workers = string.Equals(settings.DownloadMode, "Sequential", StringComparison.OrdinalIgnoreCase)
            ? 1
            : Math.Clamp(settings.Threads, 1, 64);
        return Math.Max(1, total / workers);
    }

    private async Task ValidateAndCommitChunkAsync(
        SophonChunk chunk,
        string temporaryPath,
        string destinationPath,
        CancellationToken cancellationToken,
        string engineName)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!File.Exists(temporaryPath))
            throw new FileNotFoundException($"{engineName} did not create the expected chunk file: {temporaryPath}");

        long downloadedSize = new FileInfo(temporaryPath).Length;
        if (downloadedSize != chunk.CompressedSize)
            throw new ChunkValidationException($"Compressed size mismatch for chunk {chunk.Id}: expected {chunk.CompressedSize}, actual {downloadedSize}");

        if (!string.IsNullOrWhiteSpace(chunk.CompressedMd5))
        {
            await using var validationStream = new FileStream(
                temporaryPath, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            string actualMd5 = await Utility.CalculateMd5Async(validationStream);
            cancellationToken.ThrowIfCancellationRequested();

            if (!actualMd5.Equals(chunk.CompressedMd5, StringComparison.OrdinalIgnoreCase))
                throw new ChunkValidationException($"Compressed MD5 mismatch for chunk {chunk.Id}.");
        }

        File.Move(temporaryPath, destinationPath, true);
        RemovePartialChunk(chunk.Id);
        _aria2ChunkBytes.TryRemove(chunk.Id, out _);
        _aria2ChunkSpeeds.TryRemove(chunk.Id, out _);
    }

    private void PublishAria2Progress() => PublishAggregateProgress("Downloading chunks...", false);

    private double UpdateAggregateSpeed(long currentTime)
    {
        long downloadedBytes = Interlocked.Read(ref _downloadedBytes);

        lock (_speedLock)
        {
            _speedSamples.Enqueue((currentTime, downloadedBytes));

            while (_speedSamples.Count > 1 && currentTime - _speedSamples.Peek().Time > AggregateSpeedWindowMilliseconds)
                _speedSamples.Dequeue();

            _lastSpeedSampleTime = currentTime;

            if (_speedSamples.Count >= 2)
            {
                var oldest = _speedSamples.Peek();
                long elapsedMilliseconds = currentTime - oldest.Time;
                long byteDelta = downloadedBytes - oldest.Bytes;

                if (elapsedMilliseconds >= 1000 && byteDelta > 0)
                {
                    double rawSpeed = byteDelta * 1000d / elapsedMilliseconds;

                    if (rawSpeed > 0 && !double.IsNaN(rawSpeed) && !double.IsInfinity(rawSpeed))
                    {
                        _smoothedSpeedBytesPerSecond = _smoothedSpeedBytesPerSecond <= 0
                            ? rawSpeed
                            : _smoothedSpeedBytesPerSecond * (1d - SmoothingFactor) + rawSpeed * SmoothingFactor;
                    }
                }
            }

            if (_speedSamples.Count > 0)
            {
                long latestMovementTime = _speedSamples.Reverse().First().Time;

                if (currentTime - latestMovementTime >= AggregateSpeedResetMilliseconds)
                    _smoothedSpeedBytesPerSecond = 0;
            }

            return Math.Max(0, _smoothedSpeedBytesPerSecond);
        }
    }

    private TimeSpan? CalculateAggregateEta(double aggregateSpeedBytesPerSecond, long remainingBytes)
    {
        if (remainingBytes <= 0) return TimeSpan.Zero;

        if (aggregateSpeedBytesPerSecond <= 0 ||
            double.IsNaN(aggregateSpeedBytesPerSecond) ||
            double.IsInfinity(aggregateSpeedBytesPerSecond))
            return null;

        double totalSeconds = remainingBytes / aggregateSpeedBytesPerSecond;

        if (double.IsNaN(totalSeconds) || double.IsInfinity(totalSeconds) || totalSeconds < 0)
            return null;

        return TimeSpan.FromSeconds(Math.Min(totalSeconds, TimeSpan.MaxValue.TotalSeconds));
    }

    private void PublishAggregateProgress(string status, bool force)
    {
        long currentTime = Environment.TickCount64;
        long previousPublishTime = Interlocked.Read(ref _lastProgressPublishTime);

        if (!force && currentTime - previousPublishTime < AggregateSpeedSampleIntervalMilliseconds)
            return;

        double aggregateSpeed = UpdateAggregateSpeed(currentTime);
        Interlocked.Exchange(ref _lastProgressPublishTime, currentTime);

        int completedChunks = Volatile.Read(ref _completedChunks);
        int activeChunks = _activeChunks.Count;
        long downloadedBytes = Interlocked.Read(ref _downloadedBytes);
        long cachedBytes = Interlocked.Read(ref _cachedBytes);
        long partialCacheBytes = _partialChunkBytes.Values.Sum();
        long availableBytes = Math.Clamp(cachedBytes + partialCacheBytes, 0, _totalBytes);
        long remainingBytes = Math.Max(0, _totalBytes - availableBytes);
        TimeSpan? aggregateEta = CalculateAggregateEta(aggregateSpeed, remainingBytes);
        string actualStatus = _isPaused ? "Download paused." : status;

        var progress = new ChunkDownloadProgress
        {
            TotalChunks = _totalChunks,
            CompletedChunks = completedChunks,
            ActiveChunks = activeChunks,
            TotalBytes = _totalBytes,
            DownloadedBytes = downloadedBytes,
            CachedBytes = cachedBytes,
            PartialCacheBytes = partialCacheBytes,
            AvailableBytes = availableBytes,
            AggregateSpeedBytesPerSecond = aggregateSpeed,
            AggregateEta = aggregateEta,
            CurrentSpeed = Utility.FormatSpeed(aggregateSpeed),
            StatusText = actualStatus
        };

        try
        {
            ProgressUpdateCallback?.Invoke(progress);
            StatusTextCallback?.Invoke(actualStatus);
        }
        catch (Exception ex)
        {
            Logger.Warn(ex, "Progress callback failed.");
        }
    }

    private async Task<bool> ValidateTemporaryChunkAsync(SophonChunk chunk, string temporaryPath, CancellationToken cancellationToken)
    {
        if (!File.Exists(temporaryPath)) return false;

        try
        {
            var info = new FileInfo(temporaryPath);
            if (info.Length != chunk.CompressedSize) return false;
            if (string.IsNullOrWhiteSpace(chunk.CompressedMd5)) return true;

            await using var stream = new FileStream(
                temporaryPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                BufferSize,
                FileOptions.Asynchronous | FileOptions.SequentialScan);

            string actualMd5 = await Utility.CalculateMd5Async(stream);

            cancellationToken.ThrowIfCancellationRequested();

            return actualMd5.Equals(chunk.CompressedMd5, StringComparison.OrdinalIgnoreCase);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return false;
        }
    }

    private void RemovePartialChunk(string chunkId)
    {
        _partialChunkBytes.TryRemove(chunkId, out _);
        _aria2ChunkSpeeds.TryRemove(chunkId, out _);
    }

    private async Task WaitIfPausedAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            Task waitTask;

            lock (_pauseLock)
            {
                if (!_isPaused) return;
                waitTask = _resumeSignal.Task;
            }

            await waitTask.WaitAsync(cancellationToken);
        }
    }

    private static Dictionary<string, (SophonChunk Chunk, string UrlPrefix)> BuildUniqueChunkMap(
        IEnumerable<SophonChunkFile> files,
        Dictionary<string, string> fileManifest)
    {
        var result = new Dictionary<string, (SophonChunk Chunk, string UrlPrefix)>(StringComparer.OrdinalIgnoreCase);

        foreach (SophonChunkFile file in files)
        {
            if (!fileManifest.TryGetValue(file.File, out string? urlPrefix))
                throw new InvalidOperationException($"Chunk URL prefix was not found for file: {file.File}");

            foreach (SophonChunk chunk in file.Chunks)
            {
                if (string.IsNullOrWhiteSpace(chunk.Id))
                    throw new InvalidDataException($"File '{file.File}' contains a chunk with an empty ID.");

                if (chunk.CompressedSize < 0)
                    throw new InvalidDataException($"Chunk '{chunk.Id}' has an invalid compressed size.");

                if (result.TryGetValue(chunk.Id, out var existing))
                {
                    if (existing.Chunk.CompressedSize != chunk.CompressedSize)
                        throw new InvalidDataException($"Conflicting compressed size detected for chunk '{chunk.Id}'.");

                    continue;
                }

                result.Add(chunk.Id, (chunk, urlPrefix));
            }
        }

        return result;
    }

    private static void TryDeleteFile(string path)
    {
        if (!File.Exists(path)) return;

        try { File.Delete(path); }
        catch (Exception ex) { Logger.Warn(ex, $"Unable to delete file: {path}"); }
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(ChunkDownloader));
    }

    public void Dispose()
    {
        if (_disposed) return;

        _disposed = true;

        try { _cancellationTokenSource.Cancel(); }
        catch { }

        foreach (Aria2c aria2c in _aria2Downloads.Values)
        {
            try { aria2c.Cancel(); }
            catch { }
        }

        _aria2Downloads.Clear();
        try { _http.Dispose(); } catch { }
        _aria2ChunkBytes.Clear();
        _aria2ChunkSpeeds.Clear();

        lock (_pauseLock)
            _resumeSignal.TrySetResult(true);

        lock (_speedLock)
            _speedSamples.Clear();

        try { _cancellationTokenSource.Dispose(); }
        catch { }
    }

    private sealed class Aria2PausedException : Exception { }
    private sealed class ChunkValidationException : Exception
    {
        public ChunkValidationException(string message) : base(message) { }
    }
}
