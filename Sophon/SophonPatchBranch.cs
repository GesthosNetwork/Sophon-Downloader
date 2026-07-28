using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Sophon;

namespace Sophon
{
    public partial class SophonPatch
    {
        public static async Task<SophonChunkManifestInfoPair> CreateSophonChunkManifestInfoPair(
            HttpClient client,
            string url,
            string versionUpdateFrom,
            string matchingField,
            CancellationToken token = default)
        {
            if (string.IsNullOrEmpty(matchingField))
            {
                matchingField = "game";
            }

            var patchBranch = await SophonManifest.GetSophonBranchInfo<SophonManifestPatchBranch>(
                client,
                url,
                SophonContext.Default.SophonManifestPatchBranch,
                HttpMethod.Post,
                token
            );

            if (patchBranch.Data == null)
            {
                return new SophonChunkManifestInfoPair
                {
                    IsFound = false,
                    ReturnCode = patchBranch.ReturnCode,
                    ReturnMessage = patchBranch.ReturnMessage
                };
            }

            var patchIdentity = patchBranch.Data.ManifestIdentityList?
                .FirstOrDefault(x => x.MatchingField == matchingField);

            if (patchIdentity == null)
            {
                return new SophonChunkManifestInfoPair
                {
                    IsFound = false,
                    ReturnCode = 404,
                    ReturnMessage = $"Sophon patch with matching field: {matchingField} is not found!"
                };
            }

            if (!patchIdentity.DiffTaggedInfo.TryGetValue(versionUpdateFrom, out var chunkInfo))
            {
                return new SophonChunkManifestInfoPair
                {
                    IsFound = false,
                    ReturnCode = 404,
                    ReturnMessage = $"Sophon patch diff tagged info with version: {versionUpdateFrom} is not found!"
                };
            }

            var diffUrlInfo = patchIdentity.DiffUrlInfo;
            var manifestUrlInfo = patchIdentity.ManifestUrlInfo;
            var manifestFileInfo = patchIdentity.ManifestFileInfo;

            var chunksInfo = SophonManifest.CreateChunksInfo(
                diffUrlInfo.UrlPrefix,
                chunkInfo.ChunkCount,
                chunkInfo.FileCount,
                diffUrlInfo.IsCompressed,
                chunkInfo.UncompressedSize,
                chunkInfo.CompressedSize
            );

            var manifestInfo = SophonManifest.CreateManifestInfo(
                manifestUrlInfo.UrlPrefix,
                manifestFileInfo.Checksum,
                manifestFileInfo.FileName,
                manifestUrlInfo.IsCompressed,
                manifestFileInfo.UncompressedSize,
                manifestFileInfo.CompressedSize
            );

            return new SophonChunkManifestInfoPair
            {
                ChunksInfo = chunksInfo,
                ManifestInfo = manifestInfo,
                OtherSophonBuildData = null,
                OtherSophonPatchData = patchBranch.Data
            };
        }
    }
}
