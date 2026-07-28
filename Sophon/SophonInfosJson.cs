using Sophon.Helper;
using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Sophon
{
    [JsonSerializable(typeof(SophonManifestBuildBranch))]
    [JsonSerializable(typeof(SophonManifestPatchBranch))]
    public partial class SophonContext : JsonSerializerContext { }

    public class SophonManifestBuildBranch : SophonManifestReturnedResponse
    {
        [JsonPropertyName("data")] public SophonManifestBuildData Data {get; set;}
    }

    public class SophonManifestBuildData : SophonTaggedResponse
    {
        [JsonPropertyName("manifests")] public List<SophonManifestBuildIdentity> ManifestIdentityList {get; set;}
    }

    public class SophonManifestBuildIdentity
    {
        [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)]
        [JsonPropertyName("category_id")] public int CategoryId {get; set;}
        [JsonPropertyName("category_name")] public string CategoryName {get; set;}
        [JsonPropertyName("matching_field")] public string MatchingField {get; set;}
        [JsonPropertyName("manifest")] public SophonManifestFileInfo ManifestFileInfo {get; set;}
        [JsonPropertyName("manifest_download")] public SophonManifestUrlInfo ManifestUrlInfo {get; set;}
        [JsonPropertyName("stats")] public SophonManifestChunkInfo ChunkInfo {get; set;}
        [JsonPropertyName("chunk_download")] public SophonManifestUrlInfo ChunksUrlInfo {get; set;}
        [JsonPropertyName("deduplicated_stats")] public SophonManifestChunkInfo DeduplicatedChunkInfo {get; set;}
    }

    public class SophonManifestPatchBranch : SophonManifestReturnedResponse
    {
        [JsonPropertyName("data")] public SophonManifestPatchData Data {get; set;}
    }

    public class SophonManifestPatchData : SophonTaggedResponse
    {
        [JsonPropertyName("patch_id")] public string PatchId {get; set;}
        [JsonPropertyName("manifests")] public List<SophonManifestPatchIdentity> ManifestIdentityList {get; set;}
    }

    public class SophonManifestPatchIdentity
    {
        [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)]

        [JsonPropertyName("category_id")] public int CategoryId {get; set;}
        [JsonPropertyName("category_name")] public string CategoryName {get; set;}
        [JsonPropertyName("matching_field")] public string MatchingField {get; set;}
        [JsonPropertyName("manifest")] public SophonManifestFileInfo ManifestFileInfo {get; set;}
        [JsonPropertyName("manifest_download")] public SophonManifestUrlInfo ManifestUrlInfo {get; set;}
        [JsonPropertyName("diff_download")] public SophonManifestUrlInfo DiffUrlInfo {get; set;}
        [JsonPropertyName("stats")] public Dictionary<string, SophonManifestChunkInfo> DiffTaggedInfo {get; set;}
    }

    public class SophonManifestReturnedResponse
    {
        [JsonPropertyName("retcode")] public int ReturnCode {get; set;}
        [JsonPropertyName("message")] public string ReturnMessage {get; set;}
    }

    public class SophonTaggedResponse
    {
        [JsonPropertyName("build_id")] public string BuildId {get; set;}
        [JsonPropertyName("tag")] public string TagName {get; set;}
    }

    public class SophonManifestFileInfo
    {
        [JsonPropertyName("id")] public string FileName {get; set;}
        [JsonPropertyName("checksum")] public string Checksum {get; set;}

        [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)]
        [JsonPropertyName("compressed_size")] public long CompressedSize {get; set;}

        [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)]
        [JsonPropertyName("uncompressed_size")] public long UncompressedSize {get; set;}
    }

    public class SophonManifestUrlInfo
    {
        [JsonPropertyName("password")] public string EncryptionPassword {get; set;}
        [JsonPropertyName("url_prefix")] public string UrlPrefix {get; set;}
        [JsonPropertyName("url_suffix")] public string UrlSuffix {get; set;}

        [JsonConverter(typeof(BoolConverter))]
        [JsonPropertyName("encryption")] public bool IsEncrypted {get; set;}

        [JsonConverter(typeof(BoolConverter))]
        [JsonPropertyName("compression")] public bool IsCompressed {get; set;}
    }

    public class SophonManifestChunkInfo
    {
        [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)]
        [JsonPropertyName("compressed_size")] public long CompressedSize {get; set;}

        [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)]
        [JsonPropertyName("uncompressed_size")] public long UncompressedSize {get; set;}

        [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)]
        [JsonPropertyName("file_count")] public int FileCount {get; set;}

        [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)]
        [JsonPropertyName("chunk_count")] public int ChunkCount {get; set;}
    }
}
