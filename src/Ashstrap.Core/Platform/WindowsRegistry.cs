using System.Runtime.Versioning;
using Microsoft.Win32;

namespace Ashstrap.Core.Platform;

[SupportedOSPlatform("windows")]
public sealed class WindowsRegistry : IRegistry
{
    private static RegistryKey OpenBase(RegHive hive) => hive switch
    {
        RegHive.CurrentUser => RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Default),
        RegHive.LocalMachine64 => RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64),
        RegHive.LocalMachine32 => RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry32),
        _ => throw new ArgumentOutOfRangeException(nameof(hive)),
    };

    private static RegistryKey? OpenRead(RegHive hive, string path)
    {
        using var root = OpenBase(hive);
        return root.OpenSubKey(path, writable: false);
    }

    public bool KeyExists(RegHive hive, string path)
    {
        using var key = OpenRead(hive, path);
        return key is not null;
    }

    public object? GetValue(RegHive hive, string path, string? name)
    {
        using var key = OpenRead(hive, path);
        // DoNotExpandEnvironmentNames: we want the literal stored command, then expand ourselves.
        return key?.GetValue(name ?? "", null, RegistryValueOptions.None);
    }

    public IReadOnlyList<string> GetSubKeyNames(RegHive hive, string path)
    {
        using var key = OpenRead(hive, path);
        return key?.GetSubKeyNames() ?? Array.Empty<string>();
    }

    public IReadOnlyList<string> GetValueNames(RegHive hive, string path)
    {
        using var key = OpenRead(hive, path);
        return key?.GetValueNames() ?? Array.Empty<string>();
    }

    public void SetUserValue(string path, string? name, object value)
    {
        using var root = OpenBase(RegHive.CurrentUser);
        using var key = root.CreateSubKey(path, writable: true)
            ?? throw new UnauthorizedAccessException($"Could not open HKCU\\{path} for writing.");
        key.SetValue(name ?? "", value);
    }

    public void DeleteUserKeyTree(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Trim('\\').Length == 0)
            throw new ArgumentException("Refusing to delete the root of HKEY_CURRENT_USER.", nameof(path));
        using var root = OpenBase(RegHive.CurrentUser);
        root.DeleteSubKeyTree(path, throwOnMissingSubKey: false);
    }

    public void DeleteUserValue(string path, string? name)
    {
        using var root = OpenBase(RegHive.CurrentUser);
        using var key = root.OpenSubKey(path, writable: true);
        key?.DeleteValue(name ?? "", throwOnMissingValue: false);
    }
}
