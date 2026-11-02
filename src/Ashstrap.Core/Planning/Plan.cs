namespace Ashstrap.Core.Planning;

public enum UninstallMode
{
    /// <summary>Remove only the launcher; Roblox Player and Studio stay installed.</summary>
    LauncherOnly,
    /// <summary>Remove the launcher, Roblox Player and Roblox Studio.</summary>
    Everything,
}

public sealed record UninstallOptions(
    UninstallMode Mode,
    bool RemoveRobloxCache = false,
    /// <summary>Off by default to protect Studio plugins and settings.</summary>
    bool RemoveStudioUserData = false)
{
    public bool IncludesRoblox => Mode == UninstallMode.Everything;
}

public enum ComponentKind
{
    Launcher,
    RobloxPlayer,
    RobloxStudio,
    /// <summary>Files shared by Player and Studio: settings, vendor registry keys, empty folders.</summary>
    RobloxShared,
    RobloxCache,
    StudioUserData,
}

public static class ComponentNames
{
    public static string Display(ComponentKind k) => k switch
    {
        ComponentKind.Launcher => "Launcher",
        ComponentKind.RobloxPlayer => "Roblox Player",
        ComponentKind.RobloxStudio => "Roblox Studio",
        ComponentKind.RobloxShared => "Roblox settings and shared data",
        ComponentKind.RobloxCache => "Roblox cache and temporary files",
        ComponentKind.StudioUserData => "Roblox Studio user data",
        _ => k.ToString(),
    };
}

/// <summary>A registry precondition re-checked immediately before a registry change.</summary>
public abstract record RegistryPrecondition;

/// <summary>The shell\open\command of the key must still launch an executable located under <see cref="Directory"/>.</summary>
public sealed record CommandPointsUnder(string CommandKeyPath, string Directory) : RegistryPrecondition;

/// <summary>A value must still equal the expected text (case-insensitive).</summary>
public sealed record ValueEquals(string KeyPath, string? ValueName, string Expected) : RegistryPrecondition;

public abstract record PlanStep(ComponentKind Component, string Description);

public sealed record DeleteFileStep(ComponentKind Component, string Description, string Path)
    : PlanStep(Component, Description);

public enum TreeFilter
{
    /// <summary>Every file in the tree (used only for folders positively verified as owned).</summary>
    All,
    /// <summary>Only log/dump/temp file types (used for %TEMP%\Roblox).</summary>
    TempFileTypes,
}

public sealed record DeleteTreeStep(ComponentKind Component, string Description, string Path, TreeFilter Filter)
    : PlanStep(Component, Description);

public sealed record RemoveDirectoryIfEmptyStep(ComponentKind Component, string Description, string Path, bool IsVerifiedRoot)
    : PlanStep(Component, Description);

public sealed record DeleteShortcutStep(ComponentKind Component, string Description, string Path, string RequiredTargetRoot)
    : PlanStep(Component, Description);

public sealed record RemoveStartMenuFolderIfEmptyStep(ComponentKind Component, string Description, string Path)
    : PlanStep(Component, Description);

public sealed record DeleteRegistryKeyStep(ComponentKind Component, string Description, string KeyPath, RegistryPrecondition? Precondition)
    : PlanStep(Component, Description);

public sealed record DeleteRegistryValueStep(ComponentKind Component, string Description, string KeyPath, string? ValueName, string ExpectedValue)
    : PlanStep(Component, Description);

public sealed record SetRegistryValueStep(ComponentKind Component, string Description, string KeyPath, string? ValueName, string Value, RegistryPrecondition? Precondition)
    : PlanStep(Component, Description);

public sealed record RemoveRegistryKeyIfEmptyStep(ComponentKind Component, string Description, string KeyPath)
    : PlanStep(Component, Description);

public enum NoteKind
{
    /// <summary>User content intentionally kept (Documents\ROBLOX, Studio data when not selected).</summary>
    Preserved,
    /// <summary>The user chose not to remove it.</summary>
    KeptByChoice,
    /// <summary>Machine-wide; needs an administrator. Not touched.</summary>
    NeedsAdministrator,
    /// <summary>Could not be positively identified as Roblox's or the launcher's. Not touched.</summary>
    NotRecognized,
    /// <summary>Belongs to another program (e.g. another Roblox launcher). Not touched.</summary>
    OwnedByOtherProgram,
}

public sealed record PlanNote(NoteKind Kind, ComponentKind? Component, string Text);

public sealed class UninstallPlan
{
    public List<PlanStep> Steps { get; } = new();
    public List<PlanNote> Notes { get; } = new();

    public IEnumerable<PlanStep> For(ComponentKind c) => Steps.Where(s => s.Component == c);
}
