/*
 * Early-init shim, linked into the Native AOT payload.
 *
 * Built by the CompileEarlyShim target in Payload.csproj
 */

typedef unsigned char BYTE;
typedef unsigned short USHORT;
typedef unsigned int UINT;
typedef unsigned long DWORD;
typedef int BOOL;
typedef void *HANDLE;
typedef void *HMODULE;

__declspec(dllimport) HMODULE __stdcall GetModuleHandleW(const USHORT *name);
__declspec(dllimport) BOOL __stdcall GetModuleHandleExW(DWORD flags, const USHORT *name, HMODULE *out);
__declspec(dllimport) void *__stdcall GetProcAddress(HMODULE module, const char *name);
__declspec(dllimport) BOOL __stdcall VirtualProtect(void *address, UINT size, DWORD protect, DWORD *old);

#define PAGE_READWRITE 0x04
#define GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS 0x04

/* Base of the host EXE. */
static BYTE *g_image;

/* The replaced import, called by the stub once the managed side is up. */
static void *g_original;

static int g_installed;

static int AnsiEqualsIgnoreCase(const char *a, const char *b)
{
    for (;; a++, b++)
    {
        char ca = *a;
        char cb = *b;
        if (ca >= 'A' && ca <= 'Z')
        {
            ca = (char)(ca + 32);
        }

        if (cb >= 'A' && cb <= 'Z')
        {
            cb = (char)(cb + 32);
        }

        if (ca != cb)
        {
            return 0;
        }

        if (!ca)
        {
            return 1;
        }
    }
}

static void **FindImportSlot(const char *dll, const char *function)
{
    BYTE *base = g_image;
    BYTE *nt;
    BYTE *optional;
    BYTE *desc;
    DWORD importDirRva;

    if (!base)
    {
        return 0;
    }

    /* IMAGE_DOS_HEADER.e_lfanew is at 0x3C; OptionalHeader at NT + 0x18;
       DataDirectory[1] (imports) at OptionalHeader + 0x68 for PE32. */
    nt = base + *(int *)(base + 0x3C);
    optional = nt + 0x18;
    importDirRva = *(DWORD *)(optional + 0x68);
    if (!importDirRva)
    {
        return 0;
    }

    for (desc = base + importDirRva;; desc += 20)
    {
        DWORD originalFirstThunk = *(DWORD *)(desc + 0x00);
        DWORD nameRva = *(DWORD *)(desc + 0x0C);
        DWORD firstThunk = *(DWORD *)(desc + 0x10);
        DWORD *names;
        void **iat;
        int i;

        if (!nameRva && !firstThunk)
        {
            return 0;
        }

        if (!AnsiEqualsIgnoreCase((const char *)(base + nameRva), dll))
        {
            continue;
        }

        /* If the INT is absent (bound-only), fall back to the IAT for names. */
        names = (DWORD *)(base + (originalFirstThunk ? originalFirstThunk : firstThunk));
        iat = (void **)(base + firstThunk);

        for (i = 0; names[i]; i++)
        {
            DWORD entry = names[i];
            if (entry & 0x80000000)
            {
                continue; /* imported by ordinal, no name to match */
            }

            /* IMAGE_IMPORT_BY_NAME puts the string after a 2-byte hint. */
            if (AnsiEqualsIgnoreCase((const char *)(base + entry + 2), function))
            {
                return &iat[i];
            }
        }

        return 0;
    }
}

static int PatchImport(const char *dll, const char *function, void *replacement)
{
    DWORD old;
    void **slot = FindImportSlot(dll, function);

    if (!slot)
    {
        return 0;
    }

    if (!VirtualProtect(slot, sizeof(void *), PAGE_READWRITE, &old))
    {
        return 0;
    }

    g_original = *slot;
    *slot = replacement;
    VirtualProtect(slot, sizeof(void *), old, &old);
    return 1;
}

/* Resolve and call the managed entry. Runs outside the loader lock, so the Native AOT
   runtime initializes under normal conditions. */
static void RunManagedEarlyInit(void)
{
    HMODULE self;
    void(__stdcall * entry)(void);

    if (g_installed)
    {
        return;
    }

    g_installed = 1;

    if (!GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS,
                            (const USHORT *)(void *)&RunManagedEarlyInit, &self))
    {
        return;
    }

    entry = (void(__stdcall *)(void))GetProcAddress(self, "DAWnd_EarlyInit");
    if (entry)
    {
        entry();
    }
}

/* GetStartupInfoA/W: void __stdcall (LPSTARTUPINFO). The CRT calls this just before WinMain. */
static void __stdcall GetStartupInfoStub(void *startupInfo)
{
    void(__stdcall * original)(void *) = (void(__stdcall *)(void *))g_original;
    RunManagedEarlyInit();
    original(startupInfo);
}

/* HeapCreate: HANDLE __stdcall (DWORD, SIZE_T, SIZE_T). Fallback for clients that import
   neither GetStartupInfo variant; the CRT calls it during heap init. */
static HANDLE __stdcall HeapCreateStub(DWORD options, UINT initialSize, UINT maximumSize)
{
    HANDLE(__stdcall * original)(DWORD, UINT, UINT) = (HANDLE(__stdcall *)(DWORD, UINT, UINT))g_original;
    RunManagedEarlyInit();
    return original(options, initialSize, maximumSize);
}

static int __cdecl InstallEarlyShim(void)
{
    g_image = (BYTE *)GetModuleHandleW(0);

    /* Only hook the game. This keeps the shim inert inside the injector, which loads this
       DLL locally to resolve the DAWnd_Init RVA, and inside anything else that pulls us in. */
    if (!FindImportSlot("ddraw.dll", "DirectDrawCreate"))
    {
        return 0;
    }

    /* 7.41 imports the W variant, older clients the A variant.
       Both are called from the CRT startup path before WinMain. */
    if (PatchImport("kernel32.dll", "GetStartupInfoW", (void *)GetStartupInfoStub))
    {
        return 0;
    }

    if (PatchImport("kernel32.dll", "GetStartupInfoA", (void *)GetStartupInfoStub))
    {
        return 0;
    }

    PatchImport("kernel32.dll", "HeapCreate", (void *)HeapCreateStub);
    return 0;
}

#pragma section(".CRT$XCU", long, read)
__declspec(allocate(".CRT$XCU")) int(__cdecl * g_installEarlyShim)(void) = InstallEarlyShim;
