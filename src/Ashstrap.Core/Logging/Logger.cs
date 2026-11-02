namespace Ashstrap.Core.Logging;

public enum LogLevel { Info, Warning, Error }

/// <summary>Thread-safe log that writes to a file (if given) and keeps an in-memory copy.</summary>
public sealed class Logger
{
    private readonly object _lock = new();
    private readonly string? _filePath;
    private readonly List<string> _lines = new();

    public event Action<LogLevel, string>? Logged;

    public Logger(string? filePath = null)
    {
        _filePath = filePath;
        if (filePath is not null)
        {
            try { Directory.CreateDirectory(Path.GetDirectoryName(filePath)!); }
            catch { /* logging must never break the uninstall */ }
        }
    }

    public string? FilePath => _filePath;

    public IReadOnlyList<string> Lines
    {
        get { lock (_lock) return _lines.ToArray(); }
    }

    public void Info(string message) => Write(LogLevel.Info, message);
    public void Warn(string message) => Write(LogLevel.Warning, message);
    public void Error(string message) => Write(LogLevel.Error, message);

    public void Write(LogLevel level, string message)
    {
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level.ToString().ToUpperInvariant()}] {message}";
        lock (_lock)
        {
            _lines.Add(line);
            if (_filePath is not null)
            {
                try { File.AppendAllText(_filePath, line + Environment.NewLine); }
                catch { /* ignore */ }
            }
        }
        Logged?.Invoke(level, message);
    }
}
