using System;

namespace DawndNet.Shared;

/// <summary>
///     Windowing options shared by the injector and payload
/// </summary>
internal struct WindowerOptions
{
    public const int MinScale = 1;
    public const int MaxScale = 2;

    public const int ScalingFill = 0;
    public const int ScalingInteger = 1;
    public const int MaxScalingMode = ScalingInteger;

    public bool Borderless { get; set; }
    public bool KeepIntro { get; set; }
    public bool LockAspect { get; set; }
    public bool CursorFix { get; set; }
    public bool Rain { get; set; }
    public bool Map { get; set; }
    public bool Glyphs { get; set; }

    // Integer scale of the 640x480 render size
    public int Scale { get; set; }

    // One of the Scaling* constants
    public int ScalingMode { get; set; }

    public static WindowerOptions Defaults => new()
    {
        LockAspect = true,
        CursorFix = true,
        Map = true,
        Scale = MinScale,
        ScalingMode = ScalingFill
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
        else if (key.Equals("rain", StringComparison.OrdinalIgnoreCase))
        {
            Rain = IniFile.IsTrue(value);
        }
        else if (key.Equals("map", StringComparison.OrdinalIgnoreCase))
        {
            Map = IniFile.IsTrue(value);
        }
        else if (key.Equals("glyphs", StringComparison.OrdinalIgnoreCase))
        {
            Glyphs = IniFile.IsTrue(value);
        }
        else if (key.Equals("scale", StringComparison.OrdinalIgnoreCase))
        {
            Scale = int.TryParse(value, out var scale) ? Clamp(scale) : MinScale;
        }
        else if (key.Equals("scalingmode", StringComparison.OrdinalIgnoreCase))
        {
            ScalingMode = ParseScalingMode(value);
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
        if (Rain) flags |= ConfigFlags.Rain;
        if (Map) flags |= ConfigFlags.Map;
        if (Glyphs) flags |= ConfigFlags.Glyphs;
        flags |= (uint)Clamp(Scale) << ConfigFlags.ScaleShift;
        flags |= (uint)ClampScalingMode(ScalingMode) << ConfigFlags.ScalingModeShift;
        return flags;
    }

    public static WindowerOptions FromFlags(uint flags) => new()
    {
        Borderless = (flags & ConfigFlags.Borderless) != 0,
        KeepIntro = (flags & ConfigFlags.KeepIntro) != 0,
        LockAspect = (flags & ConfigFlags.LockAspect) != 0,
        CursorFix = (flags & ConfigFlags.CursorFix) != 0,
        Rain = (flags & ConfigFlags.Rain) != 0,
        Map = (flags & ConfigFlags.Map) != 0,
        Glyphs = (flags & ConfigFlags.Glyphs) != 0,
        Scale = Clamp((int)((flags & ConfigFlags.ScaleMask) >> ConfigFlags.ScaleShift)),
        ScalingMode = ClampScalingMode((int)((flags & ConfigFlags.ScalingModeMask) >> ConfigFlags.ScalingModeShift)),
    };

    private static int Clamp(int scale) => scale < MinScale ? MinScale : scale > MaxScale ? MaxScale : scale;

    private static int ClampScalingMode(int mode) =>
        mode < ScalingFill ? ScalingFill : mode > MaxScalingMode ? ScalingFill : mode;

    private static int ParseScalingMode(string value)
    {
        if (value.Equals("fill", StringComparison.OrdinalIgnoreCase)) return ScalingFill;
        if (value.Equals("integer", StringComparison.OrdinalIgnoreCase)) return ScalingInteger;
        return int.TryParse(value, out var mode) ? ClampScalingMode(mode) : ScalingFill;
    }
}
