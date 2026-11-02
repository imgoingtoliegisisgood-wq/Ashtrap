using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace Ashstrap.Core.Platform;

[SupportedOSPlatform("windows")]
public sealed class WindowsProcessService : IProcessService
{
    public IReadOnlyList<RunningProcess> FindByNames(IEnumerable<string> processNames)
    {
        var result = new List<RunningProcess>();
        foreach (var name in processNames)
        {
            Process[] procs;
            try { procs = Process.GetProcessesByName(name); }
            catch { continue; }

            foreach (var p in procs)
            {
                using (p)
                {
                    DateTime? start = null;
                    try { start = p.StartTime; } catch { /* access denied for other-session processes */ }
                    result.Add(new RunningProcess(p.Id, p.ProcessName, QueryImagePath(p.Id), start));
                }
            }
        }
        return result;
    }

    public bool RequestClose(int processId)
    {
        try
        {
            using var p = Process.GetProcessById(processId);
            // CloseMainWindow sends WM_CLOSE to the main window: the app can save state or refuse.
            return p.CloseMainWindow();
        }
        catch
        {
            return false;
        }
    }

    public bool WaitForExit(int processId, DateTime? expectedStart, TimeSpan timeout)
    {
        Process p;
        try { p = Process.GetProcessById(processId); }
        catch (ArgumentException) { return true; } // not running
        using (p)
        {
            try
            {
                if (expectedStart is { } s && Math.Abs((p.StartTime - s).TotalSeconds) > 2)
                    return true; // the PID now belongs to a different process; ours has exited
            }
            catch { /* fall through and wait */ }

            try { return p.WaitForExit(timeout); }
            catch (InvalidOperationException) { return true; }
        }
    }

    public ProcessRunResult RunAndWait(string executable, string arguments, TimeSpan timeout, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(executable, arguments)
        {
            UseShellExecute = false, // started directly by CreateProcess; no cmd.exe, no shell
            WorkingDirectory = Path.GetDirectoryName(executable) ?? "",
        };
        Process? p;
        try
        {
            p = Process.Start(psi);
        }
        catch (Win32Exception ex)
        {
            return new ProcessRunResult(false, null, false, ExecutionBlock.FromWin32(ex.NativeErrorCode, ex.Message), ex.Message);
        }
        catch (Exception ex)
        {
            return new ProcessRunResult(false, null, false, null, ex.Message);
        }
        if (p is null)
            return new ProcessRunResult(false, null, false, null, "The process did not start.");

        using (p)
        {
            var deadline = DateTime.UtcNow + timeout;
            while (!p.WaitForExit(250))
            {
                if (ct.IsCancellationRequested || DateTime.UtcNow > deadline)
                    // Deliberately not killing it: it is the vendor's uninstaller and may be waiting on the user.
                    return new ProcessRunResult(true, null, true, null, "Still running after the time limit.");
            }
            return new ProcessRunResult(true, p.ExitCode, false, null, null);
        }
    }

    public ProcessRunResult Start(string executable, IEnumerable<string> arguments, string? workingDirectory)
    {
        var psi = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            WorkingDirectory = workingDirectory ?? Path.GetDirectoryName(executable) ?? "",
        };
        foreach (var a in arguments) psi.ArgumentList.Add(a);
        try
        {
            using var p = Process.Start(psi);
            return p is null
                ? new ProcessRunResult(false, null, false, null, "The process did not start.")
                : new ProcessRunResult(true, null, false, null, null);
        }
        catch (Win32Exception ex)
        {
            return new ProcessRunResult(false, null, false, ExecutionBlock.FromWin32(ex.NativeErrorCode, ex.Message), ex.Message);
        }
        catch (Exception ex)
        {
            return new ProcessRunResult(false, null, false, null, ex.Message);
        }
    }

    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inherit, int pid);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool QueryFullProcessImageName(IntPtr hProcess, uint flags, StringBuilder exeName, ref uint size);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr h);

    private static string? QueryImagePath(int pid)
    {
        IntPtr h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (h == IntPtr.Zero) return null;
        try
        {
            var sb = new StringBuilder(1024);
            uint size = (uint)sb.Capacity;
            return QueryFullProcessImageName(h, 0, sb, ref size) ? sb.ToString(0, (int)size) : null;
        }
        finally
        {
            CloseHandle(h);
        }
    }
}
