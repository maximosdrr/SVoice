using System.ComponentModel;
using System.Text.Json.Nodes;

namespace SVoice.Setup;

/// <summary>
/// "Reparar SVoice": re-registers the widget, checks the VB-CABLE driver,
/// verifies the model and validates the inference backend. Runtime files are
/// repaired by re-running the installer (the archives are not kept on disk).
/// </summary>
internal static class Repair
{
    public static async Task<CommandResult> RunAsync(Options options)
    {
        var installDir = options.Get("install-dir") ?? Path.GetDirectoryName(Environment.ProcessPath!)!;
        var result = CommandResult.Success("Reparo concluído.");
        var problems = new List<string>();
        var steps = new JsonObject();
        result.WithLine($"Instalação: {installDir}");

        // 1. Widget
        Status.Progress("Verificando o widget da Xbox Game Bar…", 0.05);
        var widgetDir = Path.Combine(installDir, "widget");
        var msix = Directory.Exists(widgetDir)
            ? Directory.GetFiles(widgetDir, "*.msix").OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault()
            : null;
        var cer = Path.Combine(widgetDir, "SVoice.GameBar.cer");
        if (!await Msix.IsWidgetRegisteredAsync() && msix != null)
        {
            if (File.Exists(cer))
            {
                var code = Program.IsElevated()
                    ? Msix.InstallCertificate(Options.Parse(new[] { "install-cert", "--cer", cer })).ExitCode
                    : RunElevatedSafe(new[] { "install-cert", "--cer", cer });
                steps["certificate"] = code;
            }

            var installed = await Msix.InstallPackageAsync(Options.Parse(File.Exists(cer)
                ? new[] { "install-msix", "--msix", msix, "--cer", cer }
                : new[] { "install-msix", "--msix", msix }));
            result.Lines.AddRange(installed.Lines);
            result.WithLine(installed.Message);
            steps["widget"] = installed.Ok;
            if (!installed.Ok)
            {
                problems.Add("widget não registrado");
            }
        }
        else
        {
            result.WithLine(msix == null && !await Msix.IsWidgetRegisteredAsync()
                ? "Widget: pacote MSIX não encontrado na pasta widget; execute o instalador novamente."
                : $"Widget: registrado ({Msix.InstalledVersion()})");
        }

        // 2. VB-CABLE
        Status.Progress("Verificando o VB-CABLE…", 0.2);
        var cable = await VbCable.DetectAsync();
        if (!cable.DevicesReady)
        {
            var source = Path.Combine(installDir, "vendor", "VBCABLE");
            if (Directory.Exists(source))
            {
                var code = Program.IsElevated()
                    ? (await VbCable.RunAsync(Options.Parse(new[] { "vbcable", "--install", "--source", source }))).ExitCode
                    : RunElevatedSafe(new[] { "vbcable", "--install", "--source", source });
                steps["vbcable"] = code;
                cable = await VbCable.DetectAsync();
                if (code == Program.ExitRebootRequired)
                {
                    problems.Add("reinicie o Windows para concluir o VB-CABLE");
                }
            }
        }

        result.WithLine($"VB-CABLE: {cable.Summary}");
        if (!cable.DevicesReady && !problems.Any(problem => problem.Contains("VB-CABLE")))
        {
            problems.Add("VB-CABLE indisponível");
        }

        // 3. Model
        Status.Progress("Verificando o modelo XTTS v2…", 0.3);
        var serviceDir = Path.Combine(installDir, "service");
        var runtimeDir = Path.Combine(installDir, "runtime");
        var model = await ServiceRunner.EnsureModelAsync(Options.Parse(new[] { "ensure-model", "--service-dir", serviceDir, "--runtime-dir", runtimeDir, "--target", "auto" }));
        result.WithLine(model.Message);
        steps["model"] = model.Ok;
        if (!model.Ok)
        {
            problems.Add("modelo XTTS");
        }

        // 4. Backend validation
        Status.Progress("Validando o backend de inferência…", 0.6);
        var test = await ServiceRunner.TestAsync(Options.Parse(new[] { "test-service", "--service-dir", serviceDir, "--runtime-dir", runtimeDir }));
        result.Lines.AddRange(test.Lines);
        result.WithLine(test.Message);
        steps["backend"] = test.Ok;
        if (!test.Ok)
        {
            problems.Add("síntese de teste");
        }

        // 5. Final verification
        Status.Progress("Verificação final…", 0.95);
        var verification = await Verification.RunAsync(Options.Parse(new[] { "verify", "--install-dir", installDir }));
        result.Lines.AddRange(verification.Lines);
        result.With("steps", steps)
            .With("verification", new JsonObject(verification.Data.Select(pair => KeyValuePair.Create(pair.Key, pair.Value?.DeepClone()))));

        if (problems.Count == 0)
        {
            return result;
        }

        return CommandResult.Fail($"Reparo concluído com pendências: {string.Join("; ", problems)}.", Program.ExitBlocked).Merge(result);
    }

    private static int RunElevatedSafe(IEnumerable<string> arguments)
    {
        try
        {
            return Program.RunElevated(arguments);
        }
        catch (Win32Exception)
        {
            // UAC prompt declined.
            return Program.ExitError;
        }
    }
}
