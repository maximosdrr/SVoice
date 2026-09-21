using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Windows.ApplicationModel;
using Windows.ApplicationModel.AppService;
using Windows.Foundation.Collections;

namespace SVoice.GameBarBridge;

/// <summary>
/// Full-trust companion of the Game Bar widget.
/// </summary>
/// <remarks>
/// The widget runs in an AppContainer and cannot start processes or read the
/// user's profile. This bridge is launched by the widget through
/// <c>FullTrustProcessLauncher</c>, receives JSON commands over an App Service
/// connection (or a named pipe when launched by protocol) and forwards them to
/// the local XTTS service.
/// </remarks>
internal static class Program
{
    private const string MutexName = @"Local\SVoice.GameBarBridge";

    private static async Task<int> Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "--diagnostics")
        {
            return await RunDiagnosticsAsync();
        }

        using var mutex = new Mutex(true, MutexName, out var ownsMutex);
        if (!ownsMutex)
        {
            return 0;
        }

        try
        {
            using var host = new XttsServiceHost();
            var dispatcher = new CommandDispatcher(host);
            var server = new BridgePipeServer(dispatcher);
            using var appService = new BridgeAppService(dispatcher);
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

    private static async Task<int> RunDiagnosticsAsync()
    {
        using var host = new XttsServiceHost();
        var dispatcher = new CommandDispatcher(host);
        Console.WriteLine(await dispatcher.HandleRequestAsync("""{"command":"diagnostics","protocol":2}"""));
        return 0;
    }
}

internal sealed class CommandDispatcher
{
    private readonly XttsServiceHost _host;

    public CommandDispatcher(XttsServiceHost host)
    {
        _host = host;
    }

    internal async Task<string> HandleRequestAsync(string requestText)
    {
        try
        {
            using var request = JsonDocument.Parse(requestText);
            var root = request.RootElement;
            var command = root.TryGetProperty("command", out var commandValue) ? commandValue.GetString() : null;
            if (root.TryGetProperty("protocol", out var protocol) && protocol.ValueKind == JsonValueKind.Number &&
                protocol.GetInt32() != XttsServiceHost.ProtocolVersion)
            {
                return Failure("O widget e o componente XTTS usam versões diferentes. Reinstale o SVoice.", "protocol_mismatch");
            }

            JsonObject payload = command switch
            {
                "ping" => await _host.PingAsync(),
                "profiles" => await _host.GetProfilesAsync(),
                "create_profile" => await _host.CreateProfileAsync(root),
                "rename_profile" => await _host.RenameProfileAsync(root),
                "delete_profile" => await _host.DeleteProfileAsync(root),
                "synthesize" => await _host.SynthesizeAsync(root),
                "cancel" => await _host.CancelAsync(),
                "diagnostics" => await _host.DiagnosticsAsync(),
                "test_backend" => await _host.TestBackendAsync(root),
                "set_config" => await _host.SetConfigAsync(root),
                "ensure_model" => await _host.EnsureModelAsync(),
                "restart_service" => await _host.RestartServiceAsync(),
                _ => throw new InvalidOperationException("Comando desconhecido."),
            };
            payload["ok"] = true;
            payload["protocol"] = XttsServiceHost.ProtocolVersion;
            return payload.ToJsonString();
        }
        catch (ServiceException exception)
        {
            BridgeLog.Write($"Service error [{exception.Code}]: {exception.Message}");
            return Failure(exception.Message, exception.Code, exception.Action, exception.Status);
        }
        catch (Exception exception)
        {
            BridgeLog.Write($"Command failed: {exception}");
            return Failure(exception.Message, "bridge_error");
        }
    }

    private static string Failure(string message, string? code, string? action = null, int? status = null)
    {
        var payload = new JsonObject
        {
            ["ok"] = false,
            ["protocol"] = XttsServiceHost.ProtocolVersion,
            ["error"] = message,
            ["code"] = code,
        };
        if (action != null)
        {
            payload["action"] = action;
        }

        if (status != null)
        {
            payload["status"] = status;
        }

        return payload.ToJsonString();
    }
}

internal sealed class BridgeAppService : IDisposable
{
    private const string ServiceName = "SVoice.XttsBridge";
    private readonly CommandDispatcher _dispatcher;
    private readonly TaskCompletionSource _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private AppServiceConnection? _connection;

    public BridgeAppService(CommandDispatcher dispatcher)
    {
        _dispatcher = dispatcher;
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

    private async void Connection_RequestReceived(AppServiceConnection sender, AppServiceRequestReceivedEventArgs args)
    {
        var deferral = args.GetDeferral();
        try
        {
            var requestText = args.Request.Message.TryGetValue("json", out var requestValue) ? requestValue as string : null;
            var responseText = string.IsNullOrWhiteSpace(requestText)
                ? """{"ok":false,"error":"Solicitação XTTS vazia.","code":"empty_request"}"""
                : await _dispatcher.HandleRequestAsync(requestText);
            await args.Request.SendResponseAsync(new ValueSet { ["json"] = responseText });
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

    private void Connection_ServiceClosed(AppServiceConnection sender, AppServiceClosedEventArgs args)
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
    private readonly CommandDispatcher _dispatcher;

    public BridgePipeServer(CommandDispatcher dispatcher)
    {
        _dispatcher = dispatcher;
    }

    public async Task RunAsync()
    {
        BridgeLog.Write("Bridge pipe server started.");
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
        security.AddAccessRule(new PipeAccessRule(currentUser, PipeAccessRights.FullControl, AccessControlType.Allow));

        var packageSid = PackageSid.TryDerive(GameBarPackageFamily);
        if (packageSid != null)
        {
            var packageRights = PipeAccessRights.ReadWrite | PipeAccessRights.ReadPermissions | PipeAccessRights.Synchronize;
            security.AddAccessRule(new PipeAccessRule(packageSid, packageRights, AccessControlType.Allow));
            security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier("S-1-15-2-1"), packageRights, AccessControlType.Allow));
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
            using var writer = new StreamWriter(pipe, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
            var requestText = await reader.ReadLineAsync();
            if (string.IsNullOrWhiteSpace(requestText) || requestText.Length > 1_000_000)
            {
                return;
            }

            await writer.WriteLineAsync(await _dispatcher.HandleRequestAsync(requestText));
        }
        catch (Exception exception)
        {
            BridgeLog.Write($"Pipe request failed: {exception}");
        }
    }
}
