using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace SVoice.GameBarBridge;

internal static class KnownFolders
{
    private static readonly Guid LocalAppDataId = new("F1B32785-6FBA-4FCF-9D55-7B8E7F157091");
    private const uint NoPackageRedirection = 0x00010000;
    private const uint DontVerify = 0x00004000;

    public static string UnredirectedLocalAppData { get; } = ResolveUnredirectedLocalAppData();

    private static string ResolveUnredirectedLocalAppData()
    {
        var folderId = LocalAppDataId;
        var result = SHGetKnownFolderPath(ref folderId, NoPackageRedirection | DontVerify, IntPtr.Zero, out var pathPointer);
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
    private static extern int SHGetKnownFolderPath(ref Guid folderId, uint flags, IntPtr token, out IntPtr path);
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
    private static extern int DeriveAppContainerSidFromAppContainerName(string appContainerName, out IntPtr appContainerSid);

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
        if (!ConvertStringSecurityDescriptorToSecurityDescriptor("S:(ML;;NW;;;LW)", SddlRevision1, out var securityDescriptor, out _))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        try
        {
            if (!GetSecurityDescriptorSacl(securityDescriptor, out var saclPresent, out var sacl, out _) || !saclPresent || sacl == IntPtr.Zero)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            var result = SetSecurityInfo(pipeHandle.DangerousGetHandle(), SeKernelObject, LabelSecurityInformation, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, sacl);
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
    private static extern bool ConvertStringSecurityDescriptorToSecurityDescriptor(string stringSecurityDescriptor, uint stringSdRevision, out IntPtr securityDescriptor, out uint securityDescriptorSize);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool GetSecurityDescriptorSacl(IntPtr securityDescriptor, out bool saclPresent, out IntPtr sacl, out bool saclDefaulted);

    [DllImport("advapi32.dll")]
    private static extern uint SetSecurityInfo(IntPtr handle, int objectType, uint securityInformation, IntPtr owner, IntPtr group, IntPtr dacl, IntPtr sacl);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);
}

internal static class BridgeLog
{
    private const long MaxBytes = 2 * 1024 * 1024;
    private const int Backups = 3;
    private static readonly object Sync = new();

    public static string Directory { get; } = Path.Combine(KnownFolders.UnredirectedLocalAppData, "SVoice", "Logs");

    public static string FilePath => Path.Combine(Directory, "gamebar-bridge.log");

    public static void Write(string message)
    {
        try
        {
            System.IO.Directory.CreateDirectory(Directory);
            lock (Sync)
            {
                Rotate();
                File.AppendAllText(FilePath, $"{DateTimeOffset.Now:O} {message}{Environment.NewLine}");
            }
        }
        catch
        {
            // Logging must not stop the bridge.
        }
    }

    private static void Rotate()
    {
        var info = new FileInfo(FilePath);
        if (!info.Exists || info.Length < MaxBytes)
        {
            return;
        }

        for (var index = Backups - 1; index >= 1; index--)
        {
            var older = $"{FilePath}.{index}";
            var newer = $"{FilePath}.{index + 1}";
            if (File.Exists(older))
            {
                File.Move(older, newer, overwrite: true);
            }
        }

        File.Move(FilePath, $"{FilePath}.1", overwrite: true);
    }
}
