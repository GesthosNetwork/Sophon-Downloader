using SophonDownloader;
using SophonDownloader.Core;
using SophonDownloader.Models;

namespace SophonDownloader.Services;

public sealed class SophonCoreDownloader : IDisposable
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

    private ChunkDownloader? _chunkDownloader;
    private ChunkExtractor? _chunkExtractor;
    private DownloadStage _stage = DownloadStage.None;
    private bool _disposed;

    private enum DownloadStage
    {
        None,
        DownloadingChunks,
        Extracting
    }

    public Action<ChunkDownloadProgress>? ChunkProgressUpdateCallback { get; set; }
    public Action<ExtractionProgress>? ExtractionProgressUpdateCallback { get; set; }
    public Action<string>? StatusTextCallback { get; set; }
    public Action? ChunkDownloadCompletedCallback { get; set; }
    public Action? ExtractionCompletedCallback { get; set; }
    public Action? DownloadCancelledCallback { get; set; }
    public Action? ExtractionCancelledCallback { get; set; }

    public bool IsPaused => _stage switch
    {
        DownloadStage.DownloadingChunks => _chunkDownloader?.IsPaused ?? false,
        DownloadStage.Extracting => _chunkExtractor?.IsPaused ?? false,
        _ => false
    };

    public void TogglePause()
    {
        ThrowIfDisposed();

        switch (_stage)
        {
            case DownloadStage.DownloadingChunks:
                _chunkDownloader?.TogglePause();
                break;

            case DownloadStage.Extracting:
                _chunkExtractor?.TogglePause();
                break;
        }
    }

    public async Task StartDownload(
        List<SophonChunkFile> allFiles,
        Dictionary<string, string> fileManifest,
        string saveDirectory,
        int maxConcurrency = 16)
    {
        ThrowIfDisposed();
        DisposeChunkExtractor();
        DisposeChunkDownloader();

        _stage = DownloadStage.DownloadingChunks;

        _chunkDownloader = new ChunkDownloader
        {
            ProgressUpdateCallback = progress => ChunkProgressUpdateCallback?.Invoke(progress),
            StatusTextCallback = text => StatusTextCallback?.Invoke(text),
            DownloadCompletedCallback = () => ChunkDownloadCompletedCallback?.Invoke(),
            DownloadCancelledCallback = () => DownloadCancelledCallback?.Invoke()
        };

        try
        {
            await _chunkDownloader.StartDownload(allFiles, fileManifest, saveDirectory, maxConcurrency);
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Chunk download failed.");
            throw;
        }
        finally
        {
            _stage = DownloadStage.None;
        }
    }

    public async Task StartExtraction(List<SophonChunkFile> allFiles,
        string saveDirectory, bool cleanupExtraFiles = false, bool isPatch = false, bool isLdiffPatch = false)
    {
        ThrowIfDisposed();
        DisposeChunkDownloader();
        DisposeChunkExtractor();
        _stage = DownloadStage.Extracting;

        _chunkExtractor = new ChunkExtractor
        {
            ProgressUpdateCallback = progress => ExtractionProgressUpdateCallback?.Invoke(progress),
            StatusTextCallback = text => StatusTextCallback?.Invoke(text),
            ExtractionCompletedCallback = () => ExtractionCompletedCallback?.Invoke(),
            ExtractionCancelledCallback = () => ExtractionCancelledCallback?.Invoke()
        };

        try
        {
            await _chunkExtractor.StartExtraction(allFiles, saveDirectory, cleanupExtraFiles, isPatch, isLdiffPatch);
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Offline extraction failed.");
            throw;
        }
        finally
        {
            _stage = DownloadStage.None;
        }
    }

    public void CancelDownload()
    {
        if (_disposed)
            return;

        switch (_stage)
        {
            case DownloadStage.DownloadingChunks:
                _chunkDownloader?.CancelDownload();
                break;

            case DownloadStage.Extracting:
                _chunkExtractor?.CancelExtraction();
                break;
        }
    }

    private void DisposeChunkDownloader()
    {
        _chunkDownloader?.Dispose();
        _chunkDownloader = null;
    }

    private void DisposeChunkExtractor()
    {
        _chunkExtractor?.Dispose();
        _chunkExtractor = null;
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(SophonCoreDownloader));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        DisposeChunkDownloader();
        DisposeChunkExtractor();
    }
}
