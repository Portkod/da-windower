using System;
using System.Collections.Generic;
using System.IO;

namespace DawndNet.Shared;

/// <summary>
/// Parser for the ini settings file.
/// One "key = value" per line, '#' or ';' starts a comment.
/// </summary>
internal static class IniFile
{
    public static IEnumerable<(string Key, string Value)> Read(string path)
    {
        foreach (var rawLine in File.ReadAllLines(path))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line[0] == '#' || line[0] == ';')
            {
                continue;
            }

            var eq = line.IndexOf('=');
            if (eq < 0)
            {
                continue;
            }

            yield return (line[..eq].Trim(), line[(eq + 1)..].Trim());
        }
    }

    public static bool IsTrue(string value) => value.Equals("true", StringComparison.OrdinalIgnoreCase);
}
