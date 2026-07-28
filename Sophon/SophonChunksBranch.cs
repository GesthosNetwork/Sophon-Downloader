using Sophon;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using System.Threading;
using System.Threading.Tasks;

namespace Sophon
{
    public static partial class SophonManifest
    {
        public static async Task<T> GetSophonBranchInfo<T>(
            HttpClient client,
            string url,
            JsonTypeInfo<T> jsonTypeInfo,
            HttpMethod httpMethod,
            CancellationToken token = default)
        {
            using var requestMessage = new HttpRequestMessage(httpMethod, url);
            using var responseMessage = await client.SendAsync(requestMessage, HttpCompletionOption.ResponseHeadersRead, token);
            responseMessage.EnsureSuccessStatusCode();

            await using var responseStream = await responseMessage.Content.ReadAsStreamAsync(token);
            return await JsonSerializer.DeserializeAsync(responseStream, jsonTypeInfo, token);
        }

        public static async Task<SophonChunkManifestInfoPair> CreateSophonChunkManifestInfoPair(
            HttpClient client,
            string url,
            string matchingField = null,
            CancellationToken token = default)
        {
            var sophonBranch = await GetSophonBranchInfo<SophonManifestBuildBranch>(
                client, url, SophonContext.Default.SophonManifestBuildBranch, HttpMethod.Get, token
            );

            if (sophonBranch.Data == null)
            {
                return new SophonChunkManifestInfoPair
                {
                    IsFound = false,
                    ReturnCode = sophonBranch.ReturnCode,
                    ReturnMessage = sophonBranch.ReturnMessage
                };
            }

            matchingField ??= "game";

            var sophonManifestIdentity = sophonBranch.Data.ManifestIdentityList?
                .FirstOrDefault(x => x.MatchingField == matchingField);

            if (sophonManifestIdentity == null)
                throw new KeyNotFoundException($"Sophon manifest with matching field: {matchingField} is not found!");

            return new SophonChunkManifestInfoPair
            {
                ChunksInfo = (sophonManifestIdentity.ChunkInfo != null && sophonManifestIdentity.ChunksUrlInfo != null)
                    ? CreateChunksInfo(
                        sophonManifestIdentity.ChunksUrlInfo.UrlPrefix,
                        sophonManifestIdentity.ChunkInfo.ChunkCount,
                        sophonManifestIdentity.ChunkInfo.FileCount,
                        sophonManifestIdentity.ChunksUrlInfo.IsCompressed,
                        sophonManifestIdentity.ChunkInfo.UncompressedSize,
                        sophonManifestIdentity.ChunkInfo.CompressedSize)
                    : null,

                ManifestInfo = (sophonManifestIdentity.ManifestFileInfo != null && sophonManifestIdentity.ManifestUrlInfo != null)
                    ? CreateManifestInfo(
                        sophonManifestIdentity.ManifestUrlInfo.UrlPrefix,
                        sophonManifestIdentity.ManifestFileInfo.Checksum,
                        sophonManifestIdentity.ManifestFileInfo.FileName,
                        sophonManifestIdentity.ManifestUrlInfo.IsCompressed,
                        sophonManifestIdentity.ManifestFileInfo.UncompressedSize,
                        sophonManifestIdentity.ManifestFileInfo.CompressedSize)
                    : null,

                OtherSophonBuildData = sophonBranch.Data
            };
        }
    }
}
