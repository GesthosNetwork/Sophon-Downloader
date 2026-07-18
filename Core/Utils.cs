using System;
using System.Runtime.InteropServices;

namespace Core
{
    internal static class Utils
    {
        private static readonly string[] SizeSuffixes = { "B", "KB", "MB", "GB" };

        public static string FormatSize(double value, int decimalPlaces = 2)
        {
            if (value < 0)
                return "-" + FormatSize(-value, decimalPlaces);

            if (value == 0)
                return "0 B";

            int mag = Math.Min(SizeSuffixes.Length - 1, (int)Math.Log(value, 1024));
            double adjustedSize = value / Math.Pow(1024, mag);

            return $"{Math.Round(adjustedSize, decimalPlaces)} {SizeSuffixes[mag]}";
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr GetConsoleWindow();

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool MoveWindow(
            IntPtr hWnd,
            int x,
            int y,
            int width,
            int height,
            bool repaint);

        [DllImport("user32.dll")]
        private static extern int GetSystemMetrics(int nIndex);

        private struct RECT
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        public static void CenterConsole()
        {
            IntPtr hwnd = GetConsoleWindow();
            if (hwnd == IntPtr.Zero || !GetWindowRect(hwnd, out RECT rect))
                return;

            int width = rect.Right - rect.Left;
            int height = rect.Bottom - rect.Top;
            int left = (GetSystemMetrics(0) - width) / 2;
            int top = (GetSystemMetrics(1) - height) / 2;

            MoveWindow(hwnd, left, top, width, height, true);
        }
    }
}
