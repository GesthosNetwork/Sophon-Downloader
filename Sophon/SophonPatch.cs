using Sophon.Helper;
using Sophon.Infos;
using Sophon.Protos;
using Sophon;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Threading;
using ZstdNet;

namespace Sophon
{
    public static partial class SophonPatch
    {
        private static readonly object DummyInstance = new();

        public static async IAsyncEnumerable<SophonPatchAsset> EnumerateUpdateAsync(HttpClient httpClient,
            SophonChunkManifestInfoPair infoPair,
            string versionTagUpdateFrom,
            string downloadOverUrl,
            SophonDownloadSpeedLimiter downloadSpeedLimiter = null,
            [EnumeratorCancellation] CancellationToken token = default)
        {
            await foreach (var asset in EnumerateUpdateAsync(httpClient,
                infoPair.ManifestInfo,
                infoPair.ChunksInfo,
                versionTagUpdateFrom,
                downloadOverUrl,
                downloadSpeedLimiter,
                token))
            {
                yield return asset;
            }
        }

        public static async IAsyncEnumerable<SophonPatchAsset> EnumerateUpdateAsync(HttpClient httpClient,
            SophonManifestInfo manifestInfo,
            SophonChunksInfo chunksInfo,
            string versionTagUpdateFrom,
            string downloadOverUrl,
            SophonDownloadSpeedLimiter downloadSpeedLimiter = null,
            [EnumeratorCancellation] CancellationToken token = default)
        {
            if (!DllUtils.IsLibraryExist(DllUtils.DllName))
                throw new DllNotFoundException("libzstd is not found!");

            if (string.IsNullOrEmpty(downloadOverUrl))
                throw new ArgumentNullException(nameof(downloadOverUrl), "DownloadOver URL is not defined!");

            if (string.IsNullOrEmpty(versionTagUpdateFrom))
                throw new ArgumentNullException(nameof(versionTagUpdateFrom), "Version tag is not defined!");

            ActionTimeoutTaskCallback<SophonPatchProto> manifestFromProtoTaskCallback = async (innerToken) =>
                await httpClient.ReadProtoFromManifestInfo(manifestInfo, SophonPatchProto.Parser, innerToken);

            SophonPatchProto patchManifestProto = await TaskExtensions
                .WaitForRetryAsync<SophonPatchProto>(
                () => manifestFromProtoTaskCallback, TaskExtensions.DefaultTimeoutSec, null, null, null, token);

            SophonChunksInfo chunksInfoDownloadOver = chunksInfo.CopyWithNewBaseUrl(downloadOverUrl);

            foreach (SophonPatchAssetProperty patchAssetProperty in patchManifestProto.PatchAssets)
            {
                var property = patchAssetProperty;
                SophonPatchAssetInfo patchAssetInfo = property.AssetInfos
                    .FirstOrDefault(x => x.VersionTag.Equals(versionTagUpdateFrom, StringComparison.OrdinalIgnoreCase));

                if (patchAssetInfo == null)
                {
                    yield return new SophonPatchAsset
                    {
                        PatchInfo = chunksInfoDownloadOver,
                        TargetFileHash = property.AssetHashMd5,
                        TargetFileSize = property.AssetSize,
                        TargetFilePath = property.AssetName,
                        TargetFileDownloadOverBaseUrl = downloadOverUrl,
                        PatchMethod = SophonPatchMethod.DownloadOver
                    };
                    continue;
                }

                var chunk = patchAssetInfo.Chunk;
                if (string.IsNullOrEmpty(chunk.OriginalFileName))
                {
                    yield return new SophonPatchAsset
                    {
                        PatchInfo = chunksInfo,
                        PatchNameSource = chunk.PatchName,
                        PatchHash = chunk.PatchMd5,
                        PatchOffset = chunk.PatchOffset,
                        PatchSize = chunk.PatchSize,
                        PatchChunkLength = chunk.PatchLength,
                        TargetFilePath = property.AssetName,
                        TargetFileHash = property.AssetHashMd5,
                        TargetFileSize = property.AssetSize,
                        TargetFileDownloadOverBaseUrl = downloadOverUrl,
                        PatchMethod = SophonPatchMethod.CopyOver,
                    };
                    continue;
                }

                yield return new SophonPatchAsset
                {
                    PatchInfo = chunksInfo,
                    PatchNameSource = chunk.PatchName,
                    PatchHash = chunk.PatchMd5,
                    PatchOffset = chunk.PatchOffset,
                    PatchSize = chunk.PatchSize,
                    PatchChunkLength = chunk.PatchLength,
                    TargetFilePath = property.AssetName,
                    TargetFileHash = property.AssetHashMd5,
                    TargetFileSize = property.AssetSize,
                    TargetFileDownloadOverBaseUrl = downloadOverUrl,
                    OriginalFilePath = chunk.OriginalFileName,
                    OriginalFileSize = chunk.OriginalFileLength,
                    OriginalFileHash = chunk.OriginalFileMd5,
                    PatchMethod = SophonPatchMethod.Patch,
                };
            }

            foreach (SophonUnusedAssetFile unusedAssetFile in patchManifestProto
                .UnusedAssets
                .SelectMany(x => x.AssetInfos.FirstOrDefault()?.Assets)
                .Where(x => x != null))
            {
                yield return new SophonPatchAsset
                {
                    OriginalFileHash = unusedAssetFile.FileMd5,
                    OriginalFileSize = unusedAssetFile.FileSize,
                    OriginalFilePath = unusedAssetFile.FileName,
                    PatchMethod = SophonPatchMethod.Remove
                };
            }
        }

        public static IEnumerable<SophonPatchAsset> EnsureOnlyGetDedupPatchAssets(this IEnumerable<SophonPatchAsset> patchAssetEnumerable)
        {
            HashSet<string> processedAsset = [];
            foreach (SophonPatchAsset asset in patchAssetEnumerable
                .Where(x => !string.IsNullOrEmpty(x.PatchNameSource) && processedAsset.Add(x.PatchNameSource)))
            {
                yield return asset;
            }
        }

        public static void RemovePatches(this IEnumerable<SophonPatchAsset> patchAssetEnumerable, string patchOutputDir)
        {
            foreach (SophonPatchAsset asset in patchAssetEnumerable
                .EnsureOnlyGetDedupPatchAssets())
            {
                string patchFilePath = Path.Combine(patchOutputDir, asset.PatchNameSource);

                try
                {
                    FileInfo fileInfo = new FileInfo(patchFilePath);
                    if (fileInfo.Exists)
                    {
                        fileInfo.IsReadOnly = false;
                        fileInfo.Refresh();
                        fileInfo.Delete();
                        DummyInstance.PushLogDebug($"Removed patch file: {patchFilePath}");
                    }
                }
                catch (Exception ex)
                {
                    DummyInstance.PushLogError($"Failed while trying to remove patch file: {patchFilePath} | {ex}");
                }
            }
        }
    }
}
