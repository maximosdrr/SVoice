using System.Security.Cryptography.X509Certificates;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using Windows.ApplicationModel.AppExtensions;
using Windows.Management.Deployment;

namespace SVoice.Setup;

internal static class Msix
{
    public const string PackageName = "SVoice.GameBar";
    public const string PackageFamily = "SVoice.GameBar_61qvngw278t1j";
    public const string WidgetExtensionId = "SVoiceWidget";
    private const string GameBarExtensionName = "microsoft.gameBarUIExtension";
    private const string AppUserModelId = PackageFamily + "!App";
    private const string StartupProbePassed = "Startup probe passed.";
    private const string StartupProbeFailed = "Startup probe failed:";

    public static CommandResult InstallCertificate(Options options)
    {
        var path = options.Require("cer");
        if (!Program.IsElevated())
        {
            return CommandResult.Fail("A instalação do certificado exige privilégios de administrador.");
        }

        var certificate = new X509Certificate2(path);
        using var store = new X509Store(StoreName.TrustedPeople, StoreLocation.LocalMachine);
        store.Open(OpenFlags.ReadWrite);
        var existing = store.Certificates.Find(X509FindType.FindByThumbprint, certificate.Thumbprint, validOnly: false);
        if (existing.Count == 0)
        {
            store.Add(certificate);
            Log.Write($"Certificate {certificate.Thumbprint} added to TrustedPeople.");
            return CommandResult.Success($"Certificado {certificate.Subject} instalado em Pessoas Confiáveis.").With("thumbprint", certificate.Thumbprint);
        }

        return CommandResult.Success($"Certificado {certificate.Subject} já é confiável.").With("thumbprint", certificate.Thumbprint);
    }

    public static async Task<CommandResult> InstallPackageAsync(Options options)
    {
        var msixPath = Path.GetFullPath(options.Require("msix"));
        if (!File.Exists(msixPath))
        {
            return CommandResult.Fail($"Pacote não encontrado: {msixPath}");
        }

        var cerPath = options.Get("cer");
        if (cerPath != null)
        {
            var certificate = new X509Certificate2(cerPath);
            using var store = new X509Store(StoreName.TrustedPeople, StoreLocation.LocalMachine);
            store.Open(OpenFlags.ReadOnly);
            if (store.Certificates.Find(X509FindType.FindByThumbprint, certificate.Thumbprint, false).Count == 0)
            {
                return CommandResult.Fail("O certificado do widget ainda não é confiável; execute install-cert como administrador.");
            }

            var signer = X509Certificate.CreateFromSignedFile(msixPath);
            if (!string.Equals(signer.GetCertHashString(), certificate.Thumbprint, StringComparison.OrdinalIgnoreCase))
            {
                return CommandResult.Fail("A assinatura do pacote MSIX não corresponde ao certificado do SVoice.");
            }
        }

        var manager = new PackageManager();
        var existing = manager.FindPackagesForUser(string.Empty, PackageFamily).ToList();
        var previous = existing.FirstOrDefault()?.Id.Version;
        Status.Progress(previous != null
            ? $"Atualizando o widget (versão instalada {FormatVersion(previous.Value)})…"
            : "Instalando o widget da Xbox Game Bar…", 0.2);

        // Development registrations (loose files) cannot be upgraded in place.
        foreach (var package in existing.Where(package => package.IsDevelopmentMode))
        {
            Log.Write($"Removing development registration {package.Id.FullName}.");
            var removal = await manager.RemovePackageAsync(package.Id.FullName, RemovalOptions.PreserveApplicationData);
            if (removal.ExtendedErrorCode != null)
            {
                return CommandResult.Fail($"Não foi possível remover o registro de desenvolvimento: {removal.ErrorText}");
            }
        }

        var deployment = await manager.AddPackageAsync(
            new Uri(msixPath),
            null,
            DeploymentOptions.ForceApplicationShutdown | DeploymentOptions.ForceUpdateFromAnyVersion);
        if (deployment.ExtendedErrorCode != null)
        {
            Log.Write($"AddPackageAsync failed: {deployment.ErrorText} ({deployment.ExtendedErrorCode})");
            return CommandResult.Fail($"Falha ao instalar o widget: {deployment.ErrorText}");
        }

        var installed = manager.FindPackagesForUser(string.Empty, PackageFamily).FirstOrDefault();
        var version = installed != null ? FormatVersion(installed.Id.Version) : "?";
        Status.Progress("Verificando o widget no catálogo da Game Bar…", 0.8);
        var registered = await IsWidgetRegisteredAsync();
        if (!registered)
        {
            return CommandResult.Fail("O pacote foi instalado, mas o widget SVoice não apareceu no catálogo da Xbox Game Bar.")
                .With("version", version);
        }

        var startupProbe = await ProbeStartupAsync();
        if (!startupProbe.Ok)
        {
            return CommandResult.Fail($"O widget foi registrado, mas não conseguiu inicializar: {startupProbe.Detail}")
                .With("version", version)
                .With("startup_probe", startupProbe.Detail);
        }

        Log.Write($"Widget installed: {installed?.Id.FullName}");
        return CommandResult.Success($"Widget SVoice {version} instalado e registrado na Xbox Game Bar.")
            .With("version", version)
            .With("startup_probe", startupProbe.Detail)
            .With("previous_version", previous != null ? FormatVersion(previous.Value) : null);
    }

    public static async Task<CommandResult> RemovePackageAsync(Options options)
    {
        var manager = new PackageManager();
        var packages = manager.FindPackagesForUser(string.Empty, PackageFamily).ToList();
        if (packages.Count == 0)
        {
            return CommandResult.Success("O widget não está instalado.");
        }

        foreach (var package in packages)
        {
            var removal = await manager.RemovePackageAsync(package.Id.FullName);
            if (removal.ExtendedErrorCode != null)
            {
                return CommandResult.Fail($"Não foi possível remover {package.Id.FullName}: {removal.ErrorText}");
            }

            Log.Write($"Widget removed: {package.Id.FullName}");
        }

        return CommandResult.Success("Widget SVoice removido.");
    }

    public static async Task<CommandResult> VerifyWidgetAsync(Options options)
    {
        var manager = new PackageManager();
        var package = manager.FindPackagesForUser(string.Empty, PackageFamily).FirstOrDefault();
        if (package == null)
        {
            return CommandResult.Fail("O widget SVoice não está instalado.");
        }

        var registered = await IsWidgetRegisteredAsync();
        var version = FormatVersion(package.Id.Version);
        return registered
            ? CommandResult.Success($"Widget SVoice {version} disponível no menu de widgets da Xbox Game Bar.").With("version", version)
            : CommandResult.Fail($"O widget SVoice {version} está instalado, mas não consta no catálogo da Game Bar.").With("version", version);
    }

    public static async Task<bool> IsWidgetRegisteredAsync()
    {
        try
        {
            var catalog = AppExtensionCatalog.Open(GameBarExtensionName);
            var extensions = await catalog.FindAllAsync();
            return extensions.Any(extension => extension.Id == WidgetExtensionId && extension.Package.Id.FamilyName == PackageFamily);
        }
        catch (Exception exception)
        {
            Log.Write($"AppExtensionCatalog lookup failed: {exception.Message}");
            return false;
        }
    }

    public static async Task<(bool Ok, string Detail)> ProbeStartupAsync()
    {
        var logPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Packages",
            PackageFamily,
            "LocalState",
            "gamebar.log");
        var initialLength = File.Exists(logPath) ? new FileInfo(logPath).Length : 0L;
        uint processId = 0;

        try
        {
            var manager = (IApplicationActivationManager)new ApplicationActivationManager();
            var result = manager.ActivateApplication(
                AppUserModelId,
                "--startup-probe",
                ActivateOptions.NoErrorUI | ActivateOptions.NoSplashScreen,
                out processId);
            if (result < 0)
            {
                return (false, $"ativação retornou 0x{result:X8}");
            }

            var deadline = DateTime.UtcNow.AddSeconds(30);
            while (DateTime.UtcNow < deadline)
            {
                var appended = ReadLogTail(logPath, initialLength);
                if (appended.Contains(StartupProbePassed, StringComparison.Ordinal))
                {
                    return (true, "runtime e XAML inicializados");
                }

                var failure = appended.IndexOf(StartupProbeFailed, StringComparison.Ordinal);
                if (failure >= 0)
                {
                    var detail = appended[(failure + StartupProbeFailed.Length)..].Trim();
                    return (false, string.IsNullOrWhiteSpace(detail) ? "falha no carregamento do XAML" : detail);
                }

                await Task.Delay(250);
            }

            return (false, "a ativação não confirmou a inicialização em 30 segundos; verifique o runtime e o log do widget");
        }
        catch (Exception exception)
        {
            return (false, exception.Message);
        }
        finally
        {
            if (processId != 0)
            {
                try
                {
                    using var process = System.Diagnostics.Process.GetProcessById((int)processId);
                    if (!process.HasExited)
                    {
                        process.Kill(entireProcessTree: true);
                    }
                }
                catch
                {
                    // The probe normally exits on its own.
                }
            }
        }
    }

    private static string ReadLogTail(string path, long initialLength)
    {
        try
        {
            if (!File.Exists(path))
            {
                return string.Empty;
            }

            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (initialLength > stream.Length)
            {
                initialLength = 0;
            }

            stream.Seek(initialLength, SeekOrigin.Begin);
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }
        catch
        {
            return string.Empty;
        }
    }

    public static string? InstalledVersion()
    {
        try
        {
            var package = new PackageManager().FindPackagesForUser(string.Empty, PackageFamily).FirstOrDefault();
            return package == null ? null : FormatVersion(package.Id.Version);
        }
        catch
        {
            return null;
        }
    }

    private static string FormatVersion(Windows.ApplicationModel.PackageVersion version)
    {
        return $"{version.Major}.{version.Minor}.{version.Build}.{version.Revision}";
    }

    [Flags]
    private enum ActivateOptions
    {
        None = 0,
        DesignMode = 1,
        NoErrorUI = 2,
        NoSplashScreen = 4,
    }

    [ComImport]
    [Guid("45BA127D-10A8-46EA-8AB7-56EA9078943C")]
    private class ApplicationActivationManager
    {
    }

    [ComImport]
    [Guid("2E941141-7F97-4756-BA1D-9DECDE894A3D")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IApplicationActivationManager
    {
        [PreserveSig]
        int ActivateApplication(
            [MarshalAs(UnmanagedType.LPWStr)] string appUserModelId,
            [MarshalAs(UnmanagedType.LPWStr)] string arguments,
            ActivateOptions options,
            out uint processId);

        [PreserveSig]
        int ActivateForFile(IntPtr appUserModelId, IntPtr itemArray, IntPtr verb, out uint processId);

        [PreserveSig]
        int ActivateForProtocol(IntPtr appUserModelId, IntPtr itemArray, out uint processId);
    }
}
