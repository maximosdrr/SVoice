using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json.Nodes;
using Microsoft.Win32;
using Windows.Devices.Enumeration;
using Windows.Media.Devices;

namespace SVoice.Setup;

/// <summary>
/// VB-CABLE (VB-Audio Software, donationware — https://vb-cable.com) detection
/// and installation using only the official, signed <c>VBCABLE_Setup_x64.exe</c>.
/// </summary>
internal static class VbCable
{
    public const string Origin = "https://vb-cable.com";
    public const string DonationUrl = "https://vb-audio.com/Cable/";
    private const string ServiceKey = @"SYSTEM\CurrentControlSet\Services\VBAudioVACMME";
    private const string VendorKey = @"SOFTWARE\VB-Audio\Cable";
    private const string SetupFileName = "VBCABLE_Setup_x64.exe";
    // The official package is signed by the author of VB-Audio Software.
    private static readonly string[] ExpectedSigners = { "VB-Audio", "BUREL VINCENT" };

    internal sealed record State(bool ServiceRegistered, bool InputPresent, bool OutputPresent, bool RebootPending, string? DriverVersion)
    {
        public bool DevicesReady => InputPresent && OutputPresent;

        public string Summary => this switch
        {
            { DevicesReady: true } => "instalado e ativo (CABLE Input e CABLE Output disponíveis)",
            { ServiceRegistered: true, RebootPending: true } => "instalado; aguardando reinicialização do Windows",
            { ServiceRegistered: true } => "driver registrado, mas os dispositivos CABLE Input/Output não estão disponíveis (reparo recomendado)",
            _ => "não instalado",
        };

        public JsonNode ToJson() => new JsonObject
        {
            ["service_registered"] = ServiceRegistered,
            ["input_present"] = InputPresent,
            ["output_present"] = OutputPresent,
            ["reboot_pending"] = RebootPending,
            ["driver_version"] = DriverVersion,
            ["summary"] = Summary,
        };
    }

    public static async Task<CommandResult> RunAsync(Options options)
    {
        var before = await DetectAsync();
        var result = CommandResult.Success($"VB-CABLE: {before.Summary}").With("before", before.ToJson());
        result.WithLine($"VB-CABLE é um software da VB-Audio Software ({Origin}), distribuído como donationware. Contribuições: {DonationUrl}");
        result.WithLine($"Estado atual: {before.Summary}");

        if (options.Get("source") is { } sourceDir)
        {
            var setupPath = Path.Combine(Path.GetFullPath(sourceDir), SetupFileName);
            var signatureIssue = File.Exists(setupPath) ? VerifySignature(setupPath) : "arquivo não encontrado";
            result.WithLine(signatureIssue == null
                ? $"Pacote oficial: {setupPath} (assinatura Authenticode válida: VB-Audio Software / Vincent Burel)"
                : $"Pacote oficial: {setupPath} — {signatureIssue}");
            result.With("signature_valid", signatureIssue == null);
        }

        var shouldInstall = options.Has("install") && (!before.DevicesReady || options.Has("force"));
        if (!shouldInstall)
        {
            if (before.DevicesReady)
            {
                return result;
            }

            if (before.ServiceRegistered && before.RebootPending)
            {
                return new CommandResult { Ok = true, ExitCode = Program.ExitRebootRequired, Message = "O VB-CABLE já foi instalado; reinicie o Windows para ativar os dispositivos." }
                    .With("before", before.ToJson());
            }

            return CommandResult.Fail($"VB-CABLE: {before.Summary}", Program.ExitBlocked).With("before", before.ToJson());
        }

        if (!Program.IsElevated())
        {
            return CommandResult.Fail("A instalação do VB-CABLE exige privilégios de administrador.");
        }

        var source = Path.GetFullPath(options.Require("source"));
        var setup = Path.Combine(source, SetupFileName);
        if (!File.Exists(setup))
        {
            return CommandResult.Fail($"Pacote oficial do VB-CABLE não encontrado: {setup}");
        }

        var signature = VerifySignature(setup);
        if (signature != null)
        {
            return CommandResult.Fail($"A assinatura do instalador oficial do VB-CABLE não pôde ser confirmada: {signature}");
        }

        Status.Progress(before.ServiceRegistered ? "Reparando o driver VB-CABLE…" : "Instalando o driver VB-CABLE…", 0.3);
        Log.Write($"Running {setup} -i -h");
        var (exitCode, output) = ProcessRunner.Run(setup, new[] { "-i", "-h" }, TimeSpan.FromMinutes(5), source);
        Log.Write($"VBCABLE_Setup_x64 exit {exitCode}: {output.Trim()}");
        result.With("setup_exit_code", exitCode);

        var after = await DetectAsync();
        result.With("after", after.ToJson());
        result.WithLine($"Estado após a instalação: {after.Summary}");
        if (after.DevicesReady)
        {
            return CommandResult.Success("VB-CABLE instalado: CABLE Input e CABLE Output disponíveis.").With("after", after.ToJson()).With("setup_exit_code", exitCode);
        }

        if (after.ServiceRegistered)
        {
            return new CommandResult { Ok = true, ExitCode = Program.ExitRebootRequired, Message = "VB-CABLE instalado. Reinicie o Windows para ativar CABLE Input e CABLE Output." }
                .With("after", after.ToJson()).With("setup_exit_code", exitCode);
        }

        return CommandResult.Fail($"O instalador do VB-CABLE terminou com código {exitCode}, mas o driver não foi registrado. Execute o reparo do SVoice ou instale o VB-CABLE manualmente a partir de {Origin}.")
            .With("after", after.ToJson()).With("setup_exit_code", exitCode);
    }

    public static async Task<State> DetectAsync()
    {
        var serviceRegistered = Registry.LocalMachine.OpenSubKey(ServiceKey) != null || Registry.LocalMachine.OpenSubKey(VendorKey) != null;
        string? version = null;
        using (var vendor = Registry.LocalMachine.OpenSubKey(VendorKey))
        {
            version = vendor?.GetValue("Version") as string ?? vendor?.GetValue("DisplayVersion") as string;
        }

        var input = false;
        var output = false;
        try
        {
            var renderers = await DeviceInformation.FindAllAsync(MediaDevice.GetAudioRenderSelector());
            input = renderers.Any(device => device.Name.Contains("CABLE Input", StringComparison.OrdinalIgnoreCase));
            var captures = await DeviceInformation.FindAllAsync(MediaDevice.GetAudioCaptureSelector());
            output = captures.Any(device => device.Name.Contains("CABLE Output", StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception exception)
        {
            Log.Write($"Audio device enumeration failed: {exception.Message}");
        }

        return new State(serviceRegistered, input, output, IsRebootPending(), version);
    }

    public static bool IsRebootPending()
    {
        try
        {
            using var cbs = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing\RebootPending");
            if (cbs != null)
            {
                return true;
            }

            using var update = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\RebootRequired");
            if (update != null)
            {
                return true;
            }

            using var session = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Session Manager");
            if (session?.GetValue("PendingFileRenameOperations") is string[] { Length: > 0 })
            {
                return true;
            }
        }
        catch
        {
            // Treat as not pending.
        }

        return false;
    }

    /// <summary>Returns null when the file carries a valid Authenticode signature from VB-Audio.</summary>
    public static string? VerifySignature(string path)
    {
        try
        {
            var signer = new X509Certificate2(X509Certificate.CreateFromSignedFile(path));
            if (!ExpectedSigners.Any(expected => signer.Subject.Contains(expected, StringComparison.OrdinalIgnoreCase)))
            {
                return $"assinado por '{signer.Subject}', esperado VB-Audio Software (Vincent Burel)";
            }
        }
        catch (Exception exception)
        {
            return $"arquivo sem assinatura Authenticode ({exception.Message})";
        }

        var status = WinTrust.VerifyEmbeddedSignature(path);
        return status == 0 ? null : $"WinVerifyTrust retornou 0x{status:X8}";
    }
}

internal static class WinTrust
{
    private static readonly Guid ActionGenericVerifyV2 = new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WinTrustFileInfo
    {
        public uint cbStruct;
        public IntPtr pcwszFilePath;
        public IntPtr hFile;
        public IntPtr pgKnownSubject;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WinTrustData
    {
        public uint cbStruct;
        public IntPtr pPolicyCallbackData;
        public IntPtr pSIPClientData;
        public uint dwUIChoice;
        public uint fdwRevocationChecks;
        public uint dwUnionChoice;
        public IntPtr pFile;
        public uint dwStateAction;
        public IntPtr hWVTStateData;
        public IntPtr pwszURLReference;
        public uint dwProvFlags;
        public uint dwUIContext;
        public IntPtr pSignatureSettings;
    }

    [DllImport("wintrust.dll", ExactSpelling = true, SetLastError = false, CharSet = CharSet.Unicode)]
    private static extern int WinVerifyTrust(IntPtr hwnd, [MarshalAs(UnmanagedType.LPStruct)] Guid actionId, IntPtr data);

    public static int VerifyEmbeddedSignature(string path)
    {
        var fileInfo = new WinTrustFileInfo
        {
            cbStruct = (uint)Marshal.SizeOf<WinTrustFileInfo>(),
            pcwszFilePath = Marshal.StringToCoTaskMemUni(path),
        };
        var fileInfoPointer = Marshal.AllocCoTaskMem(Marshal.SizeOf<WinTrustFileInfo>());
        Marshal.StructureToPtr(fileInfo, fileInfoPointer, false);
        var data = new WinTrustData
        {
            cbStruct = (uint)Marshal.SizeOf<WinTrustData>(),
            dwUIChoice = 2, // WTD_UI_NONE
            fdwRevocationChecks = 0, // WTD_REVOKE_NONE
            dwUnionChoice = 1, // WTD_CHOICE_FILE
            pFile = fileInfoPointer,
            dwStateAction = 0, // WTD_STATEACTION_IGNORE
            dwProvFlags = 0x00000010, // WTD_REVOCATION_CHECK_NONE
        };
        var dataPointer = Marshal.AllocCoTaskMem(Marshal.SizeOf<WinTrustData>());
        Marshal.StructureToPtr(data, dataPointer, false);
        try
        {
            return WinVerifyTrust(IntPtr.Zero, ActionGenericVerifyV2, dataPointer);
        }
        finally
        {
            Marshal.FreeCoTaskMem(fileInfo.pcwszFilePath);
            Marshal.FreeCoTaskMem(fileInfoPointer);
            Marshal.FreeCoTaskMem(dataPointer);
        }
    }
}
