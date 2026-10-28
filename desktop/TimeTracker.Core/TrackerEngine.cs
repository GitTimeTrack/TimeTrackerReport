namespace TimeTracker.Core;

public sealed class TrackerEngine
{
    private readonly Func<DateTimeOffset> clock;
    private readonly TimeZoneInfo zone;
    public TrackerDocument Document { get; private set; }
    public DateTimeOffset Now => clock();
    public DateOnly Today => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(Now, zone).DateTime);
    public WorkTask? Running => Document.Tasks(Document.SelectedDate).FirstOrDefault(t => t.Id == Document.RunningTaskId);
    public TrackerEngine(TrackerDocument document, Func<DateTimeOffset>? clock = null, TimeZoneInfo? zone = null)
    {
        DocumentValidation.Validate(document);
        Document = document;
        this.clock = clock ?? (() => DateTimeOffset.UtcNow);
        this.zone = zone ?? TimeZoneInfo.Local;
        // A checkpoint records actual seconds. Offline/crash time is never charged.
        if (document.RunningTaskId is not null)
        {
            document.RecoveredTimer = true;
            document.RunningTaskId = null;
            document.RunStartedAt = null;
        }
    }

    public void Tick()
    {
        if (Running is not { } task || Document.RunStartedAt is not { } start) return;
        var end = Now;
        if (Document.SelectedDate < Today)
        {
            var midnight = Document.SelectedDate.AddDays(1).ToDateTime(TimeOnly.MinValue);
            end = new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(midnight, zone));
        }
        task.ActualSeconds = Document.RunBaseSeconds + Math.Max(0, (end - start).TotalSeconds);
        if (Document.SelectedDate != Today) Pause();
    }

    public void Start(string id)
    {
        if (Document.SelectedDate != Today) throw new InvalidOperationException("計測は本日の日付で開始してください。");
        if (!Document.Tasks(Document.SelectedDate).Any(t => t.Id == id)) throw new InvalidOperationException("タスクが見つかりません。");
        Pause();
        Document.RunningTaskId = id;
        Document.RunStartedAt = Now;
        Document.RunBaseSeconds = Running!.ActualSeconds;
    }

    public void Pause(DateTimeOffset? cutoff = null)
    {
        if (Document.RunningTaskId is null) return;
        // Clear first to avoid recursion when Tick encounters midnight.
        var task = Running;
        var start = Document.RunStartedAt;
        Document.RunningTaskId = null;
        Document.RunStartedAt = null;
        if (task is null || start is null) return;
        var end = cutoff is { } boundary && boundary < Now ? boundary : Now;
        var midnight = Document.SelectedDate.AddDays(1).ToDateTime(TimeOnly.MinValue);
        if (Document.SelectedDate < Today) end = new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(midnight, zone)) < end ? new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(midnight, zone)) : end;
        task.ActualSeconds = Document.RunBaseSeconds + Math.Max(0, (end - start.Value).TotalSeconds);
    }

    public void SelectDate(DateOnly date)
    {
        if (date == default || date == DateOnly.MaxValue) throw new InvalidDataException("日付が範囲外です。");
        Pause();
        Document.SelectedDate = date;
        CarryPlannedTasks(date);
    }

    public bool CarryPlannedTasks(DateOnly date)
    {
        if (!Document.Plans.TryGetValue(date, out var planned) || planned.Count == 0) return false;
        if (Document.Days.TryGetValue(date, out var existing) && existing.Count > 0) return false;
        Document.Tasks(date).AddRange(planned.Select(task => task.AsPlan()));
        return true;
    }

    public void SetActual(WorkTask task, double seconds)
    {
        if (!double.IsFinite(seconds) || seconds < 0) throw new InvalidDataException("実績時間が不正です。");
        Tick();
        task.ActualSeconds = seconds;
        if (Running?.Id == task.Id)
        {
            Document.RunBaseSeconds = seconds;
            Document.RunStartedAt = Now;
        }
    }

    public TrackerDocument Checkpoint()
    {
        Tick();
        Document.SavedAt = Now;
        return Document.Copy();
    }

    public void Replace(TrackerDocument candidate)
    {
        // Validation must precede changing even the existing running timer.
        DocumentValidation.Validate(candidate);
        Pause();
        candidate.RunningTaskId = null;
        candidate.RunStartedAt = null;
        Document = candidate;
    }
}
