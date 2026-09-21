using System.Diagnostics;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SVoice.Setup;

/// <summary>
/// Command-line helper used by the SVoice installer, the uninstaller and the
/// "Reparar SVoice" shortcut. Every command prints a human-readable summary and,
/// with <c>--json</c>, a machine-readable object on the last line.
/// </summary>
internal static class Program
{
    public const int ExitOk = 0;
    public const int ExitError = 1;
    public const int ExitBlocked = 2;
    public const int ExitRebootRequired = 3010;

    private static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        var options = Options.Parse(args);
        if (options.Command == null || options.Command == "help")
        {
            PrintHelp();
            return options.Command == null ? ExitError : ExitOk;
        }

        Status.Initialize(options.Get("status-file"));
        try
        {
            var result = options.Command switch
            {
                "check" => await Checks.RunAsync(options),
                "detect-gpu" => Checks.DetectGpu(options),
                "install-cert" => Msix.InstallCertificate(options),
                "install-msix" => await Msix.InstallPackageAsync(options),
                "remove-msix" => await Msix.RemovePackageAsync(options),
                "verify-widget" => await Msix.VerifyWidgetAsync(options),
                "vbcable" => await VbCable.RunAsync(options),
                "install-runtime" => await RuntimeInstaller.RunAsync(options),
                "ensure-model" => await ServiceRunner.EnsureModelAsync(options),
                "test-service" => await ServiceRunner.TestAsync(options),
                "stop-service" => await ServiceRunner.StopAsync(options),
                "verify" => await Verification.RunAsync(options),
                "diagnostics" => await Verification.DiagnosticsAsync(options),
                "uninstall-data" => Data.Uninstall(options),
                "repair" => await Repair.RunAsync(options),
                _ => CommandResult.Fail($"Comando desconhecido: {options.Command}"),
            };
            Report(options, result);
            return result.ExitCode;
        }
        catch (Exception exception)
        {
            var failure = CommandResult.Fail(exception.Message);
            failure.Data["exception"] = exception.ToString();
            Report(options, failure);
            return ExitError;
        }
        finally
        {
            Status.Complete();
        }
    }

    private static void Report(Options options, CommandResult result)
    {
        foreach (var line in result.Lines)
        {
            Console.WriteLine(line);
        }

        if (!string.IsNullOrWhiteSpace(result.Message))
        {
            Console.WriteLine(result.Ok ? $"OK: {result.Message}" : $"ERRO: {result.Message}");
        }

        if (options.Has("json"))
        {
            var payload = new JsonObject
            {
                ["ok"] = result.Ok,
                ["exit_code"] = result.ExitCode,
                ["message"] = result.Message,
            };
            foreach (var (key, value) in result.Data)
            {
                payload[key] = value?.DeepClone();
            }

            Console.WriteLine(payload.ToJsonString(new JsonSerializerOptions { WriteIndented = false }));
        }

        var statusData = new Dictionary<string, JsonNode?>(result.Data)
        {
            ["exit_code"] = result.ExitCode,
            ["ok"] = result.Ok,
            ["lines"] = new JsonArray(result.Lines.Select(line => (JsonNode?)line).ToArray()),
        };
        Status.Write(result.Ok ? "done" : "failed", result.Message, 1.0, statusData);
        if (options.Has("pause"))
        {
            Console.WriteLine();
            Console.WriteLine("Pressione Enter para fechar.");
            Console.ReadLine();
        }
    }

    private static void PrintHelp()
    {
        Console.WriteLine("""
            SVoice.Setup — assistente de instalação do SVoice (Xbox Game Bar + XTTS v2)

            Comandos:
              check            --install-dir <dir> [--json]        pré-requisitos (Windows, Game Bar, GPU, disco)
              detect-gpu       [--json]                            GPU detectada e pack de runtime recomendado
              install-cert     --cer <arquivo>                     confia no certificado do widget (admin)
              install-msix     --msix <arquivo> [--cer <arquivo>]  instala/atualiza o widget preservando dados
              remove-msix                                          remove o widget
              verify-widget                                        confirma o widget no catálogo da Game Bar
              vbcable          --source <dir> [--install] [--json] detecta e instala o VB-CABLE (admin)
              install-runtime  --manifest <json> --target <dir> [--source <dir>] [--pack <nome>]...
              ensure-model     --service-dir <dir> [--runtime-dir <dir>] [--target <dir>]
              test-service     --service-dir <dir> [--runtime-dir <dir>] [--backend <id>]
              stop-service                                         encerra o serviço XTTS em execução
              verify           --install-dir <dir> [--after-reboot] verificação final / pós-reinicialização
              diagnostics      --install-dir <dir>                 relatório completo
              uninstall-data   [--profiles] [--models] [--all]     remove dados do usuário (com confirmação prévia)
              repair           --install-dir <dir>                 reinstala o widget, valida VB-CABLE, modelo e backend

            Opções comuns: --json, --status-file <arquivo>, --log <arquivo>
            """);
    }

    public static bool IsElevated()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    /// <summary>Re-runs this helper elevated with the given arguments and returns its exit code.</summary>
    public static int RunElevated(IEnumerable<string> arguments)
    {
        var executable = Environment.ProcessPath ?? throw new InvalidOperationException("Caminho do executável desconhecido.");
        var info = new ProcessStartInfo
        {
            FileName = executable,
            UseShellExecute = true,
            Verb = "runas",
            WindowStyle = ProcessWindowStyle.Hidden,
        };
        foreach (var argument in arguments)
        {
            info.ArgumentList.Add(argument);
        }

        using var process = Process.Start(info) ?? throw new InvalidOperationException("Não foi possível solicitar elevação.");
        process.WaitForExit();
        return process.ExitCode;
    }

    public static string LocalAppData => Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
    public static string ProgramData => Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
    public static string DataDirectory => Path.Combine(LocalAppData, "SVoice", "XTTS");
    public static string LogsDirectory => Path.Combine(LocalAppData, "SVoice", "Logs");
    public static string SharedModelsDirectory => Path.Combine(ProgramData, "SVoice", "models");
}

internal sealed class Options
{
    private readonly Dictionary<string, List<string>> _values = new(StringComparer.OrdinalIgnoreCase);

    public string? Command { get; private set; }

    public static Options Parse(string[] args)
    {
        var options = new Options();
        for (var index = 0; index < args.Length; index++)
        {
            var argument = args[index];
            if (argument.StartsWith("--", StringComparison.Ordinal))
            {
                var name = argument[2..];
                var value = "true";
                var separator = name.IndexOf('=');
                if (separator > 0)
                {
                    value = name[(separator + 1)..];
                    name = name[..separator];
                }
                else if (index + 1 < args.Length && !args[index + 1].StartsWith("--", StringComparison.Ordinal))
                {
                    value = args[++index];
                }

                if (!options._values.TryGetValue(name, out var list))
                {
                    options._values[name] = list = new List<string>();
                }

                list.Add(value);
            }
            else if (options.Command == null)
            {
                options.Command = argument.ToLowerInvariant();
            }
        }

        return options;
    }

    public bool Has(string name) => _values.ContainsKey(name);

    public string? Get(string name) => _values.TryGetValue(name, out var list) ? list[^1] : null;

    public string Require(string name)
    {
        return Get(name) ?? throw new ArgumentException($"O parâmetro --{name} é obrigatório.");
    }

    public IReadOnlyList<string> GetAll(string name) => _values.TryGetValue(name, out var list) ? list : Array.Empty<string>();
}

internal sealed class CommandResult
{
    public bool Ok { get; init; }
    public int ExitCode { get; init; }
    public string Message { get; init; } = string.Empty;
    public List<string> Lines { get; } = new();
    public Dictionary<string, JsonNode?> Data { get; } = new();

    public static CommandResult Success(string message, int exitCode = Program.ExitOk) => new() { Ok = true, ExitCode = exitCode, Message = message };

    public static CommandResult Fail(string message, int exitCode = Program.ExitError) => new() { Ok = false, ExitCode = exitCode, Message = message };

    public CommandResult WithLine(string line)
    {
        Lines.Add(line);
        return this;
    }

    public CommandResult With(string key, JsonNode? value)
    {
        Data[key] = value;
        return this;
    }
}

/// <summary>Progress reporting shared with the installer UI through a status file.</summary>
internal static class Status
{
    private static readonly JsonSerializerOptions StatusJsonOptions = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };
    private static string? _path;
    private static readonly object Sync = new();

    public static void Initialize(string? path)
    {
        _path = path;
        if (path != null)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            Write("running", "Iniciando…", 0);
        }
    }

    public static void Write(string state, string message, double? progress, Dictionary<string, JsonNode?>? data = null)
    {
        Console.Out.Flush();
        if (_path == null)
        {
            return;
        }

        var payload = new JsonObject
        {
            ["state"] = state,
            ["message"] = message,
            ["progress"] = progress,
            ["updated_at"] = DateTimeOffset.Now.ToString("O"),
        };
        if (data != null)
        {
            foreach (var (key, value) in data)
            {
                payload[key] = value?.DeepClone();
            }
        }

        lock (Sync)
        {
            try
            {
                var temporary = _path + ".tmp";
                File.WriteAllText(temporary, payload.ToJsonString(StatusJsonOptions), Encoding.UTF8);
                File.Move(temporary, _path, overwrite: true);
            }
            catch (IOException)
            {
                // The installer may be reading the file; the next update wins.
            }
        }
    }

    public static void Progress(string message, double? progress)
    {
        Console.WriteLine(progress is double value ? $"[{value * 100,3:0}%] {message}" : $"[...] {message}");
        Write("running", message, progress);
    }

    public static void Complete()
    {
        _path = null;
    }
}

internal static class Log
{
    private static readonly object Sync = new();

    public static string FilePath { get; } = Path.Combine(Program.LogsDirectory, "setup.log");

    public static void Write(string message)
    {
        try
        {
            Directory.CreateDirectory(Program.LogsDirectory);
            lock (Sync)
            {
                if (File.Exists(FilePath) && new FileInfo(FilePath).Length > 2 * 1024 * 1024)
                {
                    File.Move(FilePath, FilePath + ".1", overwrite: true);
                }

                File.AppendAllText(FilePath, $"{DateTimeOffset.Now:O} {message}{Environment.NewLine}");
            }
        }
        catch
        {
            // Logging is best-effort.
        }
    }
}

internal static class ProcessRunner
{
    public static (int ExitCode, string Output) Run(string fileName, IEnumerable<string> arguments, TimeSpan? timeout = null, string? workingDirectory = null)
    {
        var info = new ProcessStartInfo
        {
            FileName = fileName,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = workingDirectory ?? string.Empty,
        };
        foreach (var argument in arguments)
        {
            info.ArgumentList.Add(argument);
        }

        using var process = Process.Start(info) ?? throw new InvalidOperationException($"Não foi possível iniciar {fileName}.");
        var output = new StringBuilder();
        process.OutputDataReceived += (_, e) => { if (e.Data != null) { lock (output) { output.AppendLine(e.Data); } } };
        process.ErrorDataReceived += (_, e) => { if (e.Data != null) { lock (output) { output.AppendLine(e.Data); } } };
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        if (!process.WaitForExit((int)(timeout ?? TimeSpan.FromMinutes(10)).TotalMilliseconds))
        {
            try
            {
                process.Kill(true);
            }
            catch
            {
                // Ignore.
            }

            throw new TimeoutException($"{Path.GetFileName(fileName)} não terminou a tempo.");
        }

        process.WaitForExit();
        return (process.ExitCode, output.ToString());
    }
}
