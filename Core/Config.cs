using System;

namespace Core
{
    public static class Config
    {
        public static string LogLevel {get; set;} = "DEBUG";
        public static string DownloadMode {get; set;} = "Parallel";
        public static int Threads {get; set;} = ComputeAdaptiveThreads();
        public static int ChunkThreads {get; set;} = ComputeAdaptiveChunkThreads();
        public static int MaxHttpHandle {get; set;} = ComputeAdaptiveMaxHttpHandle();

        public static void Load()
        {
            Logger.SetLevel(LogLevel);
        }

        private static int ComputeAdaptiveThreads()
        {
            int cpu = Environment.ProcessorCount;
            return Math.Clamp((cpu + 1) / 2, 2, 8);
        }

        private static int ComputeAdaptiveChunkThreads()
        {
            int threads = ComputeAdaptiveThreads();

            return threads switch
            {
                <= 2 => 2,
                <= 4 => 4,
                _ => 8
            };
        }

        private static int ComputeAdaptiveMaxHttpHandle()
        {
            int handles = Threads * ChunkThreads;
            return Math.Clamp(handles, 16, 64);
        }
    }
}
