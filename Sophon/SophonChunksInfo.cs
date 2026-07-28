using Sophon.Infos;
using System;

namespace Sophon.Infos
{
    public class SophonChunksInfo : IEquatable<SophonChunksInfo>
    {
        public string ChunksBaseUrl {get; set;}
        public int ChunksCount {get; set;}
        public int FilesCount {get; set;}
        public long TotalSize {get; set;}
        public long TotalCompressedSize {get; set;}
        public bool IsUseCompression {get; set;}

        public bool Equals(SophonChunksInfo other) =>
            other != null &&
            ChunksBaseUrl == other.ChunksBaseUrl &&
            ChunksCount == other.ChunksCount &&
            FilesCount == other.FilesCount &&
            TotalSize == other.TotalSize &&
            TotalCompressedSize == other.TotalCompressedSize &&
            IsUseCompression == other.IsUseCompression;

        public override bool Equals(object obj) =>
            obj is SophonChunksInfo other && Equals(other);

        public override int GetHashCode() =>
            HashCode.Combine(ChunksBaseUrl, ChunksCount, FilesCount, TotalSize, TotalCompressedSize, IsUseCompression);

        public SophonChunksInfo CopyWithNewBaseUrl(string newBaseUrl) => new()
        {
            ChunksBaseUrl = newBaseUrl,
            ChunksCount = ChunksCount,
            FilesCount = FilesCount,
            TotalSize = TotalSize,
            TotalCompressedSize = TotalCompressedSize,
            IsUseCompression = IsUseCompression
        };
    }
}

namespace Sophon
{
    public static partial class SophonManifest
    {
        public static SophonChunksInfo CreateChunksInfo(
            string chunksBaseUrl,
            int chunksCount,
            int filesCount,
            bool isUseCompression,
            long totalSize,
            long totalCompressedSize = 0)
        {
            return new SophonChunksInfo
            {
                ChunksBaseUrl = chunksBaseUrl,
                ChunksCount = chunksCount,
                FilesCount = filesCount,
                IsUseCompression = isUseCompression,
                TotalSize = totalSize,
                TotalCompressedSize = totalCompressedSize
            };
        }
    }
}
