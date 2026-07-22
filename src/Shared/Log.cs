using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;

namespace DawndNet.Shared;

/// <summary>
/// Debug logging via OutputDebugString, viewable in Sysinternals DebugView.
/// Build with -p:DAWND_LOG=false to strip it.
/// </summary>
internal static partial class Log
{
    [Conditional("DAWND_LOG")]
    public static void Write(string message) => OutputDebugStringA("[DawndNet] " + message);

    [LibraryImport("kernel32",
        StringMarshalling = StringMarshalling.Custom, StringMarshallingCustomType = typeof(AnsiStringMarshaller))]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial void OutputDebugStringA(string lpOutputString);
}
