using System;
using System.Runtime.InteropServices;
using DawndNet.Shared;
using static DawndNet.Payload.ClientMemory;

namespace DawndNet.Payload;

/// <summary>
///     A scaled-down static render of the whole current map
/// </summary>
internal static unsafe class MapOverlay
{
    private const bool Enable = true;
    private const int ToggleVk = 0x71; // VK_F2

    // 7.41 constants
    private const uint TileLibAccessorVa = 0x4ae4e0; // render_get_map_tile_library() -> MapTileImageLib
    private const uint TileLibGlobalVa = 0x750588; // the singleton that accessor returns
    private const uint LoadTilePixelsVa = 0x4c78c0; // map_load_tile_pixels(id, buf, alt) [thiscall, ret 0xC]
    private const uint DecodeRawTileVa = 0x4c7390; // file_decode_raw_map_tile(id, buf) -> bool [thiscall]
    private const int BaseBankOffset = 0x0c; // MapTileImageLib: the tilea.bmp storage
    private const int AltBankOffset = 0x10; // and the seasonal tileas.bmp
    private const uint PaletteMgrAccessorVa = 0x44d830; // returns the palette manager singleton
    private const uint PaletteMgrGlobalVa = 0x6fc2c0; // the singleton that accessor returns
    private const uint PaletteTableVa = 0x548510; // palette key -> u16[256] color table [thiscall, ret 4]
    private const uint LoadStaticPixmapVa = 0x5fd500; // file_load_static_tile_pixmap(id, mode, out) [cdecl]
    private const uint SotpAccessorVa = 0x5cf360; // map_get_sotp_render_flags(tileId) [thiscall]
    private const uint SotpVectorSiteVa = 0x5cf372; // the "add eax, 0x1B0" naming the render vector
    private const uint SetCellVa = 0x5b90f0; // map_set_cell(x, y, ground, left, right)
    private const uint SetCellWidthSiteVa = 0x5b9102; // "movzx ecx, [eax+2]" = storage width
    private const uint SetCellArraySiteVa = 0x5b9145; // "add eax, [ecx+0xC]" = storage cell array
    private const uint ScreenPaneGlobalVa = 0x73d94c; // the root ScreenPane, which owns the cursor art
    private const uint CanvasLockPixelsVa = 0x44b6f0; // render_canvas_lock_pixels(out pitch) -> pixels
    private const uint CanvasUnlockPixelsVa = 0x44b730; // render_canvas_unlock_pixels() [thiscall]
    private const uint CanvasGetBoundsVa = 0x44bbe0; // render_canvas_get_bounds(out RECT) [thiscall]
    private const uint CursorRectSiteVa = 0x55551d; // the two stores that name the cursor rect fields

    // ScreenPane fields
    private const int CursorCanvasesOffset = 0x194; // one Canvas* per cursor mode
    private const int CursorModeOffset = 0x27c; // which of them is current
    private const int CursorRectLeftOffset = 0x280;
    private const int CursorRectTopOffset = 0x284;
    private const int MaxCursorModes = 16;
    private const int MaxCursorSide = 64; // the client composes its cursor in a 32x32 canvas

    // WorldPane fields
    private const int SotpRenderVectorOffset = 0x1b0; // the twin vector holding the render nibble
    private const int MapWidthOffset = 0x1c4;
    private const int MapHeightOffset = 0x1c8;
    private const int MapReadyOffset = 0x1cc;
    private const int MapFlagsOffset = 0x260; // full SMapSize flags byte
    private const int ViewYOffset = 0x238;
    private const int ViewXOffset = 0x23c;
    private const int MapNumberOffset = 0x26c;
    private const int TransferActiveOffset = 0x275;
    private const int CellStorageOffset = 0x27c;

    // MapCellStorage fields
    private const int StorageWidthOffset = 2;
    private const int StorageHeightOffset = 4;
    private const int StorageCellsOffset = 0xc;
    private const int CellStride = 6; // u16 ground, u16 left static, u16 right static

    // The 0x34-byte pixmap record file_load_static_tile_pixmap fills in
    private const int PixmapBytes = 0x34;
    private const int PixmapPixelsOffset = 0x00;
    private const int PixmapStrideOffset = 0x04;
    private const int PixmapBottomOffset = 0x10; // client RECT order is {top, left, bottom, right}
    private const int PixmapRightOffset = 0x14;
    private const int PixmapPaletteKeyOffset = 0x28;
    private const int MaxStaticRows = 4096; // sanity bound on a decoded static's height
    private const uint AltBankFlag = 0x80; // SMapSize flags: seasonal (tileas / sts) art
    private const int NoStaticTile = 0x2710; // sentinel the client itself treats as "no static"
    private const int TileBufferBytes = 0x620; // one decoded ground diamond
    private const int TilePixels = 784;
    private const int TileIds = 0x10000; // the map cell stores a u16, so the caches cover all of them
    private const int MaxMapSide = 512; // SMapSize dimensions are single bytes, so this is pure sanity
    private const uint Known = 0x10000; // cache entry resolved, packed color in the low 16 bits
    private const int CoverageShift = 16; // sprite pixels carry coverage above their packed color
    private const int GameTileWidth = 56;
    private const int GameTileHeight = 27;
    private const int GameHalfWidth = 28;

    // Appearance
    private const int TargetPercent = 90; // of the render surface
    private const int MaxHalfWidth = 24;
    private const int SceneDimPercent = 42; // how much of the game shows through behind the map
    private const int MaxMarkerPixels = 32;

    // Space kept clear above the map's top vertex
    private const int HeadroomHalfRows = 12;
    private const int CellRecord = 4;
    private const int CellFlat = 0; // flat color, used where the ground is not sampled
    private const int CellGround = 1; // ground tile to sample, or 0 to keep the flat color
    private const int CellLeft = 2; // the two isometric foreground layers
    private const int CellRight = 3;
    private const int MinCursorClearPercent = 25;
    private const int MaxBuildAttempts = 8;
    private const int MaxBuildLogs = 4;
    private static readonly uint* NoSprite = (uint*)1;
    private static bool _supported;
    private static bool _visible;

    // Render-surface channel layout, captured from the offscreen handed to the client.
    // Colors stay packed in this format end to end.
    private static int _rShift, _gShift, _bShift;
    private static int _rMax = 31, _gMax = 63, _bMax = 31;
    private static uint* _groundCache;
    private static uint** _groundSprite; // the ground diamond, downsampled to one tile of the overlay
    private static uint** _staticSprite; // null, NoSprite, or {width, height, pixels...}
    private static byte* _staticScreen; // 1 where SOTP asks for the wall and tree screen blend
    private static byte* _tileBuf; // scratch for one decoded ground diamond
    private static bool _cacheAlt;
    private static int _cacheHalfW, _cacheHalfH;

    // Snapshot of the current map
    private static ushort* _cell;
    private static int _mapW, _mapH;
    private static bool _snapValid;
    private static int _snapMapNumber = -1;
    private static IntPtr _snapCells = -1;

    // The overlay bitmap, in render-surface pixels.
    private static ushort* _raster;
    private static byte* _inMap; // 1 where the raster holds overlay content
    private static int _rasterCap;
    private static ushort* _dimTable; // frame pixel -> its dimmed value
    private static int _outW, _outH, _halfW, _halfH;
    private static int _headroom; // clear rows above the diamond, so tall art is not clipped
    private static int _fitW = -1, _fitH = -1; // the render size the current raster was fitted to

    // Stamped at upload time rather than baked into the raster, so a moving cursor or marker never
    // forces a re-raster.
    private static int _markerX = -1, _markerY, _markerSize;
    private static uint* _cursor; // the client's cursor canvas: {width, height, pixels...}
    private static int _cursorW, _cursorH;
    private static int _cursorLeft, _cursorTop; // that canvas's rectangle in the frame,
    private static int _cursorSpanW, _cursorSpanH; // scaled from the game's pixels to the frame's
    private static bool _cursorValid;
    private static int _cursorMode = -1;
    private static uint _cursorLogged; // one bit per mode already reported
    private static int _attempts;
    private static int _buildLogs;
    public static int ViewWidth { get; private set; }
    public static int ViewHeight { get; private set; }
    public static int OffsetX { get; private set; }
    public static int OffsetY { get; private set; }

    public static void Init(bool enabled, uint rMask, uint gMask, uint bMask)
    {
        if (!Enable || !enabled)
        {
            return;
        }

        ClientMemory.Init();
        if (!ClientSupported())
        {
            Log.Write("MapOverlay: read-site signatures not matched -> disabled (unsupported client).");
            return;
        }

        Unpack(rMask, out _rShift, out _rMax);
        Unpack(gMask, out _gShift, out _gMax);
        Unpack(bMask, out _bShift, out _bMax);

        BuildDimTable();
        _supported = true;
        Log.Write($"MapOverlay ready (toggle=VK 0x{ToggleVk:X2}, masks {rMask:X}/{gMask:X}/{bMask:X}).");
    }

    // Toggle the overlay
    public static bool OnKeyDown(int vk, int renderW, int renderH)
    {
        if (!_supported || vk != ToggleVk)
        {
            return false;
        }

        _visible = !_visible;
        if (_visible)
        {
            Pump(renderW, renderH);
        }

        return true;
    }

    public static void Pump(int renderW, int renderH)
    {
        if (!_supported || !_visible || renderW <= 0 || renderH <= 0)
        {
            return;
        }

        var worldPane = WorldPane();
        if (worldPane == IntPtr.Zero)
        {
            _snapValid = false;
            _snapMapNumber = -1;
            _snapCells = -1;
            return;
        }

        // The client swaps cursor art by mode
        var screenPane = *(IntPtr*)(void*)Rebase(ScreenPaneGlobalVa);
        if (Plausible(screenPane))
        {
            var mode = *(int*)((byte*)screenPane + CursorModeOffset);
            if (mode != _cursorMode)
            {
                _cursorMode = mode;
                CaptureCursor(mode);
            }
        }

        if (*((byte*)worldPane + TransferActiveOffset) != 0 || *((byte*)worldPane + MapReadyOffset) == 0)
        {
            return;
        }

        var mapNumber = *(int*)((byte*)worldPane + MapNumberOffset);
        var cells = Cells(worldPane);
        if (mapNumber != _snapMapNumber || cells != _snapCells)
        {
            _snapMapNumber = mapNumber;
            _snapCells = cells;
            _snapValid = false;
            _attempts = 0;
        }
        else if (_snapValid || _attempts >= MaxBuildAttempts)
        {
            return; // already have this map, or gave up on it
        }

        _attempts++;
        _snapValid = Build(worldPane, (byte*)cells, renderW, renderH);
        if (!_snapValid)
        {
            if (_attempts >= MaxBuildAttempts && _buildLogs < MaxBuildLogs)
            {
                _buildLogs++;
                Log.Write($"MapOverlay: could not read map {mapNumber} (cells=0x{cells:X}); overlay stays hidden.");
            }

            return;
        }

        _fitW = -1;
    }

    public static bool Prepare(int frameW, int frameH, int renderW, int renderH)
    {
        if (!_supported || !_visible || !_snapValid || frameW <= 0 || frameH <= 0 ||
            renderW <= 0 || renderH <= 0)
        {
            return false;
        }

        Fit(_mapW + _mapH, frameW, frameH, out var fitW, out var fitH);
        if (fitW != _cacheHalfW || fitH != _cacheHalfH)
        {
            _visible = false;
            _snapValid = false;
            _attempts = 0;
            return false;
        }

        if ((_fitW != frameW || _fitH != frameH) && !Raster(frameW, frameH))
        {
            return false;
        }

        UpdateMarker();
        UpdateCursor(frameW, frameH, renderW, renderH);
        return true;
    }

    public static void Composite(byte* bits, int pitch, int frameW, int frameH)
    {
        if (_raster == null || _inMap == null || _dimTable == null || bits == null || pitch <= 0)
        {
            return;
        }

        for (var y = 0; y < frameH; y++)
        {
            var dst = (ushort*)(bits + (long)y * pitch);
            var oy = y - OffsetY;
            var inOverlay = oy >= 0 && oy < ViewHeight;
            var overlay = inOverlay ? _raster + (long)oy * _outW : null;
            var mask = inOverlay ? _inMap + (long)oy * _outW : null;

            // The game's own cursor is already in this frame, so leaving the pixels it covers untouched
            // keeps it at full brightness above the map without drawing a second copy.
            var cy = y - _cursorTop;
            var cursor = _cursorValid && cy >= 0 && cy < _cursorSpanH
                ? _cursor + 2 + (long)(cy * _cursorH / _cursorSpanH) * _cursorW
                : null;

            for (var x = 0; x < frameW; x++)
            {
                if (cursor != null)
                {
                    var cx = x - _cursorLeft;
                    if (cx >= 0 && cx < _cursorSpanW && cursor[cx * _cursorW / _cursorSpanW] != 0)
                    {
                        continue;
                    }
                }

                var ox = x - OffsetX;
                dst[x] = mask != null && ox >= 0 && ox < ViewWidth && mask[ox] != 0
                    ? overlay[ox]
                    : _dimTable[dst[x]];
            }
        }

        Stamp(bits, pitch, frameW, frameH);
    }

    private static void Stamp(byte* bits, int pitch, int frameW, int frameH)
    {
        if (_markerX >= 0)
        {
            var marker = Pack(_rMax, (_gMax * 7 + 5) / 10, (_bMax * 4 + 5) / 10);
            for (var row = 0; row < _markerSize; row++)
            {
                for (var col = 0; col < _markerSize; col++)
                {
                    Plot(bits, pitch, frameW, frameH, OffsetX + _markerX + col,
                        OffsetY + _markerY + row, marker);
                }
            }
        }
    }

    private static void CaptureCursor(int mode)
    {
        var screenPane = *(IntPtr*)(void*)Rebase(ScreenPaneGlobalVa);
        if (!Plausible(screenPane) || mode < 0 || mode >= MaxCursorModes)
        {
            return;
        }

        var canvas = *(IntPtr*)((byte*)screenPane + CursorCanvasesOffset + mode * sizeof(IntPtr));
        if (!Plausible(canvas))
        {
            return;
        }

        // Client RECT order is {top, left, bottom, right}.
        var rect = stackalloc int[4];
        ((delegate* unmanaged[Thiscall]<IntPtr, int*, void>)(void*)Rebase(CanvasGetBoundsVa))(canvas, rect);
        var w = rect[3] - rect[1];
        var h = rect[2] - rect[0];
        if (w <= 0 || h <= 0 || w > MaxCursorSide || h > MaxCursorSide)
        {
            return;
        }

        var pitch = 0;
        var pixels = ((delegate* unmanaged[Thiscall]<IntPtr, int*, ushort*>)(void*)Rebase(CanvasLockPixelsVa))(
            canvas, &pitch);
        var unlock = (delegate* unmanaged[Thiscall]<IntPtr, void>)(void*)Rebase(CanvasUnlockPixelsVa);
        if (!Plausible((IntPtr)pixels) || pitch < w * sizeof(ushort))
        {
            unlock(canvas);
            return;
        }

        var sprite = (uint*)NativeMemory.Alloc((nuint)((2 + w * h) * sizeof(uint)));
        var clear = 0;
        int artLeft = w, artTop = h, artRight = -1, artBottom = -1;
        if (sprite != null)
        {
            sprite[0] = (uint)w;
            sprite[1] = (uint)h;
            for (var y = 0; y < h; y++)
            {
                var src = (ushort*)((byte*)pixels + (long)(rect[0] + y) * pitch) + rect[1];
                for (var x = 0; x < w; x++)
                {
                    sprite[2 + y * w + x] = src[x];
                    if (src[x] == 0)
                    {
                        clear++;
                        continue;
                    }

                    artLeft = Math.Min(artLeft, x);
                    artTop = Math.Min(artTop, y);
                    artRight = Math.Max(artRight, x);
                    artBottom = Math.Max(artBottom, y);
                }
            }
        }

        unlock(canvas);
        if (sprite == null)
        {
            return;
        }

        if (clear * 100 / (w * h) < MinCursorClearPercent)
        {
            if (FirstLogFor(mode))
            {
                Log.Write($"MapOverlay: cursor mode {mode} canvas {w}x{h} only {clear * 100 / (w * h)}% clear " +
                          "-> keeping the drawn cursor.");
            }

            NativeMemory.Free(sprite);
            return;
        }

        FreeSprite(ref _cursor);
        _cursor = sprite;
        _cursorW = w;
        _cursorH = h;

        if (FirstLogFor(mode))
        {
            Log.Write($"MapOverlay: cursor mode {mode} captured {w}x{h} ({clear * 100 / (w * h)}% clear), " +
                      $"art ({artLeft},{artTop})-({artRight},{artBottom}).");
        }
    }

    private static bool FirstLogFor(int mode)
    {
        if (mode is < 0 or >= MaxCursorModes || (_cursorLogged & (1u << mode)) != 0)
        {
            return false;
        }

        _cursorLogged |= 1u << mode;
        return true;
    }

    // One entry per possible frame pixel, so dimming the scene costs a single lookup per pixel.
    private static void BuildDimTable()
    {
        _dimTable = (ushort*)NativeMemory.Alloc(1 << 16, sizeof(ushort));
        if (_dimTable == null)
        {
            return;
        }

        for (var i = 0; i < 1 << 16; i++)
        {
            var p = (ushort)i;
            _dimTable[i] = Pack(
                ((p >> _rShift) & _rMax) * SceneDimPercent / 100,
                ((p >> _gShift) & _gMax) * SceneDimPercent / 100,
                ((p >> _bShift) & _bMax) * SceneDimPercent / 100);
        }
    }

    private static void Plot(byte* bits, int pitch, int frameW, int frameH, int x, int y, ushort pixel)
    {
        if (x >= 0 && x < frameW && y >= 0 && y < frameH)
        {
            *((ushort*)(bits + (long)y * pitch) + x) = pixel;
        }
    }

    private static bool ClientSupported() =>
        SiteHasBytes(TileLibAccessorVa, [0x55, 0x8B, 0xEC, 0xA1, 0x88, 0x05, 0x75, 0x00, 0x5D, 0xC3]) &&
        SiteHasBytes(LoadTilePixelsVa, [0x55, 0x8B, 0xEC, 0x83, 0xEC, 0x10]) &&
        SiteHasBytes(DecodeRawTileVa, [0x55, 0x8B, 0xEC, 0x83, 0xEC, 0x24, 0x56]) &&
        SiteHasBytes(PaletteMgrAccessorVa, [0x55, 0x8B, 0xEC, 0xA1, 0xC0, 0xC2, 0x6F, 0x00, 0x5D, 0xC3]) &&
        SiteHasBytes(PaletteTableVa, [0x55, 0x8B, 0xEC, 0x83, 0xEC, 0x0C]) &&
        SiteHasBytes(LoadStaticPixmapVa, [0x55, 0x8B, 0xEC, 0x83, 0xEC, 0x1C]) &&
        SiteHasBytes(SotpAccessorVa, [0x55, 0x8B, 0xEC, 0x83, 0xEC, 0x08]) &&
        SiteHasBytes(SotpVectorSiteVa, [0x05, 0xB0, 0x01, 0x00, 0x00]) && // add eax, 0x1B0
        SiteHasBytes(SetCellVa, [0x55, 0x8B, 0xEC, 0x83, 0xEC, 0x0C]) &&
        SiteHasBytes(SetCellWidthSiteVa, [0x0F, 0xB7, 0x48, 0x02]) && // movzx ecx, [eax+2]
        SiteHasBytes(SetCellArraySiteVa, [0x03, 0x41, 0x0C]) && // add eax, [ecx+0xC]
        SiteHasBytes(CanvasLockPixelsVa, [
            0x55, 0x8B, 0xEC, 0x51, 0x89, 0x4D, 0xFC, 0x8B, 0x45, 0xFC,
            0x83, 0xB8, 0xC4, 0x00, 0x00, 0x00, 0x00
        ]) && // cmp [eax+0xC4], 0
        SiteHasBytes(CanvasUnlockPixelsVa, [
            0x55, 0x8B, 0xEC, 0x51, 0x89, 0x4D, 0xFC, 0x8B, 0x45, 0xFC,
            0x83, 0xB8, 0xC4, 0x00, 0x00, 0x00, 0x00
        ]) &&
        SiteHasBytes(CanvasGetBoundsVa, [
            0x55, 0x8B, 0xEC, 0x51, 0x89, 0x4D, 0xFC, 0x8B, 0x45, 0xFC,
            0x83, 0xC0, 0x5C
        ]) && // add eax, 0x5C -> the bounds rectangle
        SiteHasBytes(CursorRectSiteVa, [0x89, 0x88, 0x84, 0x02, 0x00, 0x00]); // mov [eax+0x284], ecx

    #region Map snapshot

    private static IntPtr Cells(IntPtr worldPane)
    {
        var storage = *(IntPtr*)((byte*)worldPane + CellStorageOffset);
        return Plausible(storage) ? *(IntPtr*)((byte*)storage + StorageCellsOffset) : IntPtr.Zero;
    }

    private static bool Build(IntPtr worldPane, byte* cells, int renderW, int renderH)
    {
        var storage = *(IntPtr*)((byte*)worldPane + CellStorageOffset);
        if (!Plausible(storage) || !Plausible((IntPtr)cells))
        {
            return false;
        }

        int w = *(ushort*)((byte*)storage + StorageWidthOffset);
        int h = *(ushort*)((byte*)storage + StorageHeightOffset);
        if (w <= 0 || h <= 0 || w > MaxMapSide || h > MaxMapSide ||
            w != *(int*)((byte*)worldPane + MapWidthOffset) ||
            h != *(int*)((byte*)worldPane + MapHeightOffset))
        {
            return false;
        }

        var alt = (*(uint*)((byte*)worldPane + MapFlagsOffset) & AltBankFlag) != 0;
        var tileLib = *(IntPtr*)(void*)Rebase(TileLibGlobalVa);
        var paletteMgr = *(IntPtr*)(void*)Rebase(PaletteMgrGlobalVa);

        Fit(w + h, renderW, renderH, out var halfW, out var halfH);
        if (!Plausible(tileLib) || !Plausible(paletteMgr) || !EnsureCaches(alt, halfW, halfH))
        {
            return false;
        }

        if (w != _mapW || h != _mapH || _cell == null)
        {
            if (_cell != null)
            {
                NativeMemory.Free(_cell);
            }

            _cell = (ushort*)NativeMemory.Alloc((nuint)(w * h * CellRecord * sizeof(ushort)));
            _mapW = w;
            _mapH = h;
        }

        var render = (byte*)*(IntPtr*)((byte*)worldPane + SotpRenderVectorOffset);
        var renderEnd = (byte*)*(IntPtr*)((byte*)worldPane + SotpRenderVectorOffset + sizeof(IntPtr));
        var renderCount = Plausible((IntPtr)render) && renderEnd > render ? (int)(renderEnd - render) : 0;
        var sprites = 0;
        var hidden = 0;

        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                var cell = cells + (y * w + x) * CellStride;
                var groundId = *(ushort*)cell;
                var leftId = *(ushort*)(cell + 2);
                var rightId = *(ushort*)(cell + 4);
                var pixel = GroundPixel(tileLib, groundId, alt, halfW, halfH);

                if (!IsRenderedSprite(leftId))
                {
                    hidden += leftId != 0 ? 1 : 0;
                    leftId = 0;
                }

                if (!IsRenderedSprite(rightId))
                {
                    hidden += rightId != 0 ? 1 : 0;
                    rightId = 0;
                }

                var left = StaticSprite(paletteMgr, leftId, alt, halfW, halfH);
                var right = StaticSprite(paletteMgr, rightId, alt, halfW, halfH);

                _staticScreen[leftId] = ScreenBlended(render, renderCount, leftId);
                _staticScreen[rightId] = ScreenBlended(render, renderCount, rightId);
                if (left != null)
                {
                    sprites++;
                }

                if (right != null)
                {
                    sprites++;
                }

                var record = _cell + (y * w + x) * CellRecord;
                record[CellFlat] = pixel;
                record[CellGround] = groundId;
                record[CellLeft] = leftId;
                record[CellRight] = rightId;
            }
        }

        if (_buildLogs < MaxBuildLogs)
        {
            _buildLogs++;
            Log.Write($"MapOverlay: map {_snapMapNumber} snapshot {w}x{h} at {halfW}x{halfH} " +
                      $"(altBank={alt}, sprites={sprites}, reserved={hidden}).");
        }

        return true;
    }

    private static bool EnsureCaches(bool alt, int halfW, int halfH)
    {
        if (_groundCache == null)
        {
            _groundCache = (uint*)NativeMemory.AllocZeroed(TileIds, sizeof(uint));
            _groundSprite = (uint**)NativeMemory.AllocZeroed(TileIds, (nuint)sizeof(uint*));
            _staticSprite = (uint**)NativeMemory.AllocZeroed(TileIds, (nuint)sizeof(uint*));
            _staticScreen = (byte*)NativeMemory.AllocZeroed(TileIds, 1);
            _tileBuf = (byte*)NativeMemory.Alloc(TileBufferBytes);
        }
        else if (alt != _cacheAlt || halfW != _cacheHalfW || halfH != _cacheHalfH)
        {
            NativeMemory.Clear(_groundCache, TileIds * sizeof(uint));
            for (var i = 0; i < TileIds; i++)
            {
                FreeSprite(ref _groundSprite[i]);
                FreeSprite(ref _staticSprite[i]);
            }
        }

        _cacheAlt = alt;
        _cacheHalfW = halfW;
        _cacheHalfH = halfH;
        return _groundCache != null && _groundSprite != null && _staticSprite != null &&
               _staticScreen != null && _tileBuf != null;
    }

    private static void FreeSprite(ref uint* sprite)
    {
        if (sprite != null && sprite != NoSprite)
        {
            NativeMemory.Free(sprite);
        }

        sprite = null;
    }

    private static ushort GroundPixel(IntPtr tileLib, ushort cellGround, bool alt, int halfW, int halfH)
    {
        if (cellGround == 0)
        {
            return 0;
        }

        var tileId = (ushort)(cellGround - 1);
        var cached = _groundCache[cellGround];
        if (cached != 0)
        {
            return (ushort)cached;
        }

        var px = (ushort*)_tileBuf;
        for (var i = 0; i < TilePixels; i++)
        {
            px[i] = 0;
        }

        if (!DecodeGroundTile(tileLib, tileId, alt))
        {
            // thiscall(u16 tileId, void* buf, int useAlternateBank). Bounds-checked against the bank
            // size, so an out-of-range ID leaves the buffer
            ((delegate* unmanaged[Thiscall]<IntPtr, int, void*, int, void>)(void*)Rebase(LoadTilePixelsVa))(
                tileLib, tileId, _tileBuf, alt ? 1 : 0);
        }

        long r = 0, g = 0, b = 0;
        for (var i = 0; i < TilePixels; i++)
        {
            int p = px[i];
            r += (p >> _rShift) & _rMax;
            g += (p >> _gShift) & _gMax;
            b += (p >> _bShift) & _bMax;
        }

        // Cached by the cell's own value, so the raster can look a sprite up straight from the cell.
        var pixel = Pack((int)(r / TilePixels), (int)(g / TilePixels), (int)(b / TilePixels));
        _groundCache[cellGround] = Known | pixel;
        _groundSprite[cellGround] = DownsampleDiamond(px, halfW, halfH);
        return pixel;
    }

    private static bool DecodeGroundTile(IntPtr tileLib, ushort tileId, bool alt)
    {
        return (alt && DecodeFromBank(*(IntPtr*)((byte*)tileLib + AltBankOffset), tileId)) ||
               DecodeFromBank(*(IntPtr*)((byte*)tileLib + BaseBankOffset), tileId);
    }

    private static bool DecodeFromBank(IntPtr storage, ushort tileId)
    {
        if (!Plausible(storage))
        {
            return false;
        }

        var vtable = *(IntPtr**)storage;
        if (!Plausible((IntPtr)vtable) || vtable[0] != Rebase(DecodeRawTileVa))
        {
            return false;
        }

        return ((delegate* unmanaged[Thiscall]<IntPtr, int, void*, byte>)(void*)vtable[0])(
            storage, tileId, _tileBuf) != 0;
    }

    private static uint* DownsampleDiamond(ushort* pixels, int halfW, int halfH)
    {
        var dstW = 2 * halfW;
        var dstH = 2 * halfH;
        var bins = dstW * dstH;

        // Bounded by MaxHalfWidth, so this stays a small fixed stack allocation.
        var acc = stackalloc int[4 * 4 * MaxHalfWidth * MaxHalfWidth];
        for (var i = 0; i < 4 * bins; i++)
        {
            acc[i] = 0;
        }

        var offset = 0;
        for (var row = 0; row < GameTileHeight; row++)
        {
            // Row geometry exactly as the client's own decoder lays it out.
            var inset = Math.Abs(GameTileHeight / 2 - row) * 2;
            var width = GameTileWidth - 2 * inset;
            var dy = row * dstH / GameTileHeight;
            for (var i = 0; i < width; i++)
            {
                var dx = (inset + i) * dstW / GameTileWidth;
                var bin = (dy * dstW + dx) * 4;
                int p = pixels[offset + i];
                acc[bin] += (p >> _rShift) & _rMax;
                acc[bin + 1] += (p >> _gShift) & _gMax;
                acc[bin + 2] += (p >> _bShift) & _bMax;
                acc[bin + 3]++;
            }

            offset += width;
        }

        var sprite = (uint*)NativeMemory.Alloc((nuint)((2 + bins) * sizeof(uint)));
        if (sprite == null)
        {
            return null;
        }

        sprite[0] = (uint)dstW;
        sprite[1] = (uint)dstH;
        for (var i = 0; i < bins; i++)
        {
            var n = acc[i * 4 + 3];
            sprite[2 + i] = n > 0
                ? Texel(Pack(acc[i * 4] / n, acc[i * 4 + 1] / n, acc[i * 4 + 2] / n), 255)
                : 0; // no source pixel landed here, so the flat color stands in
        }

        return sprite;
    }

    private static uint* StaticSprite(IntPtr paletteMgr, ushort tileId, bool alt, int halfW, int halfH)
    {
        if (tileId == 0 || tileId == NoStaticTile)
        {
            return null;
        }

        var cached = _staticSprite[tileId];
        if (cached != null)
        {
            return cached == NoSprite ? null : cached;
        }

        var pixmap = stackalloc byte[PixmapBytes];
        for (var i = 0; i < PixmapBytes; i++)
        {
            pixmap[i] = 0;
        }

        // cdecl(u16 tileId, int alternateMode, PixmapRecord* out). The decoded pixels live in a shared
        // scratch buffer, so they must be consumed before anything else decodes.
        ((delegate* unmanaged[Cdecl]<int, int, void*, void>)(void*)Rebase(LoadStaticPixmapVa))(
            tileId, alt ? 1 : 0, pixmap);

        var indices = *(byte**)(pixmap + PixmapPixelsOffset);
        var stride = *(int*)(pixmap + PixmapStrideOffset);
        var rows = *(int*)(pixmap + PixmapBottomOffset);
        var cols = *(int*)(pixmap + PixmapRightOffset);
        uint* sprite = null;

        if (Plausible((IntPtr)indices) && stride > 0 && cols > 0 && cols <= stride &&
            rows > 0 && rows <= MaxStaticRows)
        {
            var table = ((delegate* unmanaged[Thiscall]<IntPtr, int, ushort*>)(void*)Rebase(PaletteTableVa))(
                paletteMgr, *(int*)(pixmap + PixmapPaletteKeyOffset));
            if (Plausible((IntPtr)table))
            {
                sprite = Downsample(indices, stride, cols, rows, table, halfW, halfH);
            }
        }

        _staticSprite[tileId] = sprite != null ? sprite : NoSprite;
        return sprite;
    }

    private static uint* Downsample(byte* indices, int stride, int cols, int rows, ushort* table,
        int halfW, int halfH)
    {
        var dstW = Math.Max(1, cols * halfW / GameHalfWidth);
        var dstH = Math.Max(1, rows * 2 * halfH / GameTileHeight);

        var sprite = (uint*)NativeMemory.Alloc((nuint)((2 + dstW * dstH) * sizeof(uint)));
        if (sprite == null)
        {
            return null;
        }

        sprite[0] = (uint)dstW;
        sprite[1] = (uint)dstH;
        var pixels = sprite + 2;
        var lit = false;

        for (var dy = 0; dy < dstH; dy++)
        {
            var sy0 = dy * rows / dstH;
            var sy1 = Math.Max(sy0 + 1, (dy + 1) * rows / dstH);
            for (var dx = 0; dx < dstW; dx++)
            {
                var sx0 = dx * cols / dstW;
                var sx1 = Math.Max(sx0 + 1, (dx + 1) * cols / dstW);

                long r = 0, g = 0, b = 0;
                var n = 0;
                for (var sy = sy0; sy < sy1; sy++)
                {
                    var line = indices + (long)sy * stride;
                    for (var sx = sx0; sx < sx1; sx++)
                    {
                        var index = line[sx];
                        if (index == 0)
                        {
                            continue; // transparent
                        }

                        int p = table[index];
                        r += (p >> _rShift) & _rMax;
                        g += (p >> _gShift) & _gMax;
                        b += (p >> _bShift) & _bMax;
                        n++;
                    }
                }

                if (n == 0)
                {
                    pixels[dy * dstW + dx] = 0;
                    continue;
                }

                var covered = (sy1 - sy0) * (sx1 - sx0);
                pixels[dy * dstW + dx] = Texel(Pack((int)(r / n), (int)(g / n), (int)(b / n)),
                    Math.Clamp(n * 255 / covered, 1, 255));
                lit = true;
            }
        }

        if (lit)
        {
            return sprite;
        }

        NativeMemory.Free(sprite);
        return null;
    }

    private static byte ScreenBlended(byte* table, int count, ushort tileId) => (byte)(tileId < count && Plausible((IntPtr)table) && (table[tileId] & 0x80) != 0 ? 1 : 0);
    private static bool IsRenderedSprite(int spriteId) => spriteId > 12 && (spriteId < 10000 || spriteId > 10012);

    #endregion

    #region Rasterising

    private static void Fit(int span, int renderW, int renderH, out int halfW, out int halfH)
    {
        halfW = span > 0
            ? Math.Clamp(
                Math.Min(renderW * TargetPercent / 100 / span,
                    2 * (renderH * TargetPercent / 100) / (span + HeadroomHalfRows)),
                1, MaxHalfWidth)
            : 1;
        halfH = Math.Max(1, halfW / 2);
    }

    // Paint the snapshot into the overlay bitmap
    private static bool Raster(int renderW, int renderH)
    {
        var span = _mapW + _mapH;
        if (span <= 0)
        {
            return false;
        }

        Fit(span, renderW, renderH, out var halfW, out var halfH);
        var outW = span * halfW;
        var headroom = HeadroomHalfRows * halfH;
        var outH = span * halfH + headroom;
        if (!EnsureRaster(outW * outH))
        {
            return false;
        }

        _outW = outW;
        _outH = outH;
        _halfW = halfW;
        _halfH = halfH;
        _headroom = headroom;
        _fitW = renderW;
        _fitH = renderH;
        ViewWidth = Math.Min(outW, renderW);
        ViewHeight = Math.Min(outH, renderH);
        OffsetX = (renderW - ViewWidth) / 2;
        OffsetY = (renderH - ViewHeight) / 2;
        _markerX = -1;

        NativeMemory.Clear(_inMap, (nuint)(outW * outH));
        NativeMemory.Clear(_raster, (nuint)(outW * outH * sizeof(ushort)));

        var originX = _mapH * halfW;
        var den = 2 * halfW * halfH;
        for (var py = 0; py < outH; py++)
        {
            var rowTerm = (py - headroom) * halfW;
            var row = _raster + (long)py * outW;
            for (var px = 0; px < outW; px++)
            {
                var skew = (px - originX) * halfH;
                var x = FloorDiv(rowTerm + skew, den);
                var y = FloorDiv(rowTerm - skew, den);
                if (x < 0 || x >= _mapW || y < 0 || y >= _mapH)
                {
                    continue;
                }

                var record = _cell + (y * _mapW + x) * CellRecord;
                if (record[CellGround] == 0)
                {
                    continue;
                }

                _inMap[(long)py * outW + px] = 1;

                var sprite = _groundSprite[record[CellGround]];
                if (sprite == null)
                {
                    row[px] = record[CellFlat];
                    continue;
                }

                var lx = px - (x - y - 1 + _mapH) * halfW;
                var ly = py - headroom - (x + y) * halfH;
                if ((uint)lx >= sprite[0] || (uint)ly >= sprite[1])
                {
                    row[px] = record[CellFlat];
                    continue;
                }

                var texel = sprite[2 + ly * (int)sprite[0] + lx];
                row[px] = texel != 0 ? (ushort)texel : record[CellFlat];
            }
        }

        DrawForeground();
        return true;
    }

    private static void DrawForeground()
    {
        if (_staticSprite == null || _cell == null)
        {
            return;
        }

        for (var depth = 0; depth <= _mapW + _mapH - 2; depth++)
        {
            var first = Math.Max(0, depth - (_mapH - 1));
            var last = Math.Min(_mapW - 1, depth);
            for (var x = first; x <= last; x++)
            {
                var y = depth - x;
                var record = _cell + (y * _mapW + x) * CellRecord;

                var boxLeft = (x - y + _mapH) * _halfW - _halfW;
                var boxBottom = (x + y + 1) * _halfH + _halfH + _headroom;

                DrawSprite(_staticSprite[record[CellLeft]], boxLeft, boxBottom,
                    _staticScreen[record[CellLeft]] != 0);
                DrawSprite(_staticSprite[record[CellRight]], boxLeft + _halfW, boxBottom,
                    _staticScreen[record[CellRight]] != 0);
            }
        }
    }

    private static void DrawSprite(uint* sprite, int left, int bottom, bool screen)
    {
        if (sprite == null || sprite == NoSprite)
        {
            return;
        }

        var w = (int)sprite[0];
        var h = (int)sprite[1];
        var pixels = sprite + 2;
        var top = bottom - h;

        for (var row = 0; row < h; row++)
        {
            var y = top + row;
            if (y < 0 || y >= _outH)
            {
                continue;
            }

            var src = pixels + (long)row * w;
            var dst = _raster + (long)y * _outW;
            for (var col = 0; col < w; col++)
            {
                var x = left + col;
                var texel = src[col];
                if (x < 0 || x >= _outW || texel == 0)
                {
                    continue;
                }

                var over = screen ? Screen(dst[x], (ushort)texel) : (ushort)texel;
                var coverage = (int)(texel >> CoverageShift);
                dst[x] = coverage >= 255 ? over : Blend(dst[x], over, coverage);
                _inMap[(long)y * _outW + x] = 1;
            }
        }
    }

    private static bool EnsureRaster(int pixels)
    {
        if (_raster != null && _rasterCap >= pixels)
        {
            return true;
        }

        if (_raster != null)
        {
            NativeMemory.Free(_raster);
            NativeMemory.Free(_inMap);
        }

        _raster = (ushort*)NativeMemory.Alloc((nuint)pixels * sizeof(ushort));
        _inMap = (byte*)NativeMemory.Alloc((nuint)pixels);
        _rasterCap = _raster != null && _inMap != null ? pixels : 0;
        return _rasterCap > 0;
    }

    // Player location
    private static void UpdateMarker()
    {
        var worldPane = WorldPane();
        if (worldPane == IntPtr.Zero)
        {
            return;
        }

        var x = *(int*)((byte*)worldPane + ViewXOffset);
        var y = *(int*)((byte*)worldPane + ViewYOffset);
        if (x < 0 || y < 0 || x >= _mapW || y >= _mapH)
        {
            return;
        }

        var size = Math.Clamp(_halfW, 3, MaxMarkerPixels);
        var left = Math.Max(0, (x - y + _mapH) * _halfW - size / 2);
        var top = Math.Max(0, (x + y + 1) * _halfH + _headroom - size / 2);
        if (left == _markerX && top == _markerY && size == _markerSize)
        {
            return;
        }

        _markerX = left;
        _markerY = top;
        _markerSize = size;
    }

    private static void UpdateCursor(int frameW, int frameH, int renderW, int renderH)
    {
        var screenPane = *(IntPtr*)(void*)Rebase(ScreenPaneGlobalVa);
        if (_cursor == null || !Plausible(screenPane))
        {
            _cursorValid = false;
            return;
        }

        var top = *(int*)((byte*)screenPane + CursorRectTopOffset);
        var left = *(int*)((byte*)screenPane + CursorRectLeftOffset);
        _cursorLeft = left * frameW / renderW;
        _cursorTop = top * frameH / renderH;
        _cursorSpanW = Math.Max(1, _cursorW * frameW / renderW);
        _cursorSpanH = Math.Max(1, _cursorH * frameH / renderH);

        _cursorValid = true;
    }

    #endregion

    #region Color and math helpers

    private static void Unpack(uint mask, out int shift, out int max)
    {
        shift = 0;
        max = 0;
        if (mask == 0)
        {
            max = 1;
            return;
        }

        while ((mask & 1) == 0)
        {
            mask >>= 1;
            shift++;
        }

        max = (int)mask;
    }

    private static ushort Pack(int r, int g, int b) => (ushort)((r << _rShift) | (g << _gShift) | (b << _bShift));
    private static uint Texel(ushort color, int coverage) => ((uint)coverage << CoverageShift) | color;

    // The client's wall and tree blend: out = max - (max - over) * (max - under) / max, per channel.
    private static ushort Screen(ushort under, ushort over)
    {
        return Pack(
            Channel(_rShift, _rMax), Channel(_gShift, _gMax), Channel(_bShift, _bMax));

        int Channel(int shift, int max)
        {
            return max - ((max - ((over >> shift) & max)) * (max - ((under >> shift) & max)) / max);
        }
    }

    private static ushort Blend(ushort under, ushort over, int coverage)
    {
        var rest = 255 - coverage;
        return Pack(
            (((under >> _rShift) & _rMax) * rest + ((over >> _rShift) & _rMax) * coverage) / 255,
            (((under >> _gShift) & _gMax) * rest + ((over >> _gShift) & _gMax) * coverage) / 255,
            (((under >> _bShift) & _bMax) * rest + ((over >> _bShift) & _bMax) * coverage) / 255);
    }

    private static int FloorDiv(int n, int d) => n >= 0 ? n / d : -((-n + d - 1) / d);

    #endregion
}
