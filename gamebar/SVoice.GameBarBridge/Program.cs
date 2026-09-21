using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;
using Windows.ApplicationModel;
using Windows.ApplicationModel.AppService;
using Windows.Foundation.Collections;

namespace SVoice.GameBarBridge;

internal static class Program
{
    private const string MutexName = @"Local\SVoice.GameBarBridge";

    private static async Task<int> Main()
    {
        using var mutex = new Mutex(true, MutexName, out var ownsMutex);
        if (!ownsMutex)
        {
            return 0;
        }

        try
        {
            using var host = new XttsServiceHost();
            var server = new BridgePipeServer(host);
            using var appService = new BridgeAppService(server);
            if (await appService.RunAsync())
            {
                return 0;
            }

            await server.RunAsync();
            return 0;
        }
        catch (Exception exception)
        {
            BridgeLog.Write($"Fatal bridge error: {exception}");
            return 1;
        }
    }
}

internal sealed class BridgeAppService : IDisposable
{
    private const string ServiceName = "SVoice.XttsBridge";
    private readonly BridgePipeServer _server;
    private readonly TaskCompletionSource _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private AppServiceConnection? _connection;

    public BridgeAppService(BridgePipeServer server)
    {
        _server = server;
    }

    public async Task<bool> RunAsync()
    {
        try
        {
            var connection = new AppServiceConnection
            {
                AppServiceName = ServiceName,
                PackageFamilyName = Package.Current.Id.FamilyName,
            };
            connection.RequestReceived += Connection_RequestReceived;
            connection.ServiceClosed += Connection_ServiceClosed;
            var status = await connection.OpenAsync();
            if (status != AppServiceConnectionStatus.Success)
            {
                BridgeLog.Write($"App Service unavailable: {status}.");
                connection.RequestReceived -= Connection_RequestReceived;
                connection.ServiceClosed -= Connection_ServiceClosed;
                connection.Dispose();
                return false;
            }

            _connection = connection;
            BridgeLog.Write("App Service connected.");
            await _closed.Task;
            return true;
        }
        catch (Exception exception)
        {
            BridgeLog.Write($"App Service setup failed: {exception}");
            return false;
        }
    }

    private async void Connection_RequestReceived(
        AppServiceConnection sender,
        AppServiceRequestReceivedEventArgs args)
    {
        var deferral = args.GetDeferral();
        try
        {
            var requestText = args.Request.Message.TryGetValue("json", out var requestValue)
                ? requestValue as string
                : null;
            var responseText = string.IsNullOrWhiteSpace(requestText)
                ? JsonSerializer.Serialize(new { ok = false, error = "Solicitação XTTS vazia." })
                : await _server.HandleRequestAsync(requestText);
            await args.Request.SendResponseAsync(new ValueSet
            {
                ["json"] = responseText,
            });
        }
        catch (Exception exception)
        {
            BridgeLog.Write($"App Service request failed: {exception}");
        }
        finally
        {
            deferral.Complete();
        }
    }

    private void Connection_ServiceClosed(
        AppServiceConnection sender,
        AppServiceClosedEventArgs args)
    {
        BridgeLog.Write($"App Service closed: {args.Status}.");
        _closed.TrySetResult();
    }

    public void Dispose()
    {
        if (_connection != null)
        {
            _connection.RequestReceived -= Connection_RequestReceived;
            _connection.ServiceClosed -= Connection_ServiceClosed;
            _connection.Dispose();
            _connection = null;
        }
    }
}

internal sealed class BridgePipeServer
{
    internal const string GameBarPackageFamily = "SVoice.GameBar_61qvngw278t1j";
    internal const string PipeName = GameBarPackageFamily + @"\SVoice.GameBar.Xtts";
    private readonly XttsServiceHost _host;

    public BridgePipeServer(XttsServiceHost host)
    {
        _host = host;
    }

    public async Task RunAsync()
    {
        BridgeLog.Write("Bridge started.");
        while (true)
        {
            using var pipe = CreatePipe();
            await pipe.WaitForConnectionAsync();
            await HandleConnectionAsync(pipe);
        }
    }

    private static NamedPipeServerStream CreatePipe()
    {
        var security = new PipeSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);

        var currentUser = WindowsIdentity.GetCurrent().User
            ?? throw new InvalidOperationException("Não foi possível identificar o usuário do bridge XTTS.");
        security.AddAccessRule(new PipeAccessRule(
            currentUser,
            PipeAccessRights.FullControl,
            AccessControlType.Allow));

        var packageSid = PackageSid.TryDerive(GameBarPackageFamily);
        if (packageSid != null)
        {
            var packageRights = PipeAccessRights.ReadWrite |
                PipeAccessRights.ReadPermissions |
                PipeAccessRights.Synchronize;
            security.AddAccessRule(new PipeAccessRule(
                packageSid,
                packageRights,
                AccessControlType.Allow));
            security.AddAccessRule(new PipeAccessRule(
                new SecurityIdentifier("S-1-15-2-1"),
                packageRights,
                AccessControlType.Allow));
        }

        var pipe = NamedPipeServerStreamAcl.Create(
            PipeName,
            PipeDirection.InOut,
            1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous,
            64 * 1024,
            64 * 1024,
            security,
            HandleInheritability.None,
            PipeAccessRights.TakeOwnership);
        LowIntegrityLabel.Apply(pipe.SafePipeHandle);
        return pipe;
    }

    private async Task HandleConnectionAsync(NamedPipeServerStream pipe)
    {
        try
        {
            using var reader = new StreamReader(pipe, Encoding.UTF8, false, leaveOpen: true);
            using var writer = new StreamWriter(pipe, new UTF8Encoding(false), leaveOpen: true)
            {
                AutoFlush = true,
            };

            var requestText = await reader.ReadLineAsync();
            if (string.IsNullOrWhiteSpace(requestText))
            {
                return;
            }

            var response = await HandleRequestAsync(requestText);
            await writer.WriteLineAsync(response);
        }
        catch (Exception exception)
        {
            BridgeLog.Write($"Pipe request failed: {exception}");
        }
    }

    internal async Task<string> HandleRequestAsync(string requestText)
    {
        try
        {
            using var request = JsonDocument.Parse(requestText);
            var root = request.RootElement;
            var command = root.TryGetProperty("command", out var commandValue)
                ? commandValue.GetString()
                : null;

            return command switch
            {
                "ping" => await _host.PingAsync(),
                "profiles" => await _host.GetProfilesAsync(),
                "create_profile" => await _host.CreateProfileAsync(root),
                "synthesize" => await _host.SynthesizeAsync(root),
                _ => JsonSerializer.Serialize(new { ok = false, error = "Comando desconhecido." }),
            };
        }
        catch (Exception exception)
        {
            BridgeLog.Write($"Command failed: {exception}");
            return JsonSerializer.Serialize(new
            {
                ok = false,
                error = exception.Message,
            });
        }
    }
}

internal sealed class XttsServiceHost : IDisposable
{
    private readonly HttpClient _client = new();
    private readonly SemaphoreSlim _startupLock = new(1, 1);
    private Process? _process;
    private int _port;
    private string? _token;

    public async Task<string> PingAsync()
    {
        await EnsureStartedAsync();
        using var health = await SendAsync(HttpMethod.Get, "/health");
        return JsonSerializer.Serialize(new
        {
            ok = true,
            runtime = health.RootElement.Clone(),
        });
    }

    public async Task<string> GetProfilesAsync()
    {
        await EnsureStartedAsync();
        using var profiles = await SendAsync(HttpMethod.Get, "/profiles");
        return JsonSerializer.Serialize(new
        {
            ok = true,
            profiles = profiles.RootElement.GetProperty("profiles").Clone(),
        });
    }

    public async Task<string> SynthesizeAsync(JsonElement request)
    {
        await EnsureStartedAsync();
        var text = RequiredString(request, "text");
        var profileId = RequiredString(request, "profile_id");
        var speed = request.TryGetProperty("speed", out var speedValue) && speedValue.TryGetDouble(out var parsedSpeed)
            ? Math.Clamp(parsedSpeed, 0.5, 2.0)
            : 1.0;
        var computeMode = request.TryGetProperty("compute_mode", out var computeValue)
            ? computeValue.GetString()
            : "auto";
        if (computeMode is not ("auto" or "gpu" or "cpu"))
        {
            computeMode = "auto";
        }

        using var synthesis = await SendAsync(
            HttpMethod.Post,
            "/synthesize",
            new
            {
                text,
                profile_id = profileId,
                speed,
                compute_mode = computeMode,
            },
            TimeSpan.FromMinutes(45));

        var outputPath = synthesis.RootElement.GetProperty("output_path").GetString();
        if (string.IsNullOrWhiteSpace(outputPath))
        {
            throw new InvalidOperationException("O XTTS não retornou o áudio gerado.");
        }

        var fullOutputPath = Path.GetFullPath(outputPath);
        var tempRoot = Path.GetFullPath(Path.Combine(DataDirectory, "temp")) + Path.DirectorySeparatorChar;
        if (!fullOutputPath.StartsWith(tempRoot, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("O XTTS retornou um caminho de áudio inválido.");
        }

        try
        {
            var audio = await File.ReadAllBytesAsync(fullOutputPath);
            var transport = request.TryGetProperty("transport", out var transportValue)
                ? transportValue.GetString()
                : null;
            if (transport == "app_service")
            {
                var transferDirectory = Path.Combine(
                    KnownFolders.UnredirectedLocalAppData,
                    "Packages",
                    BridgePipeServer.GameBarPackageFamily,
                    "TempState");
                Directory.CreateDirectory(transferDirectory);
                var transferName = $"xtts_{Guid.NewGuid():N}.wav";
                await File.WriteAllBytesAsync(Path.Combine(transferDirectory, transferName), audio);
                return JsonSerializer.Serialize(new
                {
                    ok = true,
                    audio_file = transferName,
                    content_type = "audio/wav",
                    device = synthesis.RootElement.TryGetProperty("device", out var appServiceDevice)
                        ? appServiceDevice.GetString()
                        : null,
                });
            }

            return JsonSerializer.Serialize(new
            {
                ok = true,
                audio_base64 = Convert.ToBase64String(audio),
                content_type = "audio/wav",
                device = synthesis.RootElement.TryGetProperty("device", out var pipeDevice)
                    ? pipeDevice.GetString()
                    : null,
            });
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

    public async Task<string> CreateProfileAsync(JsonElement request)
    {
        await EnsureStartedAsync();
        var name = request.TryGetProperty("name", out var nameValue)
            ? nameValue.GetString()?.Trim()
            : null;
        var referencePaths = RequiredStringArray(request, "reference_paths");

        using var profile = await SendAsync(
            HttpMethod.Post,
            "/profiles",
            new
            {
                name,
                reference_paths = referencePaths,
            },
            TimeSpan.FromMinutes(45));
        return JsonSerializer.Serialize(new
        {
            ok = true,
            profile = profile.RootElement.GetProperty("profile").Clone(),
        });
    }

    private async Task EnsureStartedAsync()
    {
        if (_process is { HasExited: false } && await IsHealthyAsync())
        {
            return;
        }

        await _startupLock.WaitAsync();
        try
        {
            if (_process is { HasExited: false } && await IsHealthyAsync())
            {
                return;
            }

            _process?.Dispose();
            var executablePath = FindServiceExecutable();
            _port = ReserveLoopbackPort();
            _token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
            Directory.CreateDirectory(DataDirectory);

            _process = Process.Start(new ProcessStartInfo
            {
                FileName = executablePath,
                WorkingDirectory = Path.GetDirectoryName(executablePath)!,
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                Arguments = $"--port {_port} --token {_token} --data-dir \"{DataDirectory}\"",
            }) ?? throw new InvalidOperationException("O processo XTTS não pôde ser iniciado.");

            BridgeLog.Write($"XTTS started on port {_port}.");
            var timeout = DateTimeOffset.UtcNow.AddSeconds(90);
            Exception? lastError = null;
            while (DateTimeOffset.UtcNow < timeout)
            {
                if (_process.HasExited)
                {
                    throw new InvalidOperationException($"O XTTS encerrou com código {_process.ExitCode}.");
                }

                try
                {
                    if (await IsHealthyAsync())
                    {
                        return;
                    }
                }
                catch (Exception exception)
                {
                    lastError = exception;
                }

                await Task.Delay(500);
            }

            throw new TimeoutException($"O XTTS não iniciou a tempo. {lastError?.Message}");
        }
        finally
        {
            _startupLock.Release();
        }
    }

    private async Task<bool> IsHealthyAsync()
    {
        if (_port == 0 || string.IsNullOrWhiteSpace(_token))
        {
            return false;
        }

        try
        {
            using var request = CreateRequest(HttpMethod.Get, "/health");
            using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            using var response = await _client.SendAsync(request, cancellation.Token);
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    private async Task<JsonDocument> SendAsync(
        HttpMethod method,
        string path,
        object? body = null,
        TimeSpan? timeout = null)
    {
        using var request = CreateRequest(method, path);
        if (body != null)
        {
            request.Content = JsonContent.Create(body);
        }

        using var cancellation = new CancellationTokenSource(timeout ?? TimeSpan.FromSeconds(30));
        using var response = await _client.SendAsync(request, cancellation.Token);
        var responseText = await response.Content.ReadAsStringAsync(cancellation.Token);
        if (!response.IsSuccessStatusCode)
        {
            try
            {
                using var error = JsonDocument.Parse(responseText);
                if (error.RootElement.TryGetProperty("error", out var message))
                {
                    throw new InvalidOperationException(message.GetString());
                }
            }
            catch (JsonException)
            {
                // Fall through to the HTTP status error.
            }

            throw new InvalidOperationException($"O XTTS retornou HTTP {(int)response.StatusCode}.");
        }

        return JsonDocument.Parse(responseText);
    }

    private HttpRequestMessage CreateRequest(HttpMethod method, string path)
    {
        var request = new HttpRequestMessage(method, $"http://127.0.0.1:{_port}{path}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token);
        return request;
    }

    private static string RequiredString(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var value) || string.IsNullOrWhiteSpace(value.GetString()))
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

    private static int ReserveLoopbackPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static string FindServiceExecutable()
    {
        var overridePath = Environment.GetEnvironmentVariable("SVOICE_XTTS_SERVICE_PATH");
        if (!string.IsNullOrWhiteSpace(overridePath) && File.Exists(overridePath))
        {
            return Path.GetFullPath(overridePath);
        }

        var candidates = new List<string>
        {
            Path.Combine(AppContext.BaseDirectory, "xtts_service", "svoice_xtts_service.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "SVoice", "xtts_service", "svoice_xtts_service.exe"),
        };

        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        for (var depth = 0; directory != null && depth < 8; depth++, directory = directory.Parent)
        {
            candidates.Add(Path.Combine(
                directory.FullName,
                "python_service",
                "dist",
                "svoice_xtts_service",
                "svoice_xtts_service.exe"));
        }

        return candidates.FirstOrDefault(File.Exists)
            ?? throw new FileNotFoundException(
                "O mecanismo XTTS não está instalado. Instale ou recompile o SVoice completo.");
    }

    private static string DataDirectory => Path.Combine(
        KnownFolders.UnredirectedLocalAppData,
        "SVoice",
        "XTTS");

    public void Dispose()
    {
        if (_process is { HasExited: false })
        {
            try
            {
                using var request = CreateRequest(HttpMethod.Post, "/shutdown");
                _client.Send(request);
                _process.WaitForExit(3000);
            }
            catch
            {
                _process.Kill(true);
            }
        }

        _process?.Dispose();
        _client.Dispose();
        _startupLock.Dispose();
    }
}

internal static class KnownFolders
{
    private static readonly Guid LocalAppDataId = new("F1B32785-6FBA-4FCF-9D55-7B8E7F157091");
    private const uint NoPackageRedirection = 0x00010000;
    private const uint DontVerify = 0x00004000;

    public static string UnredirectedLocalAppData { get; } = ResolveUnredirectedLocalAppData();

    private static string ResolveUnredirectedLocalAppData()
    {
        var folderId = LocalAppDataId;
        var result = SHGetKnownFolderPath(
            ref folderId,
            NoPackageRedirection | DontVerify,
            IntPtr.Zero,
            out var pathPointer);
        try
        {
            if (result == 0 && pathPointer != IntPtr.Zero)
            {
                var path = Marshal.PtrToStringUni(pathPointer);
                if (!string.IsNullOrWhiteSpace(path))
                {
                    return path;
                }
            }
        }
        finally
        {
            if (pathPointer != IntPtr.Zero)
            {
                Marshal.FreeCoTaskMem(pathPointer);
            }
        }

        return Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHGetKnownFolderPath(
        ref Guid folderId,
        uint flags,
        IntPtr token,
        out IntPtr path);
}

internal static class PackageSid
{
    public static SecurityIdentifier? TryDerive(string packageFamilyName)
    {
        var result = DeriveAppContainerSidFromAppContainerName(packageFamilyName, out var sidPointer);
        if (result != 0 || sidPointer == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            return new SecurityIdentifier(sidPointer);
        }
        finally
        {
            FreeSid(sidPointer);
        }
    }

    [DllImport("userenv.dll", CharSet = CharSet.Unicode)]
    private static extern int DeriveAppContainerSidFromAppContainerName(
        string appContainerName,
        out IntPtr appContainerSid);

    [DllImport("advapi32.dll")]
    private static extern IntPtr FreeSid(IntPtr sid);
}

internal static class LowIntegrityLabel
{
    private const uint SddlRevision1 = 1;
    private const uint LabelSecurityInformation = 0x00000010;
    private const int SeKernelObject = 6;

    public static void Apply(SafePipeHandle pipeHandle)
    {
        if (!ConvertStringSecurityDescriptorToSecurityDescriptor(
                "S:(ML;;NW;;;LW)",
                SddlRevision1,
                out var securityDescriptor,
                out _))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        try
        {
            if (!GetSecurityDescriptorSacl(
                    securityDescriptor,
                    out var saclPresent,
                    out var sacl,
                    out _) ||
                !saclPresent ||
                sacl == IntPtr.Zero)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            var result = SetSecurityInfo(
                pipeHandle.DangerousGetHandle(),
                SeKernelObject,
                LabelSecurityInformation,
                IntPtr.Zero,
                IntPtr.Zero,
                IntPtr.Zero,
                sacl);
            if (result != 0)
            {
                throw new Win32Exception((int)result);
            }
        }
        finally
        {
            LocalFree(securityDescriptor);
        }
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool ConvertStringSecurityDescriptorToSecurityDescriptor(
        string stringSecurityDescriptor,
        uint stringSdRevision,
        out IntPtr securityDescriptor,
        out uint securityDescriptorSize);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool GetSecurityDescriptorSacl(
        IntPtr securityDescriptor,
        out bool saclPresent,
        out IntPtr sacl,
        out bool saclDefaulted);

    [DllImport("advapi32.dll")]
    private static extern uint SetSecurityInfo(
        IntPtr handle,
        int objectType,
        uint securityInformation,
        IntPtr owner,
        IntPtr group,
        IntPtr dacl,
        IntPtr sacl);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);
}

internal static class BridgeLog
{
    private static readonly object Sync = new();

    public static void Write(string message)
    {
        try
        {
            var directory = Path.Combine(
                KnownFolders.UnredirectedLocalAppData,
                "SVoice",
                "Logs");
            Directory.CreateDirectory(directory);
            lock (Sync)
            {
                File.AppendAllText(
                    Path.Combine(directory, "gamebar-bridge.log"),
                    $"{DateTimeOffset.Now:O} {message}{Environment.NewLine}");
            }
        }
        catch
        {
            // Logging must not stop the bridge.
        }
    }
}
