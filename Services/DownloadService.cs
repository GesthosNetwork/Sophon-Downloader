using System.Net.Http.Headers;
using System.Text.RegularExpressions;

namespace SophonDownloader.Services;

public enum DownloadErrorType
{
    Unknown,
    NotFound,
    Unauthorized,
    Forbidden,
    RateLimited,
    ServerError,
    NetworkError,
    Timeout,
    InvalidUrl,
    ProcessError,
    Cancelled
}

public sealed class DownloadException(
    DownloadErrorType errorType,
    string message,
    string url,
    int? httpStatusCode = null,
    string? fileName = null,
    int? processExitCode = null,
    Exception? innerException = null)
    : Exception(message, innerException)
{
    public DownloadErrorType ErrorType { get; } = errorType;
    public int? HttpStatusCode { get; } = httpStatusCode;
    public string Url { get; } = url;
    public string? FileName { get; } = fileName;
    public int? ProcessExitCode { get; } = processExitCode;
}

public class DownloadProgressInfo
{
    public long BytesReceived { get; set; }
    public long? TotalBytes { get; set; }
    public double? Percent { get; set; }
    public long? SpeedBytesPerSecond { get; set; }
    public TimeSpan? Eta { get; set; }
    public string? FileName { get; set; }
    public int FileIndex { get; set; }
    public int FileCount { get; set; }
}

public sealed class DownloadService : IDisposable
{
    private static readonly Logger Logger =
        LogManager.GetCurrentClassLogger();

    private static readonly Regex HttpStatusRegex = new(
        @"(?:HTTP(?:/\d(?:\.\d)?)?\s+|status(?:\s+code)?\s*[:=]\s*)(?<status>401|403|404|408|429|4\d{2}|5\d{2})",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private readonly Aria2c _aria2c = new();

    private readonly HttpClient _http;

    public DownloadService()
    {
        AppSettings settings = AppSettingsStore.Load();
        _http = new HttpClient(NetworkClient.CreateHandler(settings))
        {
            Timeout = TimeSpan.FromSeconds(10)
        };
    }

    private readonly object _stateLock = new();
    private TaskCompletionSource<bool> _resumeSource = CreateCompletedSource();
    private readonly object _aggregateLock = new();
    private readonly UnifiedTransferMetrics _transferMetrics = new();
    private readonly Dictionary<int, long> _legacyFileInitialBytes = new();
    private readonly Dictionary<int, long> _legacyFileTotalBytes = new();
    private bool _isPaused;
    private bool _disposed;
    private long _legacyCompletedAvailableBytes;
    private long _legacyCompletedTransferredBytes;
    private long _legacyKnownTotalBytes;
    private bool _legacyAllTotalsKnown;

    public bool IsPaused
    {
        get
        {
            lock (_stateLock)
                return _isPaused;
        }
    }

    public void Pause()
    {
        ThrowIfDisposed();

        lock (_stateLock)
        {
            if (_isPaused)
                return;

            _isPaused = true;
            _resumeSource = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
        }

        Logger.Info("Legacy download paused.");
        _aria2c.Pause();
    }

    public void Resume()
    {
        if (_disposed)
            return;

        TaskCompletionSource<bool>? source;

        lock (_stateLock)
        {
            if (!_isPaused)
                return;

            _isPaused = false;
            source = _resumeSource;
            _resumeSource = CreateCompletedSource();
        }

        source.TrySetResult(true);
        Logger.Info("Legacy download resumed.");
    }

    public async Task DownloadAllAsync(
        List<string> urls,
        string destFolder,
        IProgress<DownloadProgressInfo> progress,
        CancellationToken ct)
    {
        ThrowIfDisposed();

        if (urls is null)
            throw new ArgumentNullException(nameof(urls));

        if (progress is null)
            throw new ArgumentNullException(nameof(progress));

        if (string.IsNullOrWhiteSpace(destFolder))
            throw new ArgumentException("Destination folder cannot be empty.", nameof(destFolder));

        Directory.CreateDirectory(destFolder);

        ResetAggregateLegacyMetrics();
        await PrepareLegacyAggregateTotalsAsync(urls, destFolder, ct);

        Logger.Info($"Legacy download started. Files: {urls.Count:N0}, Destination: {destFolder}");

        try
        {
            for (int i = 0; i < urls.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                await WaitIfPausedAsync(ct);

                string url = urls[i];
                string fileName = GetFileName(url);
                string finalPath = Path.Combine(destFolder, fileName);
                string controlPath = finalPath + ".aria2";

                Logger.Info($"Preparing file {i + 1}/{urls.Count}: {fileName}");

                if (File.Exists(finalPath) && !File.Exists(controlPath))
                {
                    long existingSize = new FileInfo(finalPath).Length;
                    Logger.Info($"Skipping existing file: {fileName} ({existingSize:N0} bytes)");
                    BeginLegacyFile(i + 1, existingSize, existingSize);
                    ReportLegacyAggregateProgress(fileName, i + 1, urls.Count, existingSize, existingSize, 100, progress, true);
                    CommitLegacyFile(i + 1, existingSize, existingSize);
                    continue;
                }

                int currentIndex = i + 1;
                long initialBytes = File.Exists(finalPath) ? new FileInfo(finalPath).Length : 0;
                BeginLegacyFile(currentIndex, initialBytes, _legacyFileTotalBytes.GetValueOrDefault(currentIndex));
                await DownloadOneAsync(url, finalPath, currentIndex, urls.Count, progress, ct);
            }

            Logger.Info("Legacy download completed successfully.");
        }
        catch (OperationCanceledException)
        {
            Logger.Info("Legacy download cancelled.");
            throw;
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Legacy download failed.");
            throw;
        }
        finally
        {
            Resume();
        }
    }

    private async Task DownloadOneAsync(
        string url,
        string finalPath,
        int fileIndex,
        int fileCount,
        IProgress<DownloadProgressInfo> progress,
        CancellationToken ct)
    {
        ThrowIfDisposed();

        string fileName = Path.GetFileName(finalPath);

        if (!AppSettingsStore.Load().UseAria2c)
        {
            await DownloadOneWithHttpAsync(url, finalPath, fileIndex, fileCount, progress, ct);
            return;
        }

        await TryValidateRemoteResourceAsync(url, ct);

        long? totalBytes = await TryGetRemoteLengthAsync(url, ct);

        Logger.Info(
            $"Legacy remote file: {fileName}, size: " +
            $"{(totalBytes.HasValue ? $"{totalBytes.Value:N0} bytes" : "unknown")}");

        ReportLegacyAggregateProgress(
            fileName, fileIndex, fileCount, 0, totalBytes, 0, progress, true);

        string? lastOutput = null;
        int exitCode;

        try
        {
            Logger.Info($"Starting aria2c download: {fileName}");

            exitCode = await _aria2c.RunAsync(
                url, Path.GetDirectoryName(finalPath)!, fileName, 0, line =>
                {
                    lastOutput = line;
                    Logger.Debug($"aria2c [{fileName}]: {line}");
                },
                ariaProgress =>
                {
                    long currentTotal = ariaProgress.TotalBytes ?? totalBytes ?? 0;
                    if (currentTotal > 0)
                        RegisterLegacyFileTotal(fileIndex, currentTotal);

                    ReportLegacyAggregateProgress(
                        fileName,
                        fileIndex,
                        fileCount,
                        ariaProgress.BytesReceived,
                        ariaProgress.TotalBytes ?? totalBytes,
                        ariaProgress.Percent ?? CalculatePercent(ariaProgress.BytesReceived, ariaProgress.TotalBytes ?? totalBytes),
                        progress,
                        false);
                },
                ct,
                Math.Clamp(AppSettingsStore.Load().MaxHttpHandle, 1, 256),
                Math.Clamp(AppSettingsStore.Load().Threads, 1, 64),
                Math.Max(0, AppSettingsStore.Load().SpeedLimitKbps) * 1024L,
                AppSettingsStore.Load().LogLevel,
                AppSettingsStore.Load().Dns,
                AppSettingsStore.Load().ProxyMode,
                AppSettingsStore.Load().ProxyHost,
                AppSettingsStore.Load().ProxyPort);
        }
        catch (OperationCanceledException)
        {
            Logger.Info($"aria2c download cancelled: {fileName}");
            throw;
        }
        catch (Exception ex)
        {
            Logger.Error(ex, $"aria2c execution failed: {fileName}");
            throw;
        }

        if (IsPaused)
        {
            Logger.Info($"Download interrupted by pause state: {fileName}");

            await WaitIfPausedAsync(ct);
            await DownloadOneAsync(url, finalPath, fileIndex, fileCount, progress, ct);

            return;
        }

        ct.ThrowIfCancellationRequested();

        if (exitCode != 0)
        {
            Logger.Error($"aria2c failed for {fileName} with exit code {exitCode}. Last output: {lastOutput}");

            throw BuildDownloadException(exitCode, lastOutput, url, fileName);
        }

        if (!File.Exists(finalPath))
        {
            Logger.Error($"aria2c completed but file was not found: {finalPath}");

            throw new DownloadException(
                DownloadErrorType.ProcessError,
                "The download process completed, but the downloaded file was not found.",
                url, fileName: fileName,
                processExitCode: exitCode);
        }

        long downloadedSize = new FileInfo(finalPath).Length;

        if (totalBytes is > 0 && downloadedSize != totalBytes.Value)
        {
            Logger.Error($"Invalid file size for {fileName}. Expected {totalBytes.Value:N0}, got {downloadedSize:N0}.");

            throw new DownloadException(
                DownloadErrorType.ProcessError,
                $"The downloaded file size is invalid. Expected {totalBytes.Value} bytes, got {downloadedSize} bytes.",
                url, fileName: fileName,
                processExitCode: exitCode);
        }

        string controlPath = finalPath + ".aria2";

        if (File.Exists(controlPath))
        {
            try
            {
                File.Delete(controlPath);
                Logger.Debug($"Removed aria2 control file: {controlPath}");
            }
            catch (Exception ex)
            {
                Logger.Warn(ex, $"Unable to remove aria2 control file: {controlPath}");
            }
        }

        ReportLegacyAggregateProgress(
            fileName, fileIndex, fileCount, downloadedSize, totalBytes ?? downloadedSize, 100, progress, true);
        CommitLegacyFile(fileIndex, downloadedSize, totalBytes ?? downloadedSize);

        Logger.Info($"Legacy file download completed: {fileName} ({downloadedSize:N0} bytes)");
    }

    private async Task DownloadOneWithHttpAsync(
        string url,
        string finalPath,
        int fileIndex,
        int fileCount,
        IProgress<DownloadProgressInfo> progress,
        CancellationToken ct)
    {
        ThrowIfDisposed();

        string fileName = Path.GetFileName(finalPath);
        AppSettings settings = AppSettingsStore.Load();
        long speedLimitBytesPerSecond = Math.Max(0, settings.SpeedLimitKbps) * 1024L;
        string? directory = Path.GetDirectoryName(finalPath);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);

        long existingBytes = File.Exists(finalPath) ? new FileInfo(finalPath).Length : 0;
        long? totalBytes = await TryGetRemoteLengthAsync(url, ct);
        RegisterLegacyFileTotal(fileIndex, totalBytes ?? 0);
        BeginLegacyFile(fileIndex, existingBytes, totalBytes ?? 0);

        Logger.Info($"Starting HTTP download: {fileName}; resume bytes: {existingBytes:N0}");
        ReportLegacyAggregateProgress(fileName, fileIndex, fileCount, existingBytes, totalBytes, CalculatePercent(existingBytes, totalBytes), progress, true);

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (existingBytes > 0)
            request.Headers.Range = new RangeHeaderValue(existingBytes, null);

        using HttpResponseMessage response = await _http.SendAsync(
            request, HttpCompletionOption.ResponseHeadersRead, ct);

        if (!response.IsSuccessStatusCode && response.StatusCode != HttpStatusCode.PartialContent)
        {
            DownloadException httpError = CreateHttpDownloadException(url, response.StatusCode);
            throw new DownloadException(
                httpError.ErrorType,
                $"HTTP download failed: {(int)response.StatusCode} {response.ReasonPhrase}",
                url, (int)response.StatusCode, fileName: fileName,
                innerException: httpError);
        }

        bool resumeAccepted = existingBytes > 0 && response.StatusCode == HttpStatusCode.PartialContent;
        if (!resumeAccepted)
            existingBytes = 0;
        SetLegacyFileInitialBytes(fileIndex, existingBytes);

        if (totalBytes is null && response.Content.Headers.ContentLength is long responseLength && responseLength > 0)
        {
            totalBytes = resumeAccepted ? existingBytes + responseLength : responseLength;
            RegisterLegacyFileTotal(fileIndex, totalBytes.Value);
        }

        long downloaded = existingBytes;
        long lastSampleBytes = downloaded;
        DateTime lastSample = DateTime.UtcNow;

        await using Stream input = await response.Content.ReadAsStreamAsync(ct);
        await using var output = new FileStream(
            finalPath,
            resumeAccepted ? FileMode.Append : FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            1024 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);

        byte[] buffer = new byte[1024 * 1024];
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            await WaitIfPausedAsync(ct);

            int read = await input.ReadAsync(buffer, ct);
            if (read == 0) break;

            await output.WriteAsync(buffer.AsMemory(0, read), ct);
            downloaded += read;

            if (speedLimitBytesPerSecond > 0)
            {
                double targetSeconds = (downloaded - existingBytes) / (double)speedLimitBytesPerSecond;
                double actualSeconds = (DateTime.UtcNow - lastSample).TotalSeconds;
                double delaySeconds = targetSeconds - actualSeconds;
                if (delaySeconds > 0)
                    await Task.Delay(TimeSpan.FromSeconds(Math.Min(delaySeconds, 5)), ct);
            }

            DateTime now = DateTime.UtcNow;
            double elapsed = (now - lastSample).TotalSeconds;
            if (elapsed >= 0.25)
            {
                ReportLegacyAggregateProgress(
                    fileName,
                    fileIndex,
                    fileCount,
                    downloaded,
                    totalBytes,
                    CalculatePercent(downloaded, totalBytes),
                    progress,
                    false);

                lastSample = now;
                lastSampleBytes = downloaded;
            }
        }

        await output.FlushAsync(ct);
        ct.ThrowIfCancellationRequested();

        if (totalBytes is > 0 && downloaded != totalBytes.Value)
            throw new DownloadException(
                DownloadErrorType.ProcessError,
                $"The downloaded file size is invalid. Expected {totalBytes.Value} bytes, got {downloaded} bytes.",
                url, fileName: fileName);

        ReportLegacyAggregateProgress(
            fileName, fileIndex, fileCount, downloaded, totalBytes ?? downloaded, 100, progress, true);
        CommitLegacyFile(fileIndex, downloaded, totalBytes ?? downloaded);
        Logger.Info($"HTTP file download completed: {fileName} ({downloaded:N0} bytes)");
    }

    private void ResetAggregateLegacyMetrics()
    {
        lock (_aggregateLock)
        {
            _legacyFileInitialBytes.Clear();
            _legacyFileTotalBytes.Clear();
            _legacyCompletedAvailableBytes = 0;
            _legacyCompletedTransferredBytes = 0;
            _legacyKnownTotalBytes = 0;
            _legacyAllTotalsKnown = false;
            _transferMetrics.Reset(0);
        }
    }

    private async Task PrepareLegacyAggregateTotalsAsync(
        IReadOnlyList<string> urls,
        string destFolder,
        CancellationToken ct)
    {
        bool allKnown = true;
        long knownTotal = 0;

        for (int i = 0; i < urls.Count; i++)
        {
            ct.ThrowIfCancellationRequested();

            string fileName = GetFileName(urls[i]);
            string finalPath = Path.Combine(destFolder, fileName);
            long? total = null;

            if (File.Exists(finalPath) && !File.Exists(finalPath + ".aria2"))
            {
                long existingSize = new FileInfo(finalPath).Length;
                total = existingSize;
            }
            else
            {
                total = await TryGetRemoteLengthAsync(urls[i], ct);
            }

            lock (_aggregateLock)
            {
                int index = i + 1;
                _legacyFileTotalBytes[index] = total.GetValueOrDefault(0);
            }

            if (total is > 0)
            {
                try
                {
                    knownTotal = checked(knownTotal + total.Value);
                }
                catch (OverflowException)
                {
                    knownTotal = long.MaxValue;
                }
            }
            else
            {
                allKnown = false;
            }
        }

        lock (_aggregateLock)
        {
            _legacyKnownTotalBytes = knownTotal;
            _legacyAllTotalsKnown = allKnown && urls.Count > 0;
            _transferMetrics.SetTotalBytes(_legacyAllTotalsKnown ? _legacyKnownTotalBytes : 0);
        }
    }

    private void RegisterLegacyFileTotal(int fileIndex, long totalBytes)
    {
        if (totalBytes <= 0)
            return;

        lock (_aggregateLock)
        {
            long oldTotal = _legacyFileTotalBytes.GetValueOrDefault(fileIndex);
            if (oldTotal == totalBytes)
                return;

            if (oldTotal > 0)
                _legacyKnownTotalBytes = Math.Max(0, _legacyKnownTotalBytes - oldTotal);

            try
            {
                _legacyKnownTotalBytes = checked(_legacyKnownTotalBytes + totalBytes);
            }
            catch (OverflowException)
            {
                _legacyKnownTotalBytes = long.MaxValue;
            }

            _legacyFileTotalBytes[fileIndex] = totalBytes;
            _transferMetrics.SetTotalBytes(_legacyAllTotalsKnown ? _legacyKnownTotalBytes : 0);
        }
    }

    private void BeginLegacyFile(int fileIndex, long initialBytes, long totalBytes)
    {
        lock (_aggregateLock)
        {
            _legacyFileInitialBytes[fileIndex] = Math.Max(0, initialBytes);
            if (totalBytes > 0)
                RegisterLegacyFileTotal(fileIndex, totalBytes);
        }
    }

    private void SetLegacyFileInitialBytes(int fileIndex, long initialBytes)
    {
        lock (_aggregateLock)
            _legacyFileInitialBytes[fileIndex] = Math.Max(0, initialBytes);
    }

    private void CommitLegacyFile(int fileIndex, long availableBytes, long totalBytes)
    {
        lock (_aggregateLock)
        {
            long available = Math.Max(0, availableBytes);
            long initial = _legacyFileInitialBytes.GetValueOrDefault(fileIndex);
            long networkTransferred = Math.Max(0, available - initial);

            try
            {
                _legacyCompletedAvailableBytes = checked(_legacyCompletedAvailableBytes + available);
            }
            catch (OverflowException)
            {
                _legacyCompletedAvailableBytes = long.MaxValue;
            }

            try
            {
                _legacyCompletedTransferredBytes = checked(_legacyCompletedTransferredBytes + networkTransferred);
            }
            catch (OverflowException)
            {
                _legacyCompletedTransferredBytes = long.MaxValue;
            }

            if (totalBytes > 0)
                RegisterLegacyFileTotal(fileIndex, totalBytes);

            _legacyFileInitialBytes.Remove(fileIndex);
        }
    }

    private void ReportLegacyAggregateProgress(
        string fileName,
        int fileIndex,
        int fileCount,
        long currentFileAvailableBytes,
        long? currentFileTotalBytes,
        double? currentFilePercent,
        IProgress<DownloadProgressInfo> progress,
        bool force)
    {
        UnifiedTransferMetricsSnapshot metrics;
        long aggregateAvailable;
        long aggregateTotal;
        bool allTotalsKnown;
        long currentInitialBytes;

        lock (_aggregateLock)
        {
            if (currentFileTotalBytes is > 0)
                RegisterLegacyFileTotal(fileIndex, currentFileTotalBytes.Value);

            currentInitialBytes = _legacyFileInitialBytes.GetValueOrDefault(fileIndex);
            long currentAvailable = Math.Max(0, currentFileAvailableBytes);
            long currentTransferred = Math.Max(0, currentAvailable - currentInitialBytes);

            aggregateAvailable = SaturatingAdd(_legacyCompletedAvailableBytes, currentAvailable);
            long aggregateTransferred = SaturatingAdd(_legacyCompletedTransferredBytes, currentTransferred);
            allTotalsKnown = _legacyAllTotalsKnown && _legacyKnownTotalBytes > 0;
            aggregateTotal = allTotalsKnown ? _legacyKnownTotalBytes : 0;

            _transferMetrics.SetTotalBytes(aggregateTotal);
            metrics = _transferMetrics.Update(
                aggregateAvailable,
                aggregateTransferred,
                force);
        }

        double? aggregatePercent = aggregateTotal > 0
            ? Math.Clamp(metrics.AvailableBytes * 100d / aggregateTotal, 0, 100)
            : currentFilePercent;

        progress.Report(new DownloadProgressInfo
        {
            BytesReceived = metrics.AvailableBytes,
            TotalBytes = aggregateTotal > 0 ? aggregateTotal : currentFileTotalBytes,
            Percent = aggregatePercent,
            SpeedBytesPerSecond = (long)Math.Clamp(metrics.SpeedBytesPerSecond, 0, long.MaxValue),
            Eta = metrics.Eta,
            FileName = fileName,
            FileIndex = fileIndex,
            FileCount = fileCount
        });
    }

    private static long SaturatingAdd(long left, long right)
    {
        if (right <= 0)
            return Math.Max(0, left);

        if (left > long.MaxValue - right)
            return long.MaxValue;

        return left + right;
    }

    private async Task TryValidateRemoteResourceAsync(string url, CancellationToken ct)
    {
        ThrowIfDisposed();

        try
        {
            using var headRequest = new HttpRequestMessage(HttpMethod.Head, url);
            using HttpResponseMessage response = await _http.SendAsync(
                headRequest, HttpCompletionOption.ResponseHeadersRead, ct);

            if (response.IsSuccessStatusCode)
            {
                Logger.Debug($"HEAD validation succeeded: {url}");
                return;
            }

            if (response.StatusCode == HttpStatusCode.MethodNotAllowed)
            {
                Logger.Debug($"HEAD not supported, attempting range validation: {url}");
                await TryRangeValidationAsync(url, ct);
                return;
            }

            DownloadException exception = CreateHttpDownloadException(
                url,
                response.StatusCode);

            Logger.Error(exception, $"HEAD validation failed: {url}");

            throw exception;
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            Logger.Warn(ex, $"HEAD validation timed out. Continuing to aria2c: {url}");
        }
        catch (HttpRequestException ex)
        {
            Logger.Warn(ex, $"HEAD validation encountered a network error. Continuing to aria2c: {url}");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (DownloadException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Logger.Warn(ex, $"HEAD validation failed unexpectedly. Continuing to aria2c: {url}");
        }
    }

    private async Task TryRangeValidationAsync(
        string url, CancellationToken ct)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Range = new RangeHeaderValue(0, 0);

            using HttpResponseMessage response = await _http.SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead, ct);

            if (response.IsSuccessStatusCode ||
                response.StatusCode == HttpStatusCode.PartialContent)
            {
                Logger.Debug($"Range validation succeeded: {url}");
                return;
            }

            DownloadException exception = CreateHttpDownloadException(
                url, response.StatusCode);

            Logger.Error(exception, $"Range validation failed: {url}");

            throw exception;
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            Logger.Warn(ex, $"Range validation timed out. Continuing to aria2c: {url}");
        }
        catch (HttpRequestException ex)
        {
            Logger.Warn(ex, $"Range validation encountered a network error. Continuing to aria2c: {url}");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (DownloadException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Logger.Warn(ex, $"Range validation failed unexpectedly. Continuing to aria2c: {url}");
        }
    }

    private static DownloadException CreateHttpDownloadException(
        string url,
        HttpStatusCode statusCode)
    {
        int status = (int)statusCode;

        return status switch
        {
            401 => new DownloadException(
                DownloadErrorType.Unauthorized,
                $"The download server requires authorization (HTTP {status}).",
                url, httpStatusCode: status),

            403 => new DownloadException(
                DownloadErrorType.Forbidden,
                $"Access to the requested file was denied (HTTP {status}).",
                url, httpStatusCode: status),

            404 => new DownloadException(
                DownloadErrorType.NotFound,
                $"The requested file was not found (HTTP {status}).",
                url, httpStatusCode: status),

            408 => new DownloadException(
                DownloadErrorType.Timeout,
                $"The download server timed out the request (HTTP {status}).",
                url, httpStatusCode: status),

            429 => new DownloadException(
                DownloadErrorType.RateLimited,
                $"The download server is rate limiting requests (HTTP {status}).",
                url, httpStatusCode: status),

            >= 500 and <= 599 => new DownloadException(
                DownloadErrorType.ServerError,
                $"The download server returned an error (HTTP {status}).",
                url, httpStatusCode: status),

            _ => new DownloadException(
                DownloadErrorType.Unknown,
                $"The download server returned HTTP {status}.",
                url, httpStatusCode: status)
        };
    }

    private static DownloadException BuildDownloadException(
        int exitCode, string? lastOutput, string url, string fileName)
    {
        Match? statusMatch = string.IsNullOrWhiteSpace(lastOutput)
            ? null
            : HttpStatusRegex.Match(lastOutput);

        if (statusMatch?.Success == true &&
            int.TryParse(
                statusMatch.Groups["status"].Value,
                out int status))
        {
            return CreateHttpDownloadException(
                url,
                (HttpStatusCode)status);
        }

        if (exitCode == 2)
        {
            return new DownloadException(
                DownloadErrorType.Timeout,
                "The download server or network connection timed out.",
                url, fileName: fileName,
                processExitCode: exitCode);
        }

        if (exitCode == 6)
        {
            return new DownloadException(
                DownloadErrorType.NetworkError,
                "A network error occurred while downloading the file.",
                url, fileName: fileName,
                processExitCode: exitCode);
        }

        if (exitCode == 3 || exitCode == 4)
        {
            return new DownloadException(
                DownloadErrorType.NotFound,
                "The requested file could not be found on the download server.",
                url, fileName: fileName,
                processExitCode: exitCode);
        }

        if (exitCode == 8)
        {
            return new DownloadException(
                DownloadErrorType.ProcessError,
                "The server does not support resuming this download.",
                url, fileName: fileName,
                processExitCode: exitCode);
        }

        if (string.IsNullOrWhiteSpace(lastOutput))
        {
            return new DownloadException(
                DownloadErrorType.ProcessError,
                $"The download process failed with exit code {exitCode}.",
                url, fileName: fileName,
                processExitCode: exitCode);
        }

        return new DownloadException(
            DownloadErrorType.ProcessError,
            $"The download process failed with exit code {exitCode}. Last output: {lastOutput}",
            url, fileName: fileName,
            processExitCode: exitCode);
    }

    private static double? CalculatePercent(
        long bytesReceived,
        long? totalBytes)
    {
        if (totalBytes is not > 0)
            return null;

        return Math.Clamp(bytesReceived * 100d / totalBytes.Value, 0, 100);
    }

    private async Task<long?> TryGetRemoteLengthAsync(
        string url, CancellationToken ct)
    {
        ThrowIfDisposed();

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Head, url);
            using HttpResponseMessage response = await _http.SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead, ct);

            if (response.IsSuccessStatusCode &&
                response.Content.Headers.ContentLength is long length)
            {
                return length;
            }
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            Logger.Warn(ex, $"Remote size HEAD request timed out: {url}");
        }
        catch (HttpRequestException ex)
        {
            Logger.Debug(ex, $"Unable to retrieve remote size through HEAD: {url}");
        }
        catch (OperationCanceledException)
        {
            if (ct.IsCancellationRequested) throw;
        }
        catch (Exception ex)
        {
            Logger.Debug(ex, $"Unexpected error retrieving remote size through HEAD: {url}");
        }

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Range = new RangeHeaderValue(0, 0);

            using HttpResponseMessage response = await _http.SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead, ct);

            if (response.Content.Headers.ContentRange?.Length is long rangeLength)
                return rangeLength;

            if (response.Content.Headers.ContentLength is long length)
                return length;
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            Logger.Warn(ex, $"Remote size range request timed out: {url}");
        }
        catch (HttpRequestException ex)
        {
            Logger.Debug(ex, $"Unable to retrieve remote size through range request: {url}");
        }
        catch (OperationCanceledException)
        {
            if (ct.IsCancellationRequested)
                throw;
        }
        catch (Exception ex)
        {
            Logger.Debug(ex, $"Unexpected error retrieving remote size through range request: {url}");
        }

        return null;
    }

    private async Task WaitIfPausedAsync(CancellationToken ct)
    {
        Task task;

        lock (_stateLock)
        {
            if (!_isPaused) return;
            task = _resumeSource.Task;
        }

        await task.WaitAsync(ct);
    }

    private static string GetFileName(string url)
    {
        if (!Uri.TryCreate(
                url, UriKind.Absolute, out Uri? uri))
        {
            throw new DownloadException(
                DownloadErrorType.InvalidUrl, "The download URL is invalid.", url);
        }

        string fileName = Path.GetFileName(uri.LocalPath);

        if (string.IsNullOrWhiteSpace(fileName))
        {
            throw new DownloadException(
                DownloadErrorType.InvalidUrl, "Could not determine the file name from the download URL.", url);
        }

        return fileName;
    }

    private static TaskCompletionSource<bool> CreateCompletedSource()
    {
        var source = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        source.TrySetResult(true);
        return source;
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(DownloadService));
    }

    public void Dispose()
    {
        if (_disposed) return;

        _disposed = true;
        Logger.Info("Legacy DownloadService disposing.");

        try
        {
            _http.Dispose();
        }
        catch (Exception ex)
        {
            Logger.Warn(ex, "Failed to dispose Legacy HTTP client.");
        }

        lock (_stateLock)
        {
            _isPaused = false;
            _resumeSource.TrySetResult(true);
        }

        Logger.Info("Legacy DownloadService disposed.");
    }
}
