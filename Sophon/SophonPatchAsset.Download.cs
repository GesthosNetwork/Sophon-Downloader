using Sophon.Helper;
using Sophon.Infos;
using Sophon;
using System;
using System.Buffers;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using TaskExtensions = Sophon.Helper.TaskExtensions;
using ZstdStream = ZstdNet.DecompressionStream;

namespace Sophon
{
    public enum SophonPatchMethod
    {
        CopyOver, DownloadOver, Patch, Remove
    }

    public partial class SophonPatchAsset
    {
        internal const int BufferSize = 256 << 10;
        public SophonChunksInfo PatchInfo {get; set;}
        public SophonPatchMethod PatchMethod {get; set;}
        public string PatchNameSource {get; set;}
        public string PatchHash {get; set;}
        public long PatchOffset {get; set;}
        public long PatchSize {get; set;}
        public long PatchChunkLength {get; set;}
        public string OriginalFilePath {get; set;}
        public string OriginalFileHash {get; set;}
        public long OriginalFileSize {get; set;}
        public string TargetFilePath {get; set;}
        public string TargetFileDownloadOverBaseUrl {get; set;}
        public string TargetFileHash {get; set;}
        public long TargetFileSize {get; set;}

        #nullable enable
        public async Task DownloadPatchAsync(HttpClient client, string patchOutputDir, bool forceVerification = false, Action<long>? downloadReadDelegate = null, SophonDownloadSpeedLimiter? downloadSpeedLimiter = null, CancellationToken token = default)
        {
            if (PatchMethod is SophonPatchMethod.Remove or SophonPatchMethod.DownloadOver) return;

            string patchNameHashed = PatchNameSource;
            string patchFilePathHashed = Path.Combine(patchOutputDir, patchNameHashed);
            FileInfo patchFilePathHashedFileInfo = new FileInfo(patchFilePathHashed).UnassignReadOnlyFromFileInfo();
            patchFilePathHashedFileInfo.Directory?.Create();

            if (!PatchNameSource.TryGetChunkXxh64Hash(out byte[] patchHash))
                patchHash = Extension.HexToBytes(PatchHash.AsSpan());

            SophonChunk patchAsChunk = new SophonChunk
            {
                ChunkHashDecompressed = patchHash,
                ChunkName = PatchNameSource,
                ChunkOffset = 0,
                ChunkOldOffset = 0,
                ChunkSize = PatchSize,
                ChunkSizeDecompressed = PatchSize
            };

            using FileStream fileStream = patchFilePathHashedFileInfo.Open(FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite);

            bool isPatchUnmatched = fileStream.Length != PatchSize;
            if (forceVerification)
            {
                isPatchUnmatched = patchHash.Length > 8
                    ? !await patchAsChunk.CheckChunkMd5HashAsync(fileStream, true, token)
                    : !await patchAsChunk.CheckChunkXxh64HashAsync(PatchNameSource, fileStream, patchHash, true, token);

                if (isPatchUnmatched)
                {
                    fileStream.Position = 0;
                    fileStream.SetLength(0);
                }
            }

            if (!isPatchUnmatched)
            {
                downloadReadDelegate?.Invoke(PatchSize);
                return;
            }

            fileStream.Position = 0;
            await InnerWriteChunkCopyAsync(client, fileStream, patchAsChunk, PatchInfo, PatchInfo, null, (_, y) => downloadReadDelegate?.Invoke(y), downloadSpeedLimiter, token);
        }

        private async Task InnerWriteChunkCopyAsync(HttpClient client, Stream outStream, SophonChunk chunk, SophonChunksInfo currentSophonChunkInfo, SophonChunksInfo altSophonChunkInfo, DelegateWriteStreamInfo? writeInfoDelegate, DelegateWriteDownloadInfo? downloadInfoDelegate, SophonDownloadSpeedLimiter? downloadSpeedLimiter, CancellationToken token)
        {
            const int retryCount = TaskExtensions.DefaultRetryAttempt;
            int currentRetry = 0;
            long currentWriteOffset = 0;

            #if !NOSTREAMLOCK
            if (outStream is FileStream fs)
                fs.Lock(chunk.ChunkOffset, chunk.ChunkSizeDecompressed);
            #endif

            long written = 0;
            long thisInstanceDownloadLimitBase = downloadSpeedLimiter?.InitialRequestedSpeed ?? -1;
            Stopwatch currentStopwatch = Stopwatch.StartNew();

            double maximumBytesPerSecond;
            double bitPerUnit;

            CalculateBps();

            if (downloadSpeedLimiter != null)
            {
                downloadSpeedLimiter.CurrentChunkProcessingChangedEvent += UpdateChunkRangesCountEvent;
                downloadSpeedLimiter.DownloadSpeedChangedEvent += DownloadClientDownloadSpeedLimitChanged;
            }

            while (true)
            {
                bool allowDispose = false;
                HttpResponseMessage? httpResponseMessage = null;
                Stream? httpResponseStream = null;
                Stream? sourceStream = null;
                byte[] buffer = ArrayPool<byte>.Shared.Rent(BufferSize);

                try
                {
                    CancellationTokenSource innerTimeoutToken = new CancellationTokenSource(TimeSpan.FromSeconds(TaskExtensions.DefaultTimeoutSec));
                    CancellationTokenSource cooperatedToken = CancellationTokenSource.CreateLinkedTokenSource(token, innerTimeoutToken.Token);

                    outStream.Position = 0;
                    httpResponseMessage = await client.GetChunkAndIfAltAsync(chunk.ChunkName, currentSophonChunkInfo, altSophonChunkInfo, cooperatedToken.Token);
                    httpResponseStream = await httpResponseMessage.EnsureSuccessStatusCode().Content.ReadAsStreamAsync(cooperatedToken.Token);

                    sourceStream = httpResponseStream;
                    downloadSpeedLimiter?.IncrementChunkProcessedCount();
                    int read;

                    while ((read = await sourceStream.ReadAsync(buffer.AsMemory(0, BufferSize), cooperatedToken.Token)) > 0)
                    {
                        await outStream.WriteAsync(buffer.AsMemory(0, read), cooperatedToken.Token);
                        currentWriteOffset += read;
                        writeInfoDelegate?.Invoke(read);
                        downloadInfoDelegate?.Invoke(read, read);
                        written += read;
                        currentRetry = 0;

                        innerTimeoutToken.Dispose();
                        cooperatedToken.Dispose();
                        innerTimeoutToken = new CancellationTokenSource(TimeSpan.FromSeconds(TaskExtensions.DefaultTimeoutSec));
                        cooperatedToken = CancellationTokenSource.CreateLinkedTokenSource(token, innerTimeoutToken.Token);

                        await ThrottleAsync();
                    }

                    outStream.Position = 0;
                    Stream checkHashStream = outStream;

                    bool isHashVerified;
                    if (chunk.ChunkName.TryGetChunkXxh64Hash(out byte[] outHash))
                    {
                        isHashVerified = await chunk.CheckChunkXxh64HashAsync(TargetFilePath, checkHashStream, outHash, true, cooperatedToken.Token);
                    }
                    else
                    {
                        if (PatchInfo.IsUseCompression)
                            checkHashStream = new ZstdStream(checkHashStream);

                        isHashVerified = await chunk.CheckChunkMd5HashAsync(checkHashStream, true, cooperatedToken.Token);
                    }

                    if (!isHashVerified)
                    {
                        writeInfoDelegate?.Invoke(-chunk.ChunkSizeDecompressed);
                        downloadInfoDelegate?.Invoke(-chunk.ChunkSizeDecompressed, 0);
                        continue;
                    }

                    return;
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    allowDispose = true;
                    throw;
                }
                catch (Exception)
                {
                    if (currentRetry < retryCount)
                    {
                        writeInfoDelegate?.Invoke(-currentWriteOffset);
                        downloadInfoDelegate?.Invoke(-currentWriteOffset, 0);
                        currentWriteOffset = 0;
                        currentRetry++;
                        await Task.Delay(TimeSpan.FromSeconds(1), token);
                        continue;
                    }

                    allowDispose = true;
                    throw;
                }
                finally
                {
                    if (allowDispose)
                    {
                        httpResponseMessage?.Dispose();
                        if (httpResponseStream != null) await httpResponseStream.DisposeAsync();
                        if (sourceStream != null) await sourceStream.DisposeAsync();
                    }

                    downloadSpeedLimiter?.DecrementChunkProcessedCount();
                    ArrayPool<byte>.Shared.Return(buffer);
                }
            }

            void CalculateBps()
            {
                if (thisInstanceDownloadLimitBase <= 0)
                    thisInstanceDownloadLimitBase = -1;
                else
                    thisInstanceDownloadLimitBase = Math.Max(64 << 10, thisInstanceDownloadLimitBase);

                double threadNum = Math.Clamp(downloadSpeedLimiter?.CurrentChunkProcessing ?? 1, 1, 16 << 10);
                maximumBytesPerSecond = thisInstanceDownloadLimitBase / threadNum;
                bitPerUnit = 940 - (threadNum - 2) / (16 - 2) * 400;
            }

            void DownloadClientDownloadSpeedLimitChanged(object? sender, long e)
            {
                thisInstanceDownloadLimitBase = e == 0 ? -1 : e;
                CalculateBps();
            }

            void UpdateChunkRangesCountEvent(object? sender, int e)
            {
                CalculateBps();
            }

            async Task ThrottleAsync()
            {
                if (maximumBytesPerSecond <= 0 || written <= 0) return;

                long elapsedMilliseconds = currentStopwatch.ElapsedMilliseconds;
                if (elapsedMilliseconds > 0)
                {
                    double bps = written * bitPerUnit / elapsedMilliseconds;
                    if (bps > maximumBytesPerSecond)
                    {
                        double wakeElapsed = written * bitPerUnit / maximumBytesPerSecond;
                        double toSleep = wakeElapsed - elapsedMilliseconds;
                        if (toSleep > 1)
                        {
                            await Task.Delay(TimeSpan.FromMilliseconds(toSleep), token);
                            currentStopwatch.Restart();
                            written = 0;
                        }
                    }
                }
            }
        }
    }
}
