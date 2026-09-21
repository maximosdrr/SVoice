using System.Diagnostics;

namespace SVoice.GameBarBridge;

/// <summary>
/// Locates the installed XTTS service (interpreter + launcher script).
/// </summary>
/// <remarks>
/// Search order:
/// <list type="number">
/// <item><c>SVOICE_SERVICE_DIR</c> / <c>SVOICE_XTTS_PYTHON</c> environment overrides (development).</item>
/// <item><c>%ProgramFiles%\SVoice\service</c> with <c>%ProgramFiles%\SVoice\runtime\python\python.exe</c> (installer layout).</item>
/// <item>The repository checkout above the bridge binary with the development virtual environment.</item>
/// </list>
/// The Flutter application is never required.
/// </remarks>
internal sealed record ServiceLocation(string Python, string Launcher, string? RuntimeDirectory, string Origin)
{
    public string WorkingDirectory => Path.GetDirectoryName(Launcher)!;
}

internal static class ServiceLocator
{
    public const string LauncherFileName = "svoice_xtts_service.py";

    public static ServiceLocation Locate()
    {
        var overrideDir = Environment.GetEnvironmentVariable("SVOICE_SERVICE_DIR");
        var overridePython = Environment.GetEnvironmentVariable("SVOICE_XTTS_PYTHON");
        if (!string.IsNullOrWhiteSpace(overrideDir))
        {
            var launcher = Path.Combine(overrideDir, LauncherFileName);
            var python = !string.IsNullOrWhiteSpace(overridePython) && File.Exists(overridePython)
                ? overridePython
                : FindPython(Path.GetFullPath(Path.Combine(overrideDir, "..", "runtime")));
            if (File.Exists(launcher) && python != null)
            {
                return new ServiceLocation(python, Path.GetFullPath(launcher), RuntimeDirectoryFor(launcher), "environment");
            }
        }

        var candidates = new List<(string Root, string Origin)>
        {
            (Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "SVoice"), "installed"),
            (Path.Combine(KnownFolders.UnredirectedLocalAppData, "Programs", "SVoice"), "installed-user"),
        };

        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        for (var depth = 0; directory != null && depth < 8; depth++, directory = directory.Parent)
        {
            candidates.Add((directory.FullName, "repository"));
        }

        foreach (var (root, origin) in candidates)
        {
            var launcher = Path.Combine(root, "service", LauncherFileName);
            if (!File.Exists(launcher))
            {
                continue;
            }

            var python = FindPython(Path.Combine(root, "runtime"))
                ?? FindDevelopmentPython(root, overridePython);
            if (python != null)
            {
                return new ServiceLocation(python, launcher, RuntimeDirectoryFor(launcher), origin);
            }
        }

        throw new FileNotFoundException(
            "O mecanismo XTTS do SVoice não está instalado. Execute o instalador do SVoice para restaurá-lo.");
    }

    private static string? RuntimeDirectoryFor(string launcher)
    {
        var runtime = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(launcher)!, "..", "runtime"));
        return Directory.Exists(runtime) ? runtime : null;
    }

    private static string? FindPython(string runtimeDirectory)
    {
        var python = Path.Combine(runtimeDirectory, "python", "python.exe");
        return File.Exists(python) ? python : null;
    }

    private static string? FindDevelopmentPython(string root, string? overridePython)
    {
        if (!string.IsNullOrWhiteSpace(overridePython) && File.Exists(overridePython))
        {
            return overridePython;
        }

        var venv = Path.Combine(root, "python_service", ".build-venv", "Scripts", "python.exe");
        return File.Exists(venv) ? venv : null;
    }

    public static ProcessStartInfo CreateStartInfo(ServiceLocation location, string dataDirectory, int idleTimeoutSeconds)
    {
        var info = new ProcessStartInfo
        {
            FileName = location.Python,
            WorkingDirectory = location.WorkingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = System.Text.Encoding.UTF8,
            StandardErrorEncoding = System.Text.Encoding.UTF8,
        };
        info.ArgumentList.Add(location.Launcher);
        info.ArgumentList.Add("--data-dir");
        info.ArgumentList.Add(dataDirectory);
        info.ArgumentList.Add("--idle-timeout");
        info.ArgumentList.Add(idleTimeoutSeconds.ToString());
        info.ArgumentList.Add("--print-discovery");
        if (location.RuntimeDirectory != null)
        {
            info.ArgumentList.Add("--runtime-dir");
            info.ArgumentList.Add(location.RuntimeDirectory);
        }

        info.Environment["PYTHONIOENCODING"] = "utf-8";
        info.Environment["PYTHONUTF8"] = "1";
        return info;
    }
}
