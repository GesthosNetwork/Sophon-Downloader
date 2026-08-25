namespace SophonDownloader.Models;

public class BranchesRoot
{
    public int retcode { get; set; }
    public string message { get; set; } = "";
    public BranchesData data { get; set; } = new();
}

public class BranchesData
{
    public List<BranchesGameBranch> game_branches { get; set; } = [];
}

public class BranchesGameBranch
{
    public BranchesGame game { get; set; } = new();
    public BranchesMain? main { get; set; }
    public BranchesMain? pre_download { get; set; }
}

public class BranchesGame
{
    public string id { get; set; } = "";
    public string biz { get; set; } = "";
}

public class BranchesMain
{
    public string package_id { get; set; } = "";
    public string branch { get; set; } = "";
    public string password { get; set; } = "";
    public string tag { get; set; } = "";
    public List<string> diff_tags { get; set; } = [];
    public List<BranchesCategory> categories { get; set; } = [];
}

public class BranchesCategory
{
    public string category_id { get; set; } = "";
    public string matching_field { get; set; } = "";
}
