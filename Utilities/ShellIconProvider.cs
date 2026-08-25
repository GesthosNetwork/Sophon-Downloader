using System;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace SophonDownloader.Utilities;

public static class ShellIconProvider
{
    private const uint SHGFI_ICON = 0x000000100;
    private const uint SHGFI_LARGEICON = 0x000000000;
    private const uint SHGFI_SMALLICON = 0x000000001;
    private const uint SHGFI_USEFILEATTRIBUTES = 0x000000010;
    private const uint FILE_ATTRIBUTE_DIRECTORY = 0x00000010;
    private const uint FILE_ATTRIBUTE_NORMAL = 0x00000080;
    private const uint SHGSI_ICON = 0x000000100;
    private const uint SHGSI_SMALLICON = 0x000000001;
    private const uint SHGSI_LARGEICON = 0x000000000;
    private const int SIID_DELETE = 84;
    private static readonly object SyncLock = new();

    public static ImageSource? GetIcon(string path, bool isFolder = false)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;

        try
        {
            string normalizedPath = isFolder
                ? Directory.Exists(path) ? Path.GetFullPath(path) : path
                : File.Exists(path) ? Path.GetFullPath(path) : path;

            uint attributes = isFolder ? FILE_ATTRIBUTE_DIRECTORY : FILE_ATTRIBUTE_NORMAL;
            uint flags = SHGFI_ICON | SHGFI_LARGEICON | SHGFI_USEFILEATTRIBUTES;
            SHFILEINFO shfi;

            lock (SyncLock)
            {
                IntPtr result = SHGetFileInfo(normalizedPath, attributes, out shfi,
                    (uint)Marshal.SizeOf<SHFILEINFO>(), flags);

                if (result == IntPtr.Zero || shfi.hIcon == IntPtr.Zero)
                {
                    flags = SHGFI_ICON | SHGFI_SMALLICON | SHGFI_USEFILEATTRIBUTES;
                    SHGetFileInfo(normalizedPath, attributes, out shfi,
                        (uint)Marshal.SizeOf<SHFILEINFO>(), flags);
                }
            }

            if (shfi.hIcon == IntPtr.Zero) return null;

            try { return ConvertIconToImageSource(shfi.hIcon); }
            finally { DestroyIcon(shfi.hIcon); }
        }
        catch { return null; }
    }

    public static ImageSource? GetFileIcon(string path) => GetIcon(path);

    public static ImageSource? GetFolderIcon() => GetIcon("folder", true);

    public static ImageSource? GetDeleteIcon()
    {
        try
        {
            SHSTOCKICONINFO iconInfo = new() { cbSize = (uint)Marshal.SizeOf<SHSTOCKICONINFO>() };
            int result;

            lock (SyncLock)
                result = SHGetStockIconInfo(SIID_DELETE, SHGSI_ICON | SHGSI_SMALLICON, ref iconInfo);

            if (result != 0 || iconInfo.hIcon == IntPtr.Zero)
            {
                if (iconInfo.hIcon != IntPtr.Zero) DestroyIcon(iconInfo.hIcon);

                iconInfo = new() { cbSize = (uint)Marshal.SizeOf<SHSTOCKICONINFO>() };

                lock (SyncLock)
                    result = SHGetStockIconInfo(SIID_DELETE, SHGSI_ICON | SHGSI_LARGEICON, ref iconInfo);
            }

            if (result != 0 || iconInfo.hIcon == IntPtr.Zero)
            {
                if (iconInfo.hIcon != IntPtr.Zero) DestroyIcon(iconInfo.hIcon);
                return null;
            }

            try { return ConvertIconToImageSource(iconInfo.hIcon); }
            finally { DestroyIcon(iconInfo.hIcon); }
        }
        catch { return null; }
    }

    private static ImageSource ConvertIconToImageSource(IntPtr hIcon)
    {
        using Icon icon = Icon.FromHandle(hIcon);
        using Bitmap bitmap = icon.ToBitmap();
        IntPtr hBitmap = bitmap.GetHbitmap(System.Drawing.Color.Transparent);

        try
        {
            BitmapSource source = Imaging.CreateBitmapSourceFromHBitmap(
                hBitmap, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());

            source.Freeze();
            return source;
        }
        finally { DeleteObject(hBitmap); }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SHGetFileInfo(string pszPath, uint dwFileAttributes,
        out SHFILEINFO psfi, uint cbFileInfo, uint uFlags);

    [DllImport("shell32.dll", EntryPoint = "SHGetStockIconInfo", CallingConvention = CallingConvention.StdCall)]
    private static extern int SHGetStockIconInfo(int siid, uint uFlags, ref SHSTOCKICONINFO psii);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr hIcon);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern bool DeleteObject(IntPtr hObject);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEINFO
    {
        public IntPtr hIcon;
        public int iIcon;
        public uint dwAttributes;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szDisplayName;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)]
        public string szTypeName;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHSTOCKICONINFO
    {
        public uint cbSize;
        public IntPtr hIcon;
        public int iSysImageIndex;
        public int iIcon;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szPath;
    }
}
