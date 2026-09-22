using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Windows.ApplicationModel;
using Windows.ApplicationModel.AppService;
using Windows.Foundation.Collections;
using Windows.Storage;
using Windows.Storage.Streams;
using Windows.System;

namespace SVoice.GameBar
{
    internal sealed class ClonedVoiceProfile
    {
        public ClonedVoiceProfile(string id, string name, double durationSeconds, int referenceCount, int missingReferences, string status)
        {
            Id = id;
            Name = name;
            DurationSeconds = durationSeconds;
            ReferenceCount = referenceCount;
            MissingReferences = missingReferences;
            Status = status;
        }

        public string Id { get; }
        public string Name { get; }
        public double DurationSeconds { get; }
        public int ReferenceCount { get; }
        public int MissingReferences { get; }
        public string Status { get; }
        public bool IsUsable => Status == "ok" || Status == "partial_references";

        public string Summary
        {
            get
            {
                var duration = DurationSeconds < 60
                    ? $"{Math.Round(DurationSeconds)} s"
                    : $"{Math.Floor(DurationSeconds / 60)} min";
                var state = Status switch
                {
                    "ok" => "pronta",
                    "partial_references" => $"{MissingReferences} trecho(s) ausente(s)",
                    _ => "áudio de referência ausente",
                };
                return $"{duration} de referência · {ReferenceCount} trecho(s) · {state}";
            }
        }
    }

    internal sealed class XttsHealth
    {
        public string State { get; set; } = "unknown";
        public string Message { get; set; } = string.Empty;
        public bool Busy { get; set; }
        public bool ModelReady { get; set; }
        public bool ModelLoaded { get; set; }
        public string? ActiveBackend { get; set; }
        public string? ActiveBackendLabel { get; set; }
        public string ComputeMode { get; set; } = "auto";
        public string? FallbackReason { get; set; }
        public string? GpuName { get; set; }
        public string? JobKind { get; set; }
        public string? JobMessage { get; set; }
        public double? JobProgress { get; set; }
        public string? ServiceVersion { get; set; }

        public static XttsHealth Parse(JsonElement root)
        {
            var health = new XttsHealth
            {
                State = root.GetStringOrDefault("state", "unknown"),
                Message = root.GetStringOrDefault("message", string.Empty),
                Busy = root.TryGetProperty("busy", out var busy) && busy.ValueKind == JsonValueKind.True,
                ModelReady = root.TryGetProperty("model_ready", out var ready) && ready.ValueKind == JsonValueKind.True,
                ModelLoaded = root.TryGetProperty("model_loaded", out var loaded) && loaded.ValueKind == JsonValueKind.True,
                ActiveBackend = root.GetStringOrNull("active_backend"),
                ActiveBackendLabel = root.GetStringOrNull("active_backend_label"),
                ComputeMode = root.GetStringOrDefault("compute_mode", "auto"),
                FallbackReason = root.GetStringOrNull("fallback_reason"),
                GpuName = root.GetStringOrNull("gpu_name"),
                ServiceVersion = root.GetStringOrNull("version"),
            };
            if (root.TryGetProperty("job", out var job) && job.ValueKind == JsonValueKind.Object)
            {
                health.JobKind = job.GetStringOrNull("kind");
                health.JobMessage = job.GetStringOrNull("message");
                if (job.TryGetProperty("progress", out var progress) && progress.ValueKind == JsonValueKind.Number)
                {
                    health.JobProgress = progress.GetDouble();
                }
            }

            return health;
        }
    }

    internal sealed class XttsAudioResult
    {
        public XttsAudioResult(byte[] bytes, string contentType, string? backendLabel, double durationSeconds)
        {
            Bytes = bytes;
            ContentType = contentType;
            BackendLabel = backendLabel;
            DurationSeconds = durationSeconds;
        }

        public byte[] Bytes { get; }
        public string ContentType { get; }
        public string? BackendLabel { get; }
        public double DurationSeconds { get; }
    }

    internal sealed class XttsBridgeException : Exception
    {
        public XttsBridgeException(string message, string? code = null, string? action = null) : base(message)
        {
            Code = code;
            Action = action;
        }

        public string? Code { get; }
        public string? Action { get; }
        public bool IsCancellation => Code == "cancelled";
    }

    internal static class JsonElementExtensions
    {
        public static string? GetStringOrNull(this JsonElement element, string name)
        {
            return element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;
        }

        public static string GetStringOrDefault(this JsonElement element, string name, string fallback)
        {
            return element.GetStringOrNull(name) ?? fallback;
        }
    }

    internal sealed class XttsBridgeClient
    {
        internal const int ProtocolVersion = 2;
        private static readonly Uri LaunchUri = new Uri("svoice-bridge://start");
        private static readonly TimeSpan LongOperation = TimeSpan.FromMinutes(45);

        public async Task<XttsHealth> PingAsync()
        {
            using var response = await SendAsync(new { command = "ping", protocol = ProtocolVersion }, TimeSpan.FromMinutes(3));
            return XttsHealth.Parse(response.RootElement);
        }

        public async Task<IReadOnlyList<ClonedVoiceProfile>> GetProfilesAsync()
        {
            using var response = await SendAsync(new { command = "profiles", protocol = ProtocolVersion }, TimeSpan.FromMinutes(3));
            if (!response.RootElement.TryGetProperty("profiles", out var profiles) || profiles.ValueKind != JsonValueKind.Array)
            {
                return Array.Empty<ClonedVoiceProfile>();
            }

            return profiles.EnumerateArray()
                .Select(ParseProfile)
                .Where(profile => !string.IsNullOrWhiteSpace(profile.Id))
                .ToArray();
        }

        public async Task<ClonedVoiceProfile> CreateProfileAsync(string? name, IReadOnlyList<string> referencePaths)
        {
            if (referencePaths.Count == 0)
            {
                throw new ArgumentException("Selecione pelo menos um áudio de referência.", nameof(referencePaths));
            }

            using var response = await SendAsync(
                new { command = "create_profile", protocol = ProtocolVersion, name = name?.Trim(), reference_paths = referencePaths },
                LongOperation);
            if (!response.RootElement.TryGetProperty("profile", out var profile))
            {
                throw new XttsBridgeException("O XTTS não confirmou a voz clonada.");
            }

            var parsed = ParseProfile(profile);
            if (string.IsNullOrWhiteSpace(parsed.Id) || parsed.DurationSeconds <= 0)
            {
                throw new XttsBridgeException("O XTTS retornou um perfil de voz inválido.");
            }

            return parsed;
        }

        public async Task<ClonedVoiceProfile> RenameProfileAsync(string profileId, string name)
        {
            using var response = await SendAsync(new { command = "rename_profile", protocol = ProtocolVersion, profile_id = profileId, name });
            return ParseProfile(response.RootElement.GetProperty("profile"));
        }

        public async Task DeleteProfileAsync(string profileId)
        {
            using var response = await SendAsync(new { command = "delete_profile", protocol = ProtocolVersion, profile_id = profileId });
        }

        public async Task<XttsAudioResult> SynthesizeAsync(string text, string profileId, double speed = 1.0, CancellationToken cancellationToken = default)
        {
            using var response = await SendAsync(
                new { command = "synthesize", protocol = ProtocolVersion, text, profile_id = profileId, speed },
                LongOperation,
                cancellationToken);

            var root = response.RootElement;
            var audioFile = root.GetStringOrNull("audio_file");
            if (string.IsNullOrWhiteSpace(audioFile))
            {
                throw new XttsBridgeException("O bridge XTTS não retornou o áudio gerado.");
            }

            var file = await ApplicationData.Current.TemporaryFolder.GetFileAsync(audioFile);
            byte[] bytes;
            try
            {
                var buffer = await FileIO.ReadBufferAsync(file);
                bytes = new byte[buffer.Length];
                using var reader = DataReader.FromBuffer(buffer);
                reader.ReadBytes(bytes);
            }
            finally
            {
                await file.DeleteAsync(StorageDeleteOption.PermanentDelete);
            }

            var duration = root.TryGetProperty("duration_seconds", out var durationValue) && durationValue.TryGetDouble(out var seconds) ? seconds : 0;
            return new XttsAudioResult(bytes, root.GetStringOrDefault("content_type", "audio/wav"), root.GetStringOrNull("backend_label"), duration);
        }

        public async Task<bool> CancelAsync()
        {
            try
            {
                using var response = await SendAsync(new { command = "cancel", protocol = ProtocolVersion }, TimeSpan.FromSeconds(15));
                return response.RootElement.TryGetProperty("cancelled", out var cancelled) && cancelled.ValueKind == JsonValueKind.True;
            }
            catch (Exception exception)
            {
                App.Log($"Cancel request failed: {exception.Message}");
                return false;
            }
        }

        public Task<JsonDocument> DiagnosticsAsync()
        {
            return SendAsync(new { command = "diagnostics", protocol = ProtocolVersion }, TimeSpan.FromMinutes(3));
        }

        public Task<JsonDocument> TestBackendAsync(string backend)
        {
            return SendAsync(new { command = "test_backend", protocol = ProtocolVersion, backend }, LongOperation);
        }

        public Task<JsonDocument> SetComputeModeAsync(string computeMode)
        {
            return SendAsync(new { command = "set_config", protocol = ProtocolVersion, compute_mode = computeMode }, TimeSpan.FromMinutes(3));
        }

        public Task<JsonDocument> EnsureModelAsync()
        {
            return SendAsync(new { command = "ensure_model", protocol = ProtocolVersion }, LongOperation);
        }

        public Task<JsonDocument> RestartServiceAsync()
        {
            return SendAsync(new { command = "restart_service", protocol = ProtocolVersion }, TimeSpan.FromMinutes(3));
        }

        private static ClonedVoiceProfile ParseProfile(JsonElement profile)
        {
            return new ClonedVoiceProfile(
                profile.GetStringOrDefault("id", string.Empty),
                profile.GetStringOrDefault("name", "Voz clonada"),
                profile.TryGetProperty("duration_seconds", out var duration) && duration.TryGetDouble(out var seconds) ? seconds : 0,
                profile.TryGetProperty("reference_count", out var references) && references.TryGetInt32(out var count) ? count : 0,
                profile.TryGetProperty("missing_references", out var missing) && missing.TryGetInt32(out var missingCount) ? missingCount : 0,
                profile.GetStringOrDefault("status", "ok"));
        }

        private static async Task<JsonDocument> SendAsync(object payload, TimeSpan? timeout = null, CancellationToken cancellationToken = default)
        {
            var serializedPayload = JsonSerializer.Serialize(payload);
            Exception? lastError = null;
            var launchedBridge = false;
            var deadline = DateTimeOffset.UtcNow.AddSeconds(30);

            while (DateTimeOffset.UtcNow < deadline)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    cancellation.CancelAfter(timeout ?? TimeSpan.FromSeconds(30));
                    var connection = await XttsBridgeChannel.WaitAsync(TimeSpan.FromSeconds(1));
                    var serviceResponse = await connection.SendMessageAsync(new ValueSet { ["json"] = serializedPayload })
                        .AsTask(cancellation.Token);
                    if (serviceResponse.Status != AppServiceResponseStatus.Success)
                    {
                        throw new IOException($"O App Service XTTS retornou {serviceResponse.Status}.");
                    }

                    var responseText = serviceResponse.Message.TryGetValue("json", out var responseValue) ? responseValue as string : null;
                    if (string.IsNullOrWhiteSpace(responseText))
                    {
                        throw new IOException("O App Service XTTS encerrou a conexão sem responder.");
                    }

                    var response = JsonDocument.Parse(responseText);
                    if (!response.RootElement.TryGetProperty("ok", out var ok) || ok.ValueKind != JsonValueKind.True)
                    {
                        var message = response.RootElement.GetStringOrNull("error");
                        var code = response.RootElement.GetStringOrNull("code");
                        var action = response.RootElement.GetStringOrNull("action");
                        response.Dispose();
                        throw new XttsBridgeException(message ?? "O bridge XTTS recusou a solicitação.", code, action);
                    }

                    return response;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception exception) when (exception is IOException or TimeoutException or OperationCanceledException)
                {
                    lastError = exception;
                    if (!launchedBridge)
                    {
                        launchedBridge = await LaunchBridgeAsync();
                    }

                    await Task.Delay(500, cancellationToken);
                }
            }

            throw new XttsBridgeException(
                launchedBridge
                    ? $"O componente XTTS não respondeu. {lastError?.Message}"
                    : "O componente XTTS não pôde ser iniciado. Reinstale o SVoice.",
                "bridge_unavailable",
                "Feche e abra o widget; se persistir, execute o reparo do SVoice.");
        }

        private static async Task<bool> LaunchBridgeAsync()
        {
            try
            {
                await FullTrustProcessLauncher.LaunchFullTrustProcessForCurrentAppAsync();
                App.Log("Packaged XTTS bridge launch requested.");
                return true;
            }
            catch (Exception packagedLaunchError)
            {
                App.Log($"Packaged XTTS bridge launch failed: {packagedLaunchError}");
                try
                {
                    var launched = await Launcher.LaunchUriAsync(LaunchUri);
                    App.Log($"External XTTS bridge launch requested: {launched}.");
                    return launched;
                }
                catch (Exception protocolLaunchError)
                {
                    App.Log($"External XTTS bridge launch failed: {protocolLaunchError}");
                    return false;
                }
            }
        }
    }

    internal sealed class OutputDeviceChoice
    {
        public OutputDeviceChoice(string label, string? id, bool isVirtualCable)
        {
            Label = label;
            Id = id;
            IsVirtualCable = isVirtualCable;
        }

        public string Label { get; }
        public string? Id { get; }
        public bool IsVirtualCable { get; }

        public override string ToString() => Label;
    }

    internal sealed class LabeledValue
    {
        public LabeledValue(string label, string? value)
        {
            Label = label;
            Value = value ?? "—";
        }

        public string Label { get; }
        public string Value { get; }
    }

    internal sealed class ComputeModeChoice
    {
        public ComputeModeChoice(string wireName, string label)
        {
            WireName = wireName;
            Label = label;
        }

        public string WireName { get; }
        public string Label { get; }

        public override string ToString() => Label;
    }
}
