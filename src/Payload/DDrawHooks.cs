using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using DawndNet.Shared;
using static DawndNet.Payload.Interop;
using static DawndNet.Payload.Interop.Win32;
using static DawndNet.Payload.Interop.DirectDraw;

namespace DawndNet.Payload;

internal static unsafe class DDrawHooks
{
    private const int Width = 640;
    private const int Height = 480;

    private const bool EnableMultiInstance = true;

    // Forces the client's DisplayMode registry value to 0
    private const bool ForceLegacyDisplayMode = true;

    // Snap distance for exact integer scale resizing.
    private const int SnapPx = 32;

    // Flicker cursor fix, present game+cursor at the same time.
    private static bool _cursorFix = true;

    // Maintain aspect ratio when resizing the window
    private static bool _lockAspectRatio = true;

    private static void* _realDirectDrawCreate; // proxy mode, system ddraw
    private static void* _origDirectDrawCreate; // inject mode, IAT original
    private static void* _origCreateSurface;
    private static void* _origSetCoopLevel;
    private static void* _origSetDisplayMode;
    private static void* _origGetDisplayMode;
    private static void* _origFlip;
    private static void* _origBlt;
    private static void* _origBltFast;
    private static void* _origUnlock;
    private static void* _origSetPalette;
    private static void* _origCreatePalette;
    private static IntPtr _palette; // client palette attached to our 8bpp offscreen
    private static void* _dibBits; // scratch DIB rows for the palettized present
    private static void* _dibInfo; // BITMAPINFOHEADER + 256 RGBQUAD
    private static int _dibW, _dibH;
    private static int _presentCount;
    private static int _bltFastCount;
    private static int _bltCount;
    private static int _unlockCount;
    private static void* _origCreateMutexA;
    private static void* _origRegQueryValueExA;
    private static void* _origQueryInterface;
    private static void* _origPeekMessageA;
    private static void* _origGetMessageA;
    private static IntPtr _origWndProc;
    private static bool _presentPending; // a composed frame is waiting to be shown
    private static int _flushCount;

    private static IntPtr _ddraw;
    private static IntPtr _hwnd;
    private static IntPtr _offscreen;
    private static IntPtr _primary;
    private static bool _auxInstalled;
    private static bool _vtableHooked;
    private static bool _windowedReady;
    private static bool _windowFixed;
    private static bool _cursorVisible;
    private static bool _selfMinimizing; // for borderless SW_MINIMIZE

    private static int _renderW = Width;
    private static int _renderH = Height;

    // Color depth from the client's SetDisplayMode
    // 8-bit palettized for 2.x, 16-bit for 3.x+
    private static int _renderBpp = 16;

    // True once the client requests exclusive fullscreen.
    // If false it uses a native windowed mode then leave its DirectDraw alone.
    private static bool _engaged;

    // Borderless fullscreen, a WS_POPUP filling the monitor with the frame letterboxed to 4:3.
    private static bool _borderless;
    private static bool _borderlessRequested;

    // Skip the intro video by forcing Bink to only play 1 frame
    private static bool _skipIntro = true;
    private static bool _configResolved;
    private static void* _origBinkOpen;

    #region Entry points

    public static void InstallInjected(IntPtr configParam)
    {
        ResolveConfig(configParam);
        InstallAuxHooks();
        var image = GetModuleHandleW(IntPtr.Zero);
        _origDirectDrawCreate = PeImage.HookImport(image, "ddraw.dll", "DirectDrawCreate",
            (delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, IntPtr, int>)&DirectDrawCreateInjected);
        Log.Write(_origDirectDrawCreate != null
            ? "Inject mode: DirectDrawCreate IAT hook installed."
            : "Inject mode: WARNING ddraw!DirectDrawCreate not found.");
    }

    // Proxy entry. Forward to the real ddraw, then hook what it returns.
    public static int DirectDrawCreateProxy(IntPtr guid, IntPtr* ppDD, IntPtr outer)
    {
        ResolveConfig(IntPtr.Zero); // no injector word -> read ini
        InstallAuxHooks(); // best-effort here, the injector installs these earlier
        if (!EnsureRealDDraw())
        {
            return unchecked((int)0x80004005); // E_FAIL
        }

        var real = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, IntPtr, int>)_realDirectDrawCreate;
        var hr = real(guid, ppDD, outer);
        if (hr == DD_OK && ppDD != null && *ppDD != IntPtr.Zero)
        {
            OnDirectDrawCreated(*ppDD);
        }

        return hr;
    }

    // Resolve the options once into the booleans DDrawHooks acts on.
    private static void ResolveConfig(IntPtr configParam)
    {
        if (_configResolved)
        {
            return;
        }

        _configResolved = true;

        PayloadConfig cfg = PayloadConfig.Resolve(configParam);
        _borderlessRequested = cfg.BorderlessRequested;
        _skipIntro = cfg.SkipIntro;
        _lockAspectRatio = cfg.LockAspectRatio;
        _cursorFix = cfg.CursorFix;
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static int DirectDrawCreateInjected(IntPtr guid, IntPtr* ppDD, IntPtr outer)
    {
        var orig = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, IntPtr, int>)_origDirectDrawCreate;
        var hr = orig(guid, ppDD, outer);
        Log.Write($"DirectDrawCreate (inject) hr=0x{hr:X8} dd=0x{(hr == DD_OK && ppDD != null ? *ppDD : 0):X}");
        if (hr == DD_OK && ppDD != null && *ppDD != IntPtr.Zero)
        {
            OnDirectDrawCreated(*ppDD);
        }

        return hr;
    }

    private static bool EnsureRealDDraw()
    {
        if (_realDirectDrawCreate != null)
        {
            return true;
        }

        // Load the genuine ddraw by full path so we do not re-enter this proxy.
        var dir = stackalloc byte[260];
        var n = GetSystemDirectoryA(dir, 260);
        if (n == 0 || n > 240)
        {
            return false;
        }

        var path = new Span<byte>(dir, (int)n);
        var sysdir = Encoding.ASCII.GetString(path);
        var real = LoadLibraryA(sysdir + "\\ddraw.dll");
        if (real == IntPtr.Zero)
        {
            Log.Write("Proxy: could not load the system ddraw.dll.");
            return false;
        }

        _realDirectDrawCreate = (void*)GetProcAddress(real, "DirectDrawCreate");
        return _realDirectDrawCreate != null;
    }

    #endregion

    #region Early aux hooks (kernel32 / advapi32)

    private static void InstallAuxHooks()
    {
        if (_auxInstalled)
        {
            return;
        }

        _auxInstalled = true;
        var image = GetModuleHandleW(IntPtr.Zero);

        if (EnableMultiInstance)
        {
            _origCreateMutexA = PeImage.HookImport(image, "kernel32.dll", "CreateMutexA",
                (delegate* unmanaged[Stdcall]<IntPtr, int, IntPtr, IntPtr>)&CreateMutexAHook);
            if (_origCreateMutexA != null)
            {
                Log.Write("Multi-instance: kernel32!CreateMutexA hooked.");
            }
        }

        if (_skipIntro)
        {
            // Force the video to one frame and block it from being drawn and playing sound
            _origBinkOpen = PeImage.HookImport(image, "binkw32.dll", "_BinkOpen@8",
                (delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr>)&BinkOpenHook);
            PeImage.HookImport(image, "binkw32.dll", "_BinkCopyToBuffer@28",
                (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, int, uint, uint, uint, uint, int>)&BinkCopyToBufferHook);
            PeImage.HookImport(image, "binkw32.dll", "_BinkSetSoundSystem@8",
                (delegate* unmanaged[Stdcall]<IntPtr, uint, int>)&BinkSetSoundSystemHook);
            Log.Write(_origBinkOpen != null
                ? "Skip intro: binkw32 BinkOpen/CopyToBuffer/SetSoundSystem hooked."
                : "Skip intro: binkw32 not imported (no Bink intro).");
        }

        if (ForceLegacyDisplayMode)
        {
            _origRegQueryValueExA = PeImage.HookImport(image, "advapi32.dll", "RegQueryValueExA",
                (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr, IntPtr, IntPtr, IntPtr, int>)&RegQueryValueExAHook);
            if (_origRegQueryValueExA != null)
            {
                Log.Write("DisplayMode: advapi32!RegQueryValueExA hooked (DisplayMode -> 0).");
            }
        }

        if (_cursorFix)
        {
            _origPeekMessageA = PeImage.HookImport(image, "user32.dll", "PeekMessageA",
                (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, uint, uint, uint, int>)&PeekMessageAHook);
            _origGetMessageA = PeImage.HookImport(image, "user32.dll", "GetMessageA",
                (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, uint, uint, int>)&GetMessageAHook);
            Log.Write($"Present coalescing: PeekMessageA hooked={_origPeekMessageA != null}, " +
                      $"GetMessageA hooked={_origGetMessageA != null}.");
        }
    }

    #endregion

    #region IDirectDraw vtable

    private static void OnDirectDrawCreated(IntPtr dd)
    {
        if (_vtableHooked)
        {
            return;
        }

        _vtableHooked = true;
        _ddraw = dd;
        _origQueryInterface = HookSlot(dd, Vtbl.Ddraw.QueryInterface, (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr*, int>)&QueryInterfaceHook);
        _origSetCoopLevel = HookSlot(dd, Vtbl.Ddraw.SetCooperativeLevel, (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, uint, int>)&SetCoopLevelHook);
        _origSetDisplayMode = HookSlot(dd, Vtbl.Ddraw.SetDisplayMode, (delegate* unmanaged[Stdcall]<IntPtr, uint, uint, uint, int>)&SetDisplayModeHook);
        _origGetDisplayMode = HookSlot(dd, Vtbl.Ddraw.GetDisplayMode, (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, int>)&GetDisplayModeHook);
        _origCreateSurface = HookSlot(dd, Vtbl.Ddraw.CreateSurface, (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr*, IntPtr, int>)&CreateSurfaceHook);
        _origCreatePalette = HookSlot(dd, Vtbl.Ddraw.CreatePalette, (delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr, IntPtr*, IntPtr, int>)&CreatePaletteHook);
        Log.Write("IDirectDraw (v1) vtable hooks installed.");
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static int QueryInterfaceHook(IntPtr thisPtr, IntPtr riid, IntPtr* ppv)
    {
        var data1 = riid != IntPtr.Zero ? *(uint*)riid : 0;
        var orig = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr*, int>)_origQueryInterface;
        var hr = orig(thisPtr, riid, ppv);
        var which = data1 switch
        {
            0x6C14DB80 => "IDirectDraw(v1)",
            0xB3A6F3E0 => "IDirectDraw2",
            0x9C59509A => "IDirectDraw4",
            0x15E65EC0 => "IDirectDraw7",
            _ => "other"
        };
        Log.Write($"QueryInterface {which} (Data1=0x{data1:X8}) hr=0x{hr:X8} out=0x{(hr == DD_OK && ppv != null ? *ppv : 0):X}");
        return hr;
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static int SetCoopLevelHook(IntPtr thisPtr, IntPtr hwnd, uint flags)
    {
        _hwnd = hwnd;
        var orig = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, uint, int>)_origSetCoopLevel;

        // Only convert exclusive fullscreen.
        // Native windowed mode render on a GDI display path so leave it alone
        if ((flags & (Scl.FULLSCREEN | Scl.EXCLUSIVE)) == 0)
        {
            Log.Write($"SetCooperativeLevel(flags=0x{flags:X}) native windowed -> pass through");
            return orig(thisPtr, hwnd, flags);
        }

        if (!_engaged)
        {
            _engaged = true;
            _renderW = Width;
            _renderH = Height;
            Log.Write("Fullscreen cooperative level detected -> converting to windowed.");
            SetupWindow(hwnd, true);
        }

        Log.Write($"SetCooperativeLevel(flags=0x{flags:X}) -> forcing NORMAL");
        return orig(thisPtr, hwnd, Scl.NORMAL);
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static int SetDisplayModeHook(IntPtr thisPtr, uint w, uint h, uint bpp)
    {
        if (!_engaged)
        {
            var o = (delegate* unmanaged[Stdcall]<IntPtr, uint, uint, uint, int>)_origSetDisplayMode;
            return o(thisPtr, w, h, bpp);
        }

        if (w > 0 && h > 0)
        {
            _renderW = (int)w;
            _renderH = (int)h;
        }

        if (bpp > 0)
        {
            _renderBpp = (int)bpp;
        }

        Log.Write($"SetDisplayMode({w}x{h}x{bpp}) swallowed; render format {_renderW}x{_renderH}x{_renderBpp}");
        return DD_OK;
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static int GetDisplayModeHook(IntPtr thisPtr, IntPtr lpDesc)
    {
        if (!_engaged)
        {
            var o = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, int>)_origGetDisplayMode;
            return o(thisPtr, lpDesc);
        }

        if (lpDesc == IntPtr.Zero)
        {
            return unchecked((int)0x80070057);
        }

        var d = (byte*)lpDesc;
        Write32(d, SurfaceDesc.Offsets.dwSize, SurfaceDesc.SIZE);
        Write32(d, SurfaceDesc.Offsets.dwFlags, SurfaceDesc.Flags.WIDTH | SurfaceDesc.Flags.HEIGHT | SurfaceDesc.Flags.PIXELFORMAT);
        Write32(d, SurfaceDesc.Offsets.dwWidth, (uint)_renderW);
        Write32(d, SurfaceDesc.Offsets.dwHeight, (uint)_renderH);
        WritePixelFormat(d);
        return DD_OK;
    }

    #endregion

    #region Surface creation and present

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static int CreateSurfaceHook(IntPtr thisPtr, IntPtr lpDesc, IntPtr* ppSurface, IntPtr outer)
    {
        var orig = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr*, IntPtr, int>)_origCreateSurface;

        if (!_engaged)
        {
            return orig(thisPtr, lpDesc, ppSurface, outer);
        }

        var d = (byte*)lpDesc;
        var caps = lpDesc != IntPtr.Zero ? Read32(d, SurfaceDesc.Offsets.ddsCaps) : 0;
        Log.Write($"CreateSurface caps=0x{caps:X} (primary={(caps & Caps.Flags.PRIMARYSURFACE) != 0})");
        if ((caps & Caps.Flags.PRIMARYSURFACE) == 0)
        {
            if (lpDesc != IntPtr.Zero && (Read32(d, SurfaceDesc.Offsets.dwFlags) & SurfaceDesc.Flags.PIXELFORMAT) == 0)
            {
                Write32(d, SurfaceDesc.Offsets.dwFlags, Read32(d, SurfaceDesc.Offsets.dwFlags) | SurfaceDesc.Flags.PIXELFORMAT);
                WritePixelFormat(d);
            }

            return orig(thisPtr, lpDesc, ppSurface, outer);
        }

        Write32(d, SurfaceDesc.Offsets.dwFlags, SurfaceDesc.Flags.CAPS | SurfaceDesc.Flags.WIDTH | SurfaceDesc.Flags.HEIGHT | SurfaceDesc.Flags.PIXELFORMAT);
        Write32(d, SurfaceDesc.Offsets.dwWidth, (uint)_renderW);
        Write32(d, SurfaceDesc.Offsets.dwHeight, (uint)_renderH);
        Write32(d, SurfaceDesc.Offsets.dwBackBufferCount, 0);
        // 8-bit palettized surfaces are not reliably available in video memory on a 32-bit
        // desktop. Pin them to system memory so the format request is honored, otherwise we
        // silently get a 32-bit surface and the client's index writes land as near-black pixels.
        Write32(d, SurfaceDesc.Offsets.ddsCaps, Caps.Flags.OFFSCREENPLAIN | (_renderBpp == 8 ? Caps.Flags.SYSTEMMEMORY : 0));
        WritePixelFormat(d);

        var hr = orig(thisPtr, lpDesc, ppSurface, outer);
        if (hr != DD_OK || ppSurface == null || *ppSurface == IntPtr.Zero)
        {
            Log.Write($"CreateSurface(offscreen) failed: 0x{hr:X8}");
            return hr;
        }

        _offscreen = *ppSurface;
        LogSurfaceDesc(_offscreen, "offscreen");
        BuildWindowedPrimary(thisPtr);

        // The fullscreen client presents a frame by BltFast-ing it onto the primary (our offscreen),
        // so we mirror the offscreen into the window after each present-blit. Flip is hooked too as a fallback.
        // Clients differ in how they get a finished frame onto the primary.
        // 7.x BltFasts it, older ones Blt from a work surface or software-render via Lock/Unlock.
        // Hook them all. BltFast wins if a client uses it, so the others cannot add redundant presents.
        _origBlt = Slot(_offscreen, Vtbl.Surface.Blt); // captured before hooking, also our present
        _origBltFast = HookSlot(_offscreen, Vtbl.Surface.BltFast, (delegate* unmanaged[Stdcall]<IntPtr, uint, uint, IntPtr, IntPtr, uint, int>)&BltFastHook);
        HookSlot(_offscreen, Vtbl.Surface.Blt, (delegate* unmanaged[Stdcall]<IntPtr, Rect*, IntPtr, Rect*, uint, void*, int>)&BltHook);
        _origFlip = HookSlot(_offscreen, Vtbl.Surface.Flip, (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, uint, int>)&FlipHook);
        _origUnlock = HookSlot(_offscreen, Vtbl.Surface.Unlock, (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, int>)&UnlockHook);
        // Palettized (8bpp) clients must attach a palette to the surface they think is
        // the primary, or our 8bpp->desktop present converts every index to black.
        _origSetPalette = HookSlot(_offscreen, Vtbl.Surface.SetPalette, (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, int>)&SetPaletteHook);
        Log.Write("Offscreen render surface ready; Blt/BltFast/Flip/Unlock/SetPalette hooked.");
        return hr;
    }

    private static void LogSurfaceDesc(IntPtr surface, string label)
    {
        var sd = stackalloc byte[SurfaceDesc.SIZE];
        for (var i = 0; i < SurfaceDesc.SIZE; i++)
        {
            sd[i] = 0;
        }

        Write32(sd, SurfaceDesc.Offsets.dwSize, SurfaceDesc.SIZE);
        var getDesc = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, int>)Slot(surface, Vtbl.Surface.GetSurfaceDesc);
        var hr = getDesc(surface, (IntPtr)sd);
        Log.Write($"{label} desc hr=0x{hr:X8} {Read32(sd, SurfaceDesc.Offsets.dwWidth)}x{Read32(sd, SurfaceDesc.Offsets.dwHeight)} " +
                  $"bpp={Read32(sd, SurfaceDesc.Offsets.pf_dwRGBBitCount)} pfFlags=0x{Read32(sd, SurfaceDesc.Offsets.pf_dwFlags):X} caps=0x{Read32(sd, SurfaceDesc.Offsets.ddsCaps):X}");
    }

    private static void BuildWindowedPrimary(IntPtr dd)
    {
        if (_windowedReady || _hwnd == IntPtr.Zero)
        {
            return;
        }

        var desc = stackalloc byte[SurfaceDesc.SIZE];
        for (var i = 0; i < SurfaceDesc.SIZE; i++)
        {
            desc[i] = 0;
        }

        Write32(desc, SurfaceDesc.Offsets.dwSize, SurfaceDesc.SIZE);
        Write32(desc, SurfaceDesc.Offsets.dwFlags, SurfaceDesc.Flags.CAPS);
        Write32(desc, SurfaceDesc.Offsets.ddsCaps, Caps.Flags.PRIMARYSURFACE);

        var createSurface = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr*, IntPtr, int>)_origCreateSurface;
        IntPtr primary;
        var hr = createSurface(dd, (IntPtr)desc, &primary, IntPtr.Zero);
        if (hr != DD_OK)
        {
            Log.Write($"CreateSurface(primary) failed: 0x{hr:X8}");
            return;
        }

        _primary = primary;

        var createClipper = (delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr*, IntPtr, int>)Slot(dd, Vtbl.Ddraw.CreateClipper);
        IntPtr clipper;
        if (createClipper(dd, 0, &clipper, IntPtr.Zero) == DD_OK)
        {
            var setHwnd = (delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr, int>)Slot(clipper, Vtbl.Clipper.SetHWnd);
            setHwnd(clipper, 0, _hwnd);
            var setClipper = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, int>)Slot(_primary, Vtbl.Surface.SetClipper);
            setClipper(_primary, clipper);
        }

        _windowedReady = true;
        Log.Write("Windowed primary + clipper attached.");
    }

    // Present trigger. The client BltFasts the finished frame onto the offscreen "primary".
    // Do the real blit, then mirror the offscreen to the window.
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static int BltFastHook(IntPtr thisPtr, uint x, uint y, IntPtr src, IntPtr srcRect, uint trans)
    {
        var orig = (delegate* unmanaged[Stdcall]<IntPtr, uint, uint, IntPtr, IntPtr, uint, int>)_origBltFast;
        var hr = orig(thisPtr, x, y, src, srcRect, trans);
        if (thisPtr == _offscreen)
        {
            if (_bltFastCount < 3 || hr != DD_OK)
            {
                Log.Write($"BltFast->offscreen #{_bltFastCount} src=0x{src:X} hr=0x{hr:X8}");
            }

            _bltFastCount++;
            RequestPresent();
        }

        return hr;
    }

    // Present triggers for clients that compose with Blt, or software-render into the
    // surface and signal completion with Unlock. Gated on BltFast never having been
    // used, so a BltFast-based client (7.x) keeps its single present path.
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static int BltHook(IntPtr thisPtr, Rect* destRect, IntPtr src, Rect* srcRect, uint flags, void* bltfx)
    {
        var orig = (delegate* unmanaged[Stdcall]<IntPtr, Rect*, IntPtr, Rect*, uint, void*, int>)_origBlt;
        var hr = orig(thisPtr, destRect, src, srcRect, flags, bltfx);
        if (thisPtr == _offscreen)
        {
            if (_bltCount < 3 || hr != DD_OK)
            {
                var dr = destRect != null ? $"({destRect->left},{destRect->top},{destRect->right},{destRect->bottom})" : "NULL";
                var sr = srcRect != null ? $"({srcRect->left},{srcRect->top},{srcRect->right},{srcRect->bottom})" : "NULL";
                Log.Write($"Blt->offscreen #{_bltCount} src=0x{src:X} dest={dr} srcRect={sr} flags=0x{flags:X} hr=0x{hr:X8}");
            }

            _bltCount++;
            if (_bltFastCount == 0)
            {
                RequestPresent();
            }
        }

        return hr;
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static int UnlockHook(IntPtr thisPtr, IntPtr ptr)
    {
        var orig = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, int>)_origUnlock;
        var hr = orig(thisPtr, ptr);
        if (thisPtr == _offscreen)
        {
            if (_unlockCount < 3 || hr != DD_OK)
            {
                Log.Write($"Unlock->offscreen #{_unlockCount} hr=0x{hr:X8}");
            }

            _unlockCount++;
            if (_bltFastCount == 0)
            {
                RequestPresent();
            }
        }

        return hr;
    }

    // Diagnostics for palettized clients
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static int CreatePaletteHook(IntPtr thisPtr, uint caps, IntPtr entries, IntPtr* ppPalette, IntPtr outer)
    {
        var orig = (delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr, IntPtr*, IntPtr, int>)_origCreatePalette;
        var hr = orig(thisPtr, caps, entries, ppPalette, outer);
        Log.Write($"CreatePalette caps=0x{caps:X} hr=0x{hr:X8} pal=0x{(hr == DD_OK && ppPalette != null ? *ppPalette : 0):X}");
        return hr;
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static int SetPaletteHook(IntPtr thisPtr, IntPtr palette)
    {
        var orig = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, int>)_origSetPalette;
        var hr = orig(thisPtr, palette);
        if (thisPtr == _offscreen && hr == DD_OK)
        {
            _palette = palette; // the palette our palettized present must convert through
        }

        Log.Write($"SetPalette on 0x{thisPtr:X} (offscreen=0x{_offscreen:X}) pal=0x{palette:X} hr=0x{hr:X8}");
        return hr;
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static int FlipHook(IntPtr thisPtr, IntPtr targetOverride, uint flags)
    {
        if (thisPtr == _offscreen)
        {
            RequestPresent();
            return DD_OK;
        }

        var orig = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, uint, int>)_origFlip;
        return orig(thisPtr, targetOverride, flags);
    }

    // The area of the client rect the frame is drawn into. Normally the whole client (stretch to fill).
    // In borderless it is centered and aspect-preserved (4:3), with the remainder left for black bars.
    private static void ContentRect(int cw, int ch, out int ox, out int oy, out int cwOut, out int chOut)
    {
        if (!_borderless)
        {
            ox = 0;
            oy = 0;
            cwOut = cw;
            chOut = ch;
            return;
        }

        var widthAtFullHeight = ch * _renderW / _renderH;
        if (widthAtFullHeight <= cw)
        {
            cwOut = widthAtFullHeight;
            chOut = ch;
        }
        else
        {
            cwOut = cw;
            chOut = cw * _renderH / _renderW;
        }

        ox = (cw - cwOut) / 2;
        oy = (ch - chOut) / 2;
    }

    // A blit finished a (partial) frame. Either present immediately, or mark it pending and let
    // the next message-queue read flush it (CursorFix).
    // Coalescing is limited to BltFast-based clients (5.x+), which draw the cursor as a separate
    // blit after the scene).
    // Older clients (<=4.x) present via Blt/Unlock with no flicker.
    private static void RequestPresent()
    {
        if (!_cursorFix || _bltFastCount == 0)
        {
            Present();
            return;
        }

        _presentPending = true;
    }

    // Show the pending frame, if any. Called between finished frames, so the frame we
    // present is always complete (never a half-composed one with the cursor mid-draw).
    private static void FlushPresent()
    {
        if (!_presentPending)
        {
            return;
        }

        _presentPending = false;
        if (_flushCount < 3)
        {
            Log.Write($"Present flush #{_flushCount} (message-pump coalesced).");
        }

        _flushCount++;
        Present();
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static int PeekMessageAHook(IntPtr msg, IntPtr hwnd, uint filterMin, uint filterMax, uint remove)
    {
        FlushPresent();
        var orig = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, uint, uint, uint, int>)_origPeekMessageA;
        return orig(msg, hwnd, filterMin, filterMax, remove);
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static int GetMessageAHook(IntPtr msg, IntPtr hwnd, uint filterMin, uint filterMax)
    {
        FlushPresent();
        var orig = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, uint, uint, int>)_origGetMessageA;
        return orig(msg, hwnd, filterMin, filterMax);
    }

    // Restore a lost DirectDraw surface. Idempotent, a no-op DD_OK if it was not lost.
    private static void RestoreSurface(IntPtr surface)
    {
        if (surface == IntPtr.Zero)
        {
            return;
        }

        var restore = (delegate* unmanaged[Stdcall]<IntPtr, int>)Slot(surface, Vtbl.Surface.Restore);
        var hr = restore(surface);
        Log.Write($"Restore surface 0x{surface:X} hr=0x{hr:X8}");
    }

    // Copy the offscreen frame into the window (stretched, or letterboxed in borderless).
    private static void Present()
    {
        if (_primary == IntPtr.Zero || _hwnd == IntPtr.Zero || _origBlt == null)
        {
            return;
        }

        if (!GetClientRect(_hwnd, out var client))
        {
            return;
        }

        var cw = client.right - client.left;
        var ch = client.bottom - client.top;
        if (cw <= 0 || ch <= 0)
        {
            return; // window minimized / no client area to present into
        }

        var origin = new Point { x = 0, y = 0 };
        ClientToScreen(_hwnd, ref origin);

        ContentRect(cw, ch, out var ox, out var oy, out var cwc, out var chc);
        Rect dest;
        dest.left = origin.x + ox;
        dest.top = origin.y + oy;
        dest.right = dest.left + cwc;
        dest.bottom = dest.top + chc;

        // 8-bit clients: DirectDraw will not convert palettized -> RGB on the present
        // blit (it succeeds and draws nothing), so go through GDI, which does the
        // palette lookup and the stretch for us.
        if (_renderBpp == 8)
        {
            if (_borderless)
            {
                Rect winB;
                winB.left = origin.x;
                winB.top = origin.y;
                winB.right = origin.x + cw;
                winB.bottom = origin.y + ch;
                FillBars(&winB, &dest);
            }

            PresentPalettized(ox, oy, cwc, chc);
            _presentCount++;
            return;
        }

        if (_borderless)
        {
            Rect win;
            win.left = origin.x;
            win.top = origin.y;
            win.right = origin.x + cw;
            win.bottom = origin.y + ch;
            FillBars(&win, &dest);
        }

        var blt = (delegate* unmanaged[Stdcall]<IntPtr, Rect*, IntPtr, Rect*, uint, void*, int>)_origBlt;
        var hr = blt(_primary, &dest, _offscreen, null, Blt.WAIT, null);
        if (hr == DDERR_SURFACELOST)
        {
            RestoreSurface(_primary);
            RestoreSurface(_offscreen);
            hr = blt(_primary, &dest, _offscreen, null, Blt.WAIT, null);
        }

        if (_presentCount < 3 || hr != DD_OK)
        {
            Log.Write($"Present #{_presentCount} client={cw}x{ch} Blt hr=0x{hr:X8} dest=({dest.left},{dest.top},{dest.right},{dest.bottom})");
        }

        _presentCount++;
    }

    // Present an 8-bit palettized frame. Lock the offscreen, hand its indices plus the client's
    // palette to GDI as a DIB, and let StretchDIBits convert and scale it into the window.
    // Destination is in client coordinates (GetDC gives a client-area DC).
    private static void PresentPalettized(int x, int y, int w, int h)
    {
        if (_palette == IntPtr.Zero || _offscreen == IntPtr.Zero)
        {
            return;
        }

        if (!EnsureDib())
        {
            return;
        }

        // Refresh the color table each frame, since clients change palettes as they go.
        var entries = stackalloc byte[256 * 4];
        var getEntries = (delegate* unmanaged[Stdcall]<IntPtr, uint, uint, uint, void*, int>)Slot(_palette, Vtbl.Palette.GetEntries);
        if (getEntries(_palette, 0, 0, 256, entries) != DD_OK)
        {
            return;
        }

        var quads = (byte*)_dibInfo + 40; // RGBQUAD table follows the header
        for (var i = 0; i < 256; i++)
        {
            // PALETTEENTRY is R,G,B,flags and RGBQUAD is B,G,R,reserved.
            quads[i * 4 + 0] = entries[i * 4 + 2];
            quads[i * 4 + 1] = entries[i * 4 + 1];
            quads[i * 4 + 2] = entries[i * 4 + 0];
            quads[i * 4 + 3] = 0;
        }

        var sd = stackalloc byte[SurfaceDesc.SIZE];
        for (var i = 0; i < SurfaceDesc.SIZE; i++)
        {
            sd[i] = 0;
        }

        Write32(sd, SurfaceDesc.Offsets.dwSize, SurfaceDesc.SIZE);
        var lockFn = (delegate* unmanaged[Stdcall]<IntPtr, Rect*, IntPtr, uint, IntPtr, int>)Slot(_offscreen, Vtbl.Surface.Lock);
        var hr = lockFn(_offscreen, null, (IntPtr)sd, Lock.WAIT, IntPtr.Zero);
        if (hr == DDERR_SURFACELOST)
        {
            // Same recovery as the RGB path: restore the lost offscreen and retry once.
            RestoreSurface(_offscreen);
            hr = lockFn(_offscreen, null, (IntPtr)sd, Lock.WAIT, IntPtr.Zero);
        }

        if (hr != DD_OK)
        {
            if (_presentCount < 3)
            {
                Log.Write($"Palettized present: Lock failed 0x{hr:X8}");
            }

            return;
        }

        var pitch = (int)Read32(sd, SurfaceDesc.Offsets.lPitch);
        var src = (byte*)(nint)Read32(sd, SurfaceDesc.Offsets.lpSurface);
        var stride = (_renderW + 3) & ~3; // DIB rows are DWORD aligned
        if (src != null)
        {
            for (var row = 0; row < _renderH; row++)
            {
                Buffer.MemoryCopy(src + (long)row * pitch, (byte*)_dibBits + (long)row * stride, stride, _renderW);
            }
        }

        var unlockFn = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, int>)_origUnlock;
        unlockFn(_offscreen, IntPtr.Zero);

        var hdc = GetDC(_hwnd);
        if (hdc != IntPtr.Zero)
        {
            _ = SetStretchBltMode(hdc, COLORONCOLOR);
            _ = StretchDIBits(hdc, x, y, w, h, 0, 0, _renderW, _renderH,
                _dibBits, _dibInfo, DIB_RGB_COLORS, SRCCOPY);
            _ = ReleaseDC(_hwnd, hdc);
        }
    }

    private static bool EnsureDib()
    {
        if (_dibBits != null && _dibW == _renderW && _dibH == _renderH)
        {
            return true;
        }

        if (_dibBits != null)
        {
            NativeMemory.Free(_dibBits);
            _dibBits = null;
        }

        if (_dibInfo != null)
        {
            NativeMemory.Free(_dibInfo);
            _dibInfo = null;
        }

        var stride = (_renderW + 3) & ~3;
        _dibBits = NativeMemory.Alloc((nuint)(stride * _renderH));
        _dibInfo = NativeMemory.AllocZeroed(40 + 256 * 4);
        if (_dibBits == null || _dibInfo == null)
        {
            return false;
        }

        var h = (byte*)_dibInfo;
        Write32(h, 0, 40); // biSize
        Write32(h, 4, (uint)_renderW); // biWidth
        Write32(h, 8, unchecked((uint)-_renderH)); // biHeight negative = top-down
        *(ushort*)(h + 12) = 1; // biPlanes
        *(ushort*)(h + 14) = 8; // biBitCount
        Write32(h, 16, 0); // biCompression = BI_RGB
        Write32(h, 32, 256); // biClrUsed
        _dibW = _renderW;
        _dibH = _renderH;
        return true;
    }

    // Fill the letterbox bars (the parts of the window outside the content) with black.
    private static void FillBars(Rect* win, Rect* content)
    {
        if (content->left > win->left)
        {
            FillBlack(win->left, win->top, content->left, win->bottom);
        }

        if (content->right < win->right)
        {
            FillBlack(content->right, win->top, win->right, win->bottom);
        }

        if (content->top > win->top)
        {
            FillBlack(content->left, win->top, content->right, content->top);
        }

        if (content->bottom < win->bottom)
        {
            FillBlack(content->left, content->bottom, content->right, win->bottom);
        }
    }

    private static void FillBlack(int left, int top, int right, int bottom)
    {
        if (right <= left || bottom <= top)
        {
            return;
        }

        var fx = stackalloc byte[BltFx.SIZE];
        for (var i = 0; i < BltFx.SIZE; i++)
        {
            fx[i] = 0;
        }

        Write32(fx, 0, BltFx.SIZE); // dwSize
        Write32(fx, BltFx.Offsets.dwFillColor, 0); // black
        Rect r;
        r.left = left;
        r.top = top;
        r.right = right;
        r.bottom = bottom;
        var blt = (delegate* unmanaged[Stdcall]<IntPtr, Rect*, IntPtr, Rect*, uint, void*, int>)_origBlt;
        blt(_primary, &r, IntPtr.Zero, null, Blt.COLORFILL | Blt.WAIT, fx);
    }

    #endregion

    #region Window setup and mouse-scaling subclass

    private static void SetupWindow(IntPtr hwnd, bool sizeToRender)
    {
        if (_windowFixed || hwnd == IntPtr.Zero)
        {
            return;
        }

        _windowFixed = true;
        _borderless = sizeToRender && _borderlessRequested;

        if (_borderless)
        {
            // Borderless fullscreen, a caption-less popup filling the primary monitor.
            var sw = GetSystemMetrics(SM_CXSCREEN);
            var sh = GetSystemMetrics(SM_CYSCREEN);
            _ = SetWindowLongA(hwnd, GWL_STYLE, unchecked((int)(WS_POPUP | WS_VISIBLE)));
            SetWindowPos(hwnd, IntPtr.Zero, 0, 0, sw, sh,
                SWP_NOZORDER | SWP_NOACTIVATE | SWP_FRAMECHANGED);
            Log.Write($"Borderless fullscreen window {sw}x{sh}.");
        }
        else if (sizeToRender)
        {
            _ = SetWindowLongA(hwnd, GWL_STYLE, (int)WS_OVERLAPPEDWINDOW);
            Rect r;
            r.left = 0;
            r.top = 0;
            r.right = _renderW;
            r.bottom = _renderH;
            AdjustWindowRectEx(ref r, WS_OVERLAPPEDWINDOW, false, 0);
            SetWindowPos(hwnd, IntPtr.Zero, 0, 0, r.right - r.left, r.bottom - r.top,
                SWP_NOMOVE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_FRAMECHANGED);
        }
        else
        {
            var style = GetWindowLongA(hwnd, GWL_STYLE);
            _ = SetWindowLongA(hwnd, GWL_STYLE, style | (int)(WS_THICKFRAME | WS_MAXIMIZEBOX));
            SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0,
                SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_FRAMECHANGED);
        }

        // The client creates its window WS_EX_TOPMOST for exclusive fullscreen.
        // Drop it out of the topmost band so it stacks like a normal window.
        SetWindowPos(hwnd, HWND_NOTOPMOST, 0, 0, 0, 0,
            SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);

        ShowWindow(hwnd, SW_SHOW);
        BringWindowToTop(hwnd);
        SetForegroundWindow(hwnd);

        var procPtr = (void*)(delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr, IntPtr, IntPtr>)&WndProcHook;
        _origWndProc = SetWindowLongA(hwnd, GWL_WNDPROC, (int)(nint)procPtr);
        Log.Write($"Window set up (render {_renderW}x{_renderH}, sizeToRender={sizeToRender}) and subclassed.");

        if (_borderless)
        {
            ClipToContent();
        }
    }

    // Post the key-up events the game missed while it was out of focus during Alt-Tab,
    private static void ResetModifiers(IntPtr hwnd)
    {
        PostMessageA(hwnd, WM_SYSKEYUP, VK_MENU, unchecked((int)0xC0380001));
        PostMessageA(hwnd, WM_KEYUP, VK_MENU, unchecked((int)0xC0380001));
        PostMessageA(hwnd, WM_KEYUP, VK_CONTROL, unchecked((int)0xC01D0001));
        PostMessageA(hwnd, WM_KEYUP, VK_SHIFT, unchecked((int)0xC02A0001));
    }

    // Confine the OS cursor to the rendered content rectangle so it cannot enter the black bars.
    private static void ClipToContent()
    {
        if (!_borderless || _hwnd == IntPtr.Zero)
        {
            return;
        }

        if (!GetClientRect(_hwnd, out var client))
        {
            return;
        }

        var cw = client.right - client.left;
        var ch = client.bottom - client.top;
        if (cw <= 0 || ch <= 0)
        {
            return;
        }

        var origin = new Point { x = 0, y = 0 };
        ClientToScreen(_hwnd, ref origin);
        ContentRect(cw, ch, out var ox, out var oy, out var cwc, out var chc);
        Rect r;
        r.left = origin.x + ox;
        r.top = origin.y + oy;
        r.right = r.left + cwc;
        r.bottom = r.top + chc;
        ClipCursor(&r);
    }

    // Snap the proposed window rectangle (from WM_SIZING) so its client area keeps the
    // render aspect ratio. The frame overhead (borders + caption) is constant, so we take
    // it from the current window vs client rects.
    private static void ConstrainSizingToAspect(Rect* r, int edge)
    {
        if (r == null || _renderW <= 0 || _renderH <= 0)
        {
            return;
        }

        if (!GetWindowRect(_hwnd, out var wr) || !GetClientRect(_hwnd, out var cr))
        {
            return;
        }

        var ncW = (wr.right - wr.left) - (cr.right - cr.left); // frame width  (constant)
        var ncH = (wr.bottom - wr.top) - (cr.bottom - cr.top); // frame height (constant)

        var clientW = (r->right - r->left) - ncW;
        var clientH = (r->bottom - r->top) - ncH;

        int minW = _renderW / 2, minH = _renderH / 2;
        var driveFromHeight = edge == WMSZ_TOP || edge == WMSZ_BOTTOM;
        if (driveFromHeight)
        {
            if (clientH < minH)
            {
                clientH = minH;
            }

            clientW = clientH * _renderW / _renderH;
        }
        else
        {
            if (clientW < minW)
            {
                clientW = minW;
            }

            clientH = clientW * _renderH / _renderW;
        }

        // Magnetic snap to an exact integer scale when the drag lands within SnapPx of one.
        var driveLen = driveFromHeight ? clientH : clientW;
        var driveRender = driveFromHeight ? _renderH : _renderW;
        var scale = (driveLen + driveRender / 2) / driveRender; // nearest integer scale
        var snapDelta = driveLen - scale * driveRender;
        if (snapDelta < 0)
        {
            snapDelta = -snapDelta;
        }

        if (scale >= 1 && snapDelta <= SnapPx)
        {
            clientW = scale * _renderW;
            clientH = scale * _renderH;
        }

        var newW = clientW + ncW;
        var newH = clientH + ncH;

        // Anchor the opposite edge/corner from the one being dragged.
        if (edge == WMSZ_LEFT || edge == WMSZ_TOPLEFT || edge == WMSZ_BOTTOMLEFT)
        {
            r->left = r->right - newW;
        }
        else
        {
            r->right = r->left + newW;
        }

        if (edge == WMSZ_TOP || edge == WMSZ_TOPLEFT || edge == WMSZ_TOPRIGHT)
        {
            r->top = r->bottom - newH;
        }
        else
        {
            r->bottom = r->top + newH;
        }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static IntPtr WndProcHook(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        // While we synthesize the borderless minimize on focus loss, keep the client's
        // own window proc out of it entirely.
        if (_selfMinimizing)
        {
            return DefWindowProcA(hwnd, msg, wParam, lParam);
        }

        // Repaint the client area from DirectDraw, so let the GDI background erase fall
        // through to us so it does not flash black over our present.
        if (msg == WM_ERASEBKGND)
        {
            return 1;
        }

        // Re-present the last frame while the window is being resized.
        if (msg == WM_SIZE)
        {
            Present();
        }

        // The OS asks us to repaint an invalidated region, the area exposed by a resize or
        // uncovered when another window moves away.
        if (msg == WM_PAINT && _primary != IntPtr.Zero)
        {
            Present();
            ValidateRect(hwnd, null);
            return IntPtr.Zero;
        }

        // Alt+key reaches DefWindowProc as a menu mnemonic. With no menu
        // to match, Windows plays the default beep. Swallow the menu-key messages.
        if (msg == WM_SYSCHAR)
        {
            return IntPtr.Zero;
        }

        if (msg == WM_MENUCHAR)
        {
            return MNC_CLOSE << 16;
        }

        if (_lockAspectRatio && msg == WM_SIZING && _engaged && !_borderless)
        {
            ConstrainSizingToAspect((Rect*)lParam, checked((int)wParam));
            return 1;
        }

        if (_engaged && (msg == WM_ACTIVATEAPP || msg == WM_ACTIVATE))
        {
            var activating = msg == WM_ACTIVATEAPP
                ? wParam != IntPtr.Zero
                : (checked((int)wParam) & 0xFFFF) != 0;
            if (activating)
            {
                // Reset modifiers to ensure the client receives the key-up
                ResetModifiers(hwnd);
                if (_borderless)
                {
                    ClipToContent();
                }
            }
            else if (_borderless)
            {
                ClipCursor(null);
                _selfMinimizing = true;
                ShowWindow(hwnd, SW_MINIMIZE);
                _selfMinimizing = false;
                return DefWindowProcA(hwnd, msg, wParam, lParam);
            }
            else
            {
                // Suppress the client's minimize on losing focus
                return DefWindowProcA(hwnd, msg, wParam, lParam);
            }
        }

        // Display the resize arrows over the border.
        if (msg == WM_NCMOUSEMOVE)
        {
            if (!_cursorVisible)
            {
                ShowCursor(true);
                _cursorVisible = true;
            }
        }
        else if (msg == WM_MOUSEFIRST /* WM_MOUSEMOVE */)
        {
            if (_cursorVisible)
            {
                ShowCursor(false);
                _cursorVisible = false;
            }
        }

        // Map client-area mouse coordinates back into the render size.
        // Clamp so clicks in the black bars stay on the edge.
        if (msg >= WM_MOUSEFIRST && msg <= WM_MOUSELAST_CLIENT &&
            GetClientRect(hwnd, out var client))
        {
            var cw = client.right - client.left;
            var ch = client.bottom - client.top;
            ContentRect(cw, ch, out var ox, out var oy, out var cwc, out var chc);
            if (cwc > 0 && chc > 0 && (cwc != _renderW || chc != _renderH || ox != 0 || oy != 0))
            {
                var x = (LoWord(lParam) - ox) * _renderW / cwc;
                var y = (HiWord(lParam) - oy) * _renderH / chc;
                if (x < 0)
                {
                    x = 0;
                }
                else if (x >= _renderW)
                {
                    x = _renderW - 1;
                }

                if (y < 0)
                {
                    y = 0;
                }
                else if (y >= _renderH)
                {
                    y = _renderH - 1;
                }

                lParam = MakeLParam(x, y);
            }
        }

        return CallWindowProcA(_origWndProc, hwnd, msg, wParam, lParam);
    }

    #endregion

    #region Multi-instance

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static IntPtr CreateMutexAHook(IntPtr attrs, int initialOwner, IntPtr name)
    {
        // A null name yields an unnamed mutex that never collides, so the client's
        // "already running" check (GetLastError == ERROR_ALREADY_EXISTS) never fires.
        var orig = (delegate* unmanaged[Stdcall]<IntPtr, int, IntPtr, IntPtr>)_origCreateMutexA;
        return orig(attrs, initialOwner, IntPtr.Zero);
    }

    #endregion

    #region Skip intro

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static IntPtr BinkOpenHook(IntPtr name, uint flags)
    {
        // Open the video and set its frame count to 1.
        var orig = (delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr>)_origBinkOpen;
        var hbink = orig(name, flags);
        if (hbink != IntPtr.Zero)
        {
            //BINK.Frames, a fixed Bink1 ABI field at +0x08
            *(uint*)((byte*)hbink + 0x08) = 1;
        }

        return hbink;
    }

    // Drop the frame copy so no video pixels reach the display surface.
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static int BinkCopyToBufferHook(IntPtr bink, IntPtr dest, int destPitch, uint destHeight, uint destX, uint destY, uint flags) => 1;

    // Swallow sound-system registration so the video opens silent.
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static int BinkSetSoundSystemHook(IntPtr openFunc, uint param) => 1;

    #endregion

    #region Force legacy DisplayMode

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static int RegQueryValueExAHook(IntPtr hKey, IntPtr valueName, IntPtr reserved, IntPtr type, IntPtr data, IntPtr cbData)
    {
        var orig = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr, IntPtr, IntPtr, IntPtr, int>)_origRegQueryValueExA;
        var rc = orig(hKey, valueName, reserved, type, data, cbData);
        if (rc == ERROR_SUCCESS && valueName != IntPtr.Zero && data != IntPtr.Zero &&
            AnsiEqualsIgnoreCase((byte*)valueName, "DisplayMode"))
        {
            *(byte*)data = 0; // 0 == Fullscreen
        }

        return rc;
    }

    #endregion

    #region Helpers

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint Read32(byte* p, int off) => *(uint*)(p + off);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Write32(byte* p, int off, uint v) => *(uint*)(p + off) = v;

    // Describe a surface in the depth the client renders at. 8-bit is palettized (the client
    // attaches its own palette, and our present Blt converts through it). 16-bit is RGB565.
    private static void WritePixelFormat(byte* d)
    {
        Write32(d, SurfaceDesc.Offsets.pf_dwSize, 0x20);
        if (_renderBpp == 8)
        {
            Write32(d, SurfaceDesc.Offsets.pf_dwFlags, PixelFormat.Flags.RGB | PixelFormat.Flags.PALETTEINDEXED8);
            Write32(d, SurfaceDesc.Offsets.pf_dwRGBBitCount, 8);
            Write32(d, SurfaceDesc.Offsets.pf_dwRBitMask, 0);
            Write32(d, SurfaceDesc.Offsets.pf_dwGBitMask, 0);
            Write32(d, SurfaceDesc.Offsets.pf_dwBBitMask, 0);
        }
        else
        {
            Write32(d, SurfaceDesc.Offsets.pf_dwFlags, PixelFormat.Flags.RGB);
            Write32(d, SurfaceDesc.Offsets.pf_dwRGBBitCount, 16);
            Write32(d, SurfaceDesc.Offsets.pf_dwRBitMask, 0xF800);
            Write32(d, SurfaceDesc.Offsets.pf_dwGBitMask, 0x07E0);
            Write32(d, SurfaceDesc.Offsets.pf_dwBBitMask, 0x001F);
        }
    }

    #endregion
}
