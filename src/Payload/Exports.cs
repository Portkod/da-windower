using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using DawndNet.Shared;

namespace DawndNet.Payload;

internal static unsafe class Exports
{
    /// <summary>
    /// Proxy mode entry
    /// </summary>
    /// <param name="guid"></param>
    /// <param name="ppDD"></param>
    /// <param name="outer"></param>
    /// <returns></returns>
    [UnmanagedCallersOnly(EntryPoint = "DirectDrawCreate", CallConvs = new[] { typeof(CallConvStdcall) })]
    public static int DirectDrawCreate(IntPtr guid, IntPtr* ppDD, IntPtr outer)
    {
        try
        {
            return DDrawHooks.DirectDrawCreateProxy(guid, ppDD, outer);
        }
        catch
        {
            Log.Write("DirectDrawCreate proxy threw; returning E_FAIL.");
            return unchecked((int)0x80004005);
        }
    }

    /// <summary>
    /// Inject mode entry
    /// </summary>
    /// <param name="configParam"></param>
    /// <returns></returns>
    [UnmanagedCallersOnly(EntryPoint = "DAWnd_Init", CallConvs = new[] { typeof(CallConvStdcall) })]
    public static uint DawndInit(IntPtr configParam)
    {
        try
        {
            Log.Write("DAWnd_Init: installing hooks (inject mode).");
            DDrawHooks.InstallInjected(configParam);
        }
        catch
        {
            Log.Write("DAWnd_Init: unhandled exception during install.");
        }

        return 0;
    }
}
