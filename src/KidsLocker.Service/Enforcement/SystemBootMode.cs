using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace KidsLocker.Service.Enforcement;

/// <summary>Detects whether Windows was booted into Safe Mode (GetSystemMetrics SM_CLEANBOOT).</summary>
[SupportedOSPlatform("windows")]
public static class SystemBootMode
{
    private const int SM_CLEANBOOT = 67;

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);

    public static bool IsSafeMode => GetSystemMetrics(SM_CLEANBOOT) != 0;

    public static string Describe => GetSystemMetrics(SM_CLEANBOOT) switch
    {
        1 => "безопасный режим",
        2 => "безопасный режим с сетью",
        _ => "обычный режим",
    };
}
