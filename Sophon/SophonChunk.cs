namespace Sophon
{
    public struct SophonChunk
    {
        public SophonChunk()
        {
            ChunkOldOffset = -1;
        }

        public string ChunkName;
        public byte[] ChunkHashDecompressed;
        public long ChunkOldOffset;
        public long ChunkOffset;
        public long ChunkSize;
        public long ChunkSizeDecompressed;
    }
}
