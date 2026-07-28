using Sophon.Infos;

namespace Sophon.Infos
{
    public class SophonManifestInfo
    {
        public string ManifestBaseUrl {get; internal set;}
        public string ManifestId {get; internal set;}
        public string ManifestChecksumMd5 {get; internal set;}
        public bool IsUseCompression {get; internal set;}
        public long ManifestSize {get; internal set;}
        public long ManifestCompressedSize {get; internal set;}
        public string ManifestFileUrl => $"{ManifestBaseUrl.TrimEnd('/')}/{ManifestId}";
    }
}

namespace Sophon
{
    public static partial class SophonManifest
    {
        public static SophonManifestInfo CreateManifestInfo(
            string manifestBaseUrl,
            string manifestChecksumMd5,
            string manifestId,
            bool isUseCompression,
            long manifestSize,
            long manifestCompressedSize = 0)
        {
            return new SophonManifestInfo
            {
                ManifestBaseUrl = manifestBaseUrl,
                ManifestChecksumMd5 = manifestChecksumMd5,
                ManifestId = manifestId,
                IsUseCompression = isUseCompression,
                ManifestSize = manifestSize,
                ManifestCompressedSize = manifestCompressedSize
            };
        }
    }
}
