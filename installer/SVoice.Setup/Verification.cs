using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SVoice.Setup;

internal static class Verification
{
    public static async Task<CommandResult> RunAsync(Options options)
    {
        var installDir = options.Get("install-dir") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "SVoice");
        var afterReboot = options.Has("after-reboot");
        var modelOptional = options.Has("model-optional");
        var problems = new List<string>();
        var result = CommandResult.Success("Instalação verificada.");

        var widgetVersion = Msix.InstalledVersion();
        var widgetRegistered = widgetVersion != null && await Msix.IsWidgetRegisteredAsync();
        result.WithLine(widgetRegistered ? $"Widget SVoice {widgetVersion}: disponível na Xbox Game Bar" : "Widget SVoice: NÃO registrado na Xbox Game Bar");
        if (!widgetRegistered)
        {
            problems.Add("widget ausente no catálogo da Game Bar");
        }

        var cable = await VbCable.DetectAsync();
        result.WithLine($"VB-CABLE: {cable.Summary}");
        if (!cable.DevicesReady)
        {
            problems.Add(cable.ServiceRegistered && cable.RebootPending ? "VB-CABLE aguardando reinicialização" : "VB-CABLE indisponível");
        }

        var launcher = Path.Combine(installDir, "service", "svoice_xtts_service.py");
        var python = Path.Combine(installDir, "runtime", "python", "python.exe");
        var basePack = Path.Combine(installDir, "runtime", "packs", "base", ".svoice-pack.json");
        var runtimeOk = File.Exists(launcher) && File.Exists(python) && File.Exists(basePack);
        var torchPacks = Directory.Exists(Path.Combine(installDir, "runtime", "packs"))
            ? Directory.GetDirectories(Path.Combine(installDir, "runtime", "packs")).Select(Path.GetFileName).Where(name => name!.StartsWith("torch-")).ToList()
            : new List<string?>();
        result.WithLine(runtimeOk ? $"Serviço XTTS: instalado (packs: {string.Join(", ", torchPacks)})" : "Serviço XTTS: arquivos ausentes");
        if (!runtimeOk || torchPacks.Count == 0)
        {
            problems.Add("runtime XTTS incompleto");
        }

        var modelDirs = new[]
        {
            Path.Combine(Program.DataDirectory, "models", "tts", "tts_models--multilingual--multi-dataset--xtts_v2"),
            Path.Combine(Program.SharedModelsDirectory, "tts", "tts_models--multilingual--multi-dataset--xtts_v2"),
        };
        var modelDir = modelDirs.FirstOrDefault(dir => File.Exists(Path.Combine(dir, "model.pth")) && new FileInfo(Path.Combine(dir, "model.pth")).Length == 1867929118L);
        result.WithLine(modelDir != null ? $"Modelo XTTS v2: presente em {modelDir}" : "Modelo XTTS v2: ausente (será baixado pelo diagnóstico do widget)");
        if (modelDir == null && !modelOptional)
        {
            problems.Add("modelo XTTS ausente");
        }

        var diagnosticsPath = Path.Combine(Program.LogsDirectory, "install-diagnostics.json");
        if (File.Exists(diagnosticsPath))
        {
            try
            {
                var report = JsonNode.Parse(File.ReadAllText(diagnosticsPath))?["report"]?.AsObject();
                if (report != null)
                {
                    var ok = report["ok"]?.GetValue<bool>() == true;
                    result.WithLine($"Último teste de síntese: {report["label"]} — {(ok ? "OK" : "falhou")} ({report["reason"]})");
                }
            }
            catch
            {
                // Ignore unreadable report.
            }
        }

        result.With("widget_version", widgetVersion)
            .With("widget_registered", widgetRegistered)
            .With("vbcable", cable.ToJson())
            .With("runtime_ok", runtimeOk)
            .With("torch_packs", new JsonArray(torchPacks.Select(item => (JsonNode?)item).ToArray()))
            .With("model_dir", modelDir)
            .With("problems", new JsonArray(problems.Select(item => (JsonNode?)item).ToArray()));

        Log.Write($"Verification ({(afterReboot ? "after reboot" : "post-install")}): {(problems.Count == 0 ? "ok" : string.Join("; ", problems))}");
        if (problems.Count == 0)
        {
            return result;
        }

        var failure = CommandResult.Fail($"Pendências: {string.Join("; ", problems)}.", cable.RebootPending && !cable.DevicesReady ? Program.ExitRebootRequired : Program.ExitBlocked).Merge(result);
        if (afterReboot)
        {
            MessageBox(IntPtr.Zero,
                $"A verificação do SVoice após a reinicialização encontrou pendências:\n\n- {string.Join("\n- ", problems)}\n\nAbra Iniciar › SVoice › Reparar SVoice para corrigir.",
                "SVoice", 0x30);
        }

        return failure;
    }

    public static async Task<CommandResult> DiagnosticsAsync(Options options)
    {
        var verification = await RunAsync(options);
        var result = new CommandResult { Ok = verification.Ok, ExitCode = verification.ExitCode, Message = verification.Message }.Merge(verification);
        var check = await Checks.RunAsync(options);
        result.Lines.AddRange(check.Lines);
        result.With("check", new JsonObject(check.Data.Select(pair => KeyValuePair.Create(pair.Key, pair.Value?.DeepClone()))));
        result.WithLine($"Logs do instalador: {Log.FilePath}");
        result.WithLine($"Logs do serviço: {Path.Combine(Program.LogsDirectory, "xtts-service.log")}");
        result.WithLine($"Logs do widget: {Path.Combine(Program.LocalAppData, "Packages", Msix.PackageFamily, "LocalState", "gamebar.log")}");
        return result;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBox(IntPtr owner, string text, string caption, uint type);
}

internal static class Data
{
    public static CommandResult Uninstall(Options options)
    {
        var all = options.Has("all");
        var removeProfiles = all || options.Has("profiles");
        var removeModels = all || options.Has("models");
        var result = CommandResult.Success("Dados do usuário tratados.");
        var data = Program.DataDirectory;

        if (removeProfiles)
        {
            foreach (var name in new[] { "voices", "backups", "temp" })
            {
                DeleteDirectory(Path.Combine(data, name), result);
            }

            foreach (var name in new[] { "profiles.json", "config.json", "service.json" })
            {
                DeleteFile(Path.Combine(data, name), result);
            }
        }
        else
        {
            DeleteDirectory(Path.Combine(data, "temp"), result);
            DeleteFile(Path.Combine(data, "service.json"), result);
            result.WithLine("Perfis de voz preservados em " + Path.Combine(data, "voices"));
        }

        if (removeModels)
        {
            DeleteDirectory(Path.Combine(data, "models"), result);
            DeleteDirectory(Program.SharedModelsDirectory, result);
            DeleteDirectory(Path.Combine(Program.LocalAppData, "SVoice", "Downloads"), result);
        }
        else
        {
            result.WithLine("Modelo XTTS preservado.");
        }

        if (all)
        {
            DeleteDirectory(Path.Combine(Program.LocalAppData, "SVoice"), result);
        }

        return result;
    }

    private static void DeleteDirectory(string path, CommandResult result)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
                result.WithLine($"Removido: {path}");
            }
        }
        catch (Exception exception)
        {
            result.WithLine($"Não foi possível remover {path}: {exception.Message}");
        }
    }

    private static void DeleteFile(string path, CommandResult result)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
                result.WithLine($"Removido: {path}");
            }
        }
        catch (Exception exception)
        {
            result.WithLine($"Não foi possível remover {path}: {exception.Message}");
        }
    }
}
