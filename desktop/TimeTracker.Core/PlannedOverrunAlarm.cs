using System.Runtime.CompilerServices;

namespace TimeTracker.Core;

/// <summary>One alert per task and planned duration; pausing does not reset the alert.</summary>
public sealed class PlannedOverrunAlarm
{
    private sealed record Notification(double PlannedHours);
    private readonly ConditionalWeakTable<WorkTask, Notification> notifications = new();

    public bool ShouldNotify(WorkTask? running, bool enabled)
    {
        if (running is null) return false;
        if (running.ActualSeconds / 3600 <= running.PlannedHours)
        {
            notifications.Remove(running);
            return false;
        }
        if (!enabled) return false;
        if (notifications.TryGetValue(running, out var previous) && previous.PlannedHours == running.PlannedHours)
            return false;
        notifications.Remove(running);
        notifications.Add(running, new Notification(running.PlannedHours));
        return true;
    }
}
