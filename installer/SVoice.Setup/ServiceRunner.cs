using System.Diagnostics;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SVoice.Setup;

/// <summary>Starts the XTTS service as the current user and drives it over HTTP.</summary>
internal sealed class ServiceRunner : IDisposable
{
    private readonly HttpClient _client = new() { Timeout = Timeout.InfiniteTimeSpan };
    private Process? _process;
    private int _port;
    private string _token = string.Empty;
    private bool _adopted;

    public static string DefaultServiceDirectory(Options options)
    {
        var configured = options.Get("service-dir")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "SVoice", "service");
        return Path.GetFullPath(configured);
    }

    public static async Task<CommandResult> EnsureModelAsync(Options options)
    {
        using var runner = new ServiceRunner();
        var start = await runner.StartAsync(options);
        if (start != null)
        {
            return start;
        }

        try
        {
            var target = options.Get("target");
            Status.Progress("Verificando o modelo XTTS v2…", 0.05);
            var body = new JsonObject { ["download"] = !options.Has("no-download") };
            if (target != null)
            {
                body["target"] = target;
            }

            var pending = runner.PostAsync("/model/ensure", body, TimeSpan.FromHours(3));
            await runner.FollowJobAsync(pending, 0.05, 0.95);
            var response = await pending;
            var model = response["model"]?.AsObject();
            var ready = model?["ready"]?.GetValue<bool>() == true;
            return ready
                ? CommandResult.Success($"Modelo XTTS v2 verificado em {model?["directory"]?.GetValue<string>()}.").With("model", model?.DeepClone())
                : CommandResult.Fail("O modelo XTTS v2 não está completo.").With("model", model?.DeepClone());
        }
        finally
        {
            await runner.ShutdownIfOwnedAsync();
        }
    }

    public static async Task<CommandResult> TestAsync(Options options)
    {
        using var runner = new ServiceRunner();
        var start = await runner.StartAsync(options);
        if (start != null)
        {
            return start;
        }

        try
        {
            if (options.Get("set-mode") is { } mode)
            {
                await runner.PostAsync("/config", new JsonObject { ["compute_mode"] = mode }, TimeSpan.FromMinutes(1));
                Log.Write($"Compute mode set to {mode}.");
            }

            var diagnostics = await runner.GetAsync("/diagnostics", TimeSpan.FromMinutes(5));
            var modelReady = diagnostics["model"]?["ready"]?.GetValue<bool>() == true;
            if (!modelReady && options.Has("skip-if-model-missing"))
            {
                return CommandResult.Success("Configuração salva. O teste de síntese será executado depois que o modelo XTTS v2 for baixado.")
                    .With("model_ready", false);
            }

            var engine = diagnostics["engine"]?.AsObject();
            var backend = options.Get("backend") ?? engine?["recommended_backend"]?.GetValue<string>() ?? "cpu";
            var result = CommandResult.Success(string.Empty);
            result.WithLine($"Backend a testar: {backend} ({engine?["recommended_reason"]?.GetValue<string>()})");
            Status.Progress($"Executando síntese de teste em {backend}…", 0.1);
            var pending = runner.PostAsync("/diagnostics/test", new JsonObject { ["backend"] = backend }, TimeSpan.FromHours(1));
            await runner.FollowJobAsync(pending, 0.1, 0.9);
            var response = await pending;
            var report = response["report"]?.AsObject() ?? new JsonObject();
            var ok = report["ok"]?.GetValue<bool>() == true;
            var reason = report["reason"]?.GetValue<string>();
            var synthesis = report["test_synthesis_seconds"]?.GetValue<double?>();
            foreach (var check in report["checks"]?.AsArray() ?? new JsonArray())
            {
                result.WithLine($"  {(check!["ok"]!.GetValue<bool>() ? "ok " : "FALHA")} {check["name"]} ({check["seconds"]} s)");
            }

            result.With("backend", backend).With("report", report.DeepClone());
            var summaryPath = Path.Combine(Program.LogsDirectory, "install-diagnostics.json");
            Directory.CreateDirectory(Program.LogsDirectory);
            File.WriteAllText(summaryPath, new JsonObject
            {
                ["tested_at"] = DateTimeOffset.Now.ToString("O"),
                ["backend"] = backend,
                ["report"] = report.DeepClone(),
                ["diagnostics"] = diagnostics.DeepClone(),
            }.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));

            if (ok)
            {
                return new CommandResult { Ok = true, ExitCode = Program.ExitOk, Message = $"{report["label"]} validado: síntese de teste em {synthesis:0.0} s.", Lines = { }, Data = { } }
                    .Merge(result);
            }

            if (backend != "cpu")
            {
                result.WithLine($"{report["label"]} não passou ({reason}); o SVoice usará CPU automaticamente.");
                Status.Progress("Validando a CPU…", 0.9);
                var cpuPending = runner.PostAsync("/diagnostics/test", new JsonObject { ["backend"] = "cpu" }, TimeSpan.FromHours(1));
                await runner.FollowJobAsync(cpuPending, 0.9, 0.99);
                var cpuReport = (await cpuPending)["report"]?.AsObject();
                result.With("cpu_report", cpuReport?.DeepClone());
                if (cpuReport?["ok"]?.GetValue<bool>() == true)
                {
                    return new CommandResult { Ok = true, ExitCode = Program.ExitOk, Message = $"{report["label"]} indisponível ({reason}); CPU validada em {cpuReport["test_synthesis_seconds"]:0.0} s." }.Merge(result);
                }
            }

            return new CommandResult { Ok = false, ExitCode = Program.ExitError, Message = $"A síntese de teste falhou: {reason}" }.Merge(result);
        }
        finally
        {
            await runner.ShutdownIfOwnedAsync();
        }
    }

    public static async Task<CommandResult> StopAsync(Options options)
    {
        var discovery = ReadDiscovery();
        if (discovery == null)
        {
            return CommandResult.Success("O serviço XTTS não está em execução.");
        }

        using var runner = new ServiceRunner { _port = discovery.Value.Port, _token = discovery.Value.Token, _adopted = true };
        try
        {
            await runner.PostAsync("/shutdown", new JsonObject(), TimeSpan.FromSeconds(10));
        }
        catch
        {
            // Already gone.
        }

        try
        {
            using var process = Process.GetProcessById(discovery.Value.Pid);
            if (!process.WaitForExit(15000))
            {
                process.Kill(true);
            }
        }
        catch
        {
            // Process already exited.
        }

        return CommandResult.Success("Serviço XTTS encerrado.");
    }

    // ------------------------------------------------------------- lifecycle

    private static string DiscoveryPath => Path.Combine(Program.DataDirectory, "service.json");

    private static (int Pid, int Port, string Token)? ReadDiscovery()
    {
        try
        {
            if (!File.Exists(DiscoveryPath))
            {
                return null;
            }

            using var document = JsonDocument.Parse(File.ReadAllText(DiscoveryPath));
            var root = document.RootElement;
            return (root.GetProperty("pid").GetInt32(), root.GetProperty("port").GetInt32(), root.GetProperty("token").GetString() ?? string.Empty);
        }
        catch
        {
            return null;
        }
    }

    private async Task<CommandResult?> StartAsync(Options options)
    {
        var serviceDir = DefaultServiceDirectory(options);
        var launcher = Path.Combine(serviceDir, "svoice_xtts_service.py");
        var runtimeDir = options.Get("runtime-dir") ?? Path.GetFullPath(Path.Combine(serviceDir, "..", "runtime"));
        var python = options.Get("python") ?? Path.Combine(runtimeDir, "python", "python.exe");
        if (!File.Exists(launcher))
        {
            return CommandResult.Fail($"O serviço XTTS não foi encontrado em {serviceDir}.");
        }

        if (!File.Exists(python))
        {
            return CommandResult.Fail($"O runtime Python do SVoice não foi encontrado em {python}.");
        }

        var existing = ReadDiscovery();
        if (existing != null)
        {
            _port = existing.Value.Port;
            _token = existing.Value.Token;
            JsonObject? health = null;
            try
            {
                health = await GetAsync("/health", TimeSpan.FromSeconds(5));
            }
            catch
            {
                // The discovery file can outlive a crashed process.
            }

            if (health?["protocol_version"]?.GetValue<int>() == 2 && RuntimeMatches(health, runtimeDir, options))
            {
                _adopted = true;
                Log.Write($"Adopted running service pid {existing.Value.Pid}.");
                return null;
            }

            if (health != null)
            {
                Log.Write($"Restarting incompatible service pid {existing.Value.Pid} before setup validation.");
                try
                {
                    await PostAsync("/shutdown", new JsonObject(), TimeSpan.FromSeconds(10));
                }
                catch
                {
                    // Fall through to the process-level stop below.
                }

                try
                {
                    using var stale = Process.GetProcessById(existing.Value.Pid);
                    if (!stale.WaitForExit(15000))
                    {
                        stale.Kill(true);
                        stale.WaitForExit(5000);
                    }
                }
                catch
                {
                    // The old process already exited.
                }
            }

            _port = 0;
            _token = string.Empty;
        }

        var info = new ProcessStartInfo
        {
            FileName = python,
            WorkingDirectory = serviceDir,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var argument in new[] { launcher, "--print-discovery", "--idle-timeout", "0", "--runtime-dir", runtimeDir })
        {
            info.ArgumentList.Add(argument);
        }

        if (options.Get("torch-pack") is { } pack)
        {
            info.ArgumentList.Add("--torch-pack");
            info.ArgumentList.Add(pack);
        }

        info.Environment["PYTHONIOENCODING"] = "utf-8";
        info.Environment["PYTHONUTF8"] = "1";
        Log.Write($"Starting service: {python} {string.Join(' ', info.ArgumentList)}");
        _process = Process.Start(info) ?? throw new InvalidOperationException("Não foi possível iniciar o serviço XTTS.");
        var stderr = new StringBuilder();
        _ = Task.Run(async () =>
        {
            while (await _process.StandardError.ReadLineAsync() is { } line)
            {
                lock (stderr)
                {
                    stderr.AppendLine(line);
                    if (stderr.Length > 8000)
                    {
                        stderr.Remove(0, stderr.Length - 8000);
                    }
                }
            }
        });

        var deadline = DateTime.UtcNow.AddSeconds(180);
        while (DateTime.UtcNow < deadline)
        {
            var line = await _process.StandardOutput.ReadLineAsync();
            if (line == null)
            {
                break;
            }

            if (line.StartsWith('{'))
            {
                try
                {
                    using var document = JsonDocument.Parse(line);
                    if (document.RootElement.TryGetProperty("port", out var port))
                    {
                        _port = port.GetInt32();
                        _token = document.RootElement.GetProperty("token").GetString() ?? string.Empty;
                        // "already_running": another instance owns the mutex; adopt it and never stop it.
                        _adopted = document.RootElement.TryGetProperty("already_running", out var running) && running.ValueKind == JsonValueKind.True;
                        break;
                    }
                }
                catch (JsonException)
                {
                    // Not the discovery line.
                }
            }
        }

        if (_port == 0)
        {
            string detail;
            lock (stderr)
            {
                detail = stderr.ToString();
            }

            return CommandResult.Fail($"O serviço XTTS não iniciou. {detail.Trim()}");
        }

        _ = Task.Run(async () => { while (await _process.StandardOutput.ReadLineAsync() != null) { } });
        deadline = DateTime.UtcNow.AddSeconds(90);
        while (DateTime.UtcNow < deadline)
        {
            if (await IsHealthyAsync())
            {
                return null;
            }

            await Task.Delay(500);
        }

        return CommandResult.Fail("O serviço XTTS iniciou, mas não respondeu a tempo.");
    }

    private static bool RuntimeMatches(JsonObject health, string requestedRuntimeDir, Options options)
    {
        // Commands launched by the installer must not adopt a service that was
        // started while runtime packs were still being extracted.
        if (options.Get("runtime-dir") == null && options.Get("torch-pack") == null)
        {
            return true;
        }

        var runtime = health["runtime"]?.AsObject();
        if (runtime == null)
        {
            return false;
        }

        var requested = Path.GetFullPath(requestedRuntimeDir).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var activeRaw = runtime["runtime_dir"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(activeRaw))
        {
            return false;
        }

        var active = Path.GetFullPath(activeRaw).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (!string.Equals(requested, active, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var packagedRuntimeExpected = File.Exists(Path.Combine(requested, "packs", "base", ".svoice-pack.json"));
        if (packagedRuntimeExpected && runtime["packaged"]?.GetValue<bool>() != true)
        {
            return false;
        }

        if (options.Get("torch-pack") is { } requestedPack)
        {
            return string.Equals(runtime["torch_pack"]?.GetValue<string>(), requestedPack, StringComparison.OrdinalIgnoreCase);
        }

        return !packagedRuntimeExpected || !string.IsNullOrWhiteSpace(runtime["torch_pack"]?.GetValue<string>());
    }

    private async Task ShutdownIfOwnedAsync()
    {
        if (_adopted || _process == null)
        {
            return;
        }

        try
        {
            await PostAsync("/shutdown", new JsonObject(), TimeSpan.FromSeconds(10));
        }
        catch
        {
            // Ignore.
        }

        if (!_process.WaitForExit(20000))
        {
            try
            {
                _process.Kill(true);
            }
            catch
            {
                // Ignore.
            }
        }
    }

    // ------------------------------------------------------------------ http

    private async Task<bool> IsHealthyAsync()
    {
        try
        {
            var health = await GetAsync("/health", TimeSpan.FromSeconds(5));
            return health["protocol_version"]?.GetValue<int>() == 2;
        }
        catch
        {
            return false;
        }
    }

    private HttpRequestMessage CreateRequest(HttpMethod method, string path)
    {
        var request = new HttpRequestMessage(method, $"http://127.0.0.1:{_port}{path}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token);
        return request;
    }

    public async Task<JsonObject> GetAsync(string path, TimeSpan timeout)
    {
        using var request = CreateRequest(HttpMethod.Get, path);
        using var cancellation = new CancellationTokenSource(timeout);
        using var response = await _client.SendAsync(request, cancellation.Token);
        return await ParseAsync(response, cancellation.Token);
    }

    public async Task<JsonObject> PostAsync(string path, JsonObject body, TimeSpan timeout)
    {
        using var request = CreateRequest(HttpMethod.Post, path);
        request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        using var cancellation = new CancellationTokenSource(timeout);
        using var response = await _client.SendAsync(request, cancellation.Token);
        return await ParseAsync(response, cancellation.Token);
    }

    private static async Task<JsonObject> ParseAsync(HttpResponseMessage response, CancellationToken token)
    {
        var text = await response.Content.ReadAsStringAsync(token);
        var parsed = JsonNode.Parse(text) as JsonObject ?? new JsonObject();
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(parsed["error"]?.GetValue<string>() ?? $"HTTP {(int)response.StatusCode}");
        }

        return parsed;
    }

    /// <summary>Polls /health while a long request is pending to surface progress.</summary>
    public async Task FollowJobAsync(Task pending, double from, double to)
    {
        while (!pending.IsCompleted)
        {
            await Task.WhenAny(pending, Task.Delay(1000));
            if (pending.IsCompleted)
            {
                break;
            }

            try
            {
                var health = await GetAsync("/health", TimeSpan.FromSeconds(5));
                var job = health["job"]?.AsObject();
                if (job != null && health["busy"]?.GetValue<bool>() == true)
                {
                    var progress = job["progress"]?.GetValue<double?>();
                    Status.Progress(job["message"]?.GetValue<string>() ?? "Processando…", progress is double value ? from + (to - from) * value : null);
                }
            }
            catch
            {
                // Keep waiting on the main request.
            }
        }
    }

    public void Dispose()
    {
        _process?.Dispose();
        _client.Dispose();
    }
}

internal static class CommandResultExtensions
{
    public static CommandResult Merge(this CommandResult target, CommandResult source)
    {
        target.Lines.AddRange(source.Lines);
        foreach (var (key, value) in source.Data)
        {
            target.Data[key] = value;
        }

        return target;
    }
}
