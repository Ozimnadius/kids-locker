using KidsLocker.Service.Config;

namespace KidsLocker.Service.Enforcement;

/// <summary>
/// A short-lived marker written by the app just before it deliberately stops the service
/// (schedule save, reinstall, uninstall). The service's stop-alert consumes it to avoid
/// crying "someone stopped the control!" when the stop was our own doing.
/// </summary>
public static class StopMarker
{
    public static void WriteExpected()
    {
        try
        {
            AppPaths.EnsureRoot();
            File.WriteAllText(AppPaths.ExpectedStopFile, DateTime.UtcNow.Ticks.ToString());
        }
        catch { /* best effort */ }
    }

    /// <summary>True if a fresh (&lt; 60s) expected-stop marker exists; deletes it either way.</summary>
    public static bool ConsumeIsExpected()
    {
        try
        {
            if (!File.Exists(AppPaths.ExpectedStopFile)) return false;
            var txt = File.ReadAllText(AppPaths.ExpectedStopFile);
            File.Delete(AppPaths.ExpectedStopFile);
            if (long.TryParse(txt, out var ticks))
            {
                var age = DateTime.UtcNow - new DateTime(ticks, DateTimeKind.Utc);
                return age >= TimeSpan.Zero && age < TimeSpan.FromSeconds(60);
            }
        }
        catch { /* treat as unexpected */ }
        return false;
    }
}
