using System;
using System.IO;
using System.Text;
using DawndNet.Shared;
using static DawndNet.Payload.Interop;

namespace DawndNet.Payload;

/// <summary>
/// The config is resolved once at install time.
/// Inject mode reads them from the injector's flags word.
/// Proxy mode reads ini from the game directory.
/// </summary>
internal readonly struct PayloadConfig
{
    public bool BorderlessRequested { get; private init; }
    public bool SkipIntro { get; private init; }
    public bool LockAspectRatio { get; private init; }
    public bool CursorFix { get; private init; }
    public bool Rain { get; private init; }
    public bool Map { get; private init; }
    public int Scale { get; private init; }

    public static PayloadConfig Resolve(IntPtr configParam)
    {
        var flags = unchecked((uint)configParam);
        if ((flags & ConfigFlags.Marker) == 0)
        {
            return LoadIni();
        }

        var cfg = From(WindowerOptions.FromFlags(flags));
        Log.Write($"Config (injector): borderless={cfg.BorderlessRequested} skipIntro={cfg.SkipIntro} " +
                  $"lockAspect={cfg.LockAspectRatio} cursorFix={cfg.CursorFix} rain={cfg.Rain} map={cfg.Map} " +
                  $"scale={cfg.Scale}");
        return cfg;
    }

    private static PayloadConfig LoadIni()
    {
        var options = WindowerOptions.Defaults;

        var dir = GameDirectory();
        if (dir != null)
        {
            var path = dir + "\\" + ConfigFlags.SettingsFile;
            if (!File.Exists(path))
            {
                Log.Write($"Proxy: no {ConfigFlags.SettingsFile}; using defaults.");
            }
            else
            {
                foreach (var (key, value) in IniFile.Read(path))
                {
                    options.Apply(key, value);
                }

                var loaded = From(options);
                Log.Write($"Config ({ConfigFlags.SettingsFile}): borderless={loaded.BorderlessRequested} " +
                          $"skipIntro={loaded.SkipIntro} lockAspect={loaded.LockAspectRatio} cursorFix={loaded.CursorFix} " +
                          $"rain={loaded.Rain} map={loaded.Map} scale={loaded.Scale}");
                return loaded;
            }
        }

        return From(options);
    }

    private static PayloadConfig From(WindowerOptions o) => new()
    {
        BorderlessRequested = o.Borderless,
        SkipIntro = !o.KeepIntro,
        LockAspectRatio = o.LockAspect,
        CursorFix = o.CursorFix,
        Rain = o.Rain,
        Map = o.Map,
        Scale = o.Scale,
    };

    private static unsafe string? GameDirectory()
    {
        var buf = stackalloc byte[260];
        var n = GetModuleFileNameA(IntPtr.Zero, buf, 260);
        if (n == 0 || n >= 260)
        {
            return null;
        }

        var full = Encoding.ASCII.GetString(buf, (int)n);
        var slash = full.LastIndexOf('\\');
        return slash > 0 ? full[..slash] : null;
    }
}
