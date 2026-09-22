using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using Windows.Management.Deployment;

namespace SVoice.Setup;

internal sealed record GpuInfo(string Name, string Vendor, string? DriverVersion, long? MemoryBytes, string? NvidiaDriver);

internal static class Checks
{
    private const int MinimumBuild = 19041;
    // XboxGameBarWidgetActivity was introduced with Game Bar 6.124.0122.0.
    // SVoice relies on it to finish XTTS jobs after the overlay is dismissed.
    private static readonly Version MinimumGameBarVersion = new(6, 124, 122, 0);
    private const string GameBarFamily = "Microsoft.XboxGamingOverlay_8wekyb3d8bbwe";
    public const string GameBarStoreUri = "ms-windows-store://pdp/?productid=9NZKPSTSNW4P";
    private const long MinimumFreeBytes = 12L * 1024 * 1024 * 1024;
    private const int MaxInstallPathLength = 120;

    public static async Task<CommandResult> RunAsync(Options options)
    {
        var installDir = options.Get("install-dir") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "SVoice");
        var result = new CommandResult { Ok = true, ExitCode = Program.ExitOk, Message = "Pré-requisitos atendidos." };
        var blocking = new List<string>();
        var warnings = new List<string>();

        var build = Environment.OSVersion.Version.Build;
        result.WithLine($"Windows: {Environment.OSVersion.VersionString} (build {build})");
        if (build < MinimumBuild)
        {
            blocking.Add($"O SVoice exige o Windows 10 versão 2004 (build {MinimumBuild}) ou superior.");
        }

        var architecture = RuntimeInformation.OSArchitecture;
        result.WithLine($"Arquitetura: {architecture}");
        if (architecture != Architecture.X64)
        {
            blocking.Add("O SVoice está disponível apenas para Windows x64.");
        }

        var gameBar = GameBarVersion();
        result.WithLine(gameBar != null ? $"Xbox Game Bar: {gameBar}" : "Xbox Game Bar: não encontrada");
        if (gameBar == null)
        {
            blocking.Add("A Xbox Game Bar não está instalada. Instale-a pela Microsoft Store antes de continuar.");
        }
        else if (!Version.TryParse(gameBar, out var parsedGameBarVersion))
        {
            blocking.Add("Não foi possível validar a versão da Xbox Game Bar. Atualize-a pela Microsoft Store e tente novamente.");
        }
        else if (parsedGameBarVersion < MinimumGameBarVersion)
        {
            blocking.Add($"A Xbox Game Bar {gameBar} é antiga demais. Atualize-a pela Microsoft Store para a versão {MinimumGameBarVersion} ou superior.");
        }

        var gpus = DetectGpus();
        var recommended = RecommendPack(gpus);
        foreach (var gpu in gpus)
        {
            result.WithLine($"GPU: {gpu.Name} [{gpu.Vendor}] driver {gpu.NvidiaDriver ?? gpu.DriverVersion ?? "?"}");
        }

        result.WithLine($"Runtime recomendado: {recommended.Pack} — {recommended.Reason}");

        var free = FreeBytes(installDir);
        result.WithLine($"Espaço livre em {Path.GetPathRoot(installDir)}: {free / (1024 * 1024 * 1024.0):0.0} GB");
        if (free < MinimumFreeBytes)
        {
            blocking.Add("São necessários pelo menos 12 GB livres para o runtime XTTS e o modelo.");
        }

        if (installDir.Length > MaxInstallPathLength)
        {
            blocking.Add($"O caminho de instalação é longo demais ({installDir.Length} caracteres). Use um caminho com até {MaxInstallPathLength}.");
        }

        if (VbCable.IsRebootPending())
        {
            warnings.Add("Há uma reinicialização pendente do Windows; a instalação do VB-CABLE pode exigir reiniciar novamente.");
        }

        foreach (var warning in warnings)
        {
            result.WithLine($"AVISO: {warning}");
        }

        result.With("windows_build", build)
            .With("game_bar_version", gameBar)
            .With("gpus", new JsonArray(gpus.Select(ToJson).ToArray()))
            .With("recommended_pack", recommended.Pack)
            .With("recommended_reason", recommended.Reason)
            .With("free_bytes", free)
            .With("warnings", new JsonArray(warnings.Select(w => (JsonNode?)w).ToArray()))
            .With("blocking", new JsonArray(blocking.Select(b => (JsonNode?)b).ToArray()));

        if (blocking.Count > 0)
        {
            var failure = CommandResult.Fail(string.Join(" ", blocking), Program.ExitBlocked);
            failure.Lines.AddRange(result.Lines);
            foreach (var (key, value) in result.Data)
            {
                failure.Data[key] = value;
            }

            return failure;
        }

        await Task.CompletedTask;
        return result;
    }

    public static CommandResult DetectGpu(Options options)
    {
        var gpus = DetectGpus();
        var recommended = RecommendPack(gpus);
        var result = CommandResult.Success($"{recommended.Pack}: {recommended.Reason}");
        foreach (var gpu in gpus)
        {
            result.WithLine($"GPU: {gpu.Name} [{gpu.Vendor}]");
        }

        return result.With("gpus", new JsonArray(gpus.Select(ToJson).ToArray()))
            .With("recommended_pack", recommended.Pack)
            .With("vendor", PrimaryVendor(gpus));
    }

    private static JsonNode ToJson(GpuInfo gpu) => new JsonObject
    {
        ["name"] = gpu.Name,
        ["vendor"] = gpu.Vendor,
        ["driver_version"] = gpu.DriverVersion,
        ["nvidia_driver"] = gpu.NvidiaDriver,
        ["memory_bytes"] = gpu.MemoryBytes,
    };

    public static string? GameBarVersion()
    {
        try
        {
            var manager = new PackageManager();
            var package = manager.FindPackagesForUser(string.Empty, GameBarFamily).FirstOrDefault();
            if (package == null)
            {
                return null;
            }

            var version = package.Id.Version;
            return $"{version.Major}.{version.Minor}.{version.Build}.{version.Revision}";
        }
        catch (Exception exception)
        {
            Log.Write($"Game Bar lookup failed: {exception.Message}");
            return null;
        }
    }

    public static long FreeBytes(string path)
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(path));
            return root == null ? 0 : new DriveInfo(root).AvailableFreeSpace;
        }
        catch
        {
            return 0;
        }
    }

    // --------------------------------------------------------------------- gpu

    private const string DisplayClassKey = @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}";

    public static List<GpuInfo> DetectGpus()
    {
        var gpus = new List<GpuInfo>();
        using var root = Registry.LocalMachine.OpenSubKey(DisplayClassKey);
        if (root == null)
        {
            return gpus;
        }

        string? nvidiaDriver = null;
        foreach (var subKeyName in root.GetSubKeyNames().Where(name => Regex.IsMatch(name, @"^\d{4}$")))
        {
            using var key = root.OpenSubKey(subKeyName);
            var description = key?.GetValue("DriverDesc") as string;
            if (key == null || string.IsNullOrWhiteSpace(description))
            {
                continue;
            }

            var matching = (key.GetValue("MatchingDeviceId") as string ?? string.Empty).ToLowerInvariant();
            var vendor = Regex.Match(matching, @"ven_([0-9a-f]{4})").Groups[1].Value switch
            {
                "10de" => "nvidia",
                "1002" or "1022" => "amd",
                "8086" => "intel",
                _ => "unknown",
            };
            if (vendor == "unknown")
            {
                var provider = (key.GetValue("ProviderName") as string ?? string.Empty).ToLowerInvariant();
                vendor = provider.Contains("nvidia") ? "nvidia" : provider.Contains("advanced micro") || provider.Contains("amd") ? "amd" : provider.Contains("intel") ? "intel" : "unknown";
            }

            long? memory = key.GetValue("HardwareInformation.qwMemorySize") switch
            {
                long value when value > 0 => value,
                byte[] bytes when bytes.Length >= 8 => BitConverter.ToInt64(bytes, 0),
                _ => null,
            };
            if (vendor == "nvidia")
            {
                nvidiaDriver ??= NvidiaSmiDriver();
            }

            gpus.Add(new GpuInfo(description, vendor, key.GetValue("DriverVersion") as string, memory, vendor == "nvidia" ? nvidiaDriver : null));
        }

        return gpus;
    }

    private static string? NvidiaSmiDriver()
    {
        var candidate = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "nvidia-smi.exe");
        if (!File.Exists(candidate))
        {
            return null;
        }

        try
        {
            var (code, output) = ProcessRunner.Run(candidate, new[] { "--query-gpu=driver_version", "--format=csv,noheader" }, TimeSpan.FromSeconds(15));
            return code == 0 ? output.Trim().Split('\n')[0].Trim() : null;
        }
        catch
        {
            return null;
        }
    }

    public static string PrimaryVendor(IEnumerable<GpuInfo> gpus)
    {
        var list = gpus.ToList();
        foreach (var vendor in new[] { "nvidia", "amd", "intel" })
        {
            if (list.Any(gpu => gpu.Vendor == vendor))
            {
                return vendor;
            }
        }

        return "none";
    }

    public static (string Pack, string Reason) RecommendPack(IEnumerable<GpuInfo> gpus)
    {
        var list = gpus.ToList();
        var vendor = PrimaryVendor(list);
        switch (vendor)
        {
            case "nvidia":
            {
                var gpu = list.First(item => item.Vendor == "nvidia");
                var driver = ParseVersion(gpu.NvidiaDriver ?? string.Empty);
                if (driver.Length > 0 && driver[0] < 580)
                {
                    return ("torch-cpu", $"driver NVIDIA {gpu.NvidiaDriver} é anterior ao 580 exigido pelo CUDA 13; atualize o driver e use Reparar para ativar a GPU");
                }

                return ("torch-cuda", $"GPU {gpu.Name} detectada");
            }

            case "amd":
                return ("torch-directml", $"GPU {list.First(item => item.Vendor == "amd").Name} detectada; DirectML será validado com uma síntese completa");
            case "intel":
                return ("torch-directml", $"GPU {list.First(item => item.Vendor == "intel").Name} detectada; DirectML será validado com uma síntese completa");
            default:
                return ("torch-cpu", "nenhuma GPU dedicada detectada");
        }
    }

    private static int[] ParseVersion(string value)
    {
        return Regex.Matches(value, @"\d+").Select(match => int.Parse(match.Value)).ToArray();
    }
}
