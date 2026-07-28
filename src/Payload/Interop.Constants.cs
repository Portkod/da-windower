namespace DawndNet.Payload;

internal static partial class Interop
{
    /// <summary>
    ///     Win32 constants (kernel32 / user32 / gdi32).
    /// </summary>
    internal static class Win32
    {
        #region Registry

        public const int ERROR_SUCCESS = 0;

        #endregion

        #region Memory protection

        public const uint PAGE_READWRITE = 0x04;
        public const uint PAGE_EXECUTE_READWRITE = 0x40;

        public const uint MEM_COMMIT = 0x1000;
        public const uint MEM_RESERVE = 0x2000;

        #endregion

        #region Window styles

        public const uint WS_OVERLAPPEDWINDOW = 0x00CF0000;
        public const uint WS_THICKFRAME = 0x00040000; // sizing border
        public const uint WS_MAXIMIZEBOX = 0x00010000;
        public const uint WS_POPUP = 0x80000000;
        public const uint WS_VISIBLE = 0x10000000;

        #endregion

        #region GetSystemMetrics indices

        public const int SM_CXSCREEN = 0;
        public const int SM_CYSCREEN = 1;

        #endregion

        #region SetWindowLong indices / SetWindowPos flags / z-order

        public const int GWL_WNDPROC = -4;
        public const int GWL_STYLE = -16;
        public const uint SWP_NOSIZE = 0x0001;
        public const uint SWP_NOMOVE = 0x0002;
        public const uint SWP_NOZORDER = 0x0004;
        public const uint SWP_NOACTIVATE = 0x0010;
        public const uint SWP_FRAMECHANGED = 0x0020;
        public const nint HWND_NOTOPMOST = -2;

        #endregion

        #region Window messages

        // Client-space mouse coords are packed in lParam across the WM_MOUSE* range.
        public const uint WM_MOUSEFIRST = 0x0200; // WM_MOUSEMOVE
        public const uint WM_MOUSELAST_CLIENT = 0x0209; // WM_MBUTTONDBLCLK (0x20A WM_MOUSEWHEEL is screen space)
        public const uint WM_ERASEBKGND = 0x0014;
        public const uint WM_SIZE = 0x0005; // client size changed (incl. live-resize drag)
        public const uint WM_PAINT = 0x000F; // an invalidated region needs repainting
        public const uint WM_ACTIVATE = 0x0006; // LOWORD(wParam)==0 is WA_INACTIVE
        public const uint WM_ACTIVATEAPP = 0x001C; // wParam==0 is deactivating
        public const uint WM_NCMOUSEMOVE = 0x00A0; // mouse over the non-client area (borders/title)
        public const uint WM_SIZING = 0x0214; // drag-resize in progress. lParam is the proposed Rect
        public const uint WM_KEYDOWN = 0x0100;
        public const uint WM_KEYUP = 0x0101;
        public const uint WM_SYSKEYUP = 0x0105;
        public const uint WM_SYSCHAR = 0x0106;
        public const uint WM_MENUCHAR = 0x0120;

        #endregion

        #region WM_SIZING drag edges (wParam)

        public const int WMSZ_LEFT = 1;
        public const int WMSZ_RIGHT = 2;
        public const int WMSZ_TOP = 3;
        public const int WMSZ_TOPLEFT = 4;
        public const int WMSZ_TOPRIGHT = 5;
        public const int WMSZ_BOTTOM = 6;
        public const int WMSZ_BOTTOMLEFT = 7;
        public const int WMSZ_BOTTOMRIGHT = 8;

        #endregion

        #region Menu-char result / ShowWindow commands

        public const int MNC_CLOSE = 1;
        public const int SW_SHOW = 5;
        public const int SW_MINIMIZE = 6;

        #endregion

        #region Virtual-key codes

        // Modifiers, for clearing stuck state after Alt-Tab.
        public const int VK_SHIFT = 0x10;
        public const int VK_CONTROL = 0x11;
        public const int VK_MENU = 0x12;
        public const int VK_LMENU = 0xA4;
        public const int VK_RMENU = 0xA5;

        // The first virtual key that is a keyboard key
        public const int VK_FIRST_KEY = 0x08;
        public const int VK_COUNT = 0x100;

        // virtual key -> scan code.
        public const uint MAPVK_VK_TO_VSC = 0;

        // Key-message lParam, repeat count 1, previous state down, transition up
        // The scan code goes in bits 16-23 and the extended-key flag in bit 24
        public const uint KEYUP_LPARAM = 0xC0000001;
        public const uint KEY_EXTENDED = 1 << 24;

        #endregion

        #region GDI

        public const uint DIB_RGB_COLORS = 0;
        public const uint SRCCOPY = 0x00CC0020;
        public const int COLORONCOLOR = 3;

        #endregion
    }

    /// <summary>
    ///     DirectDraw constants. <see href="https://learn.microsoft.com/en-us/windows/win32/api/ddraw/" />
    /// </summary>
    internal static class DirectDraw
    {
        /// <summary>
        ///     SetCooperativeLevel flags.
        /// </summary>
        public static class Scl
        {
            public const uint FULLSCREEN = 0x00000001;
            public const uint EXCLUSIVE = 0x00000010;
            public const uint NORMAL = 0x00000008;
        }

        /// <summary>
        ///     Blt() flags.
        /// </summary>
        public static class Blt
        {
            public const uint WAIT = 0x01000000;
            public const uint COLORFILL = 0x00000400;
        }

        /// <summary>
        ///     Lock() flags.
        /// </summary>
        public static class Lock
        {
            public const uint WAIT = 0x00000001;
        }

        /// <summary>
        ///     BltFast() dwTrans flags.
        /// </summary>
        public static class BltFast
        {
            public const uint WAIT = 0x00000010;
        }

        /// <summary>
        ///     DDSURFACEDESC (v1 layout, 0x6C bytes).
        /// </summary>
        public static class SurfaceDesc
        {
            public const int SIZE = 0x6C;

            /// <summary>
            ///     dwFlags values.
            /// </summary>
            public static class Flags
            {
                public const uint CAPS = 0x00000001;
                public const uint HEIGHT = 0x00000002;
                public const uint WIDTH = 0x00000004;
                public const uint PIXELFORMAT = 0x00001000;
            }

            // ReSharper disable InconsistentNaming
#pragma warning disable IDE1006
            /// <summary>
            ///     Byte offsets into the struct. pf_* are the embedded DDPIXELFORMAT fields.
            /// </summary>
            public static class Offsets
            {
                public const int dwSize = 0x00;
                public const int dwFlags = 0x04;
                public const int dwHeight = 0x08;
                public const int dwWidth = 0x0C;
                public const int dwBackBufferCount = 0x14;
                public const int lPitch = 0x10;
                public const int lpSurface = 0x24;
                public const int ddsCaps = 0x68;
                public const int pf_dwSize = 0x48;
                public const int pf_dwFlags = 0x4C;
                public const int pf_dwRGBBitCount = 0x54;
                public const int pf_dwRBitMask = 0x58;
                public const int pf_dwGBitMask = 0x5C;
                public const int pf_dwBBitMask = 0x60;
            }
#pragma warning restore IDE1006
            // ReSharper restore InconsistentNaming
        }

        /// <summary>
        ///     DDSCAPS. DirectDraw surface capabilities
        /// </summary>
        public static class Caps
        {
            /// <summary>
            ///     dwCaps values.
            /// </summary>
            public static class Flags
            {
                public const uint PRIMARYSURFACE = 0x00000200;
                public const uint FLIP = 0x00000010;
                public const uint COMPLEX = 0x00000008;
                public const uint OFFSCREENPLAIN = 0x00000040;
                public const uint SYSTEMMEMORY = 0x00000800;
            }
        }

        /// <summary>
        ///     DDPIXELFORMAT.
        /// </summary>
        public static class PixelFormat
        {
            /// <summary>
            ///     dwFlags values.
            /// </summary>
            public static class Flags
            {
                public const uint RGB = 0x00000040;
                public const uint PALETTEINDEXED8 = 0x00000020;
            }
        }

        /// <summary>
        ///     DDBLTFX (0x64 bytes).
        /// </summary>
        public static class BltFx
        {
            public const int SIZE = 0x64;

            // ReSharper disable InconsistentNaming
#pragma warning disable IDE1006
            /// <summary>
            ///     Byte offsets into the struct.
            /// </summary>
            public static class Offsets
            {
                public const int dwFillColor = 0x50; // union at 0x50
            }
#pragma warning restore IDE1006
            // ReSharper restore InconsistentNaming
        }

        // ReSharper disable InconsistentNaming
#pragma warning disable IDE1006
        /// <summary>
        ///     COM vtable byte offsets.
        ///     Slot names mirror the interface methods.
        /// </summary>
        public static class Vtbl
        {
            /// <summary>
            ///     IUnknown / IDirectDraw.
            /// </summary>
            public static class Ddraw
            {
                public const int QueryInterface = 0x00;
                public const int CreateSurface = 0x18;
                public const int CreatePalette = 0x14;
                public const int CreateClipper = 0x10;
                public const int GetDisplayMode = 0x30;
                public const int SetCooperativeLevel = 0x50;
                public const int SetDisplayMode = 0x54;
            }

            /// <summary>
            ///     IDirectDrawSurface.
            /// </summary>
            public static class Surface
            {
                public const int Release = 0x08; // IUnknown
                public const int Blt = 0x14;
                public const int BltFast = 0x1C;
                public const int Flip = 0x2C;
                public const int GetDC = 0x44;
                public const int ReleaseDC = 0x68;
                public const int Lock = 0x64;
                public const int Unlock = 0x80;
                public const int SetClipper = 0x70;
                public const int SetPalette = 0x7C;
                public const int GetSurfaceDesc = 0x58;
                public const int Restore = 0x6C;
            }

            /// <summary>
            ///     IDirectDrawPalette.
            /// </summary>
            public static class Palette
            {
                public const int GetEntries = 0x10;
            }

            /// <summary>
            ///     IDirectDrawClipper.
            /// </summary>
            public static class Clipper
            {
                public const int SetHWnd = 0x20;
            }
        }
#pragma warning restore IDE1006
        // ReSharper restore InconsistentNaming

        #region Result codes

        public const int DD_OK = 0;
        public const int DDERR_SURFACELOST = unchecked((int)0x887601C2);

        #endregion
    }
}
