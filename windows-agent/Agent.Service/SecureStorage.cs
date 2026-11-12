using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using KidSafe.Agent.Core;

namespace KidSafe.Agent.Service;

/// <summary>DPAPI at machine scope: only code on this machine can decrypt; file ACL restricts who can read it at all.</summary>
public sealed class DpapiProtector : IProtector
{
    private static readonly byte[] Entropy = "KidSafeClone.State.v1"u8.ToArray();
    public byte[] Protect(byte[] data) => ProtectedData.Protect(data, Entropy, DataProtectionScope.LocalMachine);
    public byte[] Unprotect(byte[] data) => ProtectedData.Unprotect(data, Entropy, DataProtectionScope.LocalMachine);
}

public static class SecurePaths
{
    private static SecurityIdentifier System => new(WellKnownSidType.LocalSystemSid, null);
    private static SecurityIdentifier Admins => new(WellKnownSidType.BuiltinAdministratorsSid, null);
    private static SecurityIdentifier Interactive => new(WellKnownSidType.InteractiveSid, null);

    /// <summary>%ProgramData%\KidSafeClone, writable only by SYSTEM and Administrators.</summary>
    public static string EnsureDataDir()
    {
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "KidSafeClone");
        var sec = new DirectorySecurity();
        sec.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        foreach (var sid in new[] { System, Admins })
            sec.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        if (!Directory.Exists(dir)) new DirectoryInfo(dir).Create(sec);
        else new DirectoryInfo(dir).SetAccessControl(sec);
        return dir;
    }

    /// <summary>Pipe: SYSTEM/Admins full control; interactive users may read/write (Helper runs as the child).</summary>
    public static NamedPipeServerStream CreatePipe()
    {
        var sec = new PipeSecurity();
        sec.AddAccessRule(new PipeAccessRule(System, PipeAccessRights.FullControl, AccessControlType.Allow));
        sec.AddAccessRule(new PipeAccessRule(Admins, PipeAccessRights.FullControl, AccessControlType.Allow));
        sec.AddAccessRule(new PipeAccessRule(Interactive, PipeAccessRights.ReadWrite, AccessControlType.Allow));
        return NamedPipeServerStreamAcl.Create(IpcNames.PipeName, PipeDirection.InOut,
            NamedPipeServerStream.MaxAllowedServerInstances, PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 0, 0, sec);
    }
}
