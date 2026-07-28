using Sophon.Helper;
using Sophon;
using System;
using System.Buffers;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using TaskExtensions = Sophon.Helper.TaskExtensions;
using ZstdStream = ZstdNet.DecompressionStream;

namespace Sophon
{
    public partial class SophonAsset
    {
        private int _countChunksDownload;
        private int _currentChunksDownloadPos;
        private int _currentChunksDownloadQueue;

        public async ValueTask DownloadDiffChunksAsync(
            HttpClient client,
            string chunkDirOutput,
            ParallelOptions parallelOptions = null,
            DelegateWriteStreamInfo writeInfo = null,
            DelegateWriteDownloadInfo reportInfo = null,
            DelegateDownloadAssetComplete onComplete = null,
            bool forceVerification = false)
        {
            this.EnsureOrThrowChunksState();
            this.EnsureOrThrowOutputDirectoryExistence(chunkDirOutput);

            _currentChunksDownloadPos = 0;
            _countChunksDownload = Chunks.Length;

            parallelOptions ??= new ParallelOptions
            {
                CancellationToken = CancellationToken.None,
                MaxDegreeOfParallelism = Math.Min(8, Environment.ProcessorCount)
            };

            try
            {
                await Parallel.ForEachAsync(Chunks, parallelOptions, async (chunk, token) =>
                {
                    if (chunk.ChunkOldOffset > -1) return;
                    await PerformWriteDiffChunksThreadAsync(client, chunkDirOutput, chunk, writeInfo, reportInfo, DownloadSpeedLimiter, forceVerification, token).ConfigureAwait(false);
                }).ConfigureAwait(false);
            }
            catch (AggregateException ex)
            {
                throw ex.Flatten().InnerExceptions.First();
            }

            onComplete?.Invoke(this);
        }

        private async ValueTask PerformWriteDiffChunksThreadAsync(
            HttpClient client,
            string chunkDirOutput,
            SophonChunk chunk,
            DelegateWriteStreamInfo writeInfo,
            DelegateWriteDownloadInfo reportInfo,
            SophonDownloadSpeedLimiter limiter,
            bool forceVerification,
            CancellationToken token)
        {
            string chunkName = chunk.ChunkName;
            string chunkPath = Path.Combine(chunkDirOutput, chunk.GetChunkStagingFilenameHash(this));
            string verifiedPath = chunkPath + ".verified";
            FileInfo chunkFile = new FileInfo(chunkPath).UnassignReadOnlyFromFileInfo();

            try
            {
                Interlocked.Increment(ref _currentChunksDownloadPos);
                Interlocked.Increment(ref _currentChunksDownloadQueue);

                using FileStream fs = chunkFile.Open(FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite);
                long chunkSize = chunk.ChunkSize;

                bool isMismatch = fs.Length != chunkSize;
                bool isVerified = File.Exists(verifiedPath) && !isMismatch;

                if (forceVerification || !isVerified)
                {
                    isMismatch = !(chunkName.TryGetChunkXxh64Hash(out var hash) &&
                    await chunk.CheckChunkXxh64HashAsync(AssetName, fs, hash, true, token).ConfigureAwait(false));

                    if (File.Exists(verifiedPath)) File.Delete(verifiedPath);
                }

                if (!isMismatch)
                {
                    writeInfo?.Invoke(chunkSize);
                    reportInfo?.Invoke(chunkSize, 0);
                    EnsureVerified(verifiedPath);
                    return;
                }

                fs.Position = 0;
                await InnerWriteChunkCopyAsync(client, fs, chunk, token, writeInfo, reportInfo, limiter).ConfigureAwait(false);
                EnsureVerified(verifiedPath);
            }
            finally
            {
                Interlocked.Decrement(ref _currentChunksDownloadQueue);
            }
        }

        private async ValueTask InnerWriteChunkCopyAsync(
            HttpClient client,
            Stream outStream,
            SophonChunk chunk,
            CancellationToken token,
            DelegateWriteStreamInfo writeInfo,
            DelegateWriteDownloadInfo reportInfo,
            SophonDownloadSpeedLimiter limiter)
        {
            const int retryCount = TaskExtensions.DefaultRetryAttempt;
            int currentRetry = 0;
            long currentWriteOffset = 0;
            long written = 0;
            long chunkSize = chunk.ChunkSize;
            long chunkOffset = chunk.ChunkOffset;
            long chunkSizeDecompressed = chunk.ChunkSizeDecompressed;
            string chunkName = chunk.ChunkName;

            if (OperatingSystem.IsWindows() && outStream is FileStream fs)
            {
                fs.Lock(chunkOffset, chunkSizeDecompressed);
                this.PushLogDebug($"Locked stream from 0x{chunkOffset:x8} for length 0x{chunkSizeDecompressed:x8} ({chunkName})");
            }

            long limitBase = limiter?.InitialRequestedSpeed ?? -1;
            Stopwatch sw = Stopwatch.StartNew();
            double maxBps = 0, bitUnit = 0;
            CalculateBps();

            if (limiter != null)
            {
                limiter.CurrentChunkProcessingChangedEvent += (_, _) => CalculateBps();
                limiter.DownloadSpeedChangedEvent += (_, e) => { limitBase = e == 0 ? -1 : e; CalculateBps(); };
            }

            while (true)
            {
                HttpResponseMessage resp = null;
                Stream httpStream = null;
                byte[] buffer = ArrayPool<byte>.Shared.Rent(BufferSize);

                try
                {
                    using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(TaskExtensions.DefaultTimeoutSec));
                    using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(token, timeoutCts.Token);

                    outStream.SetLength(chunkSize);
                    outStream.Position = 0;

                    resp = await client.GetChunkAndIfAltAsync(chunkName, SophonChunksInfo, SophonChunksInfoAlt, linkedCts.Token).ConfigureAwait(false);
                    httpStream = await resp.EnsureSuccessStatusCode().Content.ReadAsStreamAsync(linkedCts.Token).ConfigureAwait(false);

                    int read;
                    while ((read = await httpStream.ReadAsync(buffer.AsMemory(0, buffer.Length), linkedCts.Token).ConfigureAwait(false)) > 0)
                    {
                        await outStream.WriteAsync(buffer.AsMemory(0, read), linkedCts.Token).ConfigureAwait(false);
                        currentWriteOffset += read;
                        writeInfo?.Invoke(read);
                        reportInfo?.Invoke(read, read);
                        written += read;
                        currentRetry = 0;
                        await ThrottleAsync();
                    }

                    outStream.Position = 0;
                    var checkStream = outStream;

                    bool isVerified = chunkName.TryGetChunkXxh64Hash(out var hash)
                        ? await chunk.CheckChunkXxh64HashAsync(AssetName, checkStream, hash, true, linkedCts.Token).ConfigureAwait(false)
                        : await chunk.CheckChunkMd5HashAsync(SophonChunksInfo.IsUseCompression ? new ZstdStream(checkStream) : checkStream, true, linkedCts.Token).ConfigureAwait(false);

                    if (!isVerified)
                    {
                        writeInfo?.Invoke(-chunkSizeDecompressed);
                        reportInfo?.Invoke(-chunkSizeDecompressed, 0);
                        this.PushLogWarning($"Data corrupted. Retrying chunk {chunkName}...");
                        continue;
                    }

                    return;
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    if (++currentRetry <= retryCount)
                    {
                        writeInfo?.Invoke(-currentWriteOffset);
                        reportInfo?.Invoke(-currentWriteOffset, 0);
                        currentWriteOffset = 0;
                        this.PushLogWarning($"Error downloading chunk {chunkName}, retrying...\n{ex}");
                        await Task.Delay(TimeSpan.FromSeconds(1), token).ConfigureAwait(false);
                        continue;
                    }

                    this.PushLogError($"Failed downloading chunk {chunkName}\n{ex}");
                    throw;
                }
                finally
                {
                    resp?.Dispose();
                    if (httpStream != null) await httpStream.DisposeAsync().ConfigureAwait(false);
                    ArrayPool<byte>.Shared.Return(buffer);
                    limiter?.DecrementChunkProcessedCount();
                }
            }

            void CalculateBps()
            {
                limitBase = limitBase <= 0 ? -1 : Math.Max(64 << 10, limitBase);
                double threadCount = Math.Clamp(limiter?.CurrentChunkProcessing ?? 1, 1, 16384);
                maxBps = limitBase / threadCount;
                bitUnit = 940 - (threadCount - 2) / (16d - 2d) * 400;
            }

            async Task ThrottleAsync()
            {
                if (maxBps <= 0 || written <= 0) return;

                long ms = sw.ElapsedMilliseconds;
                if (ms <= 0) return;

                double bps = written * bitUnit / ms;
                if (bps <= maxBps) return;

                double sleepMs = written * bitUnit / maxBps - ms;
                if (sleepMs > 1)
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(sleepMs), token).ConfigureAwait(false);
                    sw.Restart();
                    written = 0;
                }
            }
        }

        private static void EnsureVerified(string path)
        {
            if (!File.Exists(path)) File.Create(path).Dispose();
        }
    }
}
