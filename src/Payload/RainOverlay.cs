using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using DawndNet.Shared;
using static DawndNet.Payload.Interop;
using static DawndNet.Payload.Interop.Win32;

namespace DawndNet.Payload;

/// <summary>
///     Repurpose the snow particle system to re-create the removed rain effect (Flag 2)
/// </summary>
internal static unsafe class RainOverlay
{
    private const bool Enable = true;

    // Runtime gate, set once in Init.
    private static bool _enabled;

    private const bool EnableWeatherHook = true; // drive the client's weather session on mode 2
    private const bool EnableFallSpeed = true; // retune the session's own fall timer
    private const bool EnableStreakRedirect = true; // edit snowa sprites into rain streaks

    // 7.41 constants
    private const uint PreferredBase = 0x400000;
    private const uint ApplyWeatherModeVa = 0x5f26c0; // map_apply_weather_mode() [thiscall, no args]
    private const uint CreateWeatherVa = 0x5c82c0; // render_create_snow_overlay() [thiscall, no args]
    private const uint ClearWeatherVa = 0x5c8380; // weather session reset (shared_ptr, +0x230=null)
    private const uint WeatherTimerVa = 0x5bdb30; // WeatherSession::OnTimer(id,interval,arg) [thiscall, ret 0xC]
    private const uint SpawnLoopVa = 0x5bdb70; // render_spawn_snow_particles(rows) [thiscall, ret 4]
    private const uint ParticleCtorVa = 0x5bd710; // ui_snow_particle_pane_ctor(name,0) [thiscall, ret 8]
    private const uint EpfLoaderVa = 0x48b530; // file_load_image_frame(this,name,b,c,d) [thiscall]
    private const uint ArchiveReadVa = 0x472790; // file_archive_read_exact(entry,buf,size) [thiscall]

    private const uint WorldImplGlobalVa = 0x73d964; // -> WorldPane + 0x2EC
    private const int WorldImplOffset = 0x2ec;

    private const int PaneBlendModeOffset = 0x134;
    private const int ParticleBlendMode = 3; // drag-preview translucency; 1 = the opaque default
    private const bool EnableParticleBlend = true;

    private const int WeatherTypeOffset = 0x264; // WorldPane+0x264: current weather flag byte
    private const int WeatherObjectOffset = 0x230; // WorldPane+0x230: weather-particle session pointer
    private const int EntryOffsetField = 4; // [entry+4] = current archive read offset
    private const int WeatherTimerId = 1; // the session arms exactly one timer, ID 1
    private const byte RainFlag = 2;
    private const byte SnowFlag = 1;

    private const int ClientStepPixels = 2;
    private const int SnowTickMs = 100;
    private const int FallPixelsPerSecond = 250;
    private const int MinTickMs = 8;
    private static readonly int RainTickMs =
        Math.Max(MinTickMs, ClientStepPixels * 1000 / FallPixelsPerSecond);

    private const int SpawnDensityPermille = 2000;

    private const int StreakStagger = 2;
    private const int RunMinLen = 10; // shortest streak
    private const int RunLenVar = 0; // length spread: RunMinLen .. RunMinLen+RunLenVar
    private const int StreakCanvasH = RunMinLen + RunLenVar + StreakStagger; // 1 px wide canvas height
    private const int StreakMinRows = 6; // don't shrink the canvas below this for tiny files
    private const int EpfHeader = 12; // frame_count, w, h, pad, table_displacement
    private const int EpfRecord = 16; // one frame record, and the terminal boundary record
    private const bool EnablePig = false;
    private const int PigSnowaIndex = 3; // which snowa type (0-3) becomes a pig
    private const byte PigBody = 35; // palette index
    private const byte PigOutline = 37; // palette index
    private const byte PigEye = 31; // palette index

    // Hotkey to toggle rain
    private const int ToggleVk = 0x7A; // VK_F11

    // P=body, D=outline, K=eye
    private static readonly string[] PigArt =
    {
        ".DD......DD.",
        ".DPD....DPD.",
        ".DPPPPPPPPD.",
        "DPPPPPPPPPPD",
        "DPKPPPPPPKPD",
        "DPPPPPPPPPPD",
        "DPPPDDDDPPPD",
        "DPPDKPPKDPPD",
        "DPPPDDDDPPPD",
        ".DPPPPPPPPD.",
        "..DDD..DDD.."
    };

    private static IntPtr _moduleBase; // client image base, resolved in Init
    private static bool _forceActive; // set by hotkey
    private static void* _weatherTramp; // trampoline to the un-patched weather-mode dispatch
    private static bool _weatherInstalled;
    private static void* _timerTramp; // trampoline for the weather session timer
    private static bool _timerInstalled;
    private static void* _spawnTramp; // trampoline for the spawn loop
    private static bool _spawnInstalled;
    private static void* _particleTramp; // trampoline for the particle constructor
    private static bool _particleInstalled;
    private static void* _loaderTramp; // trampoline for the EPF loader
    private static void* _readTramp; // trampoline for the archive block read
    private static bool _streakInstalled;
    private static bool _overwriteLogged; // one-shot latch for the archive-edit diagnostics

    // Streak-redirect state for the load in progress.
    private static bool _redirectData; // a snowa+rain load -> overwrite on first read
    private static bool _restoreData; // a snowa load while rain is off -> restore on first read
    private static int _activeSnowaIndex = -1; // snowa NN of the load in progress (for the pig)
    private static int _streakBase = -1; // first-read latch (archive offset of the snowa file loading)

    // Per-file backups of the original snowa bytes. Keyed by the file's in-memory address
    // _bkIsRain tracks whether each is currently overwritten so the block-read hook only
    // edits on a rain<->snow transition
    private static readonly byte*[] _bkFile = new byte*[8];
    private static readonly byte*[] _bkOrig = new byte*[8];
    private static readonly int[] _bkSize = new int[8];
    private static readonly bool[] _bkIsRain = new bool[8];
    private static int _bkCount;

    // Rain streak appearance painted with legend.pal colors
    private static ReadOnlySpan<byte> StreakSeq => [15, 25, 15, 216, 26, 26, 15, 26, 15, 26];

    // The hook addresses are hardcoded for 7.41
    public static void Init(bool enabled)
    {
        _enabled = enabled;
        if (!Enable || !_enabled)
        {
            return;
        }

        _moduleBase = GetModuleHandleW(IntPtr.Zero);
        if (!ClientSupported())
        {
            Log.Write("RainOverlay: hook-site signatures not matched -> disabled (unsupported client).");
            return;
        }

        InstallWeatherHook();
        InstallStreakRedirect();
        InstallFallSpeed();
        InstallSpawnMultiplier();
        InstallParticleBlend();
        Log.Write($"RainOverlay init (weather={_weatherInstalled}, timer={_timerInstalled}, " +
                  $"streak={_streakInstalled}, spawn={_spawnInstalled}, blend={_particleInstalled}, " +
                  $"tick={RainTickMs}ms).");
    }

    // Toggle rain
    public static bool OnKeyDown(int vk)
    {
        if (!Enable || !_enabled || vk != ToggleVk)
        {
            return false;
        }

        _forceActive = !_forceActive;
        Log.Write($"RainOverlay hotkey -> force={_forceActive}");
        SyncWeatherSession(WorldPane());
        return true;
    }

    // WorldPane, or zero outside the world (title screen, map transfer, relog).
    private static IntPtr WorldPane()
    {
        var impl = *(IntPtr*)(void*)Rebase(WorldImplGlobalVa);
        return Plausible(impl) ? impl - WorldImplOffset : IntPtr.Zero;
    }

    // Read the client's own flag at WorldPane+0x264, which map_apply_weather_mode has already
    // applied by the time anything here runs.
    private static bool StreakActive()
    {
        if (_forceActive)
        {
            return true;
        }

        var wp = WorldPane();
        return wp != IntPtr.Zero && *((byte*)wp + WeatherTypeOffset) == RainFlag;
    }

    // range check for a user-space heap pointer to avoid crashes
    private static bool Plausible(IntPtr p) => (nuint)p >= 0x10000 && (nuint)p < 0x7FFF0000;

    private static int SlotOf(byte* file)
    {
        for (var i = 0; i < _bkCount; i++)
        {
            if (_bkFile[i] == file)
            {
                return i;
            }
        }

        return -1;
    }

    #region Weather mode

    private static void InstallWeatherHook()
    {
        if (!EnableWeatherHook || _weatherInstalled)
        {
            return;
        }

        // Prologue push ebp; mov ebp,esp; sub esp,0x14 = 55 8B EC 83 EC 14
        if (InstallInlineHook(Rebase(ApplyWeatherModeVa),
                (delegate* unmanaged[Thiscall]<IntPtr, void>)&ApplyWeatherModeDetour, 6, out _weatherTramp))
        {
            _weatherInstalled = true;
        }
        else
        {
            Log.Write("RainOverlay: weather-mode hook install failed.");
        }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvThiscall) })]
    private static void ApplyWeatherModeDetour(IntPtr worldPane)
    {
        ((delegate* unmanaged[Thiscall]<IntPtr, void>)_weatherTramp)(worldPane);
        if (worldPane != IntPtr.Zero)
        {
            SyncWeatherSession(worldPane);
        }
    }

    private static void SyncWeatherSession(IntPtr worldPane)
    {
        if (!Plausible(worldPane) || *((byte*)worldPane + WeatherTypeOffset) == SnowFlag)
        {
            return;
        }

        var session = (IntPtr*)((byte*)worldPane + WeatherObjectOffset);
        if (StreakActive())
        {
            if (*session == IntPtr.Zero)
            {
                ((delegate* unmanaged[Thiscall]<IntPtr, void>)(void*)Rebase(CreateWeatherVa))(worldPane);
            }
        }
        else if (*session != IntPtr.Zero)
        {
            ((delegate* unmanaged[Thiscall]<IntPtr, void>)(void*)Rebase(ClearWeatherVa))(worldPane);
        }
    }

    #endregion

    #region Fall speed (weather timer interval)

    private static void InstallFallSpeed()
    {
        if (!EnableFallSpeed || RainTickMs >= SnowTickMs || _timerInstalled)
        {
            return;
        }

        // Prologue push ebp; mov ebp,esp; push ecx; mov [ebp-4],ecx = 55 8B EC 51 89 4D FC
        if (InstallInlineHook(Rebase(WeatherTimerVa),
                (delegate* unmanaged[Thiscall]<IntPtr, int, int, int, byte>)&WeatherTimerDetour, 7, out _timerTramp))
        {
            _timerInstalled = true;
        }

        Log.Write($"RainOverlay: fall-speed hook installed={_timerInstalled} " +
                  $"({FallPixelsPerSecond} px/s at {RainTickMs}ms).");
    }

    // thiscall(session, timerId, intervalMs, arg) -> bool handled.
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvThiscall) })]
    private static byte WeatherTimerDetour(IntPtr session, int timerId, int intervalMs, int arg)
    {
        if (timerId == WeatherTimerId)
        {
            intervalMs = StreakActive() ? RainTickMs : SnowTickMs;
        }

        return ((delegate* unmanaged[Thiscall]<IntPtr, int, int, int, byte>)_timerTramp)(session, timerId, intervalMs, arg);
    }

    #endregion

    #region Density (spawn multiplier)

    private static void InstallSpawnMultiplier()
    {
        if (SpawnDensityPermille <= 1000 || _spawnInstalled)
        {
            return;
        }

        // Prologue push ebp; mov ebp,esp; push -1 = 55 8B EC 6A FF
        if (InstallInlineHook(Rebase(SpawnLoopVa), (delegate* unmanaged[Thiscall]<IntPtr, int, void>)&SpawnLoopDetour, 5, out _spawnTramp))
        {
            _spawnInstalled = true;
        }

        Log.Write($"RainOverlay: spawn multiplier installed={_spawnInstalled} (permille={SpawnDensityPermille}).");
    }

    // thiscall(weatherObj, rows): one pass walks that many rows of the particle template table.
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvThiscall) })]
    private static void SpawnLoopDetour(IntPtr weatherObj, int rows)
    {
        var orig = (delegate* unmanaged[Thiscall]<IntPtr, int, void>)_spawnTramp;
        orig(weatherObj, rows); // the client's own pass (native density for snow)

        if (!StreakActive() || rows <= 0)
        {
            return; // only rain gets the density multiplier
        }

        var extra = (int)((long)rows * (SpawnDensityPermille - 1000) / 1000);
        while (extra > 0)
        {
            var pass = extra > rows ? rows : extra;
            orig(weatherObj, pass);
            extra -= pass;
        }
    }

    #endregion

    #region Blend mode (soften the streaks)

    private static void InstallParticleBlend()
    {
        if (!EnableParticleBlend || _particleInstalled)
        {
            return;
        }

        // Prologue push ebp; mov ebp,esp; push -1 = 55 8B EC 6A FF
        if (InstallInlineHook(Rebase(ParticleCtorVa),
                (delegate* unmanaged[Thiscall]<IntPtr, IntPtr, int, IntPtr>)&ParticleCtorDetour, 5, out _particleTramp))
        {
            _particleInstalled = true;
        }

        Log.Write($"RainOverlay: particle blend installed={_particleInstalled} (mode={ParticleBlendMode}).");
    }

    // thiscall(this, name, 0) -> this. Snow keeps whatever the client gives it.
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvThiscall) })]
    private static IntPtr ParticleCtorDetour(IntPtr particle, IntPtr name, int arg)
    {
        var result = ((delegate* unmanaged[Thiscall]<IntPtr, IntPtr, int, IntPtr>)_particleTramp)(particle, name, arg);
        if (particle != IntPtr.Zero && StreakActive())
        {
            *(int*)((byte*)particle + PaneBlendModeOffset) = ParticleBlendMode;
        }

        return result;
    }

    #endregion

    #region Streak/pig sprite redirect (snow -> rain look)

    private static void InstallStreakRedirect()
    {
        if (!EnableStreakRedirect || _streakInstalled)
        {
            return;
        }

        // Loader prologue push ebp; mov ebp,esp; sub esp,0xd8 = 9 bytes.
        // Archive read prologue push ebp; mov ebp,esp; push ecx; push esi = 5 bytes.
        var okA = InstallInlineHook(Rebase(EpfLoaderVa),
            (delegate* unmanaged[Thiscall]<IntPtr, IntPtr, IntPtr, IntPtr, IntPtr, int>)&EpfLoaderDetour, 9, out _loaderTramp);
        var okB = InstallInlineHook(Rebase(ArchiveReadVa),
            (delegate* unmanaged[Thiscall]<IntPtr, IntPtr, IntPtr, int, int>)&ArchiveReadDetour, 5, out _readTramp);
        _streakInstalled = okA && okB;
        Log.Write($"RainOverlay: streak redirect installed (loader={okA}, read={okB}).");
    }

    // thiscall(this, name, b, c, d): a snowa EPF load starts here. Flag whether this load should have its
    // pixels overwritten with a rain streak or restored to snow, and reset the first-read
    // latch so the block-read hook acts once per load.
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvThiscall) })]
    private static int EpfLoaderDetour(IntPtr thisPtr, IntPtr name, IntPtr b, IntPtr c, IntPtr d)
    {
        var snowa = NameIsSnowa(name);
        var active = StreakActive();
        _redirectData = snowa && active;
        _restoreData = snowa && !active;
        if (snowa)
        {
            _streakBase = -1;
            _activeSnowaIndex = SnowaDigit(name);
        }

        return ((delegate* unmanaged[Thiscall]<IntPtr, IntPtr, IntPtr, IntPtr, IntPtr, int>)_loaderTramp)(thisPtr, name, b, c, d);
    }

    // thiscall(this=archive; entry, buf, size): the archive block read. The client reads snowa's frame
    // table + pixels via direct archive pointers, not this hook, so we can't serve fake bytes here.
    // On the first read of a snowa load, edit the archive's in-memory copy in place and overwrite
    // the flake pixels with a rain streak or restore the saved originals
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvThiscall) })]
    private static int ArchiveReadDetour(IntPtr archive, IntPtr entry, IntPtr buf, int size)
    {
        if ((_redirectData || _restoreData) && archive != IntPtr.Zero && entry != IntPtr.Zero && _streakBase < 0)
        {
            _streakBase = 0; // latch: act once per load
            var basePtr = *(byte**)((byte*)archive + 0x34);
            var fileOff = *(int*)((byte*)entry + EntryOffsetField); // first read -> file start
            var fileSize = *(int*)((byte*)entry + 8);
            if (basePtr != null && fileOff >= 0 && fileSize > EpfHeader)
            {
                var file = basePtr + fileOff;
                var slot = SlotOf(file);
                var isRain = slot >= 0 && _bkIsRain[slot];

                // Only touch the archive on a transition snowa is reloaded per particle
                // so acting on every load re-edits the same bytes thousands of times
                if (_redirectData && !isRain)
                {
                    OverwriteSnowa(file, fileSize);
                }
                else if (_restoreData && isRain)
                {
                    RestoreSnowa(file, fileSize);
                }
            }
        }

        return ((delegate* unmanaged[Thiscall]<IntPtr, IntPtr, IntPtr, int, int>)_readTramp)(archive, entry, buf, size);
    }

    private static void OverwriteSnowa(byte* file, int fileSize)
    {
        var slot = EnsureBackup(file, fileSize);
        if (slot < 0)
        {
            return;
        }

        if (!TryMakeWritable(file, fileSize, out var old, out var applied))
        {
            return;
        }

        var frameCount = *(ushort*)(file + 0);
        var h = 0;
        if (EnablePig && _activeSnowaIndex == PigSnowaIndex && WritePigEpf(file, fileSize))
        {
            // pig sprite written
        }
        else if (frameCount is > 0 and <= 64)
        {
            // Keep the real frame count so the client's animation logic still matches
            var perFrame = (fileSize - EpfHeader - (frameCount + 1) * EpfRecord) / frameCount;
            h = perFrame < StreakCanvasH ? perFrame : StreakCanvasH;
            if (h >= StreakMinRows)
            {
                WriteStreakEpf(file, fileSize, frameCount, 1, h, (uint)fileSize);
            }
            else
            {
                RecolourInBox(file, fileSize); // keep the flake size when too small
                h = 0;
            }
        }

        if (applied != 0)
        {
            VirtualProtect(file, (uint)fileSize, old, out _);
        }

        _bkIsRain[slot] = true;
        if (!_overwriteLogged)
        {
            _overwriteLogged = true;
            Log.Write($"RainOverlay: snowa rain (1x{h}, frames={frameCount}, size={fileSize}).");
        }
    }

    // Rewrite the whole file as a w×h canvas holding one streak per frame
    private static void WriteStreakEpf(byte* file, int fileSize, int frameCount, int w, int h, uint seed)
    {
        var perFrame = w * h;
        var tocOffset = perFrame * frameCount;
        var tocStart = tocOffset + EpfHeader;

        if (tocStart + (frameCount + 1) * EpfRecord > fileSize)
        {
            return;
        }

        *(ushort*)(file + 0) = (ushort)frameCount;
        *(ushort*)(file + 2) = (ushort)w;
        *(ushort*)(file + 4) = (ushort)h;
        *(ushort*)(file + 6) = 0;
        *(int*)(file + 8) = tocOffset;

        for (var f = 0; f < frameCount; f++)
        {
            var px = file + EpfHeader + f * perFrame;
            for (var i = 0; i < perFrame; i++)
            {
                px[i] = 0; // transparent
            }

            PaintStreak(px, w, h, w / 2, Hash(seed, (uint)f, 0));
        }

        var toc = file + tocStart;
        for (var f = 0; f < frameCount; f++)
        {
            var e = toc + f * EpfRecord;
            *(short*)(e + 0) = 0; // top
            *(short*)(e + 2) = 0; // left
            *(short*)(e + 4) = (short)h; // bottom
            *(short*)(e + 6) = (short)w; // right
            *(int*)(e + 8) = f * perFrame; // start (pixel-region relative)
            *(int*)(e + 12) = (f + 1) * perFrame; // end
        }

        WriteTerminalRecord(toc + frameCount * EpfRecord, tocOffset);
    }

    private static void WriteTerminalRecord(byte* record, int endOffset)
    {
        for (var i = 0; i < EpfRecord; i++)
        {
            record[i] = 0;
        }

        *(int*)(record + 8) = endOffset;
    }

    private static bool WritePigEpf(byte* file, int fileSize)
    {
        var h = PigArt.Length;
        var w = PigArt[0].Length;
        var perFrame = w * h;
        var total = EpfHeader + perFrame + 2 * EpfRecord; // one frame record + the terminal boundary
        if (total > fileSize)
        {
            return false;
        }

        *(ushort*)(file + 0) = 1; // one frame
        *(ushort*)(file + 2) = (ushort)w;
        *(ushort*)(file + 4) = (ushort)h;
        *(ushort*)(file + 6) = 0;
        *(int*)(file + 8) = perFrame; // tocOffset

        var px = file + EpfHeader;
        for (var y = 0; y < h; y++)
        {
            var rowText = PigArt[y];
            for (var x = 0; x < w; x++)
            {
                px[y * w + x] = rowText[x] switch
                {
                    'P' => PigBody,
                    'D' => PigOutline,
                    'K' => PigEye,
                    _ => 0 // transparent
                };
            }
        }

        var toc = file + EpfHeader + perFrame;
        *(short*)(toc + 0) = 0; // top
        *(short*)(toc + 2) = 0; // left
        *(short*)(toc + 4) = (short)h; // bottom
        *(short*)(toc + 6) = (short)w; // right
        *(int*)(toc + 8) = 0; // start
        *(int*)(toc + 12) = perFrame; // end
        WriteTerminalRecord(toc + EpfRecord, perFrame);
        return true;
    }

    private static void PaintStreak(byte* px, int w, int h, int x, uint r)
    {
        var seq = StreakSeq;
        var top = (int)(r % (StreakStagger + 1));
        var len = RunMinLen + (int)((r >> 8) % (RunLenVar + 1));
        if (top + len > h)
        {
            len = h - top;
        }

        var reversed = ((r >> 16) & 1) != 0;
        for (var i = 0; i < len; i++)
        {
            var s = i % seq.Length;
            px[(top + i) * w + x] = reversed ? seq[seq.Length - 1 - s] : seq[s];
        }
    }

    // Deterministic per-(file, frame, column) mix, so a given sprite always rebuilds identically.
    private static uint Hash(uint a, uint b, uint c)
    {
        var v = (a * 2654435761u) ^ ((b + 0x9E3779B9u) * 2246822519u) ^ (c * 3266489917u);
        v ^= v >> 15;
        v *= 2246822519u;
        v ^= v >> 13;
        return v;
    }

    // Fallback for files too small for a streak canvas
    // Keep the original frame table, so the terminal boundary the file shipped with stays valid.
    private static void RecolourInBox(byte* file, int fileSize)
    {
        var frameCount = *(ushort*)(file + 0);
        var tocStart = *(int*)(file + 8) + EpfHeader;
        if (frameCount is <= 0 or > 64 || tocStart < EpfHeader || tocStart + frameCount * EpfRecord > fileSize)
        {
            return;
        }

        for (var f = 0; f < frameCount; f++)
        {
            var e = file + tocStart + f * EpfRecord;
            var w = *(short*)(e + 6) - *(short*)(e + 2);
            var h = *(short*)(e + 4) - *(short*)(e + 0);
            var start = *(int*)(e + 8) + EpfHeader;
            if (w <= 0 || h <= 0 || start < EpfHeader || start + w * h > fileSize)
            {
                continue;
            }

            var px = file + start;
            for (var i = 0; i < w * h; i++)
            {
                px[i] = 0;
            }

            PaintStreak(px, w, h, w / 2, Hash((uint)fileSize, (uint)f, 0));
        }
    }

    // Make a foreign region writable
    private static bool TryMakeWritable(byte* file, int size, out uint old, out uint applied)
    {
        old = 0;
        applied = 0;

        var mbi = stackalloc byte[28];
        if (VirtualQuery(file, mbi, 28) == 28)
        {
            var protect = *(uint*)(mbi + 20);
            var type = *(uint*)(mbi + 24);
            var state = *(uint*)(mbi + 16);
            if (!_overwriteLogged)
            {
                Log.Write($"RainOverlay: snowa mem file=0x{(nint)file:X} size={size} state=0x{state:X} protect=0x{protect:X} type=0x{type:X}.");
            }

            // Already writable (private heap) -> just write, no restore.
            if (protect is PAGE_READWRITE or PAGE_EXECUTE_READWRITE or 0x08 /*WRITECOPY*/ or 0x80 /*EXECUTE_WRITECOPY*/)
            {
                return true;
            }
        }

        ReadOnlySpan<uint> tries = [PAGE_READWRITE, 0x08 /*WRITECOPY*/, PAGE_EXECUTE_READWRITE, 0x80 /*EXECUTE_WRITECOPY*/];
        foreach (var p in tries)
        {
            if (VirtualProtect(file, (uint)size, p, out old))
            {
                applied = p;
                return true;
            }
        }

        if (!_overwriteLogged)
        {
            _overwriteLogged = true;
            Log.Write($"RainOverlay: snowa make-writable failed (lastErr={Marshal.GetLastPInvokeError()}).");
        }

        return false;
    }

    private static void RestoreSnowa(byte* file, int fileSize)
    {
        var slot = SlotOf(file);
        if (slot < 0)
        {
            return;
        }

        var n = _bkSize[slot] < fileSize ? _bkSize[slot] : fileSize;
        if (!TryMakeWritable(file, n, out var old, out var applied))
        {
            return;
        }

        for (var j = 0; j < n; j++)
        {
            file[j] = _bkOrig[slot][j];
        }

        if (applied != 0)
        {
            VirtualProtect(file, (uint)n, old, out _);
        }

        _bkIsRain[slot] = false;
    }

    // Back up a file's original bytes once, keyed by its in-memory address. Returns the backup slot index,
    // or -1 if the table is full (should never happen for the four snowa files).
    private static int EnsureBackup(byte* file, int fileSize)
    {
        var existing = SlotOf(file);
        if (existing >= 0)
        {
            return existing;
        }

        if (_bkCount >= _bkFile.Length)
        {
            return -1;
        }

        var copy = (byte*)NativeMemory.Alloc((nuint)fileSize);
        for (var j = 0; j < fileSize; j++)
        {
            copy[j] = file[j];
        }

        _bkFile[_bkCount] = file;
        _bkOrig[_bkCount] = copy;
        _bkSize[_bkCount] = fileSize;
        _bkIsRain[_bkCount] = false;
        return _bkCount++;
    }

    // "snowaNN.epf" -> NN (0..3), or -1. Assumes NameIsSnowa already matched the prefix.
    private static int SnowaDigit(IntPtr name)
    {
        if (name == IntPtr.Zero)
        {
            return -1;
        }

        var p = (byte*)name;
        var d0 = p[5];
        var d1 = p[6];
        if (d0 < '0' || d0 > '9')
        {
            return -1;
        }

        var idx = d0 - '0';
        if (d1 >= '0' && d1 <= '9')
        {
            idx = idx * 10 + (d1 - '0');
        }

        return idx;
    }

    private static bool NameIsSnowa(IntPtr name)
    {
        if (name == IntPtr.Zero)
        {
            return false;
        }

        var p = (byte*)name;
        var pat = "snowa"u8;
        for (var i = 0; i < pat.Length; i++)
        {
            var ch = p[i];
            if (ch == 0)
            {
                return false;
            }

            if (ch >= 'A' && ch <= 'Z')
            {
                ch += 32;
            }

            if (ch != pat[i])
            {
                return false;
            }
        }

        return true;
    }

    #endregion

    #region Inline hook helpers

    // Resolved once in Init: StreakActive walks the WorldPane global on every timer tick and every
    // snowa load, so this must not cost a P/Invoke.
    private static IntPtr Rebase(uint va) => _moduleBase + (nint)(va - PreferredBase);

    private static bool ClientSupported() =>
        SiteHasBytes(ApplyWeatherModeVa, [0x55, 0x8B, 0xEC, 0x83, 0xEC, 0x14]) &&
        SiteHasBytes(CreateWeatherVa, [0x55, 0x8B, 0xEC, 0x6A, 0xFF]) &&
        SiteHasBytes(ClearWeatherVa, [0x55, 0x8B, 0xEC, 0x6A, 0xFF]) &&
        SiteHasBytes(WeatherTimerVa, [0x55, 0x8B, 0xEC, 0x51, 0x89, 0x4D, 0xFC]) &&
        SiteHasBytes(SpawnLoopVa, [0x55, 0x8B, 0xEC, 0x6A, 0xFF]) &&
        SiteHasBytes(ParticleCtorVa, [0x55, 0x8B, 0xEC, 0x6A, 0xFF]) &&
        SiteHasBytes(EpfLoaderVa, [0x55, 0x8B, 0xEC, 0x81, 0xEC, 0xD8, 0x00, 0x00, 0x00]) &&
        SiteHasBytes(ArchiveReadVa, [0x55, 0x8B, 0xEC, 0x51, 0x56]);

    private static bool SiteHasBytes(uint va, ReadOnlySpan<byte> sig)
    {
        var p = (byte*)Rebase(va);

        var mbi = stackalloc byte[28];
        if (VirtualQuery(p, mbi, 28) != 28)
        {
            return false;
        }

        var regionBase = *(nuint*)(mbi + 0);
        var regionSize = *(nuint*)(mbi + 12);
        var state = *(uint*)(mbi + 16);
        var protect = *(uint*)(mbi + 20);

        // Committed, readable (not NOACCESS / GUARD / execute-only) and large enough to hold the signature.
        const uint readable = 0x02 | 0x04 | 0x08 | 0x20 | 0x40 | 0x80; // R / RW / WC / XR / XRW / XWC
        if (state != MEM_COMMIT || (protect & 0x100) != 0 || (protect & readable) == 0)
        {
            return false;
        }

        if ((nuint)p < regionBase || (nuint)sig.Length > regionBase + regionSize - (nuint)p)
        {
            return false;
        }

        for (var i = 0; i < sig.Length; i++)
        {
            if (p[i] != sig[i])
            {
                return false;
            }
        }

        return true;
    }

    // Minimal 5+ byte inline detour with a trampoline back to the stolen bytes.
    private static bool InstallInlineHook(IntPtr target, void* detour, int stolenLen, out void* trampoline)
    {
        trampoline = null;
        var tramp = (byte*)VirtualAlloc(null, (uint)(stolenLen + 5),
            MEM_COMMIT | MEM_RESERVE, PAGE_EXECUTE_READWRITE);
        if (tramp == null)
        {
            return false;
        }

        var src = (byte*)target;
        for (var i = 0; i < stolenLen; i++)
        {
            tramp[i] = src[i];
        }

        // jmp from trampoline back to (target + stolenLen)
        tramp[stolenLen] = 0xE9;
        *(int*)(tramp + stolenLen + 1) = (int)(src + stolenLen - (tramp + stolenLen + 5));

        if (!VirtualProtect(src, (uint)stolenLen, PAGE_EXECUTE_READWRITE, out var old))
        {
            return false;
        }

        // jmp target -> detour, pad any leftover stolen bytes with nop.
        src[0] = 0xE9;
        *(int*)(src + 1) = (int)((byte*)detour - (src + 5));
        for (var i = 5; i < stolenLen; i++)
        {
            src[i] = 0x90;
        }

        VirtualProtect(src, (uint)stolenLen, old, out _);
        FlushInstructionCache(-1, src, (uint)stolenLen);

        trampoline = tramp;
        return true;
    }

    #endregion
}
