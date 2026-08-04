using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Sophon;

namespace Core;

public static class Menu
{
    enum R {Back, Exit}

    sealed class V
    {
        public string F {get;}
        public string T {get;}

        public V(string f, string t = "") => (F, T) = (f, t);
    }

    const string L = "------------------------------";
    const string E = "==============================";

    static VersionsConfig? c;
    static string? cg, cb;

    static readonly string[] P = ["game", "en-us", "ja-jp", "zh-cn", "ko-kr"];
    static readonly string[] N = ["Game Files", "English (en-us)", "Japanese (ja-jp)", "Chinese (zh-cn)", "Korean (ko-kr)"];

    public static async Task<int> RunMenu()
    {
        while (true)
        {
            Head(
                "HK4E Package",
                "Select Game Region",
                L,
                "[1] OSREL (Global)",
                "[2] CNREL (China)",
                L,
                "[X] Exit"
            );

            switch (In())
            {
                case "1":
                    if (await Sel(Region.OSREL) == R.Exit)
                        return 0;
                    break;

                case "2":
                    if (await Sel(Region.CNREL) == R.Exit)
                        return 0;
                    break;

                case "0":
                case "x":
                    return 0;
            }
        }
    }

    static async Task<R> Sel(Region r)
    {
        while (true)
        {
            Head(
                "HK4E Package",
                $"Region : {Reg(r)}",
                L,
                "[1] Full Package",
                "[2] Update Package",
                "[3] Predownload Package",
                "[4] Single Asset Download",
                L,
                "[0] Back",
                "[X] Exit"
            );

            var x = In();
            if (x == "0") return R.Back;
            if (x == "x") return R.Exit;

            var m = x switch
            {
                "1" => "full",
                "2" => "update",
                "3" => "predownload",
                "4" => "single",
                _ => null
            };

            if (m != null)
            {
                if (m == "single")
                {
                    if (await SingleAsset(r) == R.Exit)
                        return R.Exit;
                    continue;
                }

                if (await Pack(r, m) == R.Exit)
                    return R.Exit;
            }
        }
    }

    static async Task<R> Pack(Region r, string m)
    {
        while (true)
        {
            Head(Mode(m), "Select Package", L);

            for (int i = 0; i < P.Length; i++)
                Console.WriteLine($"[{i + 1}] {N[i]}");

            BackExit();

            var x = In();
            if (x == "0") return R.Back;
            if (x == "x") return R.Exit;
            if (!int.TryParse(x, out int n) || n < 1 || n > P.Length)
                continue;

            var p = P[n - 1];

            if (m == "predownload")
            {
                await Pre(r, p);
                continue;
            }

            if (await VerMenu(r, m, p) == R.Exit)
                return R.Exit;
        }
    }

    static async Task<R> SingleAsset(Region r)
    {
        while (true)
        {
            Head(
                "HK4E Package",
                $"Region : {Reg(r)}",
                L,
                "Select Package"
            );

            for (int i = 0; i < P.Length; i++)
                Console.WriteLine($"[{i + 1}] {N[i]}");

            BackExit();

            var x = In();
            if (x == "0") return R.Back;
            if (x == "x") return R.Exit;
            if (!int.TryParse(x, out int n) || n < 1 || n > P.Length)
                continue;

            var lang = P[n - 1];
            var g = GameOf(r);
            var s = new Sophon(g, BranchType.Main);
            VersionsConfig v;

            try
            {
                v = await VerCache(s, g, BranchType.Main);
            }
            catch (Exception e)
            {
                Head(
                    "HK4E Package",
                    "Error",
                    L
                );
                Logger.Error("Cannot get version list: {0}", e.Message);
                Pause();
                return R.Back;
            }

            var list = v.Full.Select(x => new V(x)).ToArray();
            if (list.Length == 0)
            {
                Head(
                    "HK4E Package",
                    "Error",
                    L
                );
                Logger.Error("Version list is empty.");
                Pause();
                return R.Back;
            }

            while (true)
            {
                Head(
                    "Single Asset Download",
                    $"Region : {Reg(r)}",
                    $"Package: {Pkg(lang)}",
                    L,
                    "Select Version"
                );

                for (int i = 0; i < list.Length; i++)
                    Console.WriteLine($"[{i + 1}] Version {list[i].F}");

                BackExit();

                var vx = In();
                if (vx == "0") return R.Back;
                if (vx == "x") return R.Exit;
                if (!int.TryParse(vx, out int vn) || vn < 1 || vn > list.Length)
                    continue;

                var a = list[vn - 1];
                var f = Norm(a.F);
                var outDir = Path.Combine("Downloads", $"{lang}_{f}");

                Console.Clear();
                Console.WriteLine(E);
                Console.WriteLine(Center("Single Asset Download", 30));
                Console.WriteLine(E);
                Console.WriteLine($"""
                Region : {Reg(r)}
                Package: {Pkg(lang)}
                Version: {f}
                {L}
                Enter full asset path or keyword.
                Examples:
                 1. GenshinImpact_Data/StreamingAssets/AudioAssets/Banks0.pck
                 2. Video
                 3. *.dll
                {L}
                """);

                var asset = Required("Asset query: ");

                await Go([
                    "single", r.ToString(), lang, f, outDir, asset
                ]);

                return R.Back;
            }
        }
    }

    static async Task<R> VerMenu(
        Region r,
        string m,
        string lang)
    {
        var g = GameOf(r);
        var s = new Sophon(g, BranchType.Main);
        VersionsConfig v;

        try
        {
            v = await VerCache(s, g, BranchType.Main);
        }
        catch (Exception e)
        {
            Head(
                "HK4E Package",
                "Error",
                L
            );
            Logger.Error("Cannot get version list: {0}", e.Message);
            Pause();
            return R.Back;
        }

        var list = m == "full"
            ? v.Full.Select(x => new V(x)).ToArray()
            : v.Update.Select(x => new V(x[0], x[1])).ToArray();

        while (true)
        {
            Head(Mode(m), $"Package : {Pkg(lang)}", L);

            for (int i = 0; i < list.Length; i++)
                Console.WriteLine(m == "full"
                    ? $"[{i + 1}] Version {list[i].F}"
                    : $"[{i + 1}] From {list[i].F} -> {list[i].T}");

            BackExit();

            var x = In();
            if (x == "0") return R.Back;
            if (x == "x") return R.Exit;
            if (!int.TryParse(x, out int n) || n < 1 || n > list.Length)
                continue;

            var a = list[n - 1];
            var f = Norm(a.F);
            bool full = m == "full";
            var t = full ? "" : Norm(a.T);
            var o = Path.Combine(
                "Downloads",
                full ? $"{lang}_{f}" : $"{lang}_{f}_{t}_diff"
            );

            await Go(full
                ? ["full", r.ToString(), lang, f, o]
                : ["update", r.ToString(), lang, f, t, o]);

            return R.Back;
        }
    }

    static async Task Pre(Region r, string lang)
    {
        var s = new Sophon(GameOf(r), BranchType.PreDownload);

        try
        {
            Head("HK4E Package", "Loading...", L, "Initializing predownload...");
            await s.GetBuildData();
        }
        catch (Exception e)
        {
            Head(
                "HK4E Package",
                "Error",
                L,
                $"Cannot initialize predownload: {e.Message}"
            );
            Logger.Error("Cannot initialize predownload: {0}", e.Message);
            Pause();
            return;
        }

        await Go([
            "predownload",
            r.ToString(),
            lang,
            Norm(await s.GetLatestVersion() ?? "Latest"),
            Path.Combine("Downloads", $"{lang}_predownload")
        ]);
    }

    static async Task Go(string[] a)
    {
        Downloader.ConfirmPrompt = ConfirmDownload;
        Downloader.AssetPicker = PickAsset;

        Head("HK4E Package", "Downloading...", L);
        await Download.RunDownload(a);
        Pause();
    }

    static bool ConfirmDownload(int total, long totalSize, bool isResuming)
    {
        int line = Console.CursorTop;

        while (true)
        {
            ClearLine(line);
            Console.Write("Continue? (yes/no): ");

            var input = Console.ReadLine()?.Trim().ToLowerInvariant();

            if (input is "yes" or "y")
            {
                ClearLine(line);
                if (!Console.IsOutputRedirected)
                {
                    Console.SetCursorPosition(0, line);
                    Console.WriteLine();
                }
                return true;
            }

            if (input is "no" or "n")
                return false;
        }
    }

    static void ClearLine(int line)
    {
        if (Console.IsOutputRedirected)
            return;

        Console.SetCursorPosition(0, line);
        Console.Write(new string(' ', Math.Max(1, Console.BufferWidth - 1)));
        Console.SetCursorPosition(0, line);
    }

    static void ClearFromLine(int line)
    {
        if (Console.IsOutputRedirected)
            return;

        int current = Console.CursorTop;
        for (int i = line; i <= current; i++)
        {
            Console.SetCursorPosition(0, i);
            Console.Write(new string(' ', Math.Max(1, Console.BufferWidth - 1)));
        }
        Console.SetCursorPosition(0, line);
    }

    static int PickAsset(List<SophonAsset> assets)
    {
        const int pageSize = 30;
        int page = 0;
        int totalPage = (int)Math.Ceiling(assets.Count / (double)pageSize);
        int startLine = Console.CursorTop;

        while (true)
        {
            ClearFromLine(startLine);
            Console.SetCursorPosition(0, startLine);
            Logger.Info($"Multiple matches found: {assets.Count} results");

            if (totalPage > 1)
                Console.WriteLine($"\nPage {page + 1}/{totalPage}");

            Console.WriteLine();

            int start = page * pageSize;
            int end = Math.Min(start + pageSize, assets.Count);

            for (int i = start; i < end; i++)
            {
                int shown = i + 1;
                Console.WriteLine($" {shown}. {assets[i].AssetName} ({Utils.FormatSize(assets[i].AssetSize)})");
            }

            Console.WriteLine();
            Console.WriteLine(L);

            if (page < totalPage - 1)
                Console.WriteLine("[N] Next page");
            if (page > 0)
                Console.WriteLine("[B] Back page");

            Console.WriteLine("[C] Cancel");
            Console.WriteLine("Enter number to select");

            var input = In();

            if (totalPage > 1)
            {
                if (input == "n")
                {
                    if (page < totalPage - 1)
                        page++;
                    continue;
                }

                if (input == "b")
                {
                    if (page > 0)
                        page--;
                    continue;
                }
            }

            if (input == "c")
                return -1;

            if (int.TryParse(input, out int number) && number >= start + 1 && number <= end)
                return number - 1;
        }
    }

    static Game GameOf(Region r) =>
        new(r == Region.CNREL
            ? Game.GameType.hk4e_cn.ToString()
            : Game.GameType.hk4e_global.ToString());

    static string In()
    {
        Console.Write("Choose: ");
        return Console.ReadLine()?.Trim().ToLowerInvariant() ?? "";
    }

    static string Required(string prompt)
    {
        while (true)
        {
            Console.Write(prompt);
            var x = Console.ReadLine()?.Trim();

            if (!string.IsNullOrWhiteSpace(x))
                return x;

            Console.SetCursorPosition(0, Console.CursorTop - 1);
            Console.Write(new string(' ', Console.WindowWidth));
            Console.SetCursorPosition(0, Console.CursorTop - 1);
        }
    }

    static void Pause()
    {
        Console.WriteLine("\nPress any key to continue...");
        Console.ReadKey(true);
    }

    static void BackExit()
    {
        Console.WriteLine($"""
        {L}
        [0] Back
        [X] Exit
        """);
    }

    static string Mode(string x) => x switch
    {
        "full" => "Full Package",
        "update" => "Update Package",
        "predownload" => "predownload Package",
        _ => x
    };

    static string Reg(Region x) => x switch
    {
        Region.OSREL => "OSREL (Global)",
        Region.CNREL => "CNREL (China)",
        _ => x.ToString()
    };

    static string Pkg(string x) => x == "game"
        ? "Game Files"
        : x switch
        {
            "en-us" => "English (en-us)",
            "ja-jp" => "Japanese (ja-jp)",
            "zh-cn" => "Chinese (zh-cn)",
            "ko-kr" => "Korean (ko-kr)",
            _ => x
        };

    static string Norm(string x)
    {
        if (string.IsNullOrWhiteSpace(x))
            return "";

        var p = x.Split('.');
        return p.Length switch
        {
            1 => $"{p[0]}.0.0",
            2 => $"{p[0]}.{p[1]}.0",
            _ => x
        };
    }

    static async Task<VersionsConfig> VerCache(Sophon s, Game g, BranchType b)
    {
        var gk = $"{g.Type}_{g.Region}_{g.GameId}";
        var bk = b.ToString();

        if (c != null &&
            cg == gk &&
            cb == bk)
            return c;

        Head(
            "HK4E Package",
            "Loading...",
            L
        );

        Logger.Info("Fetching version list...");
        await s.GetBuildData();
        c = await s.GetVersionsAsync();
        cg = gk;
        cb = bk;

        return c;
    }

    static void Head(string t, params string[] l)
    {
        Console.Clear();
        Console.WriteLine(E);
        Console.WriteLine(Center(t, 30));
        Console.WriteLine(E);

        foreach (var x in l)
            Console.WriteLine(x);
    }

    static string Center(string x, int w) =>
        string.IsNullOrEmpty(x) || x.Length >= w
            ? x
            : new string(' ', (w - x.Length) / 2) + x;
}
