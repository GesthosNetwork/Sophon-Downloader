using System;

namespace Sophon.Helper
{
    public enum LogLevel
    {
        Info, Warning, Error, Debug
    }

    public static class Logger
    {
        public static event EventHandler<LogStruct> LogHandler;

        internal static void PushLogDebug(this object obj, string message)
        {
            LogHandler?.Invoke(obj, new LogStruct
            {
                LogLevel = LogLevel.Debug,
                Message = message
            });
        }

        internal static void PushLogInfo(this object obj, string message)
        {
            LogHandler?.Invoke(obj, new LogStruct
            {
                LogLevel = LogLevel.Info,
                Message = message
            });
        }

        internal static void PushLogWarning(this object obj, string message)
        {
            LogHandler?.Invoke(obj, new LogStruct
            {
                LogLevel = LogLevel.Warning,
                Message = message
            });
        }

        internal static void PushLogError(this object obj, string message)
        {
            LogHandler?.Invoke(obj, new LogStruct
            {
                LogLevel = LogLevel.Error,
                Message = message
            });
        }
    }

    public struct LogStruct
    {
        public LogLevel LogLevel;
        public string Message;
    }
}
