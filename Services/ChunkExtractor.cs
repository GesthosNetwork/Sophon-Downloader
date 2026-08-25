using System.IO;
using System.Security.Cryptography;
using NLog;
using SophonDownloader;
using SophonDownloader.Core;
using SophonDownloader.Models;
using SophonDownloader.Utilities;
using ZstdSharp;

namespace SophonDownloader.Services;

public sealed class ExtractionProgress
{
    public int TotalFiles { get; init; }
    public int CompletedFiles { get; init; }
    public long TotalBytes { get; init; }
    public long ExtractedBytes { get; init; }
    public string StatusText { get; init; } = "";
}

public sealed class ChunkExtractor : IDisposable
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();
    private const int BufferSize = 1024 * 1024;

    private readonly CancellationTokenSource _cancellationTokenSource = new();
    private readonly object _pauseLock = new();
    private TaskCompletionSource<bool> _resumeSignal = CreateCompletedSignal();

    private bool _isPaused;
    private bool _disposed;
    private int _totalFiles;
    private int _completedFiles;
    private long _totalBytes;
    private long _extractedBytes;

    public Action<ExtractionProgress>? ProgressUpdateCallback { get; set; }
    public Action<string>? StatusTextCallback { get; set; }
    public Action? ExtractionCompletedCallback { get; set; }
    public Action? ExtractionCancelledCallback { get; set; }
    public bool IsPaused => _isPaused;

    private static TaskCompletionSource<bool> CreateCompletedSignal()
    {
        var signal = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        signal.TrySetResult(true);
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
                Logger.Info("Extraction resumed.");
            }
            else
            {
                _isPaused = true;
                _resumeSignal = new TaskCompletionSource<bool>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                paused = true;
                Logger.Info("Extraction paused.");
            }
        }

        PublishProgress(paused ? "Extraction paused." : "Extraction resumed.");
    }

    public void CancelExtraction()
    {
        if (_disposed)
            return;

        Logger.Info("Extraction cancellation requested.");

        try
        {
            _cancellationTokenSource.Cancel();
        }
        catch {}

        lock (_pauseLock)
            _resumeSignal.TrySetResult(true);
    }

    public async Task StartExtraction(
        List<SophonChunkFile> allFiles,
        string saveDirectory,
        bool cleanupExtraFiles = false)
    {
        ThrowIfDisposed();

        if (allFiles == null)
            throw new ArgumentNullException(nameof(allFiles));

        if (string.IsNullOrWhiteSpace(saveDirectory))
            throw new ArgumentException("Save directory cannot be empty.", nameof(saveDirectory));

        _cancellationTokenSource.Token.ThrowIfCancellationRequested();

        _totalFiles = allFiles.Count(file => !file.IsFolder);
        _completedFiles = 0;
        _totalBytes = allFiles.Where(file => !file.IsFolder).Sum(file => file.Size);
        _extractedBytes = 0;

        var chunkStore = new ChunkStore(saveDirectory);

        if (!Directory.Exists(chunkStore.ChunkDirectory))
            throw new DirectoryNotFoundException($"Chunk cache not found: {chunkStore.ChunkDirectory}");

        Logger.Info(
            $"Starting extraction. Files={_totalFiles:N0}, " +
            $"Bytes={_totalBytes:N0}, " +
            $"SaveDirectory={Path.GetFullPath(saveDirectory)}, " +
            $"ChunkDirectory={chunkStore.ChunkDirectory}");

        try
        {
            foreach (SophonChunkFile file in allFiles)
            {
                _cancellationTokenSource.Token.ThrowIfCancellationRequested();
                await WaitIfPausedAsync(_cancellationTokenSource.Token);

                if (file.IsFolder)
                {
                    string directory = Path.GetFullPath(Path.Combine(saveDirectory, file.File));
                    EnsurePathInsideSaveDirectory(saveDirectory, directory);
                    Directory.CreateDirectory(directory);
                    continue;
                }

                await ExtractFileAsync(
                    file,
                    chunkStore,
                    saveDirectory,
                    _cancellationTokenSource.Token);

                Interlocked.Increment(ref _completedFiles);
                PublishProgress($"Extracted: {file.File}");
            }

            if (cleanupExtraFiles)
                await CleanupExtraFilesAsync(allFiles, saveDirectory, chunkStore);

            PublishProgress("Extraction completed successfully.");
            Logger.Info("Extraction completed successfully.");
            ExtractionCompletedCallback?.Invoke();
        }
        catch (OperationCanceledException)
        {
            Logger.Info("Extraction cancelled.");
            ExtractionCancelledCallback?.Invoke();
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Extraction failed.");
            throw;
        }
    }

    private async Task ExtractFileAsync(
        SophonChunkFile file,
        ChunkStore chunkStore,
        string saveDirectory,
        CancellationToken cancellationToken)
    {
        string filePath = Path.GetFullPath(Path.Combine(saveDirectory, file.File));
        EnsurePathInsideSaveDirectory(saveDirectory, filePath);

        string? directory = Path.GetDirectoryName(filePath);

        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        string temporaryPath = filePath + ".tmp";

        if (File.Exists(temporaryPath))
        {
            try
            {
                File.Delete(temporaryPath);
            }
            catch (Exception ex)
            {
                Logger.Warn(ex, $"Unable to remove old temporary file: {temporaryPath}");
                throw;
            }
        }

        Logger.Info($"Extracting {file.File} ({Utility.FormatFileSize(file.Size)}).");

        try
        {
            long fileBytes = 0;

            using var fileMd5 = IncrementalHash.CreateHash(HashAlgorithmName.MD5);

            using (var output = new FileStream(
                temporaryPath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                BufferSize,
                FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                foreach (SophonChunk chunk in file.Chunks)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    await WaitIfPausedAsync(cancellationToken);

                    fileBytes += await ExtractChunkAsync(
                        chunk,
                        chunkStore,
                        output,
                        fileMd5,
                        cancellationToken);
                }

                await output.FlushAsync(cancellationToken);
            }

            cancellationToken.ThrowIfCancellationRequested();

            string calculatedFileMd5 = Convert.ToHexString(fileMd5.GetHashAndReset()).ToLowerInvariant();

            if (fileBytes != file.Size)
                throw new InvalidDataException(
                    $"File size mismatch for '{file.File}': expected {file.Size}, actual {fileBytes}");

            if (!calculatedFileMd5.Equals(file.Md5, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException(
                    $"Final file MD5 mismatch for '{file.File}': expected {file.Md5}, actual {calculatedFileMd5}");

            cancellationToken.ThrowIfCancellationRequested();

            File.Move(temporaryPath, filePath, true);
            Logger.Info($"Extraction completed: {file.File}");
        }
        catch
        {
            TryDeleteFile(temporaryPath);
            throw;
        }
    }

    private async Task<long> ExtractChunkAsync(
        SophonChunk chunk,
        ChunkStore chunkStore,
        FileStream output,
        IncrementalHash fileMd5,
        CancellationToken cancellationToken)
    {
        await using FileStream chunkStream = chunkStore.OpenChunk(chunk.Id);

        if (chunkStream.Length != chunk.CompressedSize)
            throw new InvalidDataException(
                $"Cached chunk size mismatch: {chunk.Id}. " +
                $"Expected {chunk.CompressedSize}, actual {chunkStream.Length}");

        if (!string.IsNullOrWhiteSpace(chunk.CompressedMd5))
        {
            string compressedMd5 = await Utility.CalculateMd5Async(chunkStream);
            cancellationToken.ThrowIfCancellationRequested();

            if (!compressedMd5.Equals(chunk.CompressedMd5, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException(
                    $"Compressed MD5 mismatch for chunk '{chunk.Id}'. " +
                    $"Expected {chunk.CompressedMd5}, actual {compressedMd5}");
        }

        chunkStream.Position = 0;

        using var decompressor = new DecompressionStream(chunkStream);
        using var chunkMd5 = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
        var buffer = new byte[BufferSize];
        long chunkBytes = 0;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await WaitIfPausedAsync(cancellationToken);

            int bytesRead = await decompressor.ReadAsync(
                buffer.AsMemory(0, buffer.Length),
                cancellationToken);

            if (bytesRead == 0)
                break;

            await output.WriteAsync(
                buffer.AsMemory(0, bytesRead),
                cancellationToken);

            chunkMd5.AppendData(buffer, 0, bytesRead);
            fileMd5.AppendData(buffer, 0, bytesRead);
            chunkBytes += bytesRead;
            Interlocked.Add(ref _extractedBytes, bytesRead);

            PublishProgress("Extracting files...");
        }

        string calculatedChunkMd5 = Convert.ToHexString(chunkMd5.GetHashAndReset()).ToLowerInvariant();

        if (chunkBytes != chunk.UncompressedSize)
            throw new InvalidDataException(
                $"Decompressed size mismatch for chunk '{chunk.Id}': " +
                $"expected {chunk.UncompressedSize}, actual {chunkBytes}");

        if (!calculatedChunkMd5.Equals(chunk.UncompressedMd5, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException(
                $"Decompressed MD5 mismatch for chunk '{chunk.Id}': " +
                $"expected {chunk.UncompressedMd5}, actual {calculatedChunkMd5}");

        return chunkBytes;
    }

    private Task CleanupExtraFilesAsync(
        List<SophonChunkFile> allFiles,
        string saveDirectory,
        ChunkStore chunkStore)
    {
        var expectedFiles = new HashSet<string>(
            allFiles
                .Where(file => !file.IsFolder)
                .Select(file => Path.GetFullPath(Path.Combine(saveDirectory, file.File))),
            StringComparer.OrdinalIgnoreCase);

        var expectedDirectories = new HashSet<string>(
            allFiles
                .Where(file => file.IsFolder)
                .Select(file => Path.GetFullPath(Path.Combine(saveDirectory, file.File))),
            StringComparer.OrdinalIgnoreCase);

        foreach (string file in Directory.GetFiles(saveDirectory, "*", SearchOption.AllDirectories))
        {
            string fullPath = Path.GetFullPath(file);

            if (IsUnderChunkDirectory(chunkStore, fullPath))
                continue;

            if (!expectedFiles.Contains(fullPath))
            {
                try
                {
                    File.Delete(fullPath);
                    Logger.Debug($"Deleted extra file: {fullPath}");
                }
                catch (Exception ex)
                {
                    Logger.Warn(ex, $"Unable to delete extra file: {fullPath}");
                }
            }
        }

        var directories = Directory.GetDirectories(
                saveDirectory,
                "*",
                SearchOption.AllDirectories)
            .OrderByDescending(path => path.Length)
            .ToList();

        foreach (string directory in directories)
        {
            string fullPath = Path.GetFullPath(directory);

            if (IsUnderChunkDirectory(chunkStore, fullPath) ||
                expectedDirectories.Contains(fullPath))
                continue;

            try
            {
                if (!Directory.EnumerateFileSystemEntries(fullPath).Any())
                    Directory.Delete(fullPath);
            }
            catch (Exception ex)
            {
                Logger.Warn(ex, $"Unable to delete extra directory: {fullPath}");
            }
        }

        return Task.CompletedTask;
    }

    private static bool IsUnderChunkDirectory(ChunkStore chunkStore, string path)
    {
        string chunkRoot = Path.GetFullPath(chunkStore.ChunkDirectory)
            .TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;

        return path.StartsWith(chunkRoot, StringComparison.OrdinalIgnoreCase);
    }

    private static void EnsurePathInsideSaveDirectory(
        string saveDirectory,
        string targetPath)
    {
        string root = Path.GetFullPath(saveDirectory)
            .TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;

        if (!targetPath.StartsWith(root, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"Manifest file path escapes the selected directory: {targetPath}");
        }
    }

    private void PublishProgress(string status)
    {
        var progress = new ExtractionProgress
        {
            TotalFiles = _totalFiles,
            CompletedFiles = _completedFiles,
            TotalBytes = _totalBytes,
            ExtractedBytes = Interlocked.Read(ref _extractedBytes),
            StatusText =
                $"Files: {_completedFiles}/{_totalFiles} | " +
                $"{Utility.FormatFileSize(_extractedBytes)}/" +
                $"{Utility.FormatFileSize(_totalBytes)} | {status}"
        };

        try
        {
            ProgressUpdateCallback?.Invoke(progress);
            StatusTextCallback?.Invoke(progress.StatusText);
        }
        catch (Exception ex)
        {
            Logger.Warn(ex, "Extraction progress callback failed.");
        }
    }

    private async Task WaitIfPausedAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            Task waitTask;

            lock (_pauseLock)
            {
                if (!_isPaused)
                    return;

                waitTask = _resumeSignal.Task;
            }

            await waitTask.WaitAsync(cancellationToken);
        }
    }

    private static void TryDeleteFile(string path)
    {
        if (!File.Exists(path))
            return;

        try
        {
            File.Delete(path);
        }
        catch {}
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(ChunkExtractor));
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        Logger.Info("Disposing ChunkExtractor.");

        try
        {
            _cancellationTokenSource.Cancel();
        }
        catch {}

        lock (_pauseLock)
            _resumeSignal.TrySetResult(true);

        try
        {
            _cancellationTokenSource.Dispose();
        }
        catch {}

        Logger.Info("ChunkExtractor disposed.");
    }
}
