using Sophon.Helper;
using Sophon;
using System;
using System.Buffers;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace Sophon
{
    public partial class SophonAsset
    {
        public async ValueTask WriteUpdateAsync(HttpClient client,
            string oldInputDir,
            string newOutputDir,
            string chunkDir,
            bool removeChunkAfterApply = false,
            DelegateWriteStreamInfo writeInfoDelegate = null,
            DelegateWriteDownloadInfo downloadInfoDelegate = null,
            DelegateDownloadAssetComplete downloadCompleteDelegate = null,
            CancellationToken token = default)
        {
            const string tempExt = "_tempUpdate";

            this.EnsureOrThrowChunksState();
            this.EnsureOrThrowOutputDirectoryExistence(oldInputDir);
            this.EnsureOrThrowOutputDirectoryExistence(newOutputDir);
            this.EnsureOrThrowOutputDirectoryExistence(chunkDir);

            var assetName = AssetName;
            var oldPath = Path.Combine(oldInputDir, assetName);
            var newPath = Path.Combine(newOutputDir, assetName);
            var newTempPath = newPath + tempExt;
            var newDir = Path.GetDirectoryName(newPath);

            if (!Directory.Exists(newDir) && newDir != null)
                Directory.CreateDirectory(newDir);

            var oldInfo = new FileInfo(oldPath).UnassignReadOnlyFromFileInfo();
            var newInfo = new FileInfo(newPath).UnassignReadOnlyFromFileInfo();
            var newTempInfo = new FileInfo(newTempPath).UnassignReadOnlyFromFileInfo();

            foreach (var chunk in Chunks)
            {
                await InnerWriteUpdateAsync(client, chunkDir, writeInfoDelegate, downloadInfoDelegate, DownloadSpeedLimiter,
                    oldInfo, newTempInfo, chunk, removeChunkAfterApply, token);
            }

            GC.Collect();
            GC.WaitForPendingFinalizers();

            if (newTempInfo.FullName != newInfo.FullName)
                newTempInfo.Refresh();

            downloadCompleteDelegate?.Invoke(this);
        }

        public async ValueTask WriteUpdateAsync(HttpClient client,
            string oldInputDir,
            string newOutputDir,
            string chunkDir,
            bool removeChunkAfterApply = false,
            ParallelOptions parallelOptions = null,
            DelegateWriteStreamInfo writeInfoDelegate = null,
            DelegateWriteDownloadInfo downloadInfoDelegate = null,
            DelegateDownloadAssetComplete downloadCompleteDelegate = null)
        {
            const string tempExt = "_tempUpdate";

            this.EnsureOrThrowChunksState();
            this.EnsureOrThrowOutputDirectoryExistence(oldInputDir);
            this.EnsureOrThrowOutputDirectoryExistence(newOutputDir);
            this.EnsureOrThrowOutputDirectoryExistence(chunkDir);

            var assetName = AssetName;
            var oldPath = Path.Combine(oldInputDir, assetName);
            var newPath = Path.Combine(newOutputDir, assetName);
            var newTempPath = newPath + tempExt;
            var newDir = Path.GetDirectoryName(newPath);

            if (!Directory.Exists(newDir) && newDir != null)
                Directory.CreateDirectory(newDir);

            parallelOptions ??= new ParallelOptions
            {
                CancellationToken = default,
                MaxDegreeOfParallelism = Math.Min(8, Environment.ProcessorCount)
            };

            var oldInfo = new FileInfo(oldPath).UnassignReadOnlyFromFileInfo();
            var newInfo = new FileInfo(newPath).UnassignReadOnlyFromFileInfo();
            var newTempInfo = new FileInfo(newTempPath).UnassignReadOnlyFromFileInfo();

            if (newInfo.Exists && newInfo.Length == AssetSize)
                newTempInfo = newInfo;

            await Parallel.ForEachAsync(Chunks, parallelOptions, async (chunk, ct) =>
            {
                await InnerWriteUpdateAsync(client, chunkDir, writeInfoDelegate, downloadInfoDelegate,
                    DownloadSpeedLimiter, oldInfo, newTempInfo, chunk, removeChunkAfterApply, ct);
            });

            newTempInfo.Refresh();
            newInfo.Refresh();

            if (newTempInfo.FullName != newInfo.FullName && newTempInfo.Exists)
            {
                newInfo.Directory?.Create();
                newTempInfo.MoveTo(newInfo.FullName, true);
            }

            downloadCompleteDelegate?.Invoke(this);
        }

        private async Task InnerWriteUpdateAsync(HttpClient client, string chunkDir,
            DelegateWriteStreamInfo writeInfoDelegate, DelegateWriteDownloadInfo downloadInfoDelegate,
            SophonDownloadSpeedLimiter downloadSpeedLimiter, FileInfo oldInfo,
            FileInfo newInfo, SophonChunk chunk, bool removeChunkAfterApply, CancellationToken token)
        {
            Stream input = null;
            Stream output = null;
            var streamType = SourceStreamType.Internet;

            try
            {
                if (chunk.ChunkOldOffset != -1 && oldInfo.Exists && oldInfo.Length >= chunk.ChunkOldOffset + chunk.ChunkSizeDecompressed)
                {
                    input = oldInfo.Open(FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
                    streamType = SourceStreamType.OldReference;
                }
                else
                {
                    var name = chunk.GetChunkStagingFilenameHash(this);
                    var path = Path.Combine(chunkDir, name);
                    var verifiedPath = path + ".verified";
                    var info = new FileInfo(path).UnassignReadOnlyFromFileInfo();

                    if (info.Exists && info.Length != chunk.ChunkSize)
                        info.Delete();
                    else if (info.Exists)
                    {
                        input = new FileStream(info.FullName, FileMode.Open, FileAccess.Read,
                            FileShare.Read, 4096, removeChunkAfterApply ? FileOptions.DeleteOnClose : FileOptions.None);
                        streamType = SourceStreamType.CachedLocal;
                        if (File.Exists(verifiedPath)) File.Delete(verifiedPath);
                    }
                }

                output = newInfo.Open(FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite);
                await PerformWriteStreamThreadAsync(client, input, streamType, output, chunk, token,
                    writeInfoDelegate, downloadInfoDelegate, downloadSpeedLimiter);
                await output.DisposeAsync();
            }
            finally
            {
                if (input != null) await input.DisposeAsync();
            }
        }

        public async ValueTask<long> GetDownloadedPreloadSize(string chunkDir,
            string outputDir, bool useCompressedSize, CancellationToken token = default)
        {
            var fullPath = Path.Combine(outputDir, AssetName);
            var info = new FileInfo(fullPath).UnassignReadOnlyFromFileInfo();
            bool exists = info.Exists;
            long downloaded = exists ? info.Length : 0L;

            long GetLength(SophonChunk chunk)
            {
                var name = chunk.GetChunkStagingFilenameHash(this);
                var path = Path.Combine(chunkDir, name);
                var file = new FileInfo(path).UnassignReadOnlyFromFileInfo();
                var size = useCompressedSize ? chunk.ChunkSize : chunk.ChunkSizeDecompressed;

                if (exists && downloaded == AssetSize && !file.Exists) return 0L;
                return file.Exists && file.Length <= chunk.ChunkSize ? size : 0L;
            }

            if (Chunks == null || Chunks.Length == 0) return 0L;
            if (Chunks.Length < 512) return Chunks.Select(GetLength).Sum();

            var buffer = ArrayPool<long>.Shared.Rent(Chunks.Length);
            try
            {
                await Task.Run(() => Parallel.For(0, Chunks.Length, i => buffer[i] = GetLength(Chunks[i])), token);
                return buffer.Sum();
            }
            finally
            {
                ArrayPool<long>.Shared.Return(buffer);
            }
        }
    }
}
