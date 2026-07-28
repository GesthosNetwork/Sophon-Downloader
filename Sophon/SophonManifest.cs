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
    public static partial class SophonManifest
    {
        public static async IAsyncEnumerable<SophonAsset> EnumerateAsync(
            HttpClient httpClient,
            SophonChunkManifestInfoPair infoPair,
            SophonDownloadSpeedLimiter downloadSpeedLimiter = null,
            [EnumeratorCancellation] CancellationToken token = default)
        {
            await foreach (var asset in EnumerateAsync(
                httpClient,
                infoPair.ManifestInfo,
                infoPair.ChunksInfo,
                downloadSpeedLimiter).WithCancellation(token))
            {
                yield return asset;
            }
        }

        public static async IAsyncEnumerable<SophonAsset> EnumerateAsync(
            HttpClient httpClient,
            SophonManifestInfo manifestInfo,
            SophonChunksInfo chunksInfo,
            SophonDownloadSpeedLimiter downloadSpeedLimiter = null,
            [EnumeratorCancellation] CancellationToken token = default)
        {
            if (!DllUtils.IsLibraryExist(DllUtils.DllName))
                throw new DllNotFoundException("libzstd is not found!");

            var manifestProtoTaskCallback = new ActionTimeoutTaskCallback<SophonManifestProto>(
                async innerToken =>
                    await httpClient.ReadProtoFromManifestInfo(manifestInfo, SophonManifestProto.Parser, innerToken)
            );

            var manifestProto = await TaskExtensions.WaitForRetryAsync(
                () => manifestProtoTaskCallback, TaskExtensions.DefaultTimeoutSec, null, null, null, token
            );

            foreach (var asset in manifestProto.Assets)
            {
                yield return AssetProperty2SophonAsset(asset, chunksInfo, downloadSpeedLimiter);
            }
        }

        internal static SophonAsset AssetProperty2SophonAsset(
            SophonManifestAssetProperty asset,
            SophonChunksInfo chunksInfo,
            SophonDownloadSpeedLimiter downloadSpeedLimiter)
        {
            if (asset.AssetType != 0 || string.IsNullOrEmpty(asset.AssetHashMd5))
            {
                return new SophonAsset
                {
                    AssetName = asset.AssetName,
                    IsDirectory = true,
                    DownloadSpeedLimiter = downloadSpeedLimiter
                };
            }

            var chunks = asset.AssetChunks.Select(x => new SophonChunk
            {
                ChunkName = x.ChunkName,
                ChunkHashDecompressed = Extension.HexToBytes(x.ChunkDecompressedHashMd5.AsSpan()),
                ChunkOffset = x.ChunkOnFileOffset,
                ChunkSize = x.ChunkSize,
                ChunkSizeDecompressed = x.ChunkSizeDecompressed
            }).ToArray();

            return new SophonAsset
            {
                AssetName = asset.AssetName,
                AssetHash = asset.AssetHashMd5,
                AssetSize = asset.AssetSize,
                Chunks = chunks,
                SophonChunksInfo = chunksInfo,
                IsDirectory = false,
                DownloadSpeedLimiter = downloadSpeedLimiter
            };
        }
    }
}
