using Sophon.Infos;
using System.Collections.Generic;
using System.Linq;

namespace Sophon
{
    public class SophonChunkManifestInfoPair
    {
        public SophonChunksInfo ChunksInfo {get; internal set;}
        public SophonManifestInfo ManifestInfo {get; internal set;}
        public SophonManifestBuildData OtherSophonBuildData {get; internal set;}
        public SophonManifestPatchData OtherSophonPatchData {get; internal set;}
        public bool IsFound {get; internal set;} = true;
        public int ReturnCode {get; internal set;} = 0;
        public string ReturnMessage {get; internal set;}

        public SophonChunkManifestInfoPair GetOtherManifestInfoPair(string matchingField)
        {
            var manifestIdentity = OtherSophonBuildData
                .ManifestIdentityList?
                .FirstOrDefault(x => x.MatchingField == matchingField);

            if (manifestIdentity == null)
                throw new KeyNotFoundException($"Sophon manifest with matching field: {matchingField} is not found!");

            var chunkInfo = manifestIdentity.ChunkInfo;
            var chunkUrlInfo = manifestIdentity.ChunksUrlInfo;
            var manifestFileInfo = manifestIdentity.ManifestFileInfo;
            var manifestUrlInfo = manifestIdentity.ManifestUrlInfo;

            var chunksInfo = SophonManifest.CreateChunksInfo(
                chunkUrlInfo.UrlPrefix,
                chunkInfo.ChunkCount,
                chunkInfo.FileCount,
                chunkUrlInfo.IsCompressed,
                chunkInfo.UncompressedSize,
                chunkInfo.CompressedSize);

            var manifestInfo = SophonManifest.CreateManifestInfo(
                manifestUrlInfo.UrlPrefix,
                manifestFileInfo.Checksum,
                manifestFileInfo.FileName,
                manifestUrlInfo.IsCompressed,
                manifestFileInfo.UncompressedSize,
                manifestFileInfo.CompressedSize);

            return new SophonChunkManifestInfoPair
            {
                ChunksInfo = chunksInfo,
                ManifestInfo = manifestInfo,
                OtherSophonBuildData = OtherSophonBuildData,
                OtherSophonPatchData = OtherSophonPatchData
            };
        }

        public SophonChunkManifestInfoPair GetOtherPatchInfoPair(string matchingField, string versionUpdateFrom)
        {
            var patchIdentity = OtherSophonPatchData
                .ManifestIdentityList?
                .FirstOrDefault(x => x.MatchingField == matchingField);

            if (patchIdentity == null)
                throw new KeyNotFoundException($"Sophon patch with matching field: {matchingField} is not found!");

            if (!patchIdentity.DiffTaggedInfo.TryGetValue(versionUpdateFrom, out var chunkInfo))
                throw new KeyNotFoundException($"Sophon patch diff tagged info with tag: {versionUpdateFrom} is not found!");

            var diffUrlInfo = patchIdentity.DiffUrlInfo;
            var manifestFileInfo = patchIdentity.ManifestFileInfo;
            var manifestUrlInfo = patchIdentity.ManifestUrlInfo;

            var chunksInfo = SophonManifest.CreateChunksInfo(
                diffUrlInfo.UrlPrefix,
                chunkInfo.ChunkCount,
                chunkInfo.FileCount,
                diffUrlInfo.IsCompressed,
                chunkInfo.UncompressedSize,
                chunkInfo.CompressedSize);

            var manifestInfo = SophonManifest.CreateManifestInfo(
                manifestUrlInfo.UrlPrefix,
                manifestFileInfo.Checksum,
                manifestFileInfo.FileName,
                manifestUrlInfo.IsCompressed,
                manifestFileInfo.UncompressedSize,
                manifestFileInfo.CompressedSize);

            return new SophonChunkManifestInfoPair
            {
                ChunksInfo = chunksInfo,
                ManifestInfo = manifestInfo,
                OtherSophonBuildData = OtherSophonBuildData,
                OtherSophonPatchData = OtherSophonPatchData
            };
        }
    }
}
