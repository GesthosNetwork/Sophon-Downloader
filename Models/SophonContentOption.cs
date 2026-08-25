namespace SophonDownloader.Models;

public sealed class SophonContentOption
{
    public ManifestCategory Category { get; }
    public string Name => string.IsNullOrWhiteSpace(Category.category_name) ? Category.category_id : Category.category_name;
    public bool IsSelected { get; set; }

    public SophonContentOption(ManifestCategory category)
    {
        Category = category;
        IsSelected = false;
    }
}
