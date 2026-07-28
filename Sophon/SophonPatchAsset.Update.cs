using Sophon.Helper;
using Sophon.Infos;
using Sophon;
using SharpHDiffPatch.Core;
using System;
using System.Buffers;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

#nullable enable
namespace Sophon
{
    public partial class SophonPatchAsset
    {
        public async Task ApplyPatchUpdateAsync(HttpClient client,
            string inputDir,
            string patchOutputDir,
            bool removeOldAssets = true,
            Action<long>? downloadReadDelegate = null,
            Action<long>? diskWriteDelegate = null,
            SophonDownloadSpeedLimiter? downloadSpeedLimiter = null,
            CancellationToken token = default)
        {
            bool isRemove = SophonPatchMethod.Remove == PatchMethod;
            bool isCopyOver = SophonPatchMethod.CopyOver == PatchMethod;
            bool isPatchHDiff = SophonPatchMethod.Patch == PatchMethod;
            string sourceFileNameToCheck = PatchMethod switch
            {
                SophonPatchMethod.Remove => OriginalFilePath,
                SophonPatchMethod.DownloadOver => TargetFilePath,
                SophonPatchMethod.Patch => OriginalFilePath,
                SophonPatchMethod.CopyOver => TargetFilePath,
                _ => throw new InvalidOperationException($"Unsupported patch method: {PatchMethod}")
            };
            string sourceFilePathToCheck = Path.Combine(inputDir, sourceFileNameToCheck);

            if (isRemove)
            {
                if (!removeOldAssets)
                    return;

                FileInfo removableAssetFileInfo = new FileInfo(sourceFilePathToCheck);
                PerformPatchAssetRemove(removableAssetFileInfo);
                return;
            }

            if (PatchMethod is SophonPatchMethod.DownloadOver or
                               SophonPatchMethod.CopyOver or
                               SophonPatchMethod.Patch &&
                await IsFilePatched(inputDir, token))
            {
                diskWriteDelegate?.Invoke(TargetFileSize);
                return;
            }

            if (!isCopyOver)
            {
                string sourceFileHashString = PatchMethod switch
                {
                    SophonPatchMethod.Remove => OriginalFileHash,
                    SophonPatchMethod.DownloadOver => TargetFileHash,
                    SophonPatchMethod.Patch => OriginalFileHash,
                    _ => throw new InvalidOperationException($"Unsupported patch method: {PatchMethod}")
                };

                long sourceFileSizeToCheck = PatchMethod switch
                {
                    SophonPatchMethod.Remove => OriginalFileSize,
                    SophonPatchMethod.DownloadOver => TargetFileSize,
                    SophonPatchMethod.Patch => OriginalFileSize,
                    _ => throw new InvalidOperationException($"Unsupported patch method: {PatchMethod}")
                };

                SophonChunk sourceFileToCheckAsChunk = new SophonChunk
                {
                    ChunkHashDecompressed = Extension.HexToBytes(sourceFileHashString.AsSpan()),
                    ChunkName = sourceFileNameToCheck,
                    ChunkOffset = 0,
                    ChunkOldOffset = 0,
                    ChunkSize = sourceFileSizeToCheck,
                    ChunkSizeDecompressed = sourceFileSizeToCheck
                };

                FileInfo sourceFileInfoToCheck = new FileInfo(sourceFilePathToCheck);

                bool isNeedCompleteDownload = !(sourceFileInfoToCheck is { Exists: true } &&
                    sourceFileInfoToCheck.Length == sourceFileSizeToCheck);
                FileStream? sourceFileStreamToCheck = null;
                try
                {
                    if (!isNeedCompleteDownload)
                    {
                        sourceFileStreamToCheck = sourceFileInfoToCheck.Open(FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite);
                        isNeedCompleteDownload = !(sourceFileToCheckAsChunk.ChunkHashDecompressed.Length != 8 ?
                            await sourceFileToCheckAsChunk.CheckChunkMd5HashAsync(sourceFileStreamToCheck, true, token) :

                            await sourceFileToCheckAsChunk.CheckChunkXxh64HashAsync(OriginalFilePath,
                                sourceFileStreamToCheck, sourceFileToCheckAsChunk.ChunkHashDecompressed,
                                true, token));

                        if (isNeedCompleteDownload)
                        {
                            sourceFileStreamToCheck.Dispose();
                            PerformPatchAssetRemove(sourceFileInfoToCheck);
                        }
                        else
                        {
                            if (!isPatchHDiff)
                            {
                                diskWriteDelegate?.Invoke(TargetFileSize);
                                return;
                            }
                        }
                    }

                    if (isNeedCompleteDownload)
                    {
                        PatchMethod = SophonPatchMethod.DownloadOver;
                    }
                }
                finally
                {
                    if (sourceFileStreamToCheck != null)
                        await sourceFileStreamToCheck.DisposeAsync();
                }
            }

            Task writeDelegateTask = PatchMethod switch
            {
                SophonPatchMethod.DownloadOver => PerformPatchDownloadOver(client,
                    inputDir,
                    downloadReadDelegate,
                    diskWriteDelegate,
                    downloadSpeedLimiter,
                    token),
                SophonPatchMethod.CopyOver => PerformPatchCopyOver(inputDir,
                    patchOutputDir,                                   
                    diskWriteDelegate,
                    token),
                SophonPatchMethod.Patch => PerformPatchHDiff(inputDir,
                    patchOutputDir,
                    diskWriteDelegate,
                    token),
                _ => throw new InvalidOperationException($"Invalid operation while performing patch: {PatchMethod}")
            };

            await writeDelegateTask;
        }

        private async Task PerformPatchDownloadOver(HttpClient client,
            string inputDir,
            Action<long>? downloadReadDelegate,
            Action<long>? diskWriteDelegate,
            SophonDownloadSpeedLimiter? downloadSpeedLimiter,
            CancellationToken token)
        {
            string targetFilePath = Path.Combine(inputDir, TargetFilePath);
            FileInfo targetFileInfo = new FileInfo(targetFilePath);
            string targetFilePathTemp = targetFilePath + ".temp";
            FileInfo targetFileInfoTemp = new FileInfo(targetFilePathTemp);

            if (targetFileInfoTemp.Exists)
                targetFileInfoTemp.IsReadOnly = false;

            targetFileInfoTemp.Directory?.Create();
            FileStream targetFileStreamTemp = targetFileInfoTemp.Open(FileMode.Create, FileAccess.ReadWrite, FileShare.ReadWrite);
            targetFileInfoTemp.Refresh();
            try
            {
                SophonChunksInfo targetChunkInfo = PatchInfo.CopyWithNewBaseUrl(TargetFileDownloadOverBaseUrl);
                SophonChunk targetFileChunk = this.SophonPatchAssetAsChunk(false, true);

                await InnerWriteChunkCopyAsync(client,
                    targetFileStreamTemp,
                    targetFileChunk,
                    targetChunkInfo,
                    targetChunkInfo,
                    writeInfoDelegate: x =>
                    {
                        diskWriteDelegate?.Invoke(x);
                    },
                    downloadInfoDelegate: (read, write) =>
                    {
                        downloadReadDelegate?.Invoke(read);
                    },
                    downloadSpeedLimiter,
                    token: token);
            }
            finally
            {
                targetFileStreamTemp.Dispose();
                if (targetFileInfo.Exists)
                {
                    targetFileInfo.IsReadOnly = false;
                    targetFileInfo.Refresh();
                    targetFileInfo.Delete();
                }

                targetFileInfoTemp.MoveTo(targetFilePath);
            }
        }

        private void PerformPatchAssetRemove(FileInfo originalFileInfo)
        {
            try
            {
                if (!originalFileInfo.Exists)
                    return;

                originalFileInfo.IsReadOnly = false;
                originalFileInfo.Refresh();
                originalFileInfo.Delete();

                this.PushLogDebug($"[Method: Remove] Removing asset file: {OriginalFilePath} is completed!");
            }
            catch (Exception ex)
            {
                this.PushLogError($"An error has occurred while deleting old asset: {originalFileInfo.FullName} | {ex}");
            }
        }

        private async Task PerformPatchCopyOver(string inputDir,
            string patchOutputDir,
            Action<long>? diskWriteDelegate,
            CancellationToken token)
        {
            PatchTargetProperty patchTargetProperty = PatchTargetProperty.Create(patchOutputDir, PatchNameSource, inputDir, TargetFilePath, PatchOffset, PatchChunkLength, true);

            bool isUseCopyToStrategy = PatchChunkLength <= 1 << 20;

            string logMessage = $"[Method: CopyOver][Strategy: {(isUseCopyToStrategy ? "DirectCopyTo" : "BufferedCopy")}] Writing target file: {TargetFilePath} with offset: {PatchOffset:x8} and length: {PatchChunkLength:x8} from {PatchNameSource} is completed!";

            try
            {
                if (patchTargetProperty.TargetFileTempStream == null)
                {
                    ArgumentNullException.ThrowIfNull(patchTargetProperty.TargetFileTempStream,
                    nameof(patchTargetProperty.TargetFileTempStream));
                }
                if (patchTargetProperty.PatchChunkStream == null)
                {
                    ArgumentNullException.ThrowIfNull(patchTargetProperty.PatchChunkStream,
					nameof(patchTargetProperty.PatchChunkStream));
                }

                if (isUseCopyToStrategy)
                {
                    await patchTargetProperty.PatchChunkStream.CopyToAsync(patchTargetProperty.TargetFileTempStream, token);
                    diskWriteDelegate?.Invoke(PatchChunkLength);
                    return;
                }

                byte[] buffer = ArrayPool<byte>.Shared.Rent(16 << 10);

                try
                {
                    int read;
                    while ((read = await patchTargetProperty.PatchChunkStream.ReadAsync(buffer, token)) > 0)
                    {
                        await patchTargetProperty.TargetFileTempStream.WriteAsync(buffer.AsMemory(0, read), token);
                        diskWriteDelegate?.Invoke(read);
                    }
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(buffer);
                }
            }
            finally
            {
                this.PushLogDebug(logMessage);
                patchTargetProperty.Dispose();
            }
        }

        private async Task PerformPatchHDiff(string inputDir,
            string patchOutputDir,
            Action<long>? diskWriteDelegate,
            CancellationToken token)
        {
            PatchTargetProperty patchTargetProperty = PatchTargetProperty.Create(patchOutputDir, PatchNameSource, inputDir, TargetFilePath, PatchOffset, PatchChunkLength, false);
            string logMessage = $"[Method: PatchHDiff] Writing target file: {TargetFilePath} with offset: {PatchOffset:x8} and length: {PatchChunkLength:x8} from {PatchNameSource} is completed!";
            string patchPath = patchTargetProperty.PatchFilePath;
            string targetTempPath = patchTargetProperty.TargetFileTempInfo.FullName;

            try
            {
                await Task.Factory
                    .StartNew(Impl, token, token, TaskCreationOptions.DenyChildAttach, TaskScheduler.Default)
                    .ConfigureAwait(false);
                this.PushLogDebug(logMessage);
            }
            finally
            {
                patchTargetProperty.Dispose();
            }

            return;

            void Impl(object? ctx)
            {
                HDiffPatch patcher = new HDiffPatch();
                try
                {
                    patcher.Initialize(CreateChunkStream);

                    string inputPath = Path.Combine(inputDir, OriginalFilePath);
                    patcher.Patch(inputPath, targetTempPath, true, diskWriteDelegate, (CancellationToken)ctx!, false, true);
                }
                catch (Exception ex)
                {
                    this.PushLogDebug($"[Method: PatchHDiff] An error occurred while trying to perform patching on: {OriginalFilePath} -> {TargetFilePath}\r\n{ex}");
                }
            }

            ChunkStream CreateChunkStream()
            {
                FileStream fileStream = File.Open(patchPath, FileMode.Open, FileAccess.Read, FileShare.Read);
                ChunkStream chunkStream = new ChunkStream(fileStream, PatchOffset, PatchOffset + PatchChunkLength, true);

                return chunkStream;
            }
        }

        private async Task<bool> IsFilePatched(string inputPath, CancellationToken token)
        {
            string targetFilePath = Path.Combine(inputPath, TargetFilePath);
            FileInfo targetFileInfo = new FileInfo(targetFilePath);

            bool isSizeMatched = targetFileInfo.Exists && TargetFileSize == targetFileInfo.Length;
            if (!isSizeMatched)
            {
                return false;
            }

            SophonChunk checkByHashChunk = new SophonChunk
            {
                ChunkHashDecompressed = Extension.HexToBytes(TargetFileHash.AsSpan()),
                ChunkName = TargetFilePath,
                ChunkSize = TargetFileSize,
                ChunkSizeDecompressed = TargetFileSize,
                ChunkOffset = 0,
                ChunkOldOffset = 0,
            };

            using FileStream targetFileStream = targetFileInfo.Open(FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
            bool isHashMatched = checkByHashChunk.ChunkHashDecompressed.Length == 8 ?
                await checkByHashChunk.CheckChunkXxh64HashAsync(TargetFilePath,
                    targetFileStream,
                    checkByHashChunk.ChunkHashDecompressed,
                    true,
                    token) :
                await checkByHashChunk.CheckChunkMd5HashAsync(targetFileStream,
                    true,
                    token);

            return isHashMatched;
        }
    }

    internal class PatchTargetProperty : IDisposable
    {
        private FileInfo TargetFileInfo { get; }
        public FileInfo TargetFileTempInfo { get; }
        public FileStream? TargetFileTempStream { get; }
        public string PatchFilePath { get; }
        private FileStream? PatchFileStream { get; }
        public ChunkStream? PatchChunkStream { get; }

        private PatchTargetProperty(string patchOutputDir, string patchNameSource, string inputDir, string targetFilePath, long patchOffset, long patchLength, bool createStream)
        {
            PatchFilePath = Path.Combine(patchOutputDir, patchNameSource);
            targetFilePath = Path.Combine(inputDir, targetFilePath);
            string targetFileTempPath = targetFilePath + ".temp";

            TargetFileInfo = new FileInfo(targetFilePath);
            TargetFileTempInfo = new FileInfo(targetFileTempPath);
            TargetFileTempInfo.Directory?.Create();

            if (TargetFileTempInfo.Exists)
            {
                TargetFileTempInfo.IsReadOnly = false;
                TargetFileTempInfo.Refresh();
            }

            if (!File.Exists(PatchFilePath))
                throw new FileNotFoundException($"Required patch file: {PatchFilePath} is not found!");

            if (!createStream)
                return;

            long patchChunkEnd = patchOffset + patchLength;
            TargetFileTempStream = TargetFileTempInfo.Open(FileMode.Create, FileAccess.Write, FileShare.Write);
            PatchFileStream = File.Open(PatchFilePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            PatchChunkStream = new ChunkStream(PatchFileStream, patchOffset, patchChunkEnd);
        }

        public static PatchTargetProperty Create(string patchOutputDir, string patchNameSource, string inputDir, string targetFilePath, long patchOffset, long patchLength, bool createTempStream)
            => new(patchOutputDir, patchNameSource, inputDir, targetFilePath, patchOffset, patchLength, createTempStream);

        public void Dispose()
        {
            PatchChunkStream?.Dispose();
            PatchFileStream?.Dispose();
            TargetFileTempStream?.Dispose();

            TargetFileTempInfo.Refresh();
            if (TargetFileInfo.Exists)
            {
                TargetFileInfo.IsReadOnly = false;
                TargetFileInfo.Delete();
            }

            TargetFileTempInfo.MoveTo(TargetFileInfo.FullName);
        }
    }
}
