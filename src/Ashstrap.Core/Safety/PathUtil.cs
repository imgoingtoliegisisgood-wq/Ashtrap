namespace Ashstrap.Core.Safety;

public static class PathUtil
{
    /// <summary>Windows paths are case-insensitive; comparisons use this everywhere.</summary>
    public const StringComparison Cmp = StringComparison.OrdinalIgnoreCase;

    public static string Normalize(string path)
    {
        var full = Path.GetFullPath(Environment.ExpandEnvironmentVariables(path.Trim().Trim('"')));
        return TrimTrailingSeparators(full);
    }

    public static string TrimTrailingSeparators(string path)
    {
        var root = Path.GetPathRoot(path) ?? "";
        while (path.Length > root.Length &&
               (path.EndsWith(Path.DirectorySeparatorChar) || path.EndsWith(Path.AltDirectorySeparatorChar)))
        {
            path = path[..^1];
        }
        return path;
    }

    public static bool PathEquals(string a, string b) => Normalize(a).Equals(Normalize(b), Cmp);

    /// <summary>True when <paramref name="path"/> is <paramref name="root"/> or lies anywhere below it.</summary>
    public static bool IsSameOrUnder(string path, string root)
    {
        var p = Normalize(path);
        var r = Normalize(root);
        if (p.Equals(r, Cmp)) return true;
        var prefix = r.EndsWith(Path.DirectorySeparatorChar) ? r : r + Path.DirectorySeparatorChar;
        return p.StartsWith(prefix, Cmp);
    }

    public static bool IsStrictlyUnder(string path, string root) =>
        IsSameOrUnder(path, root) && !PathEquals(path, root);

    public static bool IsVolumeRoot(string path)
    {
        var p = Normalize(path);
        var root = Path.GetPathRoot(p);
        return root is not null && TrimTrailingSeparators(root).Equals(p, Cmp);
    }

    /// <summary>Splits a Windows command line such as  "C:\x\a.exe" -uninstall  into executable and arguments.</summary>
    public static (string Executable, string Arguments)? SplitCommandLine(string? command)
    {
        if (string.IsNullOrWhiteSpace(command)) return null;
        var s = Environment.ExpandEnvironmentVariables(command.Trim());
        if (s.StartsWith('"'))
        {
            int end = s.IndexOf('"', 1);
            if (end <= 1) return null;
            return (s[1..end], s[(end + 1)..].Trim());
        }
        // Unquoted: take the longest prefix ending in .exe
        int exe = s.IndexOf(".exe", Cmp);
        if (exe < 0)
        {
            int space = s.IndexOf(' ');
            return space < 0 ? (s, "") : (s[..space], s[(space + 1)..].Trim());
        }
        return (s[..(exe + 4)], s[(exe + 4)..].Trim());
    }
}
