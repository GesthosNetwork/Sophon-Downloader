namespace SophonDownloader.Models;

public sealed class GameInfo(string displayName, string gameId, string region)
{
    public string DisplayName { get; } = displayName;
    public string GameId { get; } = gameId;
    public string Region { get; } = region;

    public override string ToString() => DisplayName;
}
