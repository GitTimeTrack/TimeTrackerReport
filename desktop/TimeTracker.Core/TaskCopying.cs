namespace TimeTracker.Core;

public static class TaskCopying
{
    public static int CopyTo(TrackerDocument document, IEnumerable<WorkTask> source, DateOnly destination, bool isPlan, bool includeNotes)
    {
        if (destination == default || destination == DateOnly.MaxValue) throw new InvalidDataException("登録先の日付が範囲外です。");
        // Materialize first so copying into the source list is safe.
        var copies = source.DistinctBy(task => task.Id).Select(task => new WorkTask
        {
            Project = task.Project, Name = task.Name, PlannedHours = task.PlannedHours,
            Notes = includeNotes ? task.Notes : "", ActualSeconds = 0
        }).ToList();
        if (copies.Count == 0) return 0;
        var validation = new TrackerDocument { SelectedDate = destination };
        validation.Days[destination] = copies;
        DocumentValidation.Validate(validation);
        (isPlan ? document.Planned(destination) : document.Tasks(destination)).AddRange(copies);
        if (isPlan && document.Days.TryGetValue(destination, out var existingDayTasks) && existingDayTasks.Count > 0)
            existingDayTasks.AddRange(copies.Select(task => task.AsPlan()));
        return copies.Count;
    }
}
