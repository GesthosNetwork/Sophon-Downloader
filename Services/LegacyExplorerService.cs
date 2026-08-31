using System.IO.Compression;
using System.Security.Cryptography;
using SophonDownloader.Models;
using SharpCompress.Archives;
using SharpCompress.Archives.SevenZip;

namespace SophonDownloader.Services;

public sealed class LegacyExplorerArchive(
    string code, string name, IReadOnlyList<string> urls)
{
    public string Code { get; } = code ?? "";
    public string Name { get; } = name ?? "";
    public IReadOnlyList<string> Urls { get; } = urls ?? [];
}

public sealed class LegacyExplorerDownloadProgress
{
    public int CompletedFiles { get; init; }
    public int TotalFiles { get; init; }
    public long CompletedBytes { get; init; }
    public long TotalBytes { get; init; }
    public string CurrentFile { get; init; } = "";
    public string StatusText { get; init; } = "";
}

public sealed class LegacyExplorerService : IDisposable
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

    private static readonly HttpClient HttpClient = new()
    {
        Timeout = Timeout.InfiniteTimeSpan
    };

    private readonly ManualResetEventSlim _pauseGate = new(true);
    private readonly object _downloadSync = new();
    private CancellationTokenSource? _downloadCts;
    private string _logJobId = "n/a";
    private string _logJobTitle = "n/a";

    public void SetLogContext(string jobId, string jobTitle)
    {
        _logJobId = string.IsNullOrWhiteSpace(jobId) ? "n/a" : jobId;
        _logJobTitle = string.IsNullOrWhiteSpace(jobTitle) ? "n/a" : jobTitle;
    }

    private string JobContext => $"Job={_logJobId}; Title=\"{_logJobTitle}\"";

    public bool IsPaused => !_pauseGate.IsSet;

    public async Task<List<LegacyExplorerNode>> LoadAsync(
        IReadOnlyList<LegacyExplorerArchive> archives,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(archives);

        if (archives.Count == 0)
            throw new InvalidOperationException("No archives were selected.");

        var result = new List<LegacyExplorerNode>();

        foreach (LegacyExplorerArchive archive in archives)
        {
            cancellationToken.ThrowIfCancellationRequested();
            List<string> validUrls = archive.Urls.Where(ManifestResolver.HasValidUrl).ToList();

            if (validUrls.Count == 0) continue;

            Logger.Info($"Loading Legacy archive explorer. " + $"Archive={archive.Name}, Parts={validUrls.Count:N0}");
            Logger.Debug($"Legacy Explorer archive URLs prepared: Archive={archive.Name}, UrlCount={validUrls.Count:N0}. ");
            foreach (string url in validUrls)
                Logger.Debug($"Legacy Explorer archive part URL: Archive={archive.Name}, URL={url}");

            List<LegacyArchivePart> parts =
                await BuildArchivePartsAsync(validUrls, cancellationToken);

            Logger.Info($"Legacy Explorer archive metadata loaded: Archive={archive.Name}, Parts={parts.Count:N0}, TotalBytes={parts.Sum(p => p.Length):N0}.");

            List<LegacyExplorerNode> nodes = await Task.Run(
                () => BuildTree(parts, archive.Code, cancellationToken), cancellationToken);

            MergeTrees(result, nodes);
        }

        SortTree(result);
        return result;
    }

    public async Task<string> CalculateMd5Async(
        IReadOnlyList<LegacyExplorerArchive> archives, LegacyExplorerNode node,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(archives);
        ArgumentNullException.ThrowIfNull(node);

        if (node.IsFolder) return "";

        if (!string.IsNullOrWhiteSpace(node.Md5))
            return node.Md5;

        LegacyExplorerArchive? archive = FindArchiveForNode(archives, node);

        if (archive is null)
            throw new FileNotFoundException($"Unable to determine the archive containing: {node.FullPath}");

        List<string> urls = archive.Urls
            .Where(ManifestResolver.HasValidUrl).ToList();

        if (urls.Count == 0)
            throw new InvalidOperationException($"The archive containing '{node.FullPath}' has no valid download URL.");

        List<LegacyArchivePart> parts =
            await BuildArchivePartsAsync(urls, cancellationToken);

        string md5 = await Task.Run(
            () => CalculateMd5Core(parts, node.FullPath, cancellationToken), cancellationToken);

        node.Md5 = md5;
        return md5;
    }

    public async Task DownloadSelectedAsync(
        IReadOnlyList<LegacyExplorerArchive> archives,
        IEnumerable<LegacyExplorerNode> selectedNodes,
        string destinationDirectory,
        IProgress<LegacyExplorerDownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(archives);
        ArgumentNullException.ThrowIfNull(selectedNodes);

        if (string.IsNullOrWhiteSpace(destinationDirectory))
            throw new ArgumentException("Destination directory cannot be empty.",
                nameof(destinationDirectory));

        List<LegacyExplorerNode> files = selectedNodes
            .Where(n => n is not null && !n.IsFolder && !string.IsNullOrWhiteSpace(n.FullPath))
            .GroupBy(n => n.FullPath, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First()).ToList();

        if (files.Count == 0)
            throw new InvalidOperationException("No files were selected.");

        Directory.CreateDirectory(destinationDirectory);

        Logger.Info($"Legacy Explorer download started. {JobContext}; Files={files.Count:N0}; Destination={destinationDirectory}; TotalBytes={files.Sum(f => f.Size):N0}.");
        foreach (LegacyExplorerNode file in files)
            Logger.Debug($"Legacy Explorer queued file: {file.FullPath} ({file.Size:N0} bytes), ArchiveCode={file.ArchiveCode}");

        using CancellationTokenSource linkedCts =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        lock (_downloadSync)
        {
            if (_downloadCts is not null)
                throw new InvalidOperationException("A Legacy Explorer download is already running.");

            _downloadCts = linkedCts;
            _pauseGate.Set();
        }

        try
        {
            await Task.Run(
                () => DownloadSelectedCore(archives, files, destinationDirectory, progress, linkedCts.Token), linkedCts.Token);
            Logger.Info($"Legacy Explorer download completed successfully. {JobContext}; Files={files.Count:N0}; Destination={destinationDirectory}.");
        }
        catch (OperationCanceledException)
        {
            Logger.Warn($"Legacy Explorer download cancelled. Files={files.Count:N0}, Destination={destinationDirectory}.");
            throw;
        }
        catch (Exception ex)
        {
            Logger.Error(ex, $"Legacy Explorer download failed. Files={files.Count:N0}, Destination={destinationDirectory}.");
            throw;
        }
        finally
        {
            _pauseGate.Set();

            lock (_downloadSync)
            {
                if (ReferenceEquals(_downloadCts, linkedCts))
                    _downloadCts = null;
            }
        }
    }

    public void Pause()
    {
        lock (_downloadSync)
        {
            if (_downloadCts is null) return;
            _pauseGate.Reset();
            Logger.Info($"Legacy Explorer download paused. {JobContext}");
        }
    }

    public void Resume()
    {
        _pauseGate.Set();
        Logger.Info($"Legacy Explorer download resumed. {JobContext}");
    }

    public void Cancel()
    {
        lock (_downloadSync)
        {
            if (_downloadCts is not null)
                Logger.Info($"Legacy Explorer cancellation requested. {JobContext}");
            _downloadCts?.Cancel();
        }

        _pauseGate.Set();
    }

    public void Dispose()
    {
        _pauseGate.Set();
        lock (_downloadSync)
        {
            _downloadCts?.Cancel();
        }
        _downloadCts?.Dispose();
        _pauseGate.Dispose();
        GC.SuppressFinalize(this);
    }

    private void DownloadSelectedCore(
        IReadOnlyList<LegacyExplorerArchive> archives,
        List<LegacyExplorerNode> files,
        string destinationDirectory,
        IProgress<LegacyExplorerDownloadProgress>? progress,
        CancellationToken cancellationToken)
    {
        var archiveStreams = new Dictionary<LegacyExplorerArchive, LegacyArchiveStream>();
        var zipArchives = new Dictionary<LegacyExplorerArchive, ZipArchive>();
        var sevenZipArchives = new Dictionary<LegacyExplorerArchive, IArchive>();

        try
        {
            progress?.Report(new LegacyExplorerDownloadProgress
            {
                CompletedFiles = 0,
                TotalFiles = files.Count,
                CompletedBytes = 0,
                TotalBytes = files.Sum(f => f.Size),
                CurrentFile = "",
                StatusText = "Preparing download..."
            });

            foreach (LegacyExplorerArchive archiveInfo in archives)
            {
                cancellationToken.ThrowIfCancellationRequested();
                _pauseGate.Wait(cancellationToken);

                List<string> urls = archiveInfo.Urls
                    .Where(ManifestResolver.HasValidUrl).ToList();

                if (urls.Count == 0)
                    continue;

                List<LegacyArchivePart> parts =
                    BuildArchivePartsAsync(urls, cancellationToken)
                        .GetAwaiter().GetResult();

                var stream = new LegacyArchiveStream(HttpClient, parts);
                archiveStreams[archiveInfo] = stream;

                ArchiveKind archiveKind = DetectArchiveKind(urls[0]);
                Logger.Info($"Legacy Explorer opening archive: Name={archiveInfo.Name}, Code={archiveInfo.Code}, Kind={archiveKind}, Parts={parts.Count:N0}, Bytes={parts.Sum(p => p.Length):N0}.");

                if (archiveKind == ArchiveKind.SevenZip)
                {
                    sevenZipArchives[archiveInfo] = SevenZipArchive.Open(stream);
                    Logger.Debug($"Legacy Explorer 7z archive opened: {archiveInfo.Name}");
                }
                else
                {
                    zipArchives[archiveInfo] = new ZipArchive(stream, ZipArchiveMode.Read, false);
                    Logger.Debug($"Legacy Explorer ZIP archive opened: {archiveInfo.Name}");
                }
            }

            long totalBytes = files.Sum(f => f.Size);
            long completedBytes = 0;
            int completedFiles = 0;

            foreach (LegacyExplorerNode node in files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                _pauseGate.Wait(cancellationToken);

                LegacyExplorerArchive? archiveInfo =
                    FindArchiveForNode(archives, node);

                if (archiveInfo is null)
                    throw new FileNotFoundException($"Unable to locate archive for: {node.FullPath}");

                string outputPath = Path.GetFullPath(
                    Path.Combine(
                        destinationDirectory,
                        node.FullPath.Replace('/', Path.DirectorySeparatorChar)));

                EnsurePathInsideDirectory(destinationDirectory, outputPath);

                string? outputDirectory = Path.GetDirectoryName(outputPath);

                if (!string.IsNullOrWhiteSpace(outputDirectory))
                    Directory.CreateDirectory(outputDirectory);

                string temporaryPath = $"{outputPath}.download.{Guid.NewGuid():N}.tmp";
                TryDeleteFile(temporaryPath);
                long currentFileBytes = 0;

                progress?.Report(new LegacyExplorerDownloadProgress
                {
                    CompletedFiles = completedFiles,
                    TotalFiles = files.Count,
                    CompletedBytes = completedBytes,
                    TotalBytes = totalBytes,
                    CurrentFile = node.FullPath,
                    StatusText = $"Downloading {completedFiles + 1:N0}/{files.Count:N0}"
                });

                Logger.Info($"Legacy Explorer file started: [{completedFiles + 1:N0}/{files.Count:N0}] {node.FullPath}; Archive={archiveInfo.Name}; Size={node.Size:N0}; Output={outputPath}");

                try
                {
                    Stream input =
                        OpenEntryStream(
                            archiveInfo, node.FullPath, zipArchives, sevenZipArchives)
                            ?? throw new FileNotFoundException($"Archive entry was not found: {node.FullPath}");

                    Logger.Debug($"Legacy Explorer archive entry opened: {node.FullPath}; Archive={archiveInfo.Name}");

                    using (input)
                    using (var output = new FileStream(
                        temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                        1024 * 1024, FileOptions.SequentialScan))
                    {
                        CopyStreamWithProgress(
                            input, output, node, completedFiles, files.Count, completedBytes,
                            totalBytes, progress, ref currentFileBytes, cancellationToken);

                        output.Flush(true);
                    }

                    MoveFileWithRetry(temporaryPath, outputPath, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    Logger.Warn($"Legacy Explorer file cancelled: {node.FullPath}; Downloaded={currentFileBytes:N0} bytes");
                    TryDeleteFile(temporaryPath);
                    throw;
                }
                catch (Exception ex)
                {
                    Logger.Error(ex, $"Legacy Explorer file failed: {node.FullPath}; Archive={archiveInfo.Name}; Downloaded={currentFileBytes:N0} bytes; Output={outputPath}");
                    TryDeleteFile(temporaryPath);
                    throw;
                }

                completedFiles++;
                completedBytes += currentFileBytes;

                if (completedBytes > totalBytes)
                    completedBytes = totalBytes;

                Logger.Info($"Legacy Explorer file completed: [{completedFiles:N0}/{files.Count:N0}] {node.FullPath}; Size={currentFileBytes:N0} bytes; Output={outputPath}");

                progress?.Report(new LegacyExplorerDownloadProgress
                {
                    CompletedFiles = completedFiles,
                    TotalFiles = files.Count,
                    CompletedBytes = completedBytes,
                    TotalBytes = totalBytes,
                    CurrentFile = node.FullPath,
                    StatusText = $"Downloaded {completedFiles:N0}/{files.Count:N0}"
                });
            }
        }
        finally
        {
            foreach (IArchive archive in sevenZipArchives.Values)
            {
                try { archive.Dispose(); }
                catch {}
            }

            foreach (ZipArchive archive in zipArchives.Values)
            {
                try { archive.Dispose(); }
                catch {}
            }

            foreach (LegacyArchiveStream stream in archiveStreams.Values)
            {
                try { stream.Dispose(); }
                catch {}
            }
        }

        Logger.Debug($"Legacy Explorer core loop completed. Files={files.Count:N0}, TotalBytes={files.Sum(f => f.Size):N0}.");
    }

    private void CopyStreamWithProgress(
        Stream input, Stream output, LegacyExplorerNode node,
        int completedFiles, int totalFiles, long completedBytes, long totalBytes,
        IProgress<LegacyExplorerDownloadProgress>? progress, ref long currentFileBytes,
        CancellationToken cancellationToken)
    {
        byte[] buffer = new byte[1024 * 1024];

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _pauseGate.Wait(cancellationToken);

            int read = input.Read(buffer, 0, buffer.Length);

            if (read <= 0) break;

            output.Write(buffer, 0, read);
            currentFileBytes += read;

            long overallBytes = completedBytes + currentFileBytes;

            if (overallBytes > totalBytes)
                overallBytes = totalBytes;

            progress?.Report(new LegacyExplorerDownloadProgress
            {
                CompletedFiles = completedFiles,
                TotalFiles = totalFiles,
                CompletedBytes = overallBytes,
                TotalBytes = totalBytes,
                CurrentFile = node.FullPath,
                StatusText = $"Downloading {completedFiles + 1:N0}/{totalFiles:N0}"
            });
        }
    }

    private static Stream? OpenEntryStream(
        LegacyExplorerArchive archiveInfo, string path,
        IReadOnlyDictionary<LegacyExplorerArchive, ZipArchive> zipArchives,
        IReadOnlyDictionary<LegacyExplorerArchive, IArchive> sevenZipArchives)
    {
        string normalized = NormalizeArchivePath(path);

        if (zipArchives.TryGetValue(archiveInfo, out ZipArchive? zip))
        {
            ZipArchiveEntry? entry = zip.Entries.FirstOrDefault(
                item => NormalizeArchivePath(item.FullName ?? "")
                .Equals(normalized, StringComparison.OrdinalIgnoreCase));

            return entry?.Open();
        }

        if (sevenZipArchives.TryGetValue(archiveInfo, out IArchive? sevenZip))
        {
            IArchiveEntry? entry = sevenZip.Entries.FirstOrDefault(
                item =>
                    NormalizeArchivePath(item.Key ?? "")
                    .Equals(normalized, StringComparison.OrdinalIgnoreCase) && !item.IsDirectory);

            return entry?.OpenEntryStream();
        }

        return null;
    }

    private static LegacyExplorerArchive? FindArchiveForNode(
        IReadOnlyList<LegacyExplorerArchive> archives, LegacyExplorerNode node)
    {
        if (!string.IsNullOrWhiteSpace(node.ArchiveCode))
        {
            LegacyExplorerArchive? exactArchive = archives.FirstOrDefault(
                a => string.Equals(a.Code, node.ArchiveCode, StringComparison.OrdinalIgnoreCase));

            if (exactArchive is not null &&
                exactArchive.Urls.Any(ManifestResolver.HasValidUrl))
                return exactArchive;
        }

        foreach (LegacyExplorerArchive archive in archives)
        {
            if (!archive.Urls.Any(ManifestResolver.HasValidUrl))
                continue;

            if (ArchiveMayContainPath(archive, node.FullPath))
                return archive;
        }

        return null;
    }

    private static bool ArchiveMayContainPath(
        LegacyExplorerArchive archive, string path)
    {
        string lower = path.ToLowerInvariant();
        string archiveName = (archive.Name ?? "").ToLowerInvariant();

        if (archive.Code.Equals("game", StringComparison.OrdinalIgnoreCase))
            return true;

        if (archive.Code.StartsWith("voice:", StringComparison.OrdinalIgnoreCase))
        {
            string voiceCode = archive.Code["voice:".Length..].ToLowerInvariant();

            return lower.Contains(voiceCode, StringComparison.OrdinalIgnoreCase) ||
                   archiveName.Contains(voiceCode, StringComparison.OrdinalIgnoreCase);
        }

        return true;
    }

    private static string CalculateMd5Core(
        IReadOnlyList<LegacyArchivePart> parts,
        string filePath, CancellationToken cancellationToken)
    {
        using var archiveStream = new LegacyArchiveStream(HttpClient, parts);

        ArchiveKind kind = DetectArchiveKind(parts[0].Url);

        using Stream input = OpenArchiveEntryForMd5(archiveStream, kind, filePath, cancellationToken);
        using MD5 md5 = MD5.Create();

        byte[] buffer = new byte[1024 * 1024];
        int read;

        do
        {
            cancellationToken.ThrowIfCancellationRequested();
            read = input.Read(buffer, 0, buffer.Length);

            if (read > 0)
            {
                md5.TransformBlock(buffer, 0, read, null, 0);
            }
        }
        while (read > 0);

        md5.TransformFinalBlock([], 0, 0);

        return Convert.ToHexString(md5.Hash!).ToLowerInvariant();
    }

    private static Stream OpenArchiveEntryForMd5(
        LegacyArchiveStream archiveStream, ArchiveKind kind,
        string filePath, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        string normalized = NormalizeArchivePath(filePath);

        if (kind == ArchiveKind.SevenZip)
        {
            IArchive archive = SevenZipArchive.Open(archiveStream);

            IArchiveEntry? entry = archive.Entries.FirstOrDefault(
                item =>
                    !item.IsDirectory && NormalizeArchivePath(item.Key ?? "")
                        .Equals(normalized, StringComparison.OrdinalIgnoreCase));

            if (entry is null)
            {
                archive.Dispose();

                throw new FileNotFoundException($"Archive entry was not found: {filePath}");
            }

            return new ArchiveEntryWithOwnerStream(archive, entry.OpenEntryStream());
        }

        var zip = new ZipArchive(archiveStream, ZipArchiveMode.Read, false);

        ZipArchiveEntry? zipEntry = zip.Entries.FirstOrDefault(
            item => NormalizeArchivePath(item.FullName ?? "")
            .Equals(normalized, StringComparison.OrdinalIgnoreCase));

        if (zipEntry is null)
        {
            zip.Dispose();
            throw new FileNotFoundException($"Archive entry was not found: {filePath}");
        }

        return new ArchiveEntryWithOwnerStream(zip, zipEntry.Open());
    }

    private sealed class ArchiveEntryWithOwnerStream(
        IDisposable owner, Stream inner) : Stream
    {
        private readonly IDisposable _owner = owner;
        private readonly Stream _inner = inner;

        public override bool CanRead => _inner.CanRead;
        public override bool CanSeek => _inner.CanSeek;
        public override bool CanWrite => false;
        public override long Length => _inner.Length;

        public override long Position
        {
            get => _inner.Position;
            set => _inner.Position = value;
        }

        public override void Flush() => _inner.Flush();

        public override int Read(
            byte[] buffer, int offset, int count) =>
            _inner.Read(buffer, offset, count);

        public override int Read(Span<byte> buffer) =>
            _inner.Read(buffer);

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            _inner.ReadAsync(buffer, cancellationToken);

        public override long Seek(
            long offset, SeekOrigin origin) =>
            _inner.Seek(offset, origin);

        public override void SetLength(long value) =>
            throw new NotSupportedException();

        public override void Write(
            byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                try { _inner.Dispose(); }
                finally { _owner.Dispose(); }
            }

            base.Dispose(disposing);
        }
    }

    private static List<LegacyExplorerNode> BuildTree(
        IReadOnlyList<LegacyArchivePart> parts,
        string archiveCode,
        CancellationToken cancellationToken)
    {
        using var archiveStream = new LegacyArchiveStream(HttpClient, parts);

        ArchiveKind kind = DetectArchiveKind(parts[0].Url);

        return kind == ArchiveKind.SevenZip
            ? BuildSevenZipTree(archiveStream, archiveCode, cancellationToken)
            : BuildZipTree(archiveStream, archiveCode, cancellationToken);
    }

    private static List<LegacyExplorerNode> BuildZipTree(
        LegacyArchiveStream archiveStream, string archiveCode, CancellationToken cancellationToken)
    {
        using var archive = new ZipArchive(archiveStream, ZipArchiveMode.Read, false);

        var root = new List<LegacyExplorerNode>();
        var lookup = new Dictionary<string, LegacyExplorerNode>(StringComparer.OrdinalIgnoreCase);

        foreach (ZipArchiveEntry entry in archive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();

            string normalized = NormalizeArchivePath(entry.FullName ?? "");

            if (string.IsNullOrWhiteSpace(normalized)) continue;

            bool isFolder = (entry.FullName ?? "")
                .EndsWith("/", StringComparison.Ordinal);

            AddEntry(root, lookup, normalized, entry.Length, entry.CompressedLength, entry.CompressedLength == entry.Length
                ? "Stored" : "Deflate", isFolder, archiveCode);
        }

        SortTree(root);
        return root;
    }

    private static List<LegacyExplorerNode> BuildSevenZipTree(
        LegacyArchiveStream archiveStream,
        string archiveCode,
        CancellationToken cancellationToken)
    {
        using IArchive archive = SevenZipArchive.Open(archiveStream);

        var root = new List<LegacyExplorerNode>();
        var lookup = new Dictionary<string, LegacyExplorerNode>(StringComparer.OrdinalIgnoreCase);

        foreach (IArchiveEntry entry in archive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string normalized = NormalizeArchivePath(entry.Key ?? "");

            if (string.IsNullOrWhiteSpace(normalized)) continue;

            AddEntry(root, lookup, normalized, entry.Size, entry.CompressedSize, "7z", entry.IsDirectory, archiveCode);
        }

        SortTree(root);
        return root;
    }

    private static void AddEntry(
        List<LegacyExplorerNode> root, Dictionary<string, LegacyExplorerNode> lookup,
        string path, long size, long compressedSize, string compressionMethod, bool isFolder, string archiveCode)
    {
        string[] segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);

        List<LegacyExplorerNode> current = root;
        string currentPath = "";

        for (int i = 0; i < segments.Length; i++)
        {
            string segment = segments[i];

            currentPath = string.IsNullOrEmpty(currentPath)
                ? segment : $"{currentPath}/{segment}";

            bool isLast = i == segments.Length - 1;

            if (!lookup.TryGetValue(currentPath, out LegacyExplorerNode? node))
            {
                node = new LegacyExplorerNode
                {
                    Name = segment,
                    FullPath = currentPath,
                    ArchiveCode = archiveCode ?? "",
                    IsFolder = !isLast || isFolder
                };

                lookup[currentPath] = node;
                current.Add(node);
            }
            else if (
                string.IsNullOrWhiteSpace(node.ArchiveCode) &&
                !string.IsNullOrWhiteSpace(archiveCode))
            {
                node.ArchiveCode = archiveCode;
            }

            if (isLast)
            {
                node.IsFolder = isFolder;

                if (!isFolder)
                {
                    node.Size = size;
                    node.CompressedSize = compressedSize;
                    node.CompressionMethod = compressionMethod;
                }
            }

            current = node.Children;
        }
    }

    private static void MergeTrees(
        List<LegacyExplorerNode> destination, List<LegacyExplorerNode> source)
    {
        var lookup = new Dictionary<string, LegacyExplorerNode>(
            StringComparer.OrdinalIgnoreCase);

        foreach (LegacyExplorerNode node in destination)
            AddNodesToLookup(node, lookup);

        foreach (LegacyExplorerNode node in source)
            MergeNode(destination, lookup, node);
    }

    private static void AddNodesToLookup(
        LegacyExplorerNode node, Dictionary<string, LegacyExplorerNode> lookup)
    {
        lookup[node.FullPath] = node;

        foreach (LegacyExplorerNode child in node.Children)
            AddNodesToLookup(child, lookup);
    }

    private static void MergeNode(
        List<LegacyExplorerNode> destination, Dictionary<string, LegacyExplorerNode> lookup, LegacyExplorerNode source)
    {
        if (!lookup.TryGetValue(source.FullPath, out LegacyExplorerNode? target))
        {
            target = new LegacyExplorerNode
            {
                Name = source.Name,
                FullPath = source.FullPath,
                ArchiveCode = source.ArchiveCode,
                IsFolder = source.IsFolder,
                Size = source.Size,
                CompressedSize = source.CompressedSize,
                CompressionMethod = source.CompressionMethod,
                Md5 = source.Md5
            };

            lookup[source.FullPath] = target;
            GetParentChildren(destination, source.FullPath).Add(target);
        }
        else
        {
            if (string.IsNullOrWhiteSpace(target.ArchiveCode) &&
                !string.IsNullOrWhiteSpace(source.ArchiveCode))
            {
                target.ArchiveCode = source.ArchiveCode;
            }

            if (!target.IsFolder && target.Size == 0 && source.Size > 0)
            {
                target.Size = source.Size;
            }

            if (!target.IsFolder && target.CompressedSize == 0 && source.CompressedSize > 0)
            {
                target.CompressedSize = source.CompressedSize;
            }

            if (string.IsNullOrWhiteSpace(target.CompressionMethod) &&
                !string.IsNullOrWhiteSpace(source.CompressionMethod))
            {
                target.CompressionMethod = source.CompressionMethod;
            }
        }

        foreach (LegacyExplorerNode child in source.Children)
            MergeNode(target.Children, lookup, child);
    }

    private static List<LegacyExplorerNode> GetParentChildren(
        List<LegacyExplorerNode> roots, string fullPath)
    {
        int separator = fullPath.LastIndexOf('/');
        if (separator < 0) return roots;
        string parentPath = fullPath[..separator];
        return FindNode(roots, parentPath)?.Children ?? roots;
    }

    private static LegacyExplorerNode? FindNode(
        IEnumerable<LegacyExplorerNode> nodes, string path)
    {
        foreach (LegacyExplorerNode node in nodes)
        {
            if (node.FullPath.Equals(path, StringComparison.OrdinalIgnoreCase))
            { return node; }

            LegacyExplorerNode? found = FindNode(node.Children, path);

            if (found is not null) return found;
        }

        return null;
    }

    private static void SortTree(List<LegacyExplorerNode> nodes)
    {
        nodes.Sort((a, b) =>
        {
            if (a.IsFolder != b.IsFolder)
                return a.IsFolder ? -1 : 1;

            return StringComparer.OrdinalIgnoreCase.Compare(a.Name, b.Name);
        });

        foreach (LegacyExplorerNode node in nodes)
            SortTree(node.Children);
    }

    private enum ArchiveKind
    {
        Zip, SevenZip
    }

    private static ArchiveKind DetectArchiveKind(string url)
    {
        string path = (url ?? "").Split('?', 2)[0].ToLowerInvariant();

        return path.EndsWith(".7z", StringComparison.Ordinal) ||
            path.Contains(".7z.", StringComparison.Ordinal)
            ? ArchiveKind.SevenZip
            : ArchiveKind.Zip;
    }

    private static async Task<List<LegacyArchivePart>> BuildArchivePartsAsync(
        IReadOnlyList<string> urls, CancellationToken cancellationToken)
    {
        var result = new List<LegacyArchivePart>(urls.Count);

        foreach (string url in urls)
        {
            cancellationToken.ThrowIfCancellationRequested();
            long length = await GetRemoteLengthAsync(url, cancellationToken);

            if (length <= 0)
                throw new InvalidDataException($"Unable to determine archive size: {url}");

            result.Add(new LegacyArchivePart(url, length));
        }

        return result;
    }

    private static async Task<long> GetRemoteLengthAsync(
        string url, CancellationToken cancellationToken)
    {
        try
        {
            using var headRequest = new HttpRequestMessage(HttpMethod.Head, url);

            using HttpResponseMessage headResponse =
                await HttpClient.SendAsync(headRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

            if (headResponse.IsSuccessStatusCode && headResponse.Content.Headers.ContentLength
                is long contentLength && contentLength > 0)
            {
                return contentLength;
            }

            if (headResponse.StatusCode != HttpStatusCode.MethodNotAllowed &&
                !headResponse.IsSuccessStatusCode)
            {
                headResponse.EnsureSuccessStatusCode();
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Logger.Debug(ex, $"Legacy Explorer HEAD size probe failed; falling back to range request: {url}");
        }

        using var rangeRequest = new HttpRequestMessage(HttpMethod.Get, url);
        rangeRequest.Headers.Range = new RangeHeaderValue(0, 0);

        using HttpResponseMessage rangeResponse =
            await HttpClient.SendAsync(rangeRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

        if (rangeResponse.Content.Headers.ContentRange?.Length
            is long rangeLength && rangeLength > 0)
        {
            return rangeLength;
        }

        if (rangeResponse.Content.Headers.ContentLength
            is long responseLength && responseLength > 0)
        {
            return responseLength;
        }

        throw new InvalidDataException($"The remote archive did not provide its size: {url}");
    }

    private static string NormalizeArchivePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "";
        path = path.Replace('\\', '/');

        var segments = new List<string>();

        foreach (string rawSegment in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            string segment = rawSegment.Trim();

            if (string.IsNullOrEmpty(segment) || segment == ".") continue;

            if (segment == "..")
            {
                if (segments.Count == 0)
                    throw new InvalidDataException("Archive path escapes its root: {path}");

                segments.RemoveAt(segments.Count - 1); continue;
            }

            segments.Add(segment);
        }

        return string.Join("/", segments);
    }

    private static void EnsurePathInsideDirectory(
        string directory, string target)
    {
        string root = Path.GetFullPath(directory)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;

        string fullTarget = Path.GetFullPath(target);

        if (!fullTarget.StartsWith(root, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"Archive entry escapes the destination directory: {target}");
        }
    }

    private static void MoveFileWithRetry(
        string source, string destination, CancellationToken cancellationToken)
    {
        const int attempts = 8;
        IOException? lastException = null;

        for (int attempt = 1; attempt <= attempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                File.Move(source, destination, true);
                return;
            }
            catch (IOException ex)
            {
                lastException = ex;
            }
            catch (UnauthorizedAccessException ex)
            {
                lastException = new IOException(ex.Message, ex);
            }

            if (attempt < attempts)
                Thread.Sleep(250);
        }

        throw new IOException($"Unable to replace the destination file because it is being used or locked by another process:\n{destination}", lastException);
    }

    private static void TryDeleteFile(string path)
    {
        if (!File.Exists(path)) return;
        try { File.Delete(path); }
        catch {}
    }
}
