using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace KidsLocker.Service.Enforcement;

/// <summary>
/// Узнаёт, кто сейчас сидит за физической консолью, чтобы алерты могли сказать
/// «вошёл как &lt;пользователь&gt;», а не просто «заблокировано». По возможности: возвращает null, если
/// сведения недоступны (например, на экране входа, когда активного пользователя нет).
/// </summary>
[SupportedOSPlatform("windows")]
public static class SessionQuery
{
    public static string? ActiveConsoleUser()
    {
        try
        {
            var sessionId = WTSGetActiveConsoleSessionId();
            if (sessionId == 0xFFFFFFFF) return null;

            var user = Query(sessionId, WTS_INFO_CLASS.WTSUserName);
            if (string.IsNullOrEmpty(user)) return null;

            var domain = Query(sessionId, WTS_INFO_CLASS.WTSDomainName);
            return string.IsNullOrEmpty(domain) ? user : $"{domain}\\{user}";
        }
        catch
        {
            return null;
        }
    }

    private static string? Query(uint sessionId, WTS_INFO_CLASS info)
    {
        if (!WTSQuerySessionInformation(IntPtr.Zero, sessionId, info, out var buffer, out var bytes))
            return null;
        try
        {
            return bytes <= 2 ? null : Marshal.PtrToStringUni(buffer);
        }
        finally
        {
            WTSFreeMemory(buffer);
        }
    }

    private enum WTS_INFO_CLASS { WTSUserName = 5, WTSDomainName = 7 }

    [DllImport("kernel32.dll")] private static extern uint WTSGetActiveConsoleSessionId();

    [DllImport("wtsapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool WTSQuerySessionInformation(IntPtr server, uint sessionId,
        WTS_INFO_CLASS infoClass, out IntPtr buffer, out uint bytesReturned);

    [DllImport("wtsapi32.dll")] private static extern void WTSFreeMemory(IntPtr memory);
}
