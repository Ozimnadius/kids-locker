using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using KidsLocker.Service.Config;

namespace KidsLocker.Service.Scheduling;

/// <summary>
/// Эталонное расписание в памяти, зеркалируемое в schedule.json в %ProgramData%\KidsLocker.
/// Служба (цикл контроля + обработчик Telegram) — единственный законный писатель. Любая правка
/// файла в обход приложения обнаруживается по хэшу содержимого и откатывается, так что
/// пользователь-администратор не может тихо выставить "Enabled":false в Блокноте. Потокобезопасно; запись атомарная.
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

    /// <summary>True, если при последней загрузке файл расписания был, но не читался/был повреждён,
    /// и пришлось откатиться к блокировке всего. Цикл контроля сообщает об этом родителю.</summary>
    public bool ScheduleFileCorrupt { get; private set; }

    /// <summary>Защитная копия текущего расписания (её можно свободно читать и перебирать).</summary>
    public Schedule Get()
    {
        lock (_gate)
        {
            EnsureLoaded();
            return Clone(_current!);
        }
    }

    /// <summary>Псевдоним для обратной совместимости, используется обработчиком команд.</summary>
    public Schedule Load() => Get();

    /// <summary>Изменить эталонное расписание под блокировкой и атомарно сохранить.</summary>
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
    /// Сравнить файл на диске с тем, что мы записали последним. Если отличается (правка извне
    /// или удаление), восстановить эталонную копию и вернуть true, чтобы вызывающий поднял алерт.
    /// Законные правки (команды Telegram) идут через <see cref="Update"/>/<see cref="Save"/>
    /// и обновляют точку отсчёта, поэтому никогда не считаются подменой.
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
                return false; // временная ошибка чтения — не поднимаем ложную тревогу
            }

            if (diskHash == _lastHash)
                return false;

            // Откатываем к эталонной копии. Если запись не удалась (файл заблокирован, только для чтения
            // или на его месте каталог), контроль это затрагивать НЕ должно: цикл оценивает эталонное
            // расписание в памяти, а не файл на диске, так что неудачный откат не может отключить
            // блокировку. Нужно лишь не бросать исключение (оно прервало бы такт) и не спамить алертами.
            try
            {
                WriteToDisk(_current!);
                _lastAlertedTamperHash = "";
                return true;
            }
            catch
            {
                if (diskHash == _lastAlertedTamperHash)
                    return false; // об этом самом подменённом содержимом уже предупреждали
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
                    // Явные null в JSON ("Allowed": null) иначе обрушили бы оценку расписания.
                    s.Allowed ??= new();
                    s.Extras ??= new();
                    _current = s;
                    _lastHash = Hash(json); // точка отсчёта = фактическое содержимое на диске
                    ScheduleFileCorrupt = false;
                    return;
                }
            }
            catch
            {
                // проваливаемся в безопасный вариант ниже
            }

            // Файл есть, но не читается/повреждён. Безопасный вариант — БЛОКИРОВАТЬ ВСЁ, а не разрешающее
            // расписание по умолчанию, которое тихо ослабило бы контроль, — и файл НЕ перезаписываем:
            // настоящее расписание, возможно, удастся восстановить, а перезапись его уничтожит.
            _current = BlockedAll();
            ScheduleFileCorrupt = true;
            try { _lastHash = File.Exists(AppPaths.ScheduleFile) ? Hash(File.ReadAllText(AppPaths.ScheduleFile)) : ""; }
            catch { _lastHash = ""; }
            return;
        }

        // Первый запуск, файла ещё нет: начинаем с разумного расписания по умолчанию и сохраняем его.
        _current = Schedule.Default();
        ScheduleFileCorrupt = false;
        WriteToDisk(_current);
    }

    /// <summary>Расписание, блокирующее весь день (Enabled, без разрешённых интервалов), — безопасный
    /// вариант на случай, когда расписание на диске не читается, чтобы повреждённый файл никогда не ослаблял контроль.</summary>
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
