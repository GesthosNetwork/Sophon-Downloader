using SophonDownloader;

namespace SophonDownloader.Models;

public sealed class SophonContentSet
{
    public List<SophonChunkFile> AllFiles { get; init; } = [];

    public Dictionary<string, string> FileManifest { get; init; } =
        new(StringComparer.OrdinalIgnoreCase);

    public List<SophonContentOption> SelectedContent { get; init; } = [];

    public int FileCount =>
        AllFiles.Count(file => !file.IsFolder);

    public int UniqueChunkCount =>
        AllFiles
            .SelectMany(file => file.Chunks)
            .Select(chunk => chunk.Id)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();

    public long UniqueCompressedSize =>
        AllFiles
            .SelectMany(file => file.Chunks)
            .GroupBy(chunk => chunk.Id, StringComparer.OrdinalIgnoreCase)
            .Sum(group => group.First().CompressedSize);
}
