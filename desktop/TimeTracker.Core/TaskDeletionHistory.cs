namespace TimeTracker.Core;

public sealed record DeletedTaskInfo(string Name, DateOnly Day, bool IsPlan);

/// <summary>Holds only the most recent deletion for the current in-memory document.</summary>
public sealed class TaskDeletionHistory
{
    private TrackerDocument? owner;
    private WorkTask? deleted;
    private DateOnly day;
    private bool isPlan;
    private int position;

    public DeletedTaskInfo? Pending(TrackerDocument document) =>
        ReferenceEquals(owner, document) && deleted is not null ? new(deleted.Name, day, isPlan) : null;

    public bool Delete(TrackerEngine engine, string id, bool plan)
    {
        var document = engine.Document;
        var date = plan ? document.SelectedDate.AddDays(1) : document.SelectedDate;
        var list = plan ? document.Planned(date) : document.Tasks(date);
        var index = list.FindIndex(task => task.Id == id);
        if (index < 0) return false;
        var task = list[index];
        if (!plan && ReferenceEquals(engine.Running, task)) engine.Pause();
        deleted = new WorkTask
        {
            Id = task.Id, Project = task.Project, Name = task.Name,
            PlannedHours = task.PlannedHours, ActualSeconds = task.ActualSeconds, Notes = task.Notes
        };
        owner = document; day = date; isPlan = plan; position = index;
        list.RemoveAt(index);
        return true;
    }

    public bool Undo(TrackerDocument document)
    {
        if (Pending(document) is null || deleted is null) return false;
        var list = isPlan ? document.Planned(day) : document.Tasks(day);
        if (list.Any(task => task.Id == deleted.Id))
            throw new InvalidOperationException("同じ識別子のタスクが既にあるため、上書きせず復元を中止しました。");
        list.Insert(Math.Min(position, list.Count), deleted);
        Clear();
        return true;
    }

    public void Clear() { owner = null; deleted = null; }
}
