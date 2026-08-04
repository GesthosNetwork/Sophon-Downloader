using System;
using System.Runtime.InteropServices;

namespace Core
{
    internal static class Utils
    {
        private static readonly string[] SizeSuffixes = {"B", "KB", "MB", "GB"};

        public static string FormatSize(double value, int decimalPlaces = 2)
        {
            if (value <= 0)
            {
                return value < 0
                    ? "-" + FormatSize(-value, decimalPlaces)
                    : "0 B";
            }

            int mag = Math.Min(SizeSuffixes.Length - 1, (int)Math.Log(value, 1024));
            return $"{Math.Round(value / Math.Pow(1024, mag), decimalPlaces)} {SizeSuffixes[mag]}";
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr GetConsoleWindow();

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool MoveWindow(IntPtr hWnd, int x, int y, int width, int height, bool repaint);

        [DllImport("user32.dll")]
        private static extern int GetSystemMetrics(int nIndex);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr GetStdHandle(int nStdHandle);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetConsoleMode(IntPtr hConsoleHandle, out uint lpMode);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetConsoleMode(IntPtr hConsoleHandle, uint dwMode);

        private const int STD_INPUT_HANDLE = -10;
        private const uint ENABLE_QUICK_EDIT_MODE = 0x0040;
        private const uint ENABLE_EXTENDED_FLAGS = 0x0080;

        private struct RECT
        {
            public int Left, Top, Right, Bottom;
        }

        public static void CenterConsole()
        {
            IntPtr hwnd = GetConsoleWindow();

            if (hwnd == IntPtr.Zero || !GetWindowRect(hwnd, out RECT rect))
                return;

            int width = rect.Right - rect.Left;
            int height = rect.Bottom - rect.Top;

            MoveWindow(hwnd, (GetSystemMetrics(0) - width) / 2, (GetSystemMetrics(1) - height) / 2, width, height, true);
        }

        public static void SetQuickEdit(bool enabled)
        {
            try
            {
                IntPtr handle = GetStdHandle(STD_INPUT_HANDLE);

                if (handle == IntPtr.Zero ||
                    !GetConsoleMode(handle, out uint mode))
                {
                    return;
                }

                mode |= ENABLE_EXTENDED_FLAGS;

                if (enabled)
                {
                    mode |= ENABLE_QUICK_EDIT_MODE;
                }
                else
                {
                    mode &= ~ENABLE_QUICK_EDIT_MODE;
                }

                SetConsoleMode(handle, mode);
            }
            catch {}
        }
    }
}
