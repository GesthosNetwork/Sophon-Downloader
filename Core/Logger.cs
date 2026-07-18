using System;

namespace Core
{
    internal static class Logger
    {
        public enum LogLevel
        {
            Debug = 0,
            Info = 1,
            Warning = 2,
            Error = 3,
            None = 4
        }

        public static LogLevel MinimumLevel { get; set; } = LogLevel.Info;

        private static readonly object SyncObject = new();

        private static string? _currentProgressText, _currentLineText;
        private static int _lastProgressLength, _lastLineLength;
        private static bool _progressVisible, _lineVisible;

        public static void SetLevel(string level)
        {
            MinimumLevel = level.ToUpperInvariant() switch
            {
                "DEBUG"   => LogLevel.Debug,
                "INFO"    => LogLevel.Info,
                "WARNING" => LogLevel.Warning,
                "ERROR"   => LogLevel.Error,
                "NONE"    => LogLevel.None,
                _         => LogLevel.Info
            };
        }

        public static void Info(string message, params object[] args) =>
            Write(LogLevel.Info, "INFO", ConsoleColor.Green, message, args);

        public static void Debug(string message, params object[] args) =>
            Write(LogLevel.Debug, "DEBUG", ConsoleColor.DarkGray, message, args);

        public static void Warning(string message, params object[] args) =>
            Write(LogLevel.Warning, "WARN", ConsoleColor.Yellow, message, args);

        public static void Error(string message, params object[] args) =>
            Write(LogLevel.Error, "ERROR", ConsoleColor.Red, message, args);

        public static void WarningRefresh(string message, params object[] args) =>
            RefreshLine(LogLevel.Warning, "WARN", ConsoleColor.Yellow, message, args);

        public static void InfoRefresh(string message, params object[] args) =>
            RefreshLine(LogLevel.Info, "INFO", ConsoleColor.Green, message, args);

        public static void ErrorRefresh(string message, params object[] args) =>
            RefreshLine(LogLevel.Error, "ERROR", ConsoleColor.Red, message, args);

        public static void WriteProgress(string text)
        {
            lock (SyncObject)
            {
                if (Console.IsOutputRedirected)
                {
                    Console.WriteLine(text);
                    return;
                }

                ClearOutput(Math.Max(_lastProgressLength, text.Length));
                Console.Write(text);

                _currentProgressText = text;
                _lastProgressLength = text.Length;
                _progressVisible = true;
            }
        }

        public static void ClearProgress()
        {
            lock (SyncObject)
            {
                ClearIfVisible(ref _lineVisible, _lastLineLength);
                ClearIfVisible(ref _progressVisible, _lastProgressLength);
            }
        }

        private static bool IsEnabled(LogLevel level) =>
            level >= MinimumLevel && MinimumLevel != LogLevel.None;

        private static void Write(
            LogLevel level,
            string levelText,
            ConsoleColor levelColor,
            string message,
            params object[] args)
        {
            if (!IsEnabled(level))
                return;

            lock (SyncObject)
            {
                ClearIfVisible(ref _lineVisible, _lastLineLength);
                ClearIfVisible(ref _progressVisible, _lastProgressLength);
                WriteFormatted(levelText, levelColor, message, args);
            }
        }

        private static void RefreshLine(
            LogLevel level,
            string levelText,
            ConsoleColor levelColor,
            string message,
            params object[] args)
        {
            if (!IsEnabled(level))
                return;

            lock (SyncObject)
            {
                if (Console.IsOutputRedirected)
                {
                    WriteFormatted(levelText, levelColor, message, args);
                    return;
                }

                string timestamp = $"[{DateTime.Now:HH:mm:ss}]";
                string content = FormatMessage(message, args);
                string fullText = $"{timestamp}[{levelText}] {content}";

                ClearOutput(Math.Max(_lastLineLength, fullText.Length));

                WriteTag(timestamp, levelText, levelColor);
                Console.Write(content);

                _currentLineText = fullText;
                _lastLineLength = fullText.Length;
                _lineVisible = true;
            }
        }

        private static void WriteFormatted(
            string levelText,
            ConsoleColor color,
            string message,
            params object[] args)
        {
            WriteTag($"[{DateTime.Now:HH:mm:ss}]", levelText, color);
            Console.WriteLine(FormatMessage(message, args));
        }

        private static void WriteTag(
            string timestamp,
            string levelText,
            ConsoleColor color)
        {
            Console.Write(timestamp);

            var previousColor = Console.ForegroundColor;
            Console.ForegroundColor = color;
            Console.Write($"[{levelText}]");
            Console.ForegroundColor = previousColor;

            Console.Write(' ');
        }

        private static string FormatMessage(string message, object[] args)
        {
            if (args is null || args.Length == 0)
                return message;

            try
            {
                return string.Format(message, args);
            }
            catch
            {
                return $"{message} | {JoinArgs(args)}";
            }
        }

        private static string JoinArgs(object[] args)
        {
            var parts = new string[args.Length];

            for (int i = 0; i < args.Length; i++)
                parts[i] = args[i]?.ToString() ?? "null";

            return string.Join(", ", parts);
        }

        private static void ClearOutput(int length)
        {
            Console.Write("\r");

            if (length > 0)
                Console.Write(new string(' ', length));

            Console.Write("\r");
        }

        private static void ClearIfVisible(ref bool visible, int lastLength)
        {
            if (!visible || Console.IsOutputRedirected)
                return;

            ClearOutput(lastLength);
            visible = false;
        }
    }
}
