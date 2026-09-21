using System.Diagnostics;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SVoice.GameBarBridge;

/// <summary>
/// Talks to the local XTTS service, starting or adopting it as needed.
/// </summary>
/// <remarks>
/// The service is a per-user process discovered through
/// <c>%LOCALAPPDATA%\SVoice\XTTS\service.json</c>. The bridge never kills it on
/// exit: the service shuts itself down after a period of inactivity, so
/// reopening the widget does not reload the model.
/// </remarks>
internal sealed class XttsServiceHost : IDisposable
{
    public const int ProtocolVersion = 2;
    private const int IdleTimeoutSeconds = 15 * 60;
    private static readonly TimeSpan LongOperation = TimeSpan.FromMinutes(45);

    private readonly HttpClient _client = new() { Timeout = Timeout.InfiniteTimeSpan };
    private readonly SemaphoreSlim _startupLock = new(1, 1);
    private Process? _process;
    private Endpoint? _endpoint;

    private sealed record Endpoint(int Port, string Token, int Pid);

    public static string DataDirectory { get; } = Path.Combine(KnownFolders.UnredirectedLocalAppData, "SVoice", "XTTS");

    private static string DiscoveryPath => Path.Combine(DataDirectory, "service.json");

    // ------------------------------------------------------------------ commands

    public async Task<JsonObject> PingAsync()
    {
        await EnsureStartedAsync();
        return await GetAsync("/health");
    }

    public async Task<JsonObject> GetProfilesAsync()
    {
        await EnsureStartedAsync();
        return await GetAsync("/profiles");
    }

    public async Task<JsonObject> CreateProfileAsync(JsonElement request)
    {
        await EnsureStartedAsync();
        var name = request.TryGetProperty("name", out var nameValue) ? nameValue.GetString()?.Trim() : null;
        var referencePaths = RequiredStringArray(request, "reference_paths");
        foreach (var path in referencePaths)
        {
            if (!Path.IsPathFullyQualified(path) || !File.Exists(path))
            {
                throw new InvalidOperationException("Um dos áudios selecionados não foi encontrado.");
            }
        }

        return await SendAsync(HttpMethod.Post, "/profiles", new { name, reference_paths = referencePaths }, LongOperation);
    }

    public async Task<JsonObject> RenameProfileAsync(JsonElement request)
    {
        await EnsureStartedAsync();
        var profileId = RequiredString(request, "profile_id");
        var name = RequiredString(request, "name");
        return await SendAsync(HttpMethod.Patch, $"/profiles/{Uri.EscapeDataString(profileId)}", new { name });
    }

    public async Task<JsonObject> DeleteProfileAsync(JsonElement request)
    {
        await EnsureStartedAsync();
        var profileId = RequiredString(request, "profile_id");
        return await SendAsync(HttpMethod.Delete, $"/profiles/{Uri.EscapeDataString(profileId)}");
    }

    public async Task<JsonObject> SynthesizeAsync(JsonElement request)
    {
        await EnsureStartedAsync();
        var text = RequiredString(request, "text");
        var profileId = RequiredString(request, "profile_id");
        var speed = request.TryGetProperty("speed", out var speedValue) && speedValue.TryGetDouble(out var parsedSpeed)
            ? Math.Clamp(parsedSpeed, 0.5, 2.0)
            : 1.0;
        var computeMode = request.TryGetProperty("compute_mode", out var computeValue) ? computeValue.GetString() : null;

        var synthesis = await SendAsync(
            HttpMethod.Post,
            "/synthesize",
            new { text, profile_id = profileId, speed, compute_mode = computeMode },
            LongOperation);

        var outputPath = synthesis["output_path"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(outputPath))
        {
            throw new InvalidOperationException("O XTTS não retornou o áudio gerado.");
        }

        var fullOutputPath = Path.GetFullPath(outputPath);
        var tempRoot = Path.GetFullPath(Path.Combine(DataDirectory, "temp")) + Path.DirectorySeparatorChar;
        if (!fullOutputPath.StartsWith(tempRoot, StringComparison.OrdinalIgnoreCase) ||
            !fullOutputPath.EndsWith(".wav", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("O XTTS retornou um caminho de áudio inválido.");
        }

        try
        {
            var audio = await File.ReadAllBytesAsync(fullOutputPath);
            var transferDirectory = Path.Combine(
                KnownFolders.UnredirectedLocalAppData,
                "Packages",
                BridgePipeServer.GameBarPackageFamily,
                "TempState");
            Directory.CreateDirectory(transferDirectory);
            var transferName = $"xtts_{Guid.NewGuid():N}.wav";
            await File.WriteAllBytesAsync(Path.Combine(transferDirectory, transferName), audio);
            synthesis.Remove("output_path");
            synthesis["audio_file"] = transferName;
            synthesis["content_type"] = "audio/wav";
            synthesis["audio_bytes"] = audio.Length;
            return synthesis;
        }
        finally
        {
            try
            {
                File.Delete(fullOutputPath);
            }
            catch
            {
                // Temporary audio is cleaned again on the next service start.
            }
        }
    }

    public async Task<JsonObject> CancelAsync()
    {
        if (_endpoint == null)
        {
            return new JsonObject { ["cancelled"] = false };
        }

        return await SendAsync(HttpMethod.Post, "/jobs/cancel", timeout: TimeSpan.FromSeconds(10));
    }

    public async Task<JsonObject> DiagnosticsAsync()
    {
        await EnsureStartedAsync();
        var diagnostics = await SendAsync(HttpMethod.Get, "/diagnostics", timeout: TimeSpan.FromMinutes(3));
        diagnostics["bridge"] = new JsonObject
        {
            ["version"] = typeof(XttsServiceHost).Assembly.GetName().Version?.ToString(),
            ["service_origin"] = _location?.Origin,
            ["python"] = _location?.Python,
            ["launcher"] = _location?.Launcher,
            ["log_file"] = BridgeLog.FilePath,
            ["service_pid"] = _endpoint?.Pid,
        };
        return diagnostics;
    }

    public async Task<JsonObject> TestBackendAsync(JsonElement request)
    {
        await EnsureStartedAsync();
        var backend = RequiredString(request, "backend");
        return await SendAsync(HttpMethod.Post, "/diagnostics/test", new { backend }, LongOperation);
    }

    public async Task<JsonObject> SetConfigAsync(JsonElement request)
    {
        await EnsureStartedAsync();
        var body = new JsonObject();
        if (request.TryGetProperty("compute_mode", out var mode) && mode.ValueKind == JsonValueKind.String)
        {
            body["compute_mode"] = mode.GetString();
        }

        if (request.TryGetProperty("reset_validation", out var reset) && reset.ValueKind == JsonValueKind.True)
        {
            body["reset_validation"] = true;
        }

        var result = await SendAsync(HttpMethod.Post, "/config", body);
        if (body.ContainsKey("compute_mode"))
        {
            // The torch runtime pack is chosen at service start; restart so the
            // new mode can pick the matching pack.
            await RestartServiceAsync();
        }

        return result;
    }

    public async Task<JsonObject> EnsureModelAsync()
    {
        await EnsureStartedAsync();
        return await SendAsync(HttpMethod.Post, "/model/ensure", new { }, LongOperation);
    }

    public async Task<JsonObject> RestartServiceAsync()
    {
        await StopServiceAsync();
        await EnsureStartedAsync();
        return await GetAsync("/health");
    }

    // ------------------------------------------------------------------ lifecycle

    private ServiceLocation? _location;

    private async Task EnsureStartedAsync()
    {
        if (_endpoint != null && await IsHealthyAsync(_endpoint))
        {
            return;
        }

        await _startupLock.WaitAsync();
        try
        {
            if (_endpoint != null && await IsHealthyAsync(_endpoint))
            {
                return;
            }

            _endpoint = null;
            var existing = ReadDiscovery();
            if (existing != null && ProcessAlive(existing.Pid) && await IsHealthyAsync(existing))
            {
                _endpoint = existing;
                BridgeLog.Write($"Adopted running XTTS service pid {existing.Pid} on port {existing.Port}.");
                return;
            }

            await LaunchAsync();
        }
        finally
        {
            _startupLock.Release();
        }
    }

    private async Task LaunchAsync()
    {
        _location = ServiceLocator.Locate();
        Directory.CreateDirectory(DataDirectory);
        _process?.Dispose();
        var startInfo = ServiceLocator.CreateStartInfo(_location, DataDirectory, IdleTimeoutSeconds);
        BridgeLog.Write($"Starting XTTS service ({_location.Origin}): {_location.Python} {_location.Launcher}");
        var process = Process.Start(startInfo) ?? throw new InvalidOperationException("O processo XTTS não pôde ser iniciado.");
        _process = process;
        var stderrTail = new Queue<string>();
        _ = Task.Run(async () =>
        {
            while (await process.StandardError.ReadLineAsync() is { } line)
            {
                lock (stderrTail)
                {
                    stderrTail.Enqueue(line);
                    while (stderrTail.Count > 40)
                    {
                        stderrTail.Dequeue();
                    }
                }
            }
        });

        var deadline = DateTimeOffset.UtcNow.AddSeconds(120);
        Endpoint? endpoint = null;
        var stdoutReader = Task.Run(async () =>
        {
            while (await process.StandardOutput.ReadLineAsync() is { } line)
            {
                if (endpoint == null && line.StartsWith('{'))
                {
                    try
                    {
                        using var document = JsonDocument.Parse(line);
                        var root = document.RootElement;
                        if (root.TryGetProperty("port", out var port) && root.TryGetProperty("token", out var token))
                        {
                            endpoint = new Endpoint(port.GetInt32(), token.GetString()!, root.GetProperty("pid").GetInt32());
                        }
                    }
                    catch (JsonException)
                    {
                        // Not the discovery line.
                    }
                }
            }
        });

        while (DateTimeOffset.UtcNow < deadline)
        {
            if (endpoint != null)
            {
                break;
            }

            if (process.HasExited)
            {
                if (process.ExitCode == 3)
                {
                    // Another instance (for example one started by the installer)
                    // holds the mutex; wait for it to publish its discovery file.
                    var adoptDeadline = DateTimeOffset.UtcNow.AddSeconds(60);
                    while (DateTimeOffset.UtcNow < adoptDeadline)
                    {
                        var existing = ReadDiscovery();
                        if (existing != null && await IsHealthyAsync(existing))
                        {
                            _endpoint = existing;
                            BridgeLog.Write($"XTTS service already running (pid {existing.Pid}).");
                            return;
                        }

                        await Task.Delay(500);
                    }
                }

                string detail;
                lock (stderrTail)
                {
                    detail = string.Join(" | ", stderrTail.TakeLast(5));
                }

                throw new InvalidOperationException(
                    $"O mecanismo XTTS encerrou com código {process.ExitCode}. {detail}".Trim());
            }

            await Task.Delay(250);
        }

        if (endpoint == null)
        {
            throw new TimeoutException("O mecanismo XTTS não informou a porta a tempo.");
        }

        deadline = DateTimeOffset.UtcNow.AddSeconds(90);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (await IsHealthyAsync(endpoint))
            {
                _endpoint = endpoint;
                BridgeLog.Write($"XTTS service started: pid {endpoint.Pid}, port {endpoint.Port}.");
                _ = stdoutReader;
                return;
            }

            if (process.HasExited)
            {
                throw new InvalidOperationException($"O mecanismo XTTS encerrou com código {process.ExitCode} durante a inicialização.");
            }

            await Task.Delay(500);
        }

        throw new TimeoutException("O mecanismo XTTS não respondeu a tempo.");
    }

    private async Task StopServiceAsync()
    {
        var endpoint = _endpoint;
        _endpoint = null;
        if (endpoint == null)
        {
            return;
        }

        try
        {
            using var request = CreateRequest(HttpMethod.Post, "/shutdown", endpoint);
            using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await _client.SendAsync(request, cancellation.Token);
        }
        catch
        {
            // The service may already be gone.
        }

        var deadline = DateTimeOffset.UtcNow.AddSeconds(15);
        while (DateTimeOffset.UtcNow < deadline && ProcessAlive(endpoint.Pid))
        {
            await Task.Delay(250);
        }

        if (ProcessAlive(endpoint.Pid))
        {
            try
            {
                Process.GetProcessById(endpoint.Pid).Kill(true);
            }
            catch
            {
                // Best effort.
            }
        }
    }

    private static Endpoint? ReadDiscovery()
    {
        try
        {
            if (!File.Exists(DiscoveryPath))
            {
                return null;
            }

            using var document = JsonDocument.Parse(File.ReadAllText(DiscoveryPath));
            var root = document.RootElement;
            if (!root.TryGetProperty("protocol_version", out var protocol) || protocol.GetInt32() != ProtocolVersion)
            {
                return null;
            }

            return new Endpoint(
                root.GetProperty("port").GetInt32(),
                root.GetProperty("token").GetString() ?? string.Empty,
                root.GetProperty("pid").GetInt32());
        }
        catch (Exception exception) when (exception is IOException or JsonException or KeyNotFoundException or InvalidOperationException)
        {
            return null;
        }
    }

    private static bool ProcessAlive(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private async Task<bool> IsHealthyAsync(Endpoint endpoint)
    {
        try
        {
            using var request = CreateRequest(HttpMethod.Get, "/health", endpoint);
            using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            using var response = await _client.SendAsync(request, cancellation.Token);
            if (!response.IsSuccessStatusCode)
            {
                return false;
            }

            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellation.Token));
            return document.RootElement.TryGetProperty("protocol_version", out var protocol) && protocol.GetInt32() == ProtocolVersion;
        }
        catch
        {
            return false;
        }
    }

    // ---------------------------------------------------------------------- http

    private Task<JsonObject> GetAsync(string path) => SendAsync(HttpMethod.Get, path);

    private async Task<JsonObject> SendAsync(HttpMethod method, string path, object? body = null, TimeSpan? timeout = null)
    {
        var endpoint = _endpoint ?? throw new InvalidOperationException("O mecanismo XTTS não está disponível.");
        using var request = CreateRequest(method, path, endpoint);
        if (body != null)
        {
            request.Content = JsonContent.Create(body);
        }

        using var cancellation = new CancellationTokenSource(timeout ?? TimeSpan.FromSeconds(30));
        HttpResponseMessage response;
        try
        {
            response = await _client.SendAsync(request, cancellation.Token);
        }
        catch (HttpRequestException exception)
        {
            _endpoint = null;
            throw new InvalidOperationException($"O mecanismo XTTS parou de responder ({exception.Message}).");
        }
        catch (OperationCanceledException)
        {
            throw new TimeoutException("O mecanismo XTTS demorou mais do que o esperado.");
        }

        using (response)
        {
            var responseText = await response.Content.ReadAsStringAsync(cancellation.Token);
            JsonObject? parsed = null;
            try
            {
                parsed = JsonNode.Parse(responseText) as JsonObject;
            }
            catch (JsonException)
            {
                // Handled below.
            }

            if (!response.IsSuccessStatusCode)
            {
                var message = parsed?["error"]?.GetValue<string>() ?? $"O XTTS retornou HTTP {(int)response.StatusCode}.";
                throw new ServiceException(message, parsed?["code"]?.GetValue<string>(), parsed?["action"]?.GetValue<string>(), (int)response.StatusCode);
            }

            return parsed ?? throw new InvalidOperationException("O XTTS retornou uma resposta inválida.");
        }
    }

    private HttpRequestMessage CreateRequest(HttpMethod method, string path, Endpoint endpoint)
    {
        var request = new HttpRequestMessage(method, $"http://127.0.0.1:{endpoint.Port}{path}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", endpoint.Token);
        return request;
    }

    private static string RequiredString(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(value.GetString()))
        {
            throw new InvalidOperationException($"O campo {name} é obrigatório.");
        }

        return value.GetString()!;
    }

    private static string[] RequiredStringArray(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidOperationException($"O campo {name} é obrigatório.");
        }

        var values = value.EnumerateArray()
            .Where(item => item.ValueKind == JsonValueKind.String)
            .Select(item => item.GetString()?.Trim())
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Cast<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (values.Length == 0)
        {
            throw new InvalidOperationException("Selecione pelo menos um áudio de referência.");
        }

        return values;
    }

    public void Dispose()
    {
        // The service keeps running until its idle timeout so the model stays
        // loaded between widget sessions.
        _process?.Dispose();
        _client.Dispose();
        _startupLock.Dispose();
    }
}

internal sealed class ServiceException : Exception
{
    public ServiceException(string message, string? code, string? action, int status) : base(message)
    {
        Code = code;
        Action = action;
        Status = status;
    }

    public string? Code { get; }
    public string? Action { get; }
    public int Status { get; }
}
