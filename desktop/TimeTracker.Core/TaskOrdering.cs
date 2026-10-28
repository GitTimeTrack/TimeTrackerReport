namespace TimeTracker.Core;

public static class TaskOrdering
{
    // Destination is a gap in the original list, from 0 through Count.
    public static bool Move(List<WorkTask> tasks, WorkTask task, int destination)
    {
        var source = tasks.IndexOf(task);
        if (source < 0 || destination < 0 || destination > tasks.Count) return false;
        if (destination > source) destination--;
        if (destination == source) return false;
        tasks.RemoveAt(source);
        tasks.Insert(destination, task);
        return true;
    }

    public static bool SortByProject(List<WorkTask> tasks)
    {
        if (tasks.Count < 2) return false;
        var sorted = tasks
            .Select((task, index) => (Task: task, Index: index))
            .OrderBy(item => IsDefaultProject(item.Task.Project) ? 1 : 0)
            .ThenBy(item => item.Task.Project.Trim(), StringComparer.CurrentCulture)
            .ThenBy(item => item.Task.Project.Trim(), StringComparer.Ordinal)
            .ThenBy(item => item.Index)
            .Select(item => item.Task)
            .ToList();
        if (tasks.SequenceEqual(sorted)) return false;
        tasks.Clear();
        tasks.AddRange(sorted);
        return true;
    }

    private static bool IsDefaultProject(string? project) =>
        string.IsNullOrWhiteSpace(project) || project.Trim() == "その他";
}
