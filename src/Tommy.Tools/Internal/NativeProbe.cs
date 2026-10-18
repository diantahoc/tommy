namespace Tommy.Tools.Internal;

/// <summary>
/// Detects whether a native-dependency executable (Tesseract, Python,
/// LibreOffice) is available on PATH, so tools that lean on platform-specific
/// binaries can degrade gracefully — "tool disabled, clear message" — instead of
/// crashing when the dependency is missing.
/// </summary>
internal static class NativeProbe
{
    public static bool IsOnPath(string executable)
    {
        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(path)) return false;

        var extensions = OperatingSystem.IsWindows()
            ? (Environment.GetEnvironmentVariable("PATHEXT") ?? ".EXE;.CMD;.BAT").Split(';', StringSplitOptions.RemoveEmptyEntries)
            : new[] { string.Empty };

        foreach (var dir in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (var ext in extensions)
            {
                try
                {
                    if (File.Exists(Path.Combine(dir, executable + ext))) return true;
                }
                catch
                {
                    // Malformed PATH entry — ignore and keep scanning.
                }
            }
        }
        return false;
    }
}
