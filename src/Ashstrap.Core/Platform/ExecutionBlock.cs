namespace Ashstrap.Core.Platform;

public enum BlockKind
{
    BlockedByPolicy,        // Software Restriction Policies / AppLocker / group policy
    BlockedByAppControl,    // WDAC / Smart App Control code-integrity policy
    BlockedByAntivirus,     // Defender or another AV quarantined / refused the file
    AccessDenied,
    ElevationRequired,
    NotFound,
    Cancelled,
    Other,
}

/// <summary>Why Windows refused to start a program, translated into something a user can act on.</summary>
public sealed record ExecutionBlock(BlockKind Kind, int Win32Error, string Message, bool AdministratorCanResolve)
{
    public static ExecutionBlock FromWin32(int error, string fallbackMessage)
    {
        return error switch
        {
            1260 => new(BlockKind.BlockedByPolicy, error,
                "Windows blocked this program through group policy (AppLocker or Software Restriction Policies).", true),
            4551 or 4553 or 4556 => new(BlockKind.BlockedByAppControl, error,
                "Windows Defender Application Control (or Smart App Control) blocked this program because it is not allowed by the code-integrity policy.", true),
            225 or 226 => new(BlockKind.BlockedByAntivirus, error,
                "Your antivirus software blocked or removed this program.", true),
            5 => new(BlockKind.AccessDenied, error,
                "Windows denied access when starting this program.", true),
            740 => new(BlockKind.ElevationRequired, error,
                "This program requires administrator permissions.", true),
            2 or 3 => new(BlockKind.NotFound, error,
                "The program file was not found.", false),
            1223 => new(BlockKind.Cancelled, error,
                "The operation was cancelled.", false),
            _ => new(BlockKind.Other, error, fallbackMessage, false),
        };
    }

    public string ToDisplayString() =>
        $"{Message} (Windows error {Win32Error})" +
        (AdministratorCanResolve ? " An administrator may be able to allow it; this uninstaller will not try to bypass the restriction." : "");
}
