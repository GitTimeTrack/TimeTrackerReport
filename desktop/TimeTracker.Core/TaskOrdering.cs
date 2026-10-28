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
}
