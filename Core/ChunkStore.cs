using SophonDownloader.Utilities;

namespace SophonDownloader.Core;

public sealed class ChunkStore
{
    public string ApplicationDirectory { get; }
    public string ChunkDirectory { get; }

    public ChunkStore() : this(Utility.GetApplicationDirectory()) { }

    public ChunkStore(string? baseDirectory)
    {
        if (string.IsNullOrWhiteSpace(baseDirectory)) baseDirectory = Utility.GetApplicationDirectory();

        ApplicationDirectory = Path.GetFullPath(baseDirectory);
        Directory.CreateDirectory(ApplicationDirectory);
        ChunkDirectory = Path.Combine(ApplicationDirectory, "ldiff");
    }

    public string GetChunkPath(string chunkId)
    {
        if (string.IsNullOrWhiteSpace(chunkId))
            throw new ArgumentException("Chunk ID cannot be empty.", nameof(chunkId));

        ValidateChunkId(chunkId);
        return Path.Combine(ChunkDirectory, chunkId);
    }

    public bool HasChunk(string chunkId, long expectedSize)
    {
        string path = GetChunkPath(chunkId);
        if (!File.Exists(path)) return false;

        try { return new FileInfo(path).Length == expectedSize; }
        catch { return false; }
    }

    public FileStream OpenChunk(string chunkId)
    {
        string path = GetChunkPath(chunkId);
        if (!File.Exists(path)) throw new FileNotFoundException($"Chunk does not exist: {path}", path);

        return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
    }

    public void DeleteChunk(string chunkId)
    {
        string path = GetChunkPath(chunkId);
        TryDelete(path);
        TryDelete(path + ".tmp");
    }

    public void DeleteChunks(IEnumerable<string> chunkIds)
    {
        foreach (string chunkId in chunkIds.Distinct(StringComparer.OrdinalIgnoreCase))
            DeleteChunk(chunkId);

        TryDeleteEmptyChunkDirectory();
    }

    public void TryDeleteEmptyChunkDirectory()
    {
        if (!Directory.Exists(ChunkDirectory)) return;

        try
        {
            if (!Directory.EnumerateFileSystemEntries(ChunkDirectory).Any())
                Directory.Delete(ChunkDirectory, false);
        }
        catch { }
    }

    private static void TryDelete(string path)
    {
        if (!File.Exists(path)) return;

        try { File.Delete(path); }
        catch { }
    }

    private static void ValidateChunkId(string chunkId)
    {
        if (chunkId.Contains(Path.DirectorySeparatorChar) ||
            chunkId.Contains(Path.AltDirectorySeparatorChar) ||
            chunkId == "." ||
            chunkId == ".." ||
            chunkId.Contains("..", StringComparison.Ordinal))
        {
            throw new InvalidDataException($"Invalid chunk ID: {chunkId}");
        }
    }
}
