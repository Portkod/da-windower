using System;

namespace DawndNet.Shared;

/// <summary>
///     Windowing options shared by the injector and payload
/// </summary>
internal struct WindowerOptions
{
    public bool Borderless { get; set; }
    public bool KeepIntro { get; set; }
    public bool LockAspect { get; set; }
    public bool CursorFix { get; set; }

    public static WindowerOptions Defaults => new()
    {
        LockAspect = true,
        CursorFix = true,
    };

    // Returns false if the key is not a windowing option
    public bool Apply(string key, string value)
    {
        if (key.Equals("borderless", StringComparison.OrdinalIgnoreCase))
        {
            Borderless = IniFile.IsTrue(value);
        }
        else if (key.Equals("keepintro", StringComparison.OrdinalIgnoreCase))
        {
            KeepIntro = IniFile.IsTrue(value);
        }
        else if (key.Equals("lockaspect", StringComparison.OrdinalIgnoreCase))
        {
            LockAspect = IniFile.IsTrue(value);
        }
        else if (key.Equals("cursorfix", StringComparison.OrdinalIgnoreCase))
        {
            CursorFix = IniFile.IsTrue(value);
        }
        else
        {
            return false;
        }

        return true;
    }

    public readonly bool IsOption(string key)
    {
        var scratch = this;
        return scratch.Apply(key, "true");
    }

    public readonly uint ToFlags()
    {
        var flags = ConfigFlags.Marker;
        if (Borderless) flags |= ConfigFlags.Borderless;
        if (KeepIntro) flags |= ConfigFlags.KeepIntro;
        if (LockAspect) flags |= ConfigFlags.LockAspect;
        if (CursorFix) flags |= ConfigFlags.CursorFix;
        return flags;
    }

    public static WindowerOptions FromFlags(uint flags) => new()
    {
        Borderless = (flags & ConfigFlags.Borderless) != 0,
        KeepIntro = (flags & ConfigFlags.KeepIntro) != 0,
        LockAspect = (flags & ConfigFlags.LockAspect) != 0,
        CursorFix = (flags & ConfigFlags.CursorFix) != 0,
    };
}
