using Google.Protobuf;
using Sophon.Infos;
using Sophon;
using System;
using System.Buffers;
using System.IO;
using System.IO.Hashing;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ZstdStream = ZstdNet.DecompressionStream;

namespace Sophon.Helper
{
    internal static class Extension
    {
        private static readonly object DummyInstance = new();

        private static readonly byte[] LookupFromHexTable = new byte[]
        {
            255,255,255,255,255,255,255,255,255,255,255,255,255,255,255,255,
            255,255,255,255,255,255,255,255,255,255,255,255,255,255,255,255,
            255,255,255,255,255,255,255,255,255,255,255,255,255,255,255,255,
              0,  1,  2,  3,  4,  5,  6,  7,  8,  9,255,255,255,255,255,255,
            255, 10, 11, 12, 13, 14, 15,255,255,255,255,255,255,255,255,255,
            255,255,255,255,255,255,255,255,255,255,255,255,255,255,255,255,
            255, 10, 11, 12, 13, 14, 15
        };

        private static readonly byte[] LookupFromHexTable16 = new byte[]
        {
            255,255,255,255,255,255,255,255,255,255,255,255,255,255,255,255,
            255,255,255,255,255,255,255,255,255,255,255,255,255,255,255,255,
            255,255,255,255,255,255,255,255,255,255,255,255,255,255,255,255,
              0, 16, 32, 48, 64, 80, 96,112,128,144,255,255,255,255,255,255,
            255,160,176,192,208,224,240,255,255,255,255,255,255,255,255,255,
            255,255,255,255,255,255,255,255,255,255,255,255,255,255,255,255,
            255,160,176,192,208,224,240
        };

        internal static unsafe byte[] HexToBytes(ReadOnlySpan<char> source)
        {
            if (source.IsEmpty) return Array.Empty<byte>();
            if (source.Length % 2 == 1) throw new ArgumentException();

            int index = 0;
            int len = source.Length >> 1;

            fixed (char* sourceRef = source)
            {
                if (*(int*)sourceRef == 7864368)
                {
                    if (source.Length == 2)
                        throw new ArgumentException();

                    index += 2;
                    len -= 1;
                }

                byte[] result = new byte[len];

                fixed (byte* hiRef = LookupFromHexTable16)
                fixed (byte* lowRef = LookupFromHexTable)
                fixed (byte* resultRef = result)
                {
                    char* s = &sourceRef[index];
                    byte* r = resultRef;

                    while (*s != 0)
                    {
                        byte add;
                        if (*s > 102 || (*r = hiRef[*s++]) == 255 || *s > 102 || (add = lowRef[*s++]) == 255)
                            throw new ArgumentException();

                        *r++ += add;
                    }

                    return result;
                }
            }
        }

        internal static string BytesToHex(ReadOnlySpan<byte> bytes)
            => Convert.ToHexStringLower(bytes);

        internal static SophonChunk SophonPatchAssetAsChunk(this SophonPatchAsset asset, bool fromOriginalFile, bool fromTargetFile, bool isCompressed = false)
        {
            byte[] hash = HexToBytes((fromOriginalFile ? asset.OriginalFileHash : fromTargetFile ? asset.TargetFileHash : asset.PatchHash).AsSpan());
            string fileName = fromOriginalFile ? asset.OriginalFilePath : fromTargetFile ? asset.TargetFilePath : asset.PatchNameSource;
            long fileSize = fromOriginalFile ? asset.OriginalFileSize : fromTargetFile ? asset.TargetFileSize : asset.PatchSize;

            return new SophonChunk
            {
                ChunkHashDecompressed = hash,
                ChunkName = fileName,
                ChunkOffset = 0,
                ChunkOldOffset = 0,
                ChunkSize = fileSize,
                ChunkSizeDecompressed = fileSize
            };
        }

        internal static async ValueTask<bool> CheckChunkXxh64HashAsync(
            this SophonChunk chunk,
            string assetName,
            Stream outStream,
            byte[] chunkXxh64Hash,
            bool isSingularStream,
            CancellationToken token)
        {
            try
            {
                var hash = new XxHash64();
                if (!isSingularStream)
                    outStream.Position = chunk.ChunkOffset;

                await hash.AppendAsync(outStream, token);

                return hash.GetHashAndReset()
                    .AsSpan()
                    .SequenceEqual(chunkXxh64Hash);
            }
            catch (Exception ex) when (!token.IsCancellationRequested)
            {
                DummyInstance.PushLogWarning($"An error occurred while checking XXH64 hash for chunk: {chunk.ChunkName} | 0x{chunk.ChunkOffset:x8} -> L: 0x{chunk.ChunkSizeDecompressed:x8} for: {assetName}\r\n{ex}");
                return false;
            }
        }

        internal static async ValueTask<bool> CheckChunkMd5HashAsync(
            this SophonChunk chunk,
            Stream outStream,
            bool isSingularStream,
            CancellationToken token)
        {
            byte[] buffer = ArrayPool<byte>.Shared.Rent(SophonAsset.BufferSize);
            int bufferSize = buffer.Length;
            using var hash = MD5.Create();

            try
            {
                outStream.Position = chunk.ChunkOffset;
                long remain = chunk.ChunkSizeDecompressed;

                while (remain > 0)
                {
                    int toRead = (int)Math.Min(bufferSize, remain);
                    int read = await outStream.ReadAsync(buffer.AsMemory(0, toRead), token);
                    hash.TransformBlock(buffer, 0, read, buffer, 0);
                    remain -= read;
                }

                hash.TransformFinalBlock(buffer, 0, 0);
                return hash.Hash.AsSpan().SequenceEqual(chunk.ChunkHashDecompressed);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }

        internal static unsafe string GetChunkStagingFilenameHash(this SophonChunk chunk, SophonAsset asset)
        {
            string concatName = $"{asset.AssetName}${asset.AssetHash}${chunk.ChunkName}";
            byte[] concatBuffer = ArrayPool<byte>.Shared.Rent(concatName.Length);
            byte[] hash = ArrayPool<byte>.Shared.Rent(16);

            try
            {
                fixed (char* strPtr = concatName)
                fixed (byte* bufPtr = concatBuffer)
                {
                    int written = Encoding.UTF8.GetBytes(strPtr, concatName.Length, bufPtr, concatBuffer.Length);
                    XxHash128.Hash(concatBuffer.AsSpan(0, written), hash);
                    return BytesToHex(hash);
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(concatBuffer);
                ArrayPool<byte>.Shared.Return(hash);
            }
        }

        internal static bool TryGetChunkXxh64Hash(this string fileName, out byte[] outHash)
        {
            outHash = null;
            Span<Range> ranges = stackalloc Range[2];
            if (fileName.AsSpan().Split(ranges, '_') != 2) return false;

            var chunkHashSpan = fileName.AsSpan()[ranges[0]];
            if (chunkHashSpan.Length != 16) return false;

            outHash = HexToBytes(chunkHashSpan);
            return true;
        }

        internal static void EnsureOrThrowOutputDirectoryExistence(this SophonAsset asset, string outputDirPath)
        {
            if (string.IsNullOrEmpty(outputDirPath))
                throw new ArgumentNullException(nameof(asset), "Directory path cannot be empty or null!");

            if (!Directory.Exists(outputDirPath))
                throw new DirectoryNotFoundException($"Directory path: {outputDirPath} does not exist!");
        }

        internal static void EnsureOrThrowChunksState(this SophonAsset asset)
        {
            if (asset.Chunks == null)
                throw new NullReferenceException("This asset does not have chunk(s)!");
        }

        internal static void EnsureOrThrowStreamState(this SophonAsset asset, Stream outStream)
        {
            if (outStream == null)
                throw new NullReferenceException("Output stream cannot be null!");

            if (!outStream.CanRead)
                throw new NotSupportedException("Output stream must be readable!");

            if (!outStream.CanWrite)
                throw new NotSupportedException("Output stream must be writable!");

            if (!outStream.CanSeek)
                throw new NotSupportedException("Output stream must be seekable!");
        }

        internal static FileInfo UnassignReadOnlyFromFileInfo(this FileInfo fileInfo)
        {
            if (fileInfo.Exists && fileInfo.IsReadOnly)
                fileInfo.IsReadOnly = false;

            return fileInfo;
        }

        internal static async Task<HttpResponseMessage> GetChunkAndIfAltAsync(
            this HttpClient httpClient,
            string chunkName,
            SophonChunksInfo currentSophonChunkInfo,
            SophonChunksInfo altSophonChunkInfo,
            CancellationToken token = default)
        {
            string url = $"{currentSophonChunkInfo.ChunksBaseUrl.TrimEnd('/')}/{chunkName}";
            HttpResponseMessage response = await httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, token);

            if (response.IsSuccessStatusCode || altSophonChunkInfo == null)
                return response;

            response.Dispose();
            return await httpClient.GetChunkAndIfAltAsync(chunkName, altSophonChunkInfo, null, token);
        }

        internal static async Task<T> ReadProtoFromManifestInfo<T>(
            this HttpClient httpClient,
            SophonManifestInfo manifestInfo,
            MessageParser<T> messageParser,
            CancellationToken innerToken)
            where T : IMessage<T>
        {
            using var response = await httpClient.GetAsync(manifestInfo.ManifestFileUrl, HttpCompletionOption.ResponseHeadersRead, innerToken);
            await using var protoStream = await response.EnsureSuccessStatusCode().Content.ReadAsStreamAsync(innerToken);
            await using var decompressedStream = manifestInfo.IsUseCompression ? new ZstdStream(protoStream) : protoStream;

            return await Task.Factory.StartNew(() => messageParser.ParseFrom(decompressedStream), innerToken, TaskCreationOptions.DenyChildAttach, TaskScheduler.Default);
        }
    }
}
