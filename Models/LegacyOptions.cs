namespace SophonDownloader.Models;

public sealed class GameOption
{
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public LegacyManifest Manifest { get; set; } = new();
    public override string ToString() => Name;
}
