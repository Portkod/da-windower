using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using DawndNet.Shared;
using static DawndNet.Payload.ClientMemory;
using static DawndNet.Payload.Interop;
using static DawndNet.Payload.Interop.Win32;

namespace DawndNet.Payload;

/// <summary>
///     Unlocks the double-byte half of the client's font
/// </summary>
internal static unsafe class WideGlyphs
{
    private const bool Enable = true;

    // CP949 / Unified Hangul Code lead-byte range
    private const byte LeadFirst = 0x81;
    private const byte LeadLast = 0xFE;

    // 7.41 sites, checked so another client version declines the feature instead of
    // getting its text renderer re-split underneath it.
    private const uint PasteFnVa = 0x57dd30; // ui_line_input_paste_clipboard()
    private const uint CtrlVTestVa = 0x585090; // ui_line_input_handle_event()'s 'v' compare
    private const uint ClipboardReadVa = 0x58019f; // the CF_TEXT arm of the clipboard reader

    private static void* _origGetClipboardData;

    // Our CF_TEXT block. The client only reads it between OpenClipboard and CloseClipboard,
    // so the previous one is safe to release when the next paste asks for text.
    private static IntPtr _converted;

    public static void Init(bool enabled)
    {
        if (!Enable || !enabled)
        {
            return;
        }

        ClientMemory.Init();
        if (!IsSupportedClient())
        {
            Log.Write("Wide glyphs: client is not 7.41. Feature disabled.");
            return;
        }

        var image = GetModuleHandleW(IntPtr.Zero);

        var origIsDbcs = PeImage.HookImport(image, "kernel32.dll", "IsDBCSLeadByte",
            (delegate* unmanaged[Stdcall]<uint, int>)&IsDbcsLeadByteHook);

        _origGetClipboardData = PeImage.HookImport(image, "user32.dll", "GetClipboardData",
            (delegate* unmanaged[Stdcall]<uint, IntPtr>)&GetClipboardDataHook);

        Log.Write($"Wide glyphs: IsDBCSLeadByte {(origIsDbcs != null ? "hooked" : "NOT FOUND")}, " +
                  $"GetClipboardData {(_origGetClipboardData != null ? "hooked" : "NOT FOUND")}.");
    }

    /// <summary>
    ///     The client splits text with the host's ANSI code page. Answer for CP949 instead, so the
    ///     face it actually loaded is the one deciding which byte pairs are glyphs.
    /// </summary>
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static int IsDbcsLeadByteHook(uint testChar)
    {
        var b = (byte)testChar;
        return b >= LeadFirst && b <= LeadLast ? 1 : 0;
    }

    /// <summary>
    ///     Re-encodes the clipboard for the client. Windows would down-convert CF_UNICODETEXT
    ///     through the host ANSI code page, which loses every character the font is interesting for.
    /// </summary>
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static IntPtr GetClipboardDataHook(uint format)
    {
        var orig = (delegate* unmanaged[Stdcall]<uint, IntPtr>)_origGetClipboardData;
        if (format != CF_TEXT)
        {
            return orig(format);
        }

        // Only the synthesized ANSI text is worth replacing. With no Unicode on the clipboard
        // there is nothing better to offer, so leave the client's own read alone.
        var wide = orig(CF_UNICODETEXT);
        if (wide == IntPtr.Zero)
        {
            return orig(CF_TEXT);
        }

        var converted = Convert(wide);
        if (converted == IntPtr.Zero)
        {
            return orig(CF_TEXT);
        }

        if (_converted != IntPtr.Zero)
        {
            GlobalFree(_converted);
        }

        _converted = converted;
        return converted;
    }

    // CF_UNICODETEXT handle -> a movable block of CP949 bytes we own, or zero.
    private static IntPtr Convert(IntPtr wide)
    {
        var text = (char*)GlobalLock(wide);
        if (text == null)
        {
            return IntPtr.Zero;
        }

        IntPtr block;
        try
        {
            // -1 measures and converts through the terminator, so the count covers it.
            var bytes = WideCharToMultiByte(CP_KOREAN, WC_NO_BEST_FIT_CHARS, text, -1, null, 0, IntPtr.Zero, IntPtr.Zero);
            if (bytes <= 0)
            {
                return IntPtr.Zero;
            }

            block = GlobalAlloc(GMEM_MOVEABLE | GMEM_ZEROINIT, (uint)bytes);
            if (block == IntPtr.Zero)
            {
                return IntPtr.Zero;
            }

            var dst = (byte*)GlobalLock(block);
            if (dst == null)
            {
                GlobalFree(block);
                return IntPtr.Zero;
            }

            var written = WideCharToMultiByte(CP_KOREAN, WC_NO_BEST_FIT_CHARS, text, -1, dst, bytes, IntPtr.Zero, IntPtr.Zero);
            GlobalUnlock(block);

            if (written <= 0)
            {
                GlobalFree(block);
                return IntPtr.Zero;
            }
        }
        finally
        {
            GlobalUnlock(wide);
        }

        return block;
    }

    // The client reads its clipboard and tests for Ctrl+V at fixed addresses. Verify both,
    // plus the paste routine itself, before re-splitting every string the renderer draws.
    private static bool IsSupportedClient() =>
        SiteHasBytes(PasteFnVa, [0x55, 0x8B, 0xEC, 0x83, 0xEC, 0x18, 0x89, 0x4D, 0xE8]) &&
        SiteHasBytes(CtrlVTestVa, [0x8B, 0x45, 0x08, 0x0F, 0xB6, 0x48, 0x10, 0x83, 0xF9, 0x76]) &&
        SiteHasBytes(ClipboardReadVa, [0x6A, 0x01, 0xFF, 0x15, 0xC8, 0x93, 0x66, 0x00]);
}
