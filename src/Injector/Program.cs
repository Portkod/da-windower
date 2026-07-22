using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using DawndNet.Shared;
using static DawndNet.Injector.Native;

namespace DawndNet.Injector;

internal static unsafe class Program
{
    private const string PayloadDll = "DawndNet.dll";
    private const string InitExport = "DAWnd_Init";

    private static int Main(string[] args)
    {
        var dir = AppContext.BaseDirectory;
        var options = WindowerOptions.Defaults;
        string? iniExe = null;
        string? cliExe = null;
        var iniForwarded = new StringBuilder();
        var cliForwarded = new StringBuilder();
        var overrides = new List<(string Key, string Value)>();
        var ignoreIni = false;

        for (var i = 0; i < args.Length; i++)
        {
            var a = args[i];
            if (!TrySplitSwitch(a, out var name, out var value))
            {
                AppendArg(cliForwarded, a); // not a --switch, forward to the game
            }
            else if (name.Equals("ignoreini", StringComparison.OrdinalIgnoreCase))
            {
                ignoreIni = true;
            }
            else if (name.Equals("exe", StringComparison.OrdinalIgnoreCase))
            {
                // --exe=<path>, or --exe <path> taking the next argument.
                var exe = (value ?? (i + 1 < args.Length ? args[++i] : null))?.Trim('"');
                if (!string.IsNullOrEmpty(exe))
                {
                    cliExe = exe;
                }
            }
            else if (options.IsOption(name))
            {
                // A bare --key means =true
                overrides.Add((name, value ?? "true"));
            }
            else
            {
                AppendArg(cliForwarded, a); // unknown --flag, forward verbatim
            }
        }

        if (!ignoreIni)
        {
            ReadSettings(Path.Combine(dir, ConfigFlags.SettingsFile), ref options, ref iniExe, iniForwarded);
        }

        // Command line overrides the ini, key by key.
        foreach (var (key, value) in overrides)
        {
            options.Apply(key, value);
        }

        var configFlags = options.ToFlags();

        // The ini's args= come first, then any forwarded command-line args.
        var forwarded = iniForwarded.Append(cliForwarded);

        var gameExe = cliExe ?? iniExe ?? Path.Combine(dir, "Darkages.exe");
        var payload = Path.Combine(dir, PayloadDll);

        if (!File.Exists(gameExe))
        {
            return Fail($"Game executable not found:\n{gameExe}");
        }

        if (!File.Exists(payload))
        {
            return Fail($"Payload not found next to the injector:\n{payload}");
        }

        // kernel32 sits at the same base in every process this session, so the local address of LoadLibraryA is valid in the target.
        var k32 = GetModuleHandleA("kernel32.dll");
        var pLoadLibraryA = GetProcAddress(k32, "LoadLibraryA");
        if (pLoadLibraryA == IntPtr.Zero)
        {
            return Fail("Could not resolve LoadLibraryA.");
        }

        // The payload's DAWnd_Init lives at a fixed RVA.
        // Map the payload into this process to read that RVA then rebase it onto the target's module base.
        var localPayload = LoadLibraryA(payload);
        if (localPayload == IntPtr.Zero)
        {
            return Fail("Could not load the payload locally to resolve its export.");
        }

        var pInitLocal = GetProcAddress(localPayload, InitExport);
        if (pInitLocal == IntPtr.Zero)
        {
            return Fail($"Payload does not export {InitExport}.");
        }

        var initRva = (uint)pInitLocal - (uint)localPayload;

        var si = new StartupInfo { cb = (uint)sizeof(StartupInfo) };
        // argv[0] is the quoted game path, the forwarded args follow it.
        var cmdline = Encoding.ASCII.GetBytes("\"" + gameExe + "\"" + forwarded + "\0");

        ProcessInformation pi;
        bool created;
        fixed (byte* pCmd = cmdline)
        {
            created = CreateProcessA(
                gameExe, pCmd, IntPtr.Zero, IntPtr.Zero, false,
                CREATE_SUSPENDED, IntPtr.Zero, Path.GetDirectoryName(gameExe),
                ref si, out pi);
        }

        if (!created)
        {
            return Fail($"CreateProcess failed (Win32 error {Marshal.GetLastWin32Error()}).");
        }

        try
        {
            // Write the payload path so the remote LoadLibraryA can find it.
            var pathBytes = Encoding.ASCII.GetBytes(payload + "\0");
            var pPath = VirtualAllocEx(pi.hProcess, IntPtr.Zero, (uint)pathBytes.Length, MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE);
            if (pPath == IntPtr.Zero)
            {
                return FailKill(pi, "VirtualAllocEx failed.");
            }

            fixed (byte* src = pathBytes)
            {
                if (!WriteProcessMemory(pi.hProcess, pPath, src, (uint)pathBytes.Length, out _))
                {
                    return FailKill(pi, "WriteProcessMemory failed.");
                }
            }

            // Load the payload in the target. Its HMODULE comes back as the exit code.
            if (!RunRemote(pi.hProcess, pLoadLibraryA, pPath, out var remoteModule) || remoteModule == 0)
            {
                return FailKill(pi, "Remote LoadLibrary failed.");
            }

            // Call DAWnd_Init at its rebased address and pass the config flags word
            var remoteInit = (IntPtr)(remoteModule + initRva);
            if (!RunRemote(pi.hProcess, remoteInit, unchecked((int)configFlags), out _))
            {
                return FailKill(pi, "Remote DAWnd_Init failed.");
            }

            if (ResumeThread(pi.hThread) == 0xFFFFFFFF)
            {
                return FailKill(pi, "ResumeThread failed.");
            }

            return 0;
        }
        finally
        {
            CloseHandle(pi.hThread);
            CloseHandle(pi.hProcess);
        }
    }

    /// <summary>
    ///     Read settings from the ini file if it exists
    /// </summary>
    /// <param name="path"></param>
    /// <param name="options"></param>
    /// <param name="exePath"></param>
    /// <param name="forwarded"></param>
    private static void ReadSettings(string path, ref WindowerOptions options, ref string? exePath, StringBuilder forwarded)
    {
        if (!File.Exists(path))
        {
            return;
        }

        foreach (var (key, value) in IniFile.Read(path))
        {
            if (options.Apply(key, value))
            {
                continue;
            }

            if (key.Equals("exe", StringComparison.OrdinalIgnoreCase))
            {
                var exe = value.Trim('"');
                if (exe.Length > 0)
                {
                    exePath = exe;
                }
            }
            else if (key.Equals("args", StringComparison.OrdinalIgnoreCase) && value.Length > 0)
            {
                forwarded.Append(' ').Append(value);
            }
        }
    }

    // Split a "--key" or "--key=value" argument into its name and optional value.
    private static bool TrySplitSwitch(string arg, out string name, out string? value)
    {
        name = "";
        value = null;
        if (arg.Length <= 2 || !arg.StartsWith("--", StringComparison.Ordinal))
        {
            return false;
        }

        var body = arg[2..];
        var eq = body.IndexOf('=');
        if (eq < 0)
        {
            name = body;
        }
        else
        {
            name = body[..eq];
            value = body[(eq + 1)..];
        }

        return true;
    }

    // Append one forwarded argument, re-quoting if needed. The OS already stripped outer quotes.
    private static void AppendArg(StringBuilder forwarded, string arg)
    {
        forwarded.Append(' ');
        if (arg.Length == 0 || arg.Contains(' '))
        {
            forwarded.Append('"').Append(arg).Append('"');
        }
        else
        {
            forwarded.Append(arg);
        }
    }

    // Run start(parameter) on a new thread in the target and return its exit code.
    private static bool RunRemote(IntPtr hProcess, IntPtr start, IntPtr parameter, out uint exitCode)
    {
        exitCode = 0;
        var hThread = CreateRemoteThread(hProcess, IntPtr.Zero, 0, start, parameter, 0, out _);
        if (hThread == IntPtr.Zero)
        {
            return false;
        }

        try
        {
            WaitForSingleObject(hThread, INFINITE);
            return GetExitCodeThread(hThread, out exitCode);
        }
        finally
        {
            CloseHandle(hThread);
        }
    }

    private static int Fail(string message)
    {
        MessageBoxA(IntPtr.Zero, message, "DawndNet", 0x10 /* MB_ICONERROR */);
        return 1;
    }

    private static int FailKill(ProcessInformation pi, string message)
    {
        return Fail(message + $"\n(Win32 error {Marshal.GetLastWin32Error()})");
    }
}
