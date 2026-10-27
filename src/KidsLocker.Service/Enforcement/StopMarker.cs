using KidsLocker.Service.Config;

namespace KidsLocker.Service.Enforcement;

/// <summary>
/// Короткоживущая метка, которую приложение пишет прямо перед тем, как само намеренно остановить
/// службу (сохранение расписания, переустановка, удаление). Алерт службы об остановке забирает её,
/// чтобы не кричать «кто-то остановил контроль!», когда остановка была нашей собственной.
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
        catch { /* по возможности */ }
    }

    /// <summary>True, если есть свежая (&lt; 60 с) метка ожидаемой остановки; удаляет её в любом случае.</summary>
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
        catch { /* считаем остановку неожиданной */ }
        return false;
    }
}
