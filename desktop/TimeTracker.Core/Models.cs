using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TimeTracker.Core;

public sealed class WorkTask
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Project { get; set; } = "その他";
    public string Name { get; set; } = "";
    public double PlannedHours { get; set; } = 1;
    public double ActualSeconds { get; set; }
    public string Notes { get; set; } = "";
    public WorkTask AsPlan() => new() { Project = Project, Name = Name, PlannedHours = PlannedHours, Notes = Notes };
}

public sealed class Preferences
{
    public bool EnablePlannedOverrunAlert { get; set; }
    public bool EnableLongAlert { get; set; } = true;
    public int LongAlertMinutes { get; set; } = 90;
    public bool EnableIdleAlert { get; set; } = true;
    public int IdleAlertMinutes { get; set; } = 15;
    public bool PauseOnIdle { get; set; } = true;
    public bool PauseOnLock { get; set; } = true;
    public bool StartWithWindows { get; set; }
    public bool CloseToTray { get; set; } = true;
    public string Theme { get; set; } = "System";
    private string colorTheme = "Slate";
    public string ColorTheme { get => colorTheme; set => colorTheme = ColorThemes.Normalize(value); }
}

public sealed class TrackerDocument
{
    public int Version { get; set; } = 1;
    public DateOnly SelectedDate { get; set; } = DateOnly.FromDateTime(DateTime.Today);
    public Dictionary<DateOnly, List<WorkTask>> Days { get; set; } = [];
    public Dictionary<DateOnly, List<WorkTask>> Plans { get; set; } = [];
    public Preferences Settings { get; set; } = new();
    public DateTimeOffset SavedAt { get; set; } = DateTimeOffset.UtcNow;
    public string? RunningTaskId { get; set; }
    public DateTimeOffset? RunStartedAt { get; set; }
    public double RunBaseSeconds { get; set; }
    public bool RecoveredTimer { get; set; }

    public List<WorkTask> Tasks(DateOnly day) => Days.TryGetValue(day, out var tasks) ? tasks : Days[day] = [];
    public List<WorkTask> Planned(DateOnly day) => Plans.TryGetValue(day, out var tasks) ? tasks : Plans[day] = [];
    public TrackerDocument Copy() => JsonSerializer.Deserialize<TrackerDocument>(JsonSerializer.Serialize(this, JsonOptions), JsonOptions)!;
    public static JsonSerializerOptions JsonOptions { get; } = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Skip
    };
}

public static class DocumentValidation
{
    public static void Validate(TrackerDocument doc)
    {
        if (doc.Version != 1 || doc.Days is null || doc.Plans is null || doc.Settings is null)
            throw new InvalidDataException("対応していないバックアップ形式です。");
        if (doc.SelectedDate == default || doc.SelectedDate == DateOnly.MaxValue)
            throw new InvalidDataException("対象日付が不正です。");
        foreach (var table in new[] { doc.Days, doc.Plans })
        foreach (var (day, tasks) in table)
        {
            if (day == default || day == DateOnly.MaxValue || tasks is null) throw new InvalidDataException("日付またはタスク一覧が不正です。");
            var ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (var task in tasks)
            {
                if (task is null || string.IsNullOrWhiteSpace(task.Id) || !ids.Add(task.Id)
                    || string.IsNullOrWhiteSpace(task.Name) || task.Project is null || task.Notes is null
                    || !double.IsFinite(task.PlannedHours) || task.PlannedHours < 0
                    || !double.IsFinite(task.ActualSeconds) || task.ActualSeconds < 0)
                    throw new InvalidDataException("タスクの名前・時間・識別子が不正です。");
            }
        }
        var p = doc.Settings;
        if (p.LongAlertMinutes is < 15 or > 360 || p.IdleAlertMinutes is < 5 or > 120
            || p.Theme is not ("System" or "Light" or "Dark") || !double.IsFinite(doc.RunBaseSeconds) || doc.RunBaseSeconds < 0)
            throw new InvalidDataException("設定またはタイマー情報が不正です。");
        if (doc.RunningTaskId is not null && (doc.RunStartedAt is null
            || !doc.Tasks(doc.SelectedDate).Any(t => t.Id == doc.RunningTaskId)))
            throw new InvalidDataException("計測中タスクを確認できません。");
    }

    public static double ParseHours(string text, bool blankDefaultsToOne = true)
    {
        if (string.IsNullOrWhiteSpace(text) && blankDefaultsToOne) return 1;
        text = string.Concat(text.Select(character => character is >= '！' and <= '～' ? (char)(character - 0xFEE0) : character)).Trim();
        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out var hours)
            || !double.IsFinite(hours) || hours < 0)
            throw new InvalidDataException("時間は0以上の数値で入力してください。");
        return hours;
    }
}
