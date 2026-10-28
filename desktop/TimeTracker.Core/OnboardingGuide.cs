namespace TimeTracker.Core;

public sealed class OnboardingProgress
{
    public int AutomaticDisplays { get; set; }
    public bool Completed { get; set; }
}

public sealed class OnboardingGuide(OnboardingProgress progress)
{
    private bool attemptedAutomatic;
    private string? taskId;
    private DateOnly taskDay;
    private bool stopped;
    public bool Visible { get; private set; }
    public int Step { get; private set; }
    public OnboardingProgress Progress { get; } = progress;

    public bool ShowAutomatic()
    {
        if (attemptedAutomatic) return false;
        attemptedAutomatic = true;
        if (Progress.Completed || Progress.AutomaticDisplays >= 3) return false;
        Progress.AutomaticDisplays = Math.Clamp(Progress.AutomaticDisplays, 0, 2) + 1;
        ShowManual();
        return true;
    }
    public void ShowManual() { Visible = true; Step = 0; taskId = null; stopped = false; }
    public void Dismiss() => Visible = false;
    public void Started(WorkTask task, DateOnly day)
    {
        if (!Visible) return;
        taskId = task.Id; taskDay = day; stopped = false; Step = 1;
    }
    public void Observe(WorkTask? running)
    {
        if (Visible && Step == 1) stopped = running?.Id != taskId;
    }
    public bool MemoSaved(WorkTask task, DateOnly day)
    {
        if (!Visible || Step != 1 || !stopped || taskId != task.Id || day != taskDay || string.IsNullOrWhiteSpace(task.Notes)) return false;
        Progress.Completed = true; Visible = false;
        return true;
    }
}
