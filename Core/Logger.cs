using System;
using System.Collections.Generic;

namespace Core
{
    internal static class Logger
    {
        public enum LogLevel
        {
            Debug, Info, Warning, Error, None
        }

        public static LogLevel MinimumLevel {get; set;} = LogLevel.Info;
        private static readonly object SyncObject = new();

        private static readonly Dictionary<LogLevel, (string Tag, ConsoleColor Color)> LogInfo = new()
        {
            [LogLevel.Debug] = ("DEBUG", ConsoleColor.DarkGray),
            [LogLevel.Info] = ("INFO", ConsoleColor.Green),
            [LogLevel.Warning] = ("WARN", ConsoleColor.Yellow),
            [LogLevel.Error] = ("ERROR", ConsoleColor.Red)
        };

        private static int _progressLength;
        private static int _lineLength;
        private static int _statusLength;
        private static bool _progressVisible;
        private static bool _lineVisible;
        private static bool _statusVisible;

        public static void SetLevel(string level)
        {
            MinimumLevel = level.ToUpperInvariant() switch
            {
                "DEBUG" => LogLevel.Debug,
                "INFO" => LogLevel.Info,
                "WARNING" => LogLevel.Warning,
                "ERROR" => LogLevel.Error,
                "NONE" => LogLevel.None,
                _ => LogLevel.Info
            };
        }

        public static void Debug(string message, params object[] args) =>
            WriteLog(LogLevel.Debug, message, args);

        public static void Info(string message, params object[] args) =>
            WriteLog(LogLevel.Info, message, args);

        public static void Warning(string message, params object[] args) =>
            WriteLog(LogLevel.Warning, message, args);

        public static void Error(string message, params object[] args) =>
            WriteLog(LogLevel.Error, message, args);

        public static void WarningRefresh(string message, params object[] args) =>
            RefreshLog(LogLevel.Warning, message, args);

        public static void InfoRefresh(string message, params object[] args) =>
            RefreshLog(LogLevel.Info, message, args);

        public static void ErrorRefresh(string message, params object[] args) =>
            RefreshLog(LogLevel.Error, message, args);

        public static void SetStatus(string text)
        {
            lock (SyncObject)
            {
                if (Console.IsOutputRedirected)
                {
                    Console.WriteLine(text);
                    return;
                }

                Clear(ref _statusVisible, _statusLength);
                Console.WriteLine(text);
                _statusLength = text.Length;
                _statusVisible = true;
            }
        }

        public static void ClearStatus()
        {
            lock (SyncObject)
            {
                Clear(ref _statusVisible, _statusLength);
            }
        }

        public static void WriteProgress(string text)
        {
            lock (SyncObject)
            {
                if (Console.IsOutputRedirected)
                {
                    Console.WriteLine(text);
                    return;
                }

                Clear(ref _progressVisible, _progressLength);
                Console.Write(text);

                _progressLength = text.Length;
                _progressVisible = true;
            }
        }

        public static void ClearProgress()
        {
            lock (SyncObject)
            {
                Clear(ref _lineVisible, _lineLength);
                Clear(ref _progressVisible, _progressLength);
                Clear(ref _statusVisible, _statusLength);
            }
        }

        private static void WriteLog(LogLevel level, string message, params object[] args)
        {
            if (!Enabled(level))
                return;

            var info = LogInfo[level];
            lock (SyncObject)
            {
                Clear(ref _lineVisible, _lineLength);
                Clear(ref _progressVisible, _progressLength);
                WriteFormatted(info.Tag, info.Color, message, args);
            }
        }

        private static void RefreshLog(LogLevel level, string message, params object[] args)
        {
            if (!Enabled(level))
                return;

            var info = LogInfo[level];
            lock (SyncObject)
            {
                if (Console.IsOutputRedirected)
                {
                    WriteFormatted(info.Tag, info.Color, message, args);
                    return;
                }

                string timestamp = $"[{DateTime.Now:HH:mm:ss}]";
                string content = Format(message, args);
                string text = $"{timestamp}[{info.Tag}] {content}";

                ClearOutput(Math.Max(_lineLength, text.Length));
                WriteTag(timestamp, info.Tag, info.Color);
                Console.Write(content);

                _lineLength = text.Length;
                _lineVisible = true;
            }
        }

        private static bool Enabled(LogLevel level) =>
            MinimumLevel != LogLevel.None &&
            level >= MinimumLevel;

        private static void WriteFormatted(
            string tag, ConsoleColor color,
            string message, params object[] args)
        {
            WriteTag($"[{DateTime.Now:HH:mm:ss}]", tag, color);
            Console.WriteLine(Format(message, args));
        }

        private static void WriteTag(
            string timestamp,
            string tag, ConsoleColor color)
        {
            Console.Write(timestamp);
            ConsoleColor old = Console.ForegroundColor;
            Console.ForegroundColor = color;
            Console.Write($"[{tag}]");
            Console.ForegroundColor = old;
            Console.Write(' ');
        }

        private static string Format(string message, object[] args)
        {
            if (args?.Length > 0)
                try { return string.Format(message, args); }
                catch {}

            return args?.Length > 0
                ? $"{message} | {string.Join(", ", args)}"
                : message;
        }

        private static void Clear(ref bool visible, int length)
        {
            if (!visible || Console.IsOutputRedirected)
                return;

            ClearOutput(length);
            visible = false;
        }

        private static void ClearOutput(int length)
        {
            Console.Write($"\r{new string(' ', length)}\r");
        }
    }
}
