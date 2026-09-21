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
        public ClonedVoiceProfile(string id, string name, double durationSeconds)
        {
            Id = id;
            Name = name;
            DurationSeconds = durationSeconds;
        }

        public string Id { get; }
        public string Name { get; }
        public double DurationSeconds { get; }
    }

    internal sealed class XttsAudioResult
    {
        public XttsAudioResult(byte[] bytes, string contentType)
        {
            Bytes = bytes;
            ContentType = contentType;
        }

        public byte[] Bytes { get; }
        public string ContentType { get; }
    }

    internal sealed class XttsBridgeClient
    {
        private static readonly Uri LaunchUri = new Uri("svoice-bridge://start");

        public async Task<IReadOnlyList<ClonedVoiceProfile>> GetProfilesAsync()
        {
            using var response = await SendAsync(new { command = "profiles" });
            if (!response.RootElement.TryGetProperty("profiles", out var profiles) ||
                profiles.ValueKind != JsonValueKind.Array)
            {
                return Array.Empty<ClonedVoiceProfile>();
            }

            return profiles.EnumerateArray()
                .Select(ParseProfile)
                .Where(profile => !string.IsNullOrWhiteSpace(profile.Id))
                .ToArray();
        }

        public async Task<ClonedVoiceProfile> CreateProfileAsync(
            string? name,
            IReadOnlyList<string> referencePaths)
        {
            if (referencePaths.Count == 0)
            {
                throw new ArgumentException("Selecione pelo menos um áudio de referência.", nameof(referencePaths));
            }

            using var response = await SendAsync(
                new
                {
                    command = "create_profile",
                    name = name?.Trim(),
                    reference_paths = referencePaths,
                },
                TimeSpan.FromMinutes(45));
            if (!response.RootElement.TryGetProperty("profile", out var profile))
            {
                throw new InvalidOperationException("O XTTS não confirmou a voz clonada.");
            }

            var parsed = ParseProfile(profile);
            if (string.IsNullOrWhiteSpace(parsed.Id) || parsed.DurationSeconds <= 0)
            {
                throw new InvalidOperationException("O XTTS retornou um perfil de voz inválido.");
            }

            return parsed;
        }

        public async Task<XttsAudioResult> SynthesizeAsync(
            string text,
            string profileId,
            double speed = 1.0,
            string computeMode = "auto")
        {
            using var response = await SendAsync(
                new
                {
                    command = "synthesize",
                    transport = "app_service",
                    text,
                    profile_id = profileId,
                    speed,
                    compute_mode = computeMode,
                },
                TimeSpan.FromMinutes(45));

            byte[] bytes;
            if (response.RootElement.TryGetProperty("audio_file", out var audioFileValue) &&
                !string.IsNullOrWhiteSpace(audioFileValue.GetString()))
            {
                var audioFile = await ApplicationData.Current.TemporaryFolder.GetFileAsync(
                    audioFileValue.GetString());
                try
                {
                    var buffer = await FileIO.ReadBufferAsync(audioFile);
                    bytes = new byte[buffer.Length];
                    using var reader = DataReader.FromBuffer(buffer);
                    reader.ReadBytes(bytes);
                }
                finally
                {
                    await audioFile.DeleteAsync(StorageDeleteOption.PermanentDelete);
                }
            }
            else
            {
                var audioBase64 = response.RootElement.TryGetProperty("audio_base64", out var audio)
                    ? audio.GetString()
                    : null;
                if (string.IsNullOrWhiteSpace(audioBase64))
                {
                    throw new InvalidOperationException("O bridge XTTS não retornou o áudio gerado.");
                }

                bytes = Convert.FromBase64String(audioBase64);
            }

            var contentType = response.RootElement.TryGetProperty("content_type", out var type)
                ? type.GetString() ?? "audio/wav"
                : "audio/wav";
            return new XttsAudioResult(bytes, contentType);
        }

        private static ClonedVoiceProfile ParseProfile(JsonElement profile)
        {
            return new ClonedVoiceProfile(
                profile.TryGetProperty("id", out var id) ? id.GetString() ?? string.Empty : string.Empty,
                profile.TryGetProperty("name", out var name) ? name.GetString() ?? "Voz clonada" : "Voz clonada",
                profile.TryGetProperty("duration_seconds", out var duration) && duration.TryGetDouble(out var seconds)
                    ? seconds
                    : 0);
        }

        private static async Task<JsonDocument> SendAsync(object payload, TimeSpan? timeout = null)
        {
            var serializedPayload = JsonSerializer.Serialize(payload);
            Exception? lastError = null;
            var launchedBridge = false;
            var deadline = DateTimeOffset.UtcNow.AddSeconds(30);

            while (DateTimeOffset.UtcNow < deadline)
            {
                try
                {
                    using var cancellation = new CancellationTokenSource(timeout ?? TimeSpan.FromSeconds(30));
                    var connection = await XttsBridgeChannel.WaitAsync(TimeSpan.FromSeconds(1));
                    var requestMessage = new ValueSet
                    {
                        ["json"] = serializedPayload,
                    };
                    var serviceResponse = await connection.SendMessageAsync(requestMessage)
                        .AsTask(cancellation.Token);
                    if (serviceResponse.Status != AppServiceResponseStatus.Success)
                    {
                        throw new IOException($"O App Service XTTS retornou {serviceResponse.Status}.");
                    }

                    var responseText = serviceResponse.Message.TryGetValue("json", out var responseValue)
                        ? responseValue as string
                        : null;
                    if (string.IsNullOrWhiteSpace(responseText))
                    {
                        throw new IOException("O App Service XTTS encerrou a conexão sem responder.");
                    }

                    var response = JsonDocument.Parse(responseText);
                    if (!response.RootElement.TryGetProperty("ok", out var ok) || !ok.GetBoolean())
                    {
                        var message = response.RootElement.TryGetProperty("error", out var error)
                            ? error.GetString()
                            : null;
                        response.Dispose();
                        throw new InvalidOperationException(message ?? "O bridge XTTS recusou a solicitação.");
                    }

                    return response;
                }
                catch (Exception exception) when (exception is IOException or TimeoutException or OperationCanceledException)
                {
                    lastError = exception;
                    if (!launchedBridge)
                    {
                        launchedBridge = await LaunchBridgeAsync();
                    }

                    await Task.Delay(500);
                }
            }

            throw new InvalidOperationException(
                launchedBridge
                    ? $"O componente XTTS não respondeu. {lastError?.Message}"
                    : "O SVoice completo não está instalado ou o bridge XTTS não foi registrado.");
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

    internal sealed class VoiceChoice
    {
        public VoiceChoice(string label, string? profileId = null)
        {
            Label = label;
            ProfileId = profileId;
        }

        public string Label { get; }
        public string? ProfileId { get; }
        public bool IsCloned => !string.IsNullOrWhiteSpace(ProfileId);

        public override string ToString() => Label;
    }
}
