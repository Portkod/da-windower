using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using static DawndNet.Payload.Interop.Win32;

namespace DawndNet.Payload;

internal static unsafe partial class Interop
{
    #region Imports

    [LibraryImport("kernel32")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    public static partial IntPtr GetModuleHandleW(IntPtr lpModuleName); // null -> the process image

    [LibraryImport("kernel32",
        StringMarshalling = StringMarshalling.Custom, StringMarshallingCustomType = typeof(AnsiStringMarshaller))]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    public static partial IntPtr LoadLibraryA(string lpLibFileName);

    [LibraryImport("kernel32",
        StringMarshalling = StringMarshalling.Custom, StringMarshallingCustomType = typeof(AnsiStringMarshaller))]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    public static partial IntPtr GetProcAddress(IntPtr hModule, string lpProcName);

    [LibraryImport("kernel32")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool VirtualProtect(void* lpAddress, uint dwSize, uint flNewProtect, out uint lpflOldProtect);

    [LibraryImport("kernel32")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    public static partial uint GetTickCount();

    [LibraryImport("kernel32")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    public static partial uint GetModuleFileNameA(IntPtr hModule, byte* lpFilename, uint nSize);

    [LibraryImport("user32")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    public static partial int GetSystemMetrics(int nIndex);

    // Pass null to release the clip.
    [LibraryImport("user32")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool ClipCursor(Rect* lpRect);

    [LibraryImport("user32")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    public static partial IntPtr GetDC(IntPtr hWnd);

    [LibraryImport("user32")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    public static partial int ReleaseDC(IntPtr hWnd, IntPtr hDC);

    [LibraryImport("gdi32")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    public static partial int SetStretchBltMode(IntPtr hdc, int mode);

    [LibraryImport("gdi32")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    public static partial int StretchDIBits(IntPtr hdc,
        int xDest, int yDest, int wDest, int hDest,
        int xSrc, int ySrc, int wSrc, int hSrc,
        void* lpBits, void* lpbmi, uint iUsage, uint rop);

    [LibraryImport("user32")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetClientRect(IntPtr hWnd, out Rect lpRect);

    [LibraryImport("user32")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetWindowRect(IntPtr hWnd, out Rect lpRect);

    // lpRect == null clears the whole client update region.
    [LibraryImport("user32")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool ValidateRect(IntPtr hWnd, Rect* lpRect);

    [LibraryImport("user32")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool ClientToScreen(IntPtr hWnd, ref Point lpPoint);

    [LibraryImport("user32")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool AdjustWindowRectEx(ref Rect lpRect, uint dwStyle, [MarshalAs(UnmanagedType.Bool)] bool bMenu, uint dwExStyle);

    [LibraryImport("user32")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    public static partial int SetWindowLongA(IntPtr hWnd, int nIndex, int dwNewLong);

    [LibraryImport("user32")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    public static partial int GetWindowLongA(IntPtr hWnd, int nIndex);

    [LibraryImport("user32")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);

    [LibraryImport("user32")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    public static partial IntPtr CallWindowProcA(IntPtr lpPrevWndFunc, IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [LibraryImport("user32")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool PostMessageA(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [LibraryImport("user32")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [LibraryImport("user32")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool BringWindowToTop(IntPtr hWnd);

    [LibraryImport("user32")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetForegroundWindow(IntPtr hWnd);

    [LibraryImport("user32")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    public static partial IntPtr DefWindowProcA(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [LibraryImport("user32")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    public static partial int ShowCursor([MarshalAs(UnmanagedType.Bool)] bool bShow);

    [LibraryImport("kernel32")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    public static partial uint GetSystemDirectoryA(byte* lpBuffer, uint uSize);

    #endregion

    #region lParam packing

    // Signed 16-bit halves.
    public static int LoWord(IntPtr lp) => (short)((uint)lp & 0xFFFF);
    public static int HiWord(IntPtr lp) => (short)(((uint)lp >> 16) & 0xFFFF);
    public static IntPtr MakeLParam(int x, int y) => ((y & 0xFFFF) << 16) | (x & 0xFFFF);

    #endregion

    #region Vtable helpers

    // Read the function pointer at (vtable + byteOffset) for a COM object.
    public static void* Slot(IntPtr comObject, int byteOffset)
    {
        var vtable = *(byte**)comObject;
        return *(void**)(vtable + byteOffset);
    }

    // Overwrite the function pointer at (vtable + byteOffset) and return the old value.
    // The vtable is shared by every object of the interface, so hook bodies must verify
    // before assuming the call is for our surface or ddraw object.
    public static void* HookSlot(IntPtr comObject, int byteOffset, void* replacement)
    {
        var vtable = *(byte**)comObject;
        var slot = (void**)(vtable + byteOffset);
        var original = *slot;
        if (!VirtualProtect(slot, (uint)IntPtr.Size, PAGE_READWRITE, out var old))
        {
            return original;
        }

        *slot = replacement;
        VirtualProtect(slot, (uint)IntPtr.Size, old, out _);
        return original;
    }

    #endregion

    #region ASCII comparison

    public static bool AnsiEquals(byte* p, string s)
    {
        for (var i = 0; i < s.Length; i++)
        {
            if (p[i] == 0 || p[i] != (byte)s[i])
            {
                return false;
            }
        }

        return p[s.Length] == 0;
    }

    public static bool AnsiEqualsIgnoreCase(byte* p, string s)
    {
        for (var i = 0; i < s.Length; i++)
        {
            var a = p[i];
            if (a == 0)
            {
                return false;
            }

            if (ToLower(a) != ToLower((byte)s[i]))
            {
                return false;
            }
        }

        return p[s.Length] == 0;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static byte ToLower(byte c) => (byte)(c >= 'A' && c <= 'Z' ? c + 32 : c);

    #endregion

    #region Structs

    [StructLayout(LayoutKind.Sequential)]
    internal struct Rect
    {
        public int left, top, right, bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Point
    {
        public int x, y;
    }

    #endregion
}
