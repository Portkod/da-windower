using System;

namespace DawndNet.Payload;

/// <summary>
/// Minimal PE parsing to repoint a loaded module's Import Address Table slot
/// </summary>
internal static unsafe class PeImage
{
    /// <summary>
    /// Repoints the IAT slot for dllName!funcName. Returns the original pointer, or null if not found.
    /// </summary>
    /// <param name="moduleBase"></param>
    /// <param name="dllName"></param>
    /// <param name="funcName"></param>
    /// <param name="replacement"></param>
    /// <returns></returns>
    public static void* HookImport(IntPtr moduleBase, string dllName, string funcName, void* replacement)
    {
        var baseAddr = (byte*)moduleBase;

        // IMAGE_DOS_HEADER.e_lfanew is at 0x3C.
        var lfanew = *(int*)(baseAddr + 0x3C);
        var nt = baseAddr + lfanew; // IMAGE_NT_HEADERS32
        // Signature(4) + FileHeader(20) puts OptionalHeader at nt + 0x18.
        var optional = nt + 0x18;
        // DataDirectory starts at OptionalHeader + 0x60 (PE32).
        // Entry [1] is the import table, 8 bytes each, so its RVA is at +0x68.
        var importDirRva = *(uint*)(optional + 0x68);
        if (importDirRva == 0)
        {
            return null;
        }

        // Walk IMAGE_IMPORT_DESCRIPTOR[] (20 bytes each) until a zeroed terminator.
        var desc = baseAddr + importDirRva;
        for (;; desc += 20)
        {
            var originalFirstThunk = *(uint*)(desc + 0x00); // INT (names)
            var nameRva = *(uint*)(desc + 0x0C);
            var firstThunk = *(uint*)(desc + 0x10); // IAT
            if (nameRva == 0 && firstThunk == 0)
            {
                break;
            }

            if (!Interop.AnsiEqualsIgnoreCase(baseAddr + nameRva, dllName))
            {
                continue;
            }

            // If the INT is absent (bound-only), fall back to the IAT for names.
            var intThunk = (uint*)(baseAddr + (originalFirstThunk != 0 ? originalFirstThunk : firstThunk));
            var iat = (void**)(baseAddr + firstThunk);

            for (var i = 0; intThunk[i] != 0; i++)
            {
                var entry = intThunk[i];
                if ((entry & 0x80000000) != 0)
                {
                    continue; // imported by ordinal, no name to match
                }

                // IMAGE_IMPORT_BY_NAME { WORD Hint; CHAR Name[]; } puts the name at +2.
                var impName = baseAddr + entry + 2;
                if (!Interop.AnsiEquals(impName, funcName))
                {
                    continue;
                }

                var original = iat[i];
                if (Interop.VirtualProtect(&iat[i], (uint)IntPtr.Size, Interop.Win32.PAGE_READWRITE, out var old))
                {
                    iat[i] = replacement;
                    Interop.VirtualProtect(&iat[i], (uint)IntPtr.Size, old, out _);
                }

                return original;
            }
            // The DLL matched but the function was not imported here.
            // Keep scanning in case another descriptor with the same name imports it.
        }

        return null;
    }
}
