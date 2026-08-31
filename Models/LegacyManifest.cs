namespace SophonDownloader.Models;

public sealed class LegacyManifest : Dictionary<string, LegacyVersion> {}

public sealed class LegacyVersion
{
    [JsonPropertyName("game")]
    public LegacyGame Game { get; set; } = new();

    [JsonPropertyName("voice")]
    public Dictionary<string, LegacyPackage> Voice { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);

    [JsonPropertyName("update")]
    public Dictionary<string, LegacyUpdate> Update { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);

    [JsonPropertyName("decompressed_path")]
    public string? DecompressedPath { get; set; }

    [JsonPropertyName("chunk")]
    public object? Chunk { get; set; }
}

public sealed class LegacyGame
{
    [JsonPropertyName("full")]
    public LegacyPackage? Full { get; set; }

    [JsonPropertyName("segments")]
    public List<LegacyPackage> Segments { get; set; } = [];
}

public sealed class LegacyUpdate
{
    [JsonPropertyName("game")]
    public LegacyPackage? Game { get; set; }

    [JsonPropertyName("voice")]
    public Dictionary<string, LegacyPackage> Voice { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);
}

public sealed class LegacyPackage
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("url")]
    public string Url { get; set; } = "";

    [JsonPropertyName("checksum")]
    public string Checksum { get; set; } = "";

    [JsonPropertyName("size")]
    public long Size { get; set; }
}

public sealed class VoiceOption
{
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public bool IsSelected { get; set; }
    public override string ToString() => Name;
}
