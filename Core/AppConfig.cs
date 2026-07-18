using System;
using System.IO;
using System.Text;
using System.Text.Json;

namespace Core
{
    public class AppConfig
    {
        private static readonly string ConfigPath = "config.json";

        public string Region {get; set;} = "OSREL";
        public string Branch {get; set;} = "main";
        public string LauncherId {get; set;} = "VYTpXlbWo8";
        public string PackageId {get; set;} = "ScSYQBFhu9";
        public string PlatApp {get; set;} = "ddxf6vlr1reo";
        public string Password {get; set;} = "bDL4JUHL625x";
        public string DownloadMode {get; set;} = "Parallel";

        private static readonly int MaxThreadsCap = ComputeMaxThreadsCap();
        private static readonly int MaxHttpHandleCap = ComputeMaxHttpHandleCap();

        public int Threads {get; set;} = ComputeAdaptiveThreads();
        public int MaxHttpHandle {get; set;} = ComputeAdaptiveMaxHttpHandle();
        public string LogLevel {get; set;} = "INFO";

        private static AppConfig? _config;

        public static AppConfig Config
        {
            get
            {
                return _config ??= new AppConfig();
            }

            private set
            {
                _config = value;
            }
        }

        private static int ComputeMaxThreadsCap()
            => Math.Max(1, Environment.ProcessorCount);

        private static int ComputeAdaptiveThreads()
        {
            int cores = Environment.ProcessorCount;
            int adaptive = cores <= 2
                ? cores
                : (int)Math.Ceiling(cores * 0.75);

            return Math.Clamp(adaptive, 1, MaxThreadsCap);
        }

        private static int ComputeMaxHttpHandleCap()
        {
            long availableMb = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / (1024 * 1024);
            int byMemory = (int)Math.Clamp(availableMb / 8, 16, 1024);

            return byMemory;
        }

        private static int ComputeAdaptiveMaxHttpHandle()
        {
            int threads = ComputeAdaptiveThreads();
            int byThreads = threads * 8;

            return Math.Clamp(byThreads, 4, MaxHttpHandleCap);
        }

        public static void Load()
        {
            Config = LoadInternal();
        }

        private static AppConfig LoadInternal()
        {
            if (!File.Exists(ConfigPath))
            {
                var cfg = new AppConfig();

                cfg.SetRegionDefaults();
                cfg.SetPasswordByBranch();
                cfg.Save();
                Logger.SetLevel(cfg.LogLevel);

                return cfg;
            }

            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(ConfigPath));
                var root = doc.RootElement;
                var cfg = new AppConfig();

                if (root.TryGetProperty("Region", out var region))
                {
                    string? value = region.GetString()?.ToUpperInvariant();

                    if (value == "OSREL" || value == "CNREL")
                    {
                        cfg.Region = value;
                    }
                }

                if (root.TryGetProperty("Branch", out var branch))
                {
                    string? value = branch.GetString();

                    if (!string.IsNullOrWhiteSpace(value))
                    {
                        cfg.Branch = value;
                    }
                }

                if (root.TryGetProperty("LauncherId", out var launcher))
                {
                    cfg.LauncherId = launcher.GetString() ?? cfg.LauncherId;
                }

                if (root.TryGetProperty("PackageId", out var package))
                {
                    cfg.PackageId = package.GetString() ?? cfg.PackageId;
                }

                if (root.TryGetProperty("PlatApp", out var plat))
                {
                    cfg.PlatApp = plat.GetString() ?? cfg.PlatApp;
                }

                if (root.TryGetProperty("DownloadMode", out var mode))
                {
                    string? value = mode.GetString();

                    if (value == "Sequential" ||
                        value == "Parallel")
                    {
                        cfg.DownloadMode = value;
                    }
                }

                if (root.TryGetProperty("Threads", out var threads)
                    && threads.TryGetInt32(out int t))
                {
                    if (t > 0 &&
                        t <= MaxThreadsCap)
                    {
                        cfg.Threads = t;
                    }
                }

                if (root.TryGetProperty("MaxHttpHandle", out var handles)
                    && handles.TryGetInt32(out int h))
                {
                    if (h > 0 &&
                        h <= MaxHttpHandleCap)
                    {
                        cfg.MaxHttpHandle = h;
                    }
                }

                if (root.TryGetProperty("LogLevel", out var logLevel))
                {
                    string? value =
                        logLevel.GetString()
                        ?.ToUpperInvariant();

                    if (value == "DEBUG" ||
                        value == "INFO" ||
                        value == "WARNING" ||
                        value == "ERROR" ||
                        value == "NONE")
                    {
                        cfg.LogLevel = value;
                    }
                }

                cfg.SetRegionDefaults();
                cfg.SetPasswordByBranch();
                cfg.Save();
                Logger.SetLevel(cfg.LogLevel);

                return cfg;
            }
            catch
            {
                var fallback = new AppConfig();

                fallback.SetRegionDefaults();
                fallback.SetPasswordByBranch();
                fallback.Save();
                Logger.SetLevel(fallback.LogLevel);

                return fallback;
            }
        }

        private void SetRegionDefaults()
        {
            if (Region == "CNREL")
            {
                LauncherId = "jGHBHlcOq1";
                PackageId = "8xfMve0uwQ";
                PlatApp = "ddxf5qt290cg";
            }
            else
            {
                LauncherId = "VYTpXlbWo8";
                PackageId = "ScSYQBFhu9";
                PlatApp = "ddxf6vlr1reo";
            }
        }

        public void SetPasswordByBranch()
        {
            Password = (Region, Branch.ToLowerInvariant()) switch
            {
                ("OSREL", "main") => "bDL4JUHL625x",
                ("OSREL", "predownload") => "ZOJpUiKu4Sme",
                ("CNREL", "main") => "CW8GbLNU8f",
                ("CNREL", "predownload") => "",
                _ =>
                    ""
            };
        }

        public void Save()
        {
            var options = new JsonSerializerOptions
            {
                WriteIndented = true
            };

            var json = JsonSerializer.Serialize(new
            {
                Region,
                Branch,
                LauncherId,
                PackageId,
                PlatApp,
                Password,
                DownloadMode,
                Threads,
                MaxHttpHandle,
                LogLevel
            },
            options);

            json = json.Replace("\r\n", "\n");

            File.WriteAllText(
                ConfigPath,
                json,
                new UTF8Encoding(false)
            );
        }
    }
}
