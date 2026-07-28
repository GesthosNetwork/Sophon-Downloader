using Sophon.Helper;
using Sophon.Infos;
using Sophon;
using System;
using System.Buffers;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using System.Threading.Tasks.Dataflow;
using TaskExtensions = Sophon.Helper.TaskExtensions;
using ZstdStream = ZstdNet.DecompressionStream;

namespace Sophon
{
    public partial class SophonAsset
    {
        private enum SourceStreamType { Internet, CachedLocal, OldReference }

        internal const int BufferSize = 256 << 10;
        private const int ZstdBufferSize = 0;

        public string AssetName {get; internal set;}
        public long AssetSize {get; internal set;}
        public string AssetHash {get; internal set;}
        public bool IsDirectory {get; internal set;}
        public bool IsHasPatch {get; internal set;}
        public SophonChunk[] Chunks {get; internal set;}
        internal SophonDownloadSpeedLimiter DownloadSpeedLimiter {get; set;}
        internal SophonChunksInfo SophonChunksInfo {get; set;}
        internal SophonChunksInfo SophonChunksInfoAlt {get; set;}

        public async ValueTask WriteToStreamAsync(HttpClient client, Stream outStream, DelegateWriteStreamInfo writeInfo = null, DelegateWriteDownloadInfo reportInfo = null, DelegateDownloadAssetComplete onComplete = null, CancellationToken token = default)
        {
            this.EnsureOrThrowChunksState();
            this.EnsureOrThrowStreamState(outStream);
            if (outStream.Length > AssetSize) outStream.SetLength(AssetSize);

            foreach (var chunk in Chunks)
                await PerformWriteStreamThreadAsync(client, null, SourceStreamType.Internet, outStream, chunk, token, writeInfo, reportInfo, DownloadSpeedLimiter);

            onComplete?.Invoke(this);
        }

        public async ValueTask WriteToStreamAsync(HttpClient client, Func<Stream> outStreamFunc, ParallelOptions parallelOptions = null, DelegateWriteStreamInfo writeInfo = null, DelegateWriteDownloadInfo reportInfo = null, DelegateDownloadAssetComplete onComplete = null)
        {
            this.EnsureOrThrowChunksState();
            using var initStream = outStreamFunc();
            this.EnsureOrThrowStreamState(initStream);
            if (initStream.Length > AssetSize) initStream.SetLength(AssetSize);

            parallelOptions ??= new ParallelOptions
            {
                CancellationToken = default,
                MaxDegreeOfParallelism = Math.Min(8, Environment.ProcessorCount)
            };

            try
            {
                using var cts = new CancellationTokenSource();
                using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cts.Token, parallelOptions.CancellationToken);
                var block = new ActionBlock<SophonChunk>(
                    async chunk =>
                    {
                        using var stream = outStreamFunc();
                        await PerformWriteStreamThreadAsync(client, null, SourceStreamType.Internet, stream, chunk, linkedCts.Token, writeInfo, reportInfo, DownloadSpeedLimiter);
                    },
                    new ExecutionDataflowBlockOptions
                    {
                        MaxDegreeOfParallelism = parallelOptions.MaxDegreeOfParallelism,
                        CancellationToken = linkedCts.Token
                    });

                foreach (var chunk in Chunks)
                    await block.SendAsync(chunk, linkedCts.Token);

                block.Complete();
                await block.Completion;
            }
            catch (AggregateException ex)
            {
                throw ex.Flatten().InnerExceptions.First();
            }

            onComplete?.Invoke(this);
        }

        private async ValueTask PerformWriteStreamThreadAsync(HttpClient client, Stream source, SourceStreamType type, Stream dest, SophonChunk chunk, CancellationToken token, DelegateWriteStreamInfo writeInfo, DelegateWriteDownloadInfo reportInfo, SophonDownloadSpeedLimiter limiter)
        {
            var (offset, size, hash, name, oldOffset) = (chunk.ChunkOffset, chunk.ChunkSizeDecompressed, chunk.ChunkHashDecompressed, chunk.ChunkName, chunk.ChunkOldOffset);
            bool skip = dest.Length >= offset + size && await chunk.CheckChunkMd5HashAsync(dest, false, token);

            if (skip)
            {
                writeInfo?.Invoke(size);
                reportInfo?.Invoke(oldOffset != -1 ? 0 : size, 0);
                return;
            }

            await InnerWriteStreamToAsync(client, source, type, dest, chunk, token, writeInfo, reportInfo, limiter);
        }

        private async ValueTask InnerWriteStreamToAsync(HttpClient client, Stream source, SourceStreamType type, Stream dest, SophonChunk chunk, CancellationToken token, DelegateWriteStreamInfo writeInfo, DelegateWriteDownloadInfo reportInfo, SophonDownloadSpeedLimiter limiter)
        {
            if ((type != SourceStreamType.Internet && source == null) || (type == SourceStreamType.OldReference && chunk.ChunkOldOffset < 0))
                throw new InvalidOperationException("Invalid source stream or reference offset.");

            const int retryMax = TaskExtensions.DefaultRetryAttempt;
            int retry = 0;
            long offset = chunk.ChunkOffset, size = chunk.ChunkSizeDecompressed, written = 0;
            string name = chunk.ChunkName;

            if (OperatingSystem.IsWindows() && dest is FileStream fs)
            {
                fs.Lock(offset, size);
                this.PushLogDebug($"Locked stream 0x{offset:x8} -> 0x{size:x8} ({name})");
            }

            long limit = limiter?.InitialRequestedSpeed ?? -1;
            Stopwatch sw = Stopwatch.StartNew();
            double maxBps = 0, unit = 0;
            void CalcBps()
            {
                limit = limit <= 0 ? -1 : Math.Max(64 << 10, limit);
                var t = Math.Clamp(limiter?.CurrentChunkProcessing ?? 1, 1, 16384);
                maxBps = limit / t;
                unit = 940 - (t - 2) / 14d * 400;
            }

            if (limiter != null)
            {
                limiter.CurrentChunkProcessingChangedEvent += (_, _) => CalcBps();
                limiter.DownloadSpeedChangedEvent += (_, e) => { limit = e == 0 ? -1 : e; CalcBps(); };
            }
            CalcBps();

            while (true)
            {
                HttpResponseMessage resp = null;
                Stream net = null;
                using MD5 md5 = MD5.Create();
                byte[] buf = ArrayPool<byte>.Shared.Rent(BufferSize);
                try
                {
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(TaskExtensions.DefaultTimeoutSec));
                    using var cts = CancellationTokenSource.CreateLinkedTokenSource(token, timeout.Token);
                    dest.Position = offset;

                    if (type == SourceStreamType.Internet)
                    {
                        limiter?.IncrementChunkProcessedCount();
                        resp = await client.GetChunkAndIfAltAsync(name, SophonChunksInfo, SophonChunksInfoAlt, cts.Token);
                        net = await resp.EnsureSuccessStatusCode().Content.ReadAsStreamAsync(cts.Token);
                        source = SophonChunksInfo.IsUseCompression ? new ZstdStream(net, ZstdBufferSize) : net;
                    }
                    else if (type == SourceStreamType.CachedLocal && SophonChunksInfo.IsUseCompression)
                        source = new ZstdStream(source, ZstdBufferSize);
                    else if (type == SourceStreamType.OldReference)
                        source.Position = chunk.ChunkOldOffset;

                    long remain = size, current = 0;
                    while (remain > 0)
                    {
                        int read = await source.ReadAsync(buf.AsMemory(0, (int)Math.Min(remain, buf.Length)), cts.Token);
                        if (read == 0) throw new InvalidDataException($"Corrupted chunk {name}. Remain: {remain}");
                        await dest.WriteAsync(buf.AsMemory(0, read), cts.Token);
                        md5.TransformBlock(buf, 0, read, buf, 0);
                        remain -= read;
                        current += read;
                        writeInfo?.Invoke(read);
                        if (type != SourceStreamType.OldReference)
                            reportInfo?.Invoke(read, type == SourceStreamType.Internet ? read : 0);
                        if (type == SourceStreamType.Internet) { written += read; await Throttle(); }
                    }

                    md5.TransformFinalBlock(buf, 0, 0);
                    if (!md5.Hash.AsSpan().SequenceEqual(chunk.ChunkHashDecompressed))
                    {
                        writeInfo?.Invoke(-current);
                        if (type != SourceStreamType.OldReference) reportInfo?.Invoke(-current, 0);
                        this.PushLogWarning($"Corrupt source {type} at {name}. Retrying...");
                        type = SourceStreamType.Internet;
                        continue;
                    }

                    return;
                }
                catch (Exception ex) when (++retry <= retryMax)
                {
                    writeInfo?.Invoke(-size);
                    if (type != SourceStreamType.OldReference) reportInfo?.Invoke(-size, 0);
                    this.PushLogWarning($"Retry {retry}/{retryMax} failed for {name}: {ex.Message}");
                    await Task.Delay(1000, token);
                    type = SourceStreamType.Internet;
                    continue;
                }
                finally
                {
                    if (type == SourceStreamType.Internet) limiter?.DecrementChunkProcessedCount();
                    if (net != null) await net.DisposeAsync();
                    if (source != null && source != net) await source.DisposeAsync();
                    ArrayPool<byte>.Shared.Return(buf);
                }
            }

            async Task Throttle()
            {
                if (maxBps <= 0 || written <= 0) return;
                long ms = sw.ElapsedMilliseconds;
                if (ms <= 0) return;
                double bps = written * unit / ms;
                if (bps > maxBps)
                {
                    double sleep = written * unit / maxBps - ms;
                    if (sleep > 1)
                    {
                        await Task.Delay(TimeSpan.FromMilliseconds(sleep), token);
                        sw.Restart();
                        written = 0;
                    }
                }
            }
        }
    }
}
