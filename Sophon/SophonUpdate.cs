using Google.Protobuf.Collections;
using Sophon.Helper;
using Sophon.Infos;
using Sophon.Protos;
using Sophon;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using TaskExtensions = Sophon.Helper.TaskExtensions;
using ZstdNet;

namespace Sophon
{
    public static class SophonUpdate
    {
        private static readonly object DummyInstance = new();

        public static async IAsyncEnumerable<SophonAsset> EnumerateUpdateAsync(
            HttpClient httpClient,
            SophonChunkManifestInfoPair infoPairOld,
            SophonChunkManifestInfoPair infoPairNew,
            bool removeChunkAfterApply,
            SophonDownloadSpeedLimiter downloadSpeedLimiter = null,
            [EnumeratorCancellation] CancellationToken token = default)
        {
            await foreach (SophonAsset asset in EnumerateUpdateAsync(
                httpClient,
                infoPairOld.ManifestInfo,
                infoPairOld.ChunksInfo,
                infoPairNew.ManifestInfo,
                infoPairNew.ChunksInfo,
                removeChunkAfterApply,
                downloadSpeedLimiter,
                token))
            {
                yield return asset;
            }
        }

        public static async IAsyncEnumerable<SophonAsset> EnumerateUpdateAsync(
            HttpClient httpClient,
            SophonManifestInfo manifestInfoFrom,
            SophonChunksInfo chunksInfoFrom,
            SophonManifestInfo manifestInfoTo,
            SophonChunksInfo chunksInfoTo,
            bool removeChunkAfterApply,
            SophonDownloadSpeedLimiter downloadSpeedLimiter = null,
            [EnumeratorCancellation] CancellationToken token = default)
        {
            if (!DllUtils.IsLibraryExist(DllUtils.DllName))
                throw new DllNotFoundException("libzstd is not found!");

            var manifestFromProtoTaskCallback = new ActionTimeoutTaskCallback<SophonManifestProto>(
                async innerToken => await httpClient.ReadProtoFromManifestInfo(manifestInfoFrom, SophonManifestProto.Parser, innerToken)
            );

            var manifestToProtoTaskCallback = new ActionTimeoutTaskCallback<SophonManifestProto>(
                async innerToken => await httpClient.ReadProtoFromManifestInfo(manifestInfoTo, SophonManifestProto.Parser, innerToken)
            );

            SophonManifestProto manifestFromProto = await TaskExtensions.WaitForRetryAsync(
                () => manifestFromProtoTaskCallback,
                TaskExtensions.DefaultTimeoutSec,
                null,
                null,
                null,
                token);

            SophonManifestProto manifestToProto = await TaskExtensions.WaitForRetryAsync(
                () => manifestToProtoTaskCallback,
                TaskExtensions.DefaultTimeoutSec,
                null,
                null,
                null,
                token);

            var oldAssetNameIdx = GetProtoAssetHashKvpSet(manifestFromProto, x => x.AssetName);

            var oldAssetNameHashSet = manifestFromProto.Assets.Select(x => x.AssetName).ToHashSet();
            var newAssetNameHashSet = manifestToProto.Assets.Select(x => x.AssetName).ToHashSet();

            foreach (var newAssetProperty in manifestToProto.Assets.Where(x =>
            {
                bool isOldExist = oldAssetNameHashSet.Contains(x.AssetName);
                bool isNewExist = newAssetNameHashSet.Contains(x.AssetName);
                return (!isOldExist && isNewExist) || isOldExist;
            }))
            {
                yield return GetPatchedTargetAsset(
                    oldAssetNameIdx,
                    manifestFromProto,
                    newAssetProperty,
                    chunksInfoFrom,
                    chunksInfoTo,
                    downloadSpeedLimiter);
            }
        }

        public static async ValueTask<long> GetCalculatedDiffSizeAsync(
            this IAsyncEnumerable<SophonAsset> sophonAssetsEnumerable,
            bool isGetDecompressSize = true,
            CancellationToken token = default)
        {
            long sizeDiff = 0;

            await foreach (SophonAsset asset in sophonAssetsEnumerable.WithCancellation(token))
            {
                if (asset.IsDirectory) continue;

                foreach (var chunk in asset.Chunks)
                {
                    if (chunk.ChunkOldOffset != -1) continue;
                    sizeDiff += isGetDecompressSize ? chunk.ChunkSizeDecompressed : chunk.ChunkSize;
                }
            }

            return sizeDiff;
        }

        public static long GetCalculatedDiffSize(
            this IEnumerable<SophonAsset> sophonAssetsEnumerable,
            bool isGetDecompressSize = true)
        {
            long sizeDiff = 0;

            foreach (SophonAsset asset in sophonAssetsEnumerable)
            {
                if (asset.IsDirectory) continue;

                foreach (var chunk in asset.Chunks)
                {
                    if (chunk.ChunkOldOffset != -1) continue;
                    sizeDiff += isGetDecompressSize ? chunk.ChunkSizeDecompressed : chunk.ChunkSize;
                }
            }

            return sizeDiff;
        }

        private static SophonAsset GetPatchedTargetAsset(
            Dictionary<string, int> oldAssetNameIdx,
            SophonManifestProto oldAssetProto,
            SophonManifestAssetProperty newAssetProperty,
            SophonChunksInfo oldChunksInfo,
            SophonChunksInfo newChunksInfo,
            SophonDownloadSpeedLimiter downloadSpeedLimiter)
        {
            if (newAssetProperty.AssetType != 0 ||
                string.IsNullOrEmpty(newAssetProperty.AssetHashMd5) ||
                !oldAssetNameIdx.TryGetValue(newAssetProperty.AssetName, out int oldAssetIdx))
            {
                return SophonManifest.AssetProperty2SophonAsset(newAssetProperty, newChunksInfo, downloadSpeedLimiter);
            }

            var oldAssetProperty = oldAssetProto.Assets[oldAssetIdx];
            if (oldAssetProperty == null)
            {
                throw new NullReferenceException($"The old asset proto is null for: {newAssetProperty.AssetName} at index: {oldAssetIdx}");
            }

            var patchedChunks = GetSophonChunkWithOldReference(
                oldAssetProperty.AssetChunks,
                newAssetProperty.AssetChunks,
                out bool isNewAssetHasPatch);

            return new SophonAsset
            {
                AssetName = newAssetProperty.AssetName,
                AssetHash = newAssetProperty.AssetHashMd5,
                AssetSize = newAssetProperty.AssetSize,
                Chunks = patchedChunks,
                SophonChunksInfo = newChunksInfo,
                SophonChunksInfoAlt = oldChunksInfo,
                IsDirectory = false,
                IsHasPatch = isNewAssetHasPatch,
                DownloadSpeedLimiter = downloadSpeedLimiter
            };
        }

        private static SophonChunk[] GetSophonChunkWithOldReference(
            RepeatedField<SophonManifestAssetChunk> oldProtoChunks,
            RepeatedField<SophonManifestAssetChunk> newProtoChunks,
            out bool isNewAssetHasPatch)
        {
            int newLen = newProtoChunks.Count;
            var resultChunks = new SophonChunk[newLen];
            isNewAssetHasPatch = false;

            var oldChunkIdx = new Dictionary<string, int>();
            for (int i = 0; i < oldProtoChunks.Count; i++)
            {
                if (!oldChunkIdx.TryAdd(oldProtoChunks[i].ChunkDecompressedHashMd5, i))
                    DummyInstance.PushLogWarning($"Chunk: {oldProtoChunks[i].ChunkName} is duplicated!");
            }

            for (int i = 0; i < newLen; i++)
            {
                var newChunkProto = newProtoChunks[i];
                var chunk = new SophonChunk
                {
                    ChunkName = newChunkProto.ChunkName,
                    ChunkHashDecompressed = Extension.HexToBytes(newChunkProto.ChunkDecompressedHashMd5.AsSpan()),
                    ChunkOldOffset = -1,
                    ChunkOffset = newChunkProto.ChunkOnFileOffset,
                    ChunkSize = newChunkProto.ChunkSize,
                    ChunkSizeDecompressed = newChunkProto.ChunkSizeDecompressed
                };

                if (oldChunkIdx.TryGetValue(newChunkProto.ChunkDecompressedHashMd5, out int oldIdx))
                {
                    isNewAssetHasPatch = true;
                    chunk.ChunkOldOffset = oldProtoChunks[oldIdx].ChunkOnFileOffset;
                }

                resultChunks[i] = chunk;
            }

            return resultChunks;
        }

        private static Dictionary<string, int> GetProtoAssetHashKvpSet(
            SophonManifestProto proto,
            Func<SophonManifestAssetProperty, string> funcDelegate)
        {
            var dict = new Dictionary<string, int>();
            for (int i = 0; i < proto.Assets.Count; i++)
            {
                dict[funcDelegate(proto.Assets[i])] = i;
            }

            return dict;
        }
    }
}
