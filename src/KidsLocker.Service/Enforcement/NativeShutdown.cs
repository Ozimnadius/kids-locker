using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace KidsLocker.Service.Enforcement;

/// <summary>
/// Принудительное выключение ПК: сначала запуск shutdown.exe, а если он не стартует —
/// прямой P/Invoke как запасной путь. P/Invoke включает SeShutdownPrivilege у токена текущего
/// процесса (есть у аккаунта LocalSystem и у администратора) и зовёт InitiateSystemShutdownEx.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class NativeShutdown
{
    /// <summary>Немедленно и принудительно выключить ПК: сначала shutdown.exe, при неудаче его
    /// запуска — запасной P/Invoke. Общий путь выключения для службы (Enforcer) и кнопки
    /// «Выключить сейчас» в пульте, чтобы не дублировать вызов shutdown.exe в двух местах.</summary>
    public static void Force(string comment)
    {
        try
        {
            // /s — выключение, /f — принудительно закрыть приложения, /t 0 — немедленно, /c — комментарий.
            var psi = new ProcessStartInfo
            {
                FileName = "shutdown.exe",
                Arguments = $"/s /f /t 0 /c \"{comment}\"",
                CreateNoWindow = true,
                UseShellExecute = false,
            };
            Process.Start(psi);
        }
        catch
        {
            TryForce();
        }
    }

    public static bool TryForce()
    {
        try
        {
            EnableShutdownPrivilege();
            // machineName null = локально, message null, таймаут 0, закрыть приложения принудительно, без перезагрузки.
            return InitiateSystemShutdownEx(null, null, 0, bForceAppsClosed: true, bRebootAfterShutdown: false, dwReason: 0);
        }
        catch
        {
            return false;
        }
    }

    private static void EnableShutdownPrivilege()
    {
        if (!OpenProcessToken(GetCurrentProcess(), TOKEN_ADJUST_PRIVILEGES | TOKEN_QUERY, out var token))
            return;
        try
        {
            if (!LookupPrivilegeValue(null, SE_SHUTDOWN_NAME, out var luid))
                return;

            var tp = new TOKEN_PRIVILEGES
            {
                PrivilegeCount = 1,
                Luid = luid,
                Attributes = SE_PRIVILEGE_ENABLED,
            };
            AdjustTokenPrivileges(token, false, ref tp, 0, IntPtr.Zero, IntPtr.Zero);
        }
        finally
        {
            CloseHandle(token);
        }
    }

    private const string SE_SHUTDOWN_NAME = "SeShutdownPrivilege";
    private const uint SE_PRIVILEGE_ENABLED = 0x00000002;
    private const uint TOKEN_ADJUST_PRIVILEGES = 0x0020;
    private const uint TOKEN_QUERY = 0x0008;

    [StructLayout(LayoutKind.Sequential)]
    private struct LUID { public uint LowPart; public int HighPart; }

    [StructLayout(LayoutKind.Sequential)]
    private struct TOKEN_PRIVILEGES
    {
        public uint PrivilegeCount;
        public LUID Luid;
        public uint Attributes;
    }

    [DllImport("kernel32.dll")] private static extern IntPtr GetCurrentProcess();
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool CloseHandle(IntPtr h);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(IntPtr processHandle, uint desiredAccess, out IntPtr tokenHandle);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool LookupPrivilegeValue(string? host, string name, out LUID luid);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool AdjustTokenPrivileges(IntPtr tokenHandle, bool disableAll,
        ref TOKEN_PRIVILEGES newState, uint bufferLength, IntPtr prevState, IntPtr returnLength);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool InitiateSystemShutdownEx(string? machineName, string? message,
        uint timeout, bool bForceAppsClosed, bool bRebootAfterShutdown, uint dwReason);
}
