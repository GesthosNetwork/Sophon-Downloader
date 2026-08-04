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

        private static string? _statusText;
        private static string? _progressText;
        private static int _progressLength;
        private static int _statusLength;
        private static bool _progressVisible;
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
                HideBottom();
                _statusText = text;
                _statusLength = text.Length;
                _statusVisible = true;
                ShowBottom();
            }
        }

        public static void ClearStatus()
        {
            lock (SyncObject)
            {
                if (Console.IsOutputRedirected)
                    return;
                HideBottom();
                _statusVisible = false;
                _statusText = null;
                ShowBottom();
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
                HideBottom();
                _progressText = text;
                _progressLength = text.Length;
                _progressVisible = true;
                ShowBottom();
            }
        }

        public static void ClearProgress()
        {
            lock (SyncObject)
            {
                if (Console.IsOutputRedirected)
                    return;
                HideBottom();
                _progressVisible = false;
                _progressText = null;
                ShowBottom();
            }
        }

        public static void ClearAll()
        {
            lock (SyncObject)
            {
                if (Console.IsOutputRedirected)
                    return;
                HideBottom();
                _progressVisible = false;
                _statusVisible = false;
                _progressText = null;
                _statusText = null;
            }
        }

        private static void WriteLog(LogLevel level, string message, params object[] args)
        {
            if (!Enabled(level))
                return;
            var info = LogInfo[level];
            lock (SyncObject)
            {
                HideBottom();
                WriteFormatted(info.Tag, info.Color, message, args);
                ShowBottom();
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
                HideBottom();
                WriteFormatted(info.Tag, info.Color, message, args);
                ShowBottom();
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

        private static void HideBottom()
        {
            if (Console.IsOutputRedirected)
                return;
            if (_progressVisible)
            {
                ClearOutput(_progressLength);
                if (_statusVisible)
                {
                    try
                    {
                        int top = Math.Max(0, Console.CursorTop - 1);
                        Console.SetCursorPosition(0, top);
                    }
                    catch { return; }
                    ClearOutput(_statusLength);
                }
            }
            else if (_statusVisible)
            {
                ClearOutput(_statusLength);
            }
        }

        private static void ShowBottom()
        {
            if (Console.IsOutputRedirected)
                return;
            if (_statusVisible && _statusText != null)
            {
                Console.Write(_statusText);
                if (_progressVisible && _progressText != null)
                {
                    Console.WriteLine();
                    Console.Write(_progressText);
                }
            }
            else if (_progressVisible && _progressText != null)
            {
                Console.Write(_progressText);
            }
        }

        private static void ClearOutput(int length)
        {
            Console.Write($"\r{new string(' ', length)}\r");
        }
    }
}
