using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Sophon;

namespace Core
{
    internal class Assets
    {
        public static async Task<Tuple<List<SophonAsset>?, long>> GetAssetsFromManifests(
            HttpClient httpClient,
            string matchingField,
            string prevManifestUrl,
            string newManifestUrl,
            CancellationTokenSource tokenSource)
        {
            var assets = new List<SophonAsset>();
            long updateSize = 0;

            SophonChunkManifestInfoPair? manifestFrom = null;
            SophonChunkManifestInfoPair? manifestTo = null;

            try
            {
                manifestFrom = await SophonManifest.CreateSophonChunkManifestInfoPair(
                    httpClient, prevManifestUrl, matchingField, tokenSource.Token);

                if (!string.IsNullOrEmpty(newManifestUrl))
                {
                    manifestTo = await SophonManifest.CreateSophonChunkManifestInfoPair(
                        httpClient, newManifestUrl, matchingField, tokenSource.Token);
                }

                if (manifestFrom?.ManifestInfo == null || manifestFrom?.ChunksInfo == null ||
                    (!string.IsNullOrEmpty(newManifestUrl) &&
                     (manifestTo?.ManifestInfo == null || manifestTo?.ChunksInfo == null)))
                {
                    return Tuple.Create<List<SophonAsset>?, long>(null, 0);
                }
            }
            catch
            {
                return Tuple.Create<List<SophonAsset>?, long>(null, 0);
            }

            try
            {
                if (!string.IsNullOrEmpty(newManifestUrl))
                {
                    await foreach (var asset in SophonUpdate.EnumerateUpdateAsync(
                        httpClient, manifestFrom!, manifestTo!, true, null, tokenSource.Token))
                    {
                        ProcessUpdateAsset(asset, ref updateSize, ref assets);
                    }
                }
                else
                {
                    await foreach (var asset in SophonManifest.EnumerateAsync(
                        httpClient, manifestFrom!, null, tokenSource.Token))
                    {
                        ProcessAsset(asset, ref updateSize, ref assets);
                    }
                }
            }
            catch
            {
                return Tuple.Create<List<SophonAsset>?, long>(null, 0);
            }

            return Tuple.Create<List<SophonAsset>?, long>(assets, updateSize);
        }

        private static void ProcessAsset(SophonAsset asset, ref long updateSize, ref List<SophonAsset> assets)
        {
            if (!asset.IsDirectory)
            {
                updateSize += asset.AssetSize;
                assets.Add(asset);
            }
        }

        private static void ProcessUpdateAsset(SophonAsset asset, ref long updateSize, ref List<SophonAsset> assets)
        {
            if (asset.IsDirectory) return;

            foreach (var chunk in asset.Chunks)
            {
                if (chunk.ChunkOldOffset == -1)
                {
                    updateSize += asset.AssetSize;
                    assets.Add(asset);
                    break;
                }
            }
        }
    }
}
