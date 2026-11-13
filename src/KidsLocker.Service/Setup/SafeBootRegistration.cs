using Microsoft.Win32;

namespace KidsLocker.Service.Setup;

/// <summary>Регистрация проверяется по значению, а не только по существованию ключа.</summary>
internal static class SafeBootRegistration
{
    private static string PathFor(string mode) => $@"SYSTEM\CurrentControlSet\Control\SafeBoot\{mode}\{Installer.ServiceName}";

    public static string? Read(string mode)
    {
        using var key = Registry.LocalMachine.OpenSubKey(PathFor(mode));
        return key?.GetValue(null) as string;
    }

    public static void Register() => Ensure(Read, mode =>
    {
        using var key = Registry.LocalMachine.CreateSubKey(PathFor(mode), writable: true);
        key.SetValue("", "Service", RegistryValueKind.String);
    });

    internal static void Ensure(Func<string, string?> read, Action<string> write)
    {
        foreach (var mode in new[] { "Minimal", "Network" })
        {
            if (string.Equals(read(mode), "Service", StringComparison.OrdinalIgnoreCase)) continue;
            write(mode);
            if (!string.Equals(read(mode), "Service", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"Не удалось включить службу в безопасном режиме ({mode}).");
        }
    }
}
