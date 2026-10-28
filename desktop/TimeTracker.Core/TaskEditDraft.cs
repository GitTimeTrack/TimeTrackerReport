using System.Globalization;

namespace TimeTracker.Core;

public sealed class TaskEditDraft
{
    private readonly string initialPlanned;
    private readonly string initialActual;
    private readonly string initialNotes;
    public string PlannedText { get; set; }
    public string ActualText { get; set; }
    public string Notes { get; set; }
    public bool IsDirty => PlannedText != initialPlanned || ActualText != initialActual || Notes != initialNotes;

    public TaskEditDraft(WorkTask task)
    {
        PlannedText = initialPlanned = task.PlannedHours.ToString(CultureInfo.CurrentCulture);
        ActualText = initialActual = (task.ActualSeconds / 3600).ToString("0.########", CultureInfo.CurrentCulture);
        Notes = initialNotes = task.Notes;
    }

    public void Apply(TrackerEngine engine, WorkTask task, bool isPlan)
    {
        // Parse all inputs before changing live data, including the running timer.
        var hours = DocumentValidation.ParseHours(PlannedText);
        double? seconds = !isPlan && ActualText != initialActual ? DocumentValidation.ParseHours(ActualText, false) * 3600 : null;
        if (seconds.HasValue && !double.IsFinite(seconds.Value)) throw new InvalidDataException("実績時間が大きすぎます。");
        if (seconds.HasValue) engine.SetActual(task, seconds.Value);
        task.PlannedHours = hours;
        task.Notes = Notes;
    }
}
