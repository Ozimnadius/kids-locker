using System.Security.AccessControl;
using System.Security.Principal;

namespace KidsLocker.Service.Setup;

internal static class RecoveryProtection
{
    internal static DirectorySecurity DirectoryPolicy()
    {
        var policy = new DirectorySecurity();
        policy.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        policy.SetOwner(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null));
        foreach (var sid in new[] { WellKnownSidType.LocalSystemSid, WellKnownSidType.BuiltinAdministratorsSid })
            policy.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(sid, null), FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        policy.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null),
            FileSystemRights.ReadAndExecute, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
            PropagationFlags.None, AccessControlType.Allow));
        return policy;
    }

    public static void SecureDirectory(string path)
    {
        RejectRedirect(path);
        Directory.CreateDirectory(path);
        RejectRedirect(path);
        // Полностью заменяем DACL и владельца, а не только разрешения известных групп.
        new DirectoryInfo(path).SetAccessControl(DirectoryPolicy());
    }

    public static void RejectRedirect(string path)
    {
        try
        {
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Папка KidsLocker не должна быть ссылкой или точкой подключения: " + path);
        }
        catch (FileNotFoundException) { }
        catch (DirectoryNotFoundException) { }
    }
}
