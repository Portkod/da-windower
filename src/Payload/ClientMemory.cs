using System;
using static DawndNet.Payload.Interop;
using static DawndNet.Payload.Interop.Win32;

namespace DawndNet.Payload;

/// <summary>
///     Access to the 7.41 client's memory
/// </summary>
internal static unsafe class ClientMemory
{
    private const uint PreferredBase = 0x400000;
    private const uint WorldImplGlobalVa = 0x73d964; // -> WorldPane + 0x2EC
    private const int WorldImplOffset = 0x2ec;

    private static IntPtr _moduleBase;

    public static void Init()
    {
        if (_moduleBase == IntPtr.Zero)
        {
            _moduleBase = GetModuleHandleW(IntPtr.Zero);
        }
    }

    public static IntPtr Rebase(uint va) => _moduleBase + (nint)(va - PreferredBase);

    // Range check for a user-space heap pointer to avoid crashes.
    public static bool Plausible(IntPtr p) => (nuint)p >= 0x10000 && (nuint)p < 0x7FFF0000;

    // WorldPane, or zero outside the world (title screen, map transfer, relog).
    // The ctor publishes the interface sub-object into the global and the dtor clears it.
    public static IntPtr WorldPane()
    {
        var impl = *(IntPtr*)(void*)Rebase(WorldImplGlobalVa);
        return Plausible(impl) ? impl - WorldImplOffset : IntPtr.Zero;
    }

    // Verify a hardcoded address still holds the expected bytes, so a different client
    // version fails the check instead of faulting or corrupting
    public static bool SiteHasBytes(uint va, ReadOnlySpan<byte> sig)
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
}
