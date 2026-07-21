using System;
using System.IO;
using System.Text;
using System.Text.Json;

namespace Core
{
    public class AppConfig
    {
        private static readonly string ConfigPath = "config.json";

        private static readonly int MaxThreadsCap =
            Math.Max(1, Environment.ProcessorCount);

        private static readonly int MaxHttpHandleCap =
            ComputeMaxHttpHandleCap();

        public string DownloadMode {get; set;} = "Parallel";
        public int Threads {get; set;} = ComputeAdaptiveThreads();
        public int MaxHttpHandle {get; set;} = ComputeAdaptiveMaxHttpHandle();
        public string LogLevel {get; set;} = "INFO";

        private static AppConfig? _config;

        public static AppConfig Config
        {
            get => _config ??= new AppConfig();
            private set => _config = value;
        }

        public static void Load()
        {
            Config = LoadInternal();
            Logger.SetLevel(Config.LogLevel);
        }

        private static AppConfig LoadInternal()
        {
            if (!File.Exists(ConfigPath))
            {
                var cfg = new AppConfig();
                cfg.Save();

                return cfg;
            }

            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(ConfigPath));

                var root = doc.RootElement;
                var cfg = new AppConfig();

                if (root.TryGetProperty("DownloadMode", out var mode))
                {
                    string? value = mode.GetString();

                    if (value == "Parallel" || value == "Sequential")
                    {
                        cfg.DownloadMode = value;
                    }
                }

                if (root.TryGetProperty("Threads", out var threads) &&
                    threads.TryGetInt32(out int threadValue))
                {
                    if (threadValue > 0 &&
                        threadValue <= MaxThreadsCap)
                    {
                        cfg.Threads = threadValue;
                    }
                }

                if (root.TryGetProperty("MaxHttpHandle", out var handles) &&
                    handles.TryGetInt32(out int handleValue))
                {
                    if (handleValue > 0 &&
                        handleValue <= MaxHttpHandleCap)
                    {
                        cfg.MaxHttpHandle = handleValue;
                    }
                }

                if (root.TryGetProperty("LogLevel", out var logLevel))
                {
                    string? value = logLevel.GetString()?.ToUpperInvariant();

                    if (value == "DEBUG" ||
                        value == "INFO" ||
                        value == "WARNING" ||
                        value == "ERROR" ||
                        value == "NONE")
                    {
                        cfg.LogLevel = value;
                    }
                }

                return cfg;
            }
            catch
            {
                var fallback = new AppConfig();
                fallback.Save();

                return fallback;
            }
        }

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

            return (int)Math.Clamp(availableMb / 8, 16, 1024);
        }

        private static int ComputeAdaptiveMaxHttpHandle()
        {
            int threads = ComputeAdaptiveThreads();

            return Math.Clamp(threads * 8, 4, MaxHttpHandleCap);
        }

        public void Save()
        {
            var options = new JsonSerializerOptions
            {
                WriteIndented = true
            };

            var json = JsonSerializer.Serialize(
                new
                {
                    LogLevel,
                    DownloadMode,
                    Threads,
                    MaxHttpHandle
                },
                options);

            File.WriteAllText(
                ConfigPath,
                json.Replace("\r\n", "\n"),
                new UTF8Encoding(false));
        }
    }
}
