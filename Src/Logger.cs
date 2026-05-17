using System.Reflection;

namespace MediaPreviewSan;

public static class Logger
{
    private static readonly object _sync = new();
    private static readonly string _logPath = ResolveLogPath();

    private static string ResolveLogPath()
    {
        try
        {
            string? exePath = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exePath))
            {
                try { exePath = Application.ExecutablePath; }
                catch { exePath = null; }
            }
            if (string.IsNullOrEmpty(exePath))
            {
                exePath = Assembly.GetExecutingAssembly().Location;
            }
            string dir = !string.IsNullOrEmpty(exePath)
                ? (Path.GetDirectoryName(exePath) ?? AppContext.BaseDirectory)
                : AppContext.BaseDirectory;
            string name = !string.IsNullOrEmpty(exePath)
                ? Path.GetFileNameWithoutExtension(exePath)
                : "MediaPreviewSan";
            return Path.Combine(dir, $"{name}.log");
        }
        catch
        {
            return Path.Combine(AppContext.BaseDirectory, "MediaPreviewSan.log");
        }
    }

    public static void Log(string message)
    {
        try
        {
            string line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {message}{Environment.NewLine}";
            lock (_sync)
            {
                File.AppendAllText(_logPath, line);
            }
        }
        catch
        {
            // 書き込み失敗は黙殺
        }
    }
}
