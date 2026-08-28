using SophonDownloader;

namespace SophonDownloader.Models;

public sealed class SophonPatchArtifact
{
    public required string TargetFile { get; init; }
    public required string OutputFile { get; init; }
    public required SophonPatch Patch { get; init; }
}
