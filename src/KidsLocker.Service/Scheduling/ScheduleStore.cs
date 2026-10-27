using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using KidsLocker.Service.Config;

namespace KidsLocker.Service.Scheduling;

/// <summary>
/// Authoritative in-memory schedule, mirrored to schedule.json under %ProgramData%\KidsLocker.
/// The service (enforcement loop + Telegram handler) is the only legitimate writer. Any edit
/// to the file made outside the app is detected via a content hash and reverted, so an admin
/// user can't silently flip "Enabled":false in Notepad. Thread-safe; writes are atomic.
/// </summary>
public sealed class ScheduleStore
{
    private readonly object _gate = new();
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    private Schedule? _current;
    private string _lastHash = "";
    private string _lastAlertedTamperHash = "";

    /// <summary>True when the last load found the schedule file present but unreadable/corrupt and
    /// fell back to blocking everything. The enforcement worker surfaces this to the parent.</summary>
    public bool ScheduleFileCorrupt { get; private set; }

    /// <summary>A defensive copy of the current schedule (safe to read/enumerate freely).</summary>
    public Schedule Get()
    {
        lock (_gate)
        {
            EnsureLoaded();
            return Clone(_current!);
        }
    }

    /// <summary>Back-compat alias used by the command handler.</summary>
    public Schedule Load() => Get();

    /// <summary>Mutate the authoritative schedule under lock and persist atomically.</summary>
    public Schedule Update(Action<Schedule> mutate)
    {
        lock (_gate)
        {
            EnsureLoaded();
            mutate(_current!);
            WriteToDisk(_current!);
            return Clone(_current!);
        }
    }

    public void Save(Schedule schedule)
    {
        lock (_gate)
        {
            _current = schedule;
            WriteToDisk(schedule);
        }
    }

    /// <summary>
    /// Compare the on-disk file against what we last wrote. If it differs (external edit or
    /// deletion), restore our authoritative copy and return true so the caller can alert.
    /// Legitimate edits (Telegram commands) go through <see cref="Update"/>/<see cref="Save"/>
    /// and update the baseline, so they never register as tampering.
    /// </summary>
    public bool SyncAndDetectTamper()
    {
        lock (_gate)
        {
            EnsureLoaded();
            string diskHash;
            try
            {
                diskHash = File.Exists(AppPaths.ScheduleFile)
                    ? Hash(File.ReadAllText(AppPaths.ScheduleFile))
                    : "";
            }
            catch
            {
                return false; // transient read error — don't cry wolf
            }

            if (diskHash == _lastHash)
                return false;

            // Revert to the authoritative copy. If the write fails (file locked, read-only, or a
            // directory put in its place), enforcement must NOT be affected: the loop evaluates the
            // in-memory authoritative schedule, not the on-disk file, so a failed revert can't disable
            // blocking. We only need to avoid throwing (which would abort the tick) and avoid alert spam.
            try
            {
                WriteToDisk(_current!);
                _lastAlertedTamperHash = "";
                return true;
            }
            catch
            {
                if (diskHash == _lastAlertedTamperHash)
                    return false; // already alerted for this exact tampered content
                _lastAlertedTamperHash = diskHash;
                return true;
            }
        }
    }

    private void EnsureLoaded()
    {
        if (_current is not null) return;

        if (File.Exists(AppPaths.ScheduleFile))
        {
            try
            {
                var json = File.ReadAllText(AppPaths.ScheduleFile);
                var s = JsonSerializer.Deserialize<Schedule>(json, JsonOpts);
                if (s is not null)
                {
                    // Explicit nulls in the JSON ("Allowed": null) would otherwise crash evaluation.
                    s.Allowed ??= new();
                    s.Extras ??= new();
                    _current = s;
                    _lastHash = Hash(json); // baseline = actual disk content
                    ScheduleFileCorrupt = false;
                    return;
                }
            }
            catch
            {
                // fall through to the fail-safe below
            }

            // The file exists but is unreadable/corrupt. Fail safe to BLOCKING EVERYTHING — never the
            // permissive default, which would silently relax enforcement — and do NOT overwrite the
            // file: the real schedule may be recoverable, and overwriting would destroy it.
            _current = BlockedAll();
            ScheduleFileCorrupt = true;
            try { _lastHash = File.Exists(AppPaths.ScheduleFile) ? Hash(File.ReadAllText(AppPaths.ScheduleFile)) : ""; }
            catch { _lastHash = ""; }
            return;
        }

        // First run, no file yet: start from the sensible default and persist it.
        _current = Schedule.Default();
        ScheduleFileCorrupt = false;
        WriteToDisk(_current);
    }

    /// <summary>A schedule that blocks the whole day (Enabled, no allowed intervals) — the fail-safe
    /// used when the on-disk schedule can't be read, so a corrupt file never relaxes enforcement.</summary>
    private static Schedule BlockedAll() => new() { Enabled = true };

    private void WriteToDisk(Schedule s)
    {
        AppPaths.EnsureRoot();
        var json = JsonSerializer.Serialize(s, JsonOpts);
        var tmp = AppPaths.ScheduleFile + ".tmp";
        File.WriteAllText(tmp, json);
        File.Move(tmp, AppPaths.ScheduleFile, overwrite: true);
        _lastHash = Hash(json);
    }

    private static Schedule Clone(Schedule s) =>
        JsonSerializer.Deserialize<Schedule>(JsonSerializer.Serialize(s, JsonOpts), JsonOpts)!;

    private static string Hash(string content) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content)));
}
