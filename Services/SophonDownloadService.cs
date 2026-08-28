using ProtoBuf;
using SophonDownloader;
using SophonDownloader.Core;
using SophonDownloader.Models;
using SophonDownloader.Utilities;
using ZstdSharp;

namespace SophonDownloader.Services;

public sealed class SophonDownloadService : IDisposable
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

    private static readonly HttpClient HttpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(30)
    };

    private readonly SophonCoreDownloader _downloader = new();
    private readonly ILdiffMetadataProvider _ldiffMetadataProvider = new LdiffMetadataProvider();
    private readonly SemaphoreSlim _operationLock = new(1, 1);

    private ManifestConfig? _currentManifest;
    private SophonContentSet? _currentContent;
    private string? _currentSaveDirectory;

    private bool _disposed;
    private bool _chunkDownloadCompletedPending;
    private bool _chunkDownloadCancelledPending;
    private bool _extractionCompletedPending;
    private bool _extractionCancelledPending;
    private bool _chunkDownloadRunning;
    private bool _extractionRunning;

    public ManifestConfig? CurrentManifest => _currentManifest;
    public SophonContentSet? CurrentContent => _currentContent;
    public string? CurrentSaveDirectory => _currentSaveDirectory;
    public bool IsPaused => _downloader.IsPaused;

    public Action<ChunkDownloadProgress>? ChunkProgressCallback { get; set; }
    public Action<ExtractionProgress>? ExtractionProgressCallback { get; set; }
    public Action? ChunkDownloadCompletedCallback { get; set; }
    public Action? ExtractionCompletedCallback { get; set; }
    public Action? DownloadCancelledCallback { get; set; }
    public Action? ExtractionCancelledCallback { get; set; }

    public SophonDownloadService()
    {
        ConfigureCallbacks();
    }

    public async Task<ManifestConfig> LoadManifestAsync(
        GameInfo game, string version, string branch = "main",
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        BranchesGameBranch branchInfo =
            await SophonGameService.GetGameBranches(game.GameId, game.Region);

        BranchesMain? branchData =
            branch.Equals("predownload", StringComparison.OrdinalIgnoreCase)
                ? branchInfo.pre_download
                : branchInfo.main;

        if (branchData is null)
            throw new InvalidOperationException($"Branch '{branch}' is not available.");

        bool useLatestPreDownloadTag =
            branch.Equals("predownload", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(version, branchData.tag, StringComparison.OrdinalIgnoreCase);

        string url = SophonGameService.BuildGetBuildUrl(
            game.GameId, game.Region,
            branchData.package_id, branchData.password,
            useLatestPreDownloadTag ? null : version, branch);

        Logger.Info($"Loading Sophon build manifest: {url}");

        using HttpResponseMessage response =
            await HttpClient.GetAsync(url, cancellationToken);

        response.EnsureSuccessStatusCode();
        string json = await response.Content.ReadAsStringAsync(cancellationToken);
        ManifestConfig? config = JsonSerializer.Deserialize<ManifestConfig>(json);

        if (config is null)
            throw new InvalidDataException("Sophon manifest could not be parsed.");

        if (config.retcode != 0)
            throw new InvalidOperationException($"Sophon manifest error: {config.message}");

        if (string.IsNullOrWhiteSpace(config.data.tag))
            throw new InvalidDataException("Sophon manifest does not contain a version tag.");

        _currentManifest = config;
        Logger.Info($"Sophon build manifest loaded: {config.data.tag}");

        return config;
    }

    public IReadOnlyList<SophonContentOption> BuildContentOptions(
        ManifestConfig manifest)
    {
        ThrowIfDisposed();

        return manifest.data.manifests
            .Select(category => new SophonContentOption(category) { IsSelected = false }).ToList();
    }

    public async Task<SophonContentSet> LoadSelectedPatchContentAsync(
        GameInfo game,
        ManifestConfig fromManifest,
        ManifestConfig toManifest,
        IEnumerable<SophonContentOption> selected,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        List<SophonContentOption> selectedList =
            selected.Where(option => option.IsSelected).ToList();

        if (selectedList.Count == 0)
            throw new InvalidOperationException("No Sophon content was selected.");

        var allFiles = new List<SophonChunkFile>();
        var fileManifest = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (SophonContentOption option in selectedList)
        {
            cancellationToken.ThrowIfCancellationRequested();

            string matchingField = option.Category.matching_field;
            if (string.IsNullOrWhiteSpace(matchingField))
                throw new InvalidOperationException(
                    $"Sophon category '{option.Category.category_id}' does not provide a matching field.");

            LdiffSource? source = await _ldiffMetadataProvider.ResolveAsync(
                game,
                toManifest.data.tag,
                matchingField,
                fromManifest.data.tag,
                cancellationToken);

            if (source is null)
            {
                throw new InvalidOperationException(
                    $"No native LDiff metadata was found for {game.GameId} " +
                    $"({matchingField}) {fromManifest.data.tag} → {toManifest.data.tag}.");
            }

            Logger.Info(
                $"Loading LDiff manifest. Game={game.GameId}, Package={matchingField}, " +
                $"From={fromManifest.data.tag}, To={toManifest.data.tag}, URL={source.DiffManifestUrl}");

            byte[] manifestBytes = await DownloadBytesAsync(source.DiffManifestUrl, cancellationToken);
            SophonPatchManifest patchManifest;
            try
            {
                using MemoryStream direct = new(manifestBytes);
                patchManifest = Serializer.Deserialize<SophonPatchManifest>(direct);
                if (patchManifest.Patches.Count == 0)
                    throw new InvalidDataException("Direct LDiff protobuf was empty.");
            }
            catch
            {
                byte[] decompressedManifest = DecompressZstd(manifestBytes);
                using MemoryStream decompressed = new(decompressedManifest);
                patchManifest = Serializer.Deserialize<SophonPatchManifest>(decompressed);
            }

            foreach (SophonPatchFile patchFile in patchManifest.Patches)
            {
                SophonPatchInfo? patchInfo = patchFile.Patches.FirstOrDefault(x =>
                    string.Equals(x.Tag, fromManifest.data.tag, StringComparison.OrdinalIgnoreCase));

                if (patchInfo?.Patch is null || string.IsNullOrWhiteSpace(patchInfo.Patch.Id))
                    continue;

                SophonPatch patch = patchInfo.Patch;
                string outputFile = BuildPatchArtifactPath(patchFile.File, patch.OriginalFileName);

                var artifact = new SophonChunkFile
                {
                    File = outputFile,
                    IsFolder = false,
                    Size = patch.PatchFileSize,
                    Md5 = patch.PatchFileMd5
                };

                artifact.Chunks.Add(new SophonChunk
                {
                    Id = patch.Id,
                    CompressedSize = patch.PatchFileSize,
                    CompressedMd5 = patch.PatchFileMd5 ?? string.Empty,
                    UncompressedSize = patch.PatchFileSize,
                    UncompressedMd5 = patch.PatchFileMd5 ?? string.Empty,
                    Offset = 0
                });

                if (allFiles.Any(x => x.Chunks.Any(c =>
                        string.Equals(c.Id, patch.Id, StringComparison.OrdinalIgnoreCase))))
                    continue;

                allFiles.Add(artifact);
                fileManifest[artifact.File] = Utility.EnsureTrailingSlash(source.DiffListUrl);
            }
        }

        if (allFiles.Count == 0)
            throw new InvalidOperationException(
                $"No LDiff artifacts were found for {fromManifest.data.tag} → {toManifest.data.tag}.");

        var content = new SophonContentSet
        {
            AllFiles = allFiles,
            FileManifest = fileManifest,
            SelectedContent = selectedList,
            IsPatch = true,
            IsLdiffPatch = true,
            PatchFromVersion = fromManifest.data.tag,
            PatchToVersion = toManifest.data.tag
        };

        _currentContent = content;
        Logger.Info(
            $"Sophon LDiff patch loaded. From={content.PatchFromVersion}, To={content.PatchToVersion}, " +
            $"Files={content.FileCount:N0}, Artifacts={content.UniqueChunkCount:N0}, " +
            $"Size={Utility.FormatFileSize(content.UniqueCompressedSize)}");
        return content;
    }

    private static async Task<byte[]> DownloadBytesAsync(string url, CancellationToken cancellationToken)
    {
        using HttpResponseMessage response = await HttpClient.GetAsync(url, cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsByteArrayAsync(cancellationToken);
    }

    private static byte[] DecompressZstd(byte[] compressed)
    {
        using Decompressor decompressor = new();
        return decompressor.Unwrap(compressed).ToArray();
    }

    private static string BuildPatchArtifactPath(string file, string? originalFileName)
    {
        string safe = file.Replace('\\', '/').TrimStart('/');
        return string.IsNullOrWhiteSpace(originalFileName) ? safe : safe + ".hdiff";
    }

    public async Task<SophonContentSet> LoadSelectedContentAsync(
        ManifestConfig manifest,
        IEnumerable<SophonContentOption> selected,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        List<SophonContentOption> selectedList =
            selected.Where(option => option.IsSelected).ToList();

        if (selectedList.Count == 0)
            throw new InvalidOperationException("No Sophon content was selected.");

        var allFiles = new List<SophonChunkFile>();
        var fileManifest = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (SophonContentOption option in selectedList)
        {
            cancellationToken.ThrowIfCancellationRequested();

            ManifestCategory category = option.Category;
            string manifestPrefix = Utility.EnsureTrailingSlash(category.manifest_download.url_prefix);
            string manifestId = category.manifest.id;
            string chunkPrefix = Utility.EnsureTrailingSlash(category.chunk_download.url_prefix);
            string manifestUrl = manifestPrefix + manifestId;

            Logger.Info($"Downloading Sophon file manifest: {manifestUrl}");

            using HttpResponseMessage response = await HttpClient.GetAsync(manifestUrl, cancellationToken);
            response.EnsureSuccessStatusCode();
            byte[] compressedManifest = await response.Content.ReadAsByteArrayAsync(cancellationToken);

            using Decompressor decompressor = new();
            byte[] decompressedManifest = decompressor.Unwrap(compressedManifest).ToArray();
            SophonChunkManifest parsedManifest;

            using MemoryStream stream = new(decompressedManifest);
            parsedManifest = Serializer.Deserialize<SophonChunkManifest>(stream);

            foreach (SophonChunkFile file in parsedManifest.Chuncks)
            {
                if (fileManifest.ContainsKey(file.File)) continue;
                allFiles.Add(file);
                fileManifest[file.File] = chunkPrefix;
            }
        }

        var content = new SophonContentSet
        {
            AllFiles = allFiles,
            FileManifest = fileManifest,
            SelectedContent = selectedList
        };

        _currentContent = content;

        Logger.Info($"Sophon content loaded. " + $"Files={content.FileCount:N0}, " + $"Chunks={content.UniqueChunkCount:N0}");

        return content;
    }

    public async Task StartChunkDownloadAsync(SophonContentSet content, string saveDirectory, int maxConcurrency = 0)
    {
        ThrowIfDisposed();

        if (string.IsNullOrWhiteSpace(saveDirectory))
            throw new ArgumentException("Save directory cannot be empty.", nameof(saveDirectory));

        await _operationLock.WaitAsync();

        try
        {
            if (_chunkDownloadRunning)
                throw new InvalidOperationException("A Sophon chunk download is already running.");

            if (_extractionRunning)
                throw new InvalidOperationException("Sophon extraction is currently running.");

            Directory.CreateDirectory(saveDirectory);

            _currentContent = content;
            _currentSaveDirectory = Path.GetFullPath(saveDirectory);
            _chunkDownloadCompletedPending = false;
            _chunkDownloadCancelledPending = false;
            _chunkDownloadRunning = true;

            AppSettings settings = AppSettingsStore.Load();
            int configuredConcurrency = maxConcurrency > 0 ? maxConcurrency : settings.Threads;
            int concurrency = string.Equals(settings.DownloadMode, "Sequential", StringComparison.OrdinalIgnoreCase)
                ? 1
                : Math.Clamp(configuredConcurrency, 1, 64);

            Logger.Info($"Starting Sophon chunk download. " + $"Destination={_currentSaveDirectory}, " + $"Concurrency={concurrency}");

            string[] files = content.AllFiles
                .Where(file => !file.IsFolder && !string.IsNullOrWhiteSpace(file.File))
                .Select(file => file.File)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            if (files.Length > 0)
            {
                const int previewCount = 8;
                string preview = string.Join(", ", files.Take(previewCount));
                if (files.Length > previewCount)
                    preview += $", ... (+{files.Length - previewCount:N0} more)";

                Logger.Info($"Sophon files queued: {files.Length:N0}. Files={preview}");
            }

            try
            {
                await _downloader.StartDownload(content.AllFiles, content.FileManifest, _currentSaveDirectory, concurrency);
            }
            catch
            {
                _chunkDownloadRunning = false;
                throw;
            }

            _chunkDownloadRunning = false;

            if (_chunkDownloadCompletedPending)
            {
                _chunkDownloadCompletedPending = false;
                Logger.Info("Sophon chunk download fully completed. " + "Dispatching completion callback.");
                ChunkDownloadCompletedCallback?.Invoke();
            }
            else if (_chunkDownloadCancelledPending)
            {
                _chunkDownloadCancelledPending = false;
                Logger.Info("Sophon chunk download fully cancelled. " + "Dispatching cancellation callback.");
                DownloadCancelledCallback?.Invoke();
            }
        }
        finally
        {
            _chunkDownloadRunning = false;
            _operationLock.Release();
        }
    }

    public async Task StartExtractionAsync(
        SophonContentSet content, string saveDirectory, bool cleanupExtraFiles = false, bool deleteChunksAfterExtraction = false)
    {
        ThrowIfDisposed();

        if (string.IsNullOrWhiteSpace(saveDirectory))
            throw new ArgumentException("Save directory cannot be empty.", nameof(saveDirectory));

        await _operationLock.WaitAsync();

        try
        {
            if (_chunkDownloadRunning)
                throw new InvalidOperationException("Sophon chunk download is still running.");

            if (_extractionRunning)
                throw new InvalidOperationException("A Sophon extraction is already running.");

            Directory.CreateDirectory(saveDirectory);

            _currentContent = content;
            _currentSaveDirectory = Path.GetFullPath(saveDirectory);
            _extractionCompletedPending = false;
            _extractionCancelledPending = false;
            _extractionRunning = true;

            Logger.Info($"Starting Sophon extraction. " + $"Destination={_currentSaveDirectory}");

            try
            {
                await _downloader.StartExtraction(content.AllFiles, _currentSaveDirectory, cleanupExtraFiles, content.IsPatch, content.IsLdiffPatch);
            }
            catch
            {
                _extractionRunning = false;
                throw;
            }

            _extractionRunning = false;

            if (_extractionCompletedPending)
            {
                if (deleteChunksAfterExtraction)
                    DeleteContentChunks(content, _currentSaveDirectory);

                _extractionCompletedPending = false;
                Logger.Info("Sophon extraction fully completed. " + "Dispatching completion callback.");
                ExtractionCompletedCallback?.Invoke();
            }
            else if (_extractionCancelledPending)
            {
                _extractionCancelledPending = false;
                Logger.Info("Sophon extraction fully cancelled. " + "Dispatching cancellation callback.");
                ExtractionCancelledCallback?.Invoke();
            }
        }
        finally
        {
            _extractionRunning = false;
            _operationLock.Release();
        }
    }

    private static void DeleteContentChunks(
        SophonContentSet content,
        string saveDirectory)
    {
        List<string> chunkIds = content.AllFiles
            .SelectMany(x => x.Chunks)
            .Select(x => x.Id)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (chunkIds.Count == 0)
            return;

        Logger.Info($"Deleting {chunkIds.Count:N0} Sophon chunks after extraction.");

        var store = new ChunkStore(saveDirectory);
        store.DeleteChunks(chunkIds);
    }

    public void TogglePause()
    {
        ThrowIfDisposed();
        _downloader.TogglePause();
    }

    public void Cancel()
    {
        if (_disposed) return;
        Logger.Info("Sophon cancellation requested.");
        _downloader.CancelDownload();
    }

    private void ConfigureCallbacks()
    {
        _downloader.ChunkProgressUpdateCallback =
            progress => ChunkProgressCallback?.Invoke(progress);

        _downloader.ExtractionProgressUpdateCallback =
            progress => ExtractionProgressCallback?.Invoke(progress);

        _downloader.ChunkDownloadCompletedCallback =
            () => _chunkDownloadCompletedPending = true;

        _downloader.ExtractionCompletedCallback =
            () => _extractionCompletedPending = true;

        _downloader.DownloadCancelledCallback =
            () => _chunkDownloadCancelledPending = true;

        _downloader.ExtractionCancelledCallback =
            () => _extractionCancelledPending = true;
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(SophonDownloadService));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Logger.Info("Disposing SophonDownloadService.");

        try
        {
            _downloader.Dispose();
        }
        catch (Exception ex)
        {
            Logger.Warn(ex, "Failed to dispose Sophon downloader.");
        }

        _operationLock.Dispose();
    }
}
