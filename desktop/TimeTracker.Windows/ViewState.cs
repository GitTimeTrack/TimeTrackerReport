using System.Collections.ObjectModel;
using System.ComponentModel;
using TimeTracker.Core;

namespace TimeTracker.Windows;

public class ObservableState : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    public void Refresh(params string[] properties)
    {
        if (properties.Length == 0) PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
        else foreach (var property in properties) PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));
    }
}

public sealed class TaskRow(WorkTask task, bool isPlan, Func<string?> runningId) : ObservableState
{
    public WorkTask Task { get; } = task;
    public bool IsPlan { get; } = isPlan;
    public string Project => Task.Project;
    public string Name => Task.Name;
    public string Notes => Task.Notes;
    public bool HasNotes => !string.IsNullOrWhiteSpace(Notes);
    private bool notesExpanded;
    public bool IsNotesExpanded
    {
        get => notesExpanded;
        set { if (notesExpanded == value) return; notesExpanded = value; Refresh(nameof(IsNotesExpanded)); }
    }
    public TaskEditDraft? Draft { get; private set; }
    public bool IsInlineEditing => Draft is not null;
    public bool CanEditActual => !IsPlan;
    public string InlineError { get; set; } = "";
    public void BeginInlineEdit() { Draft = new TaskEditDraft(Task); InlineError = ""; IsNotesExpanded = true; Refresh(); }
    public void EndInlineEdit() { Draft = null; InlineError = ""; Refresh(); }
    public string PlannedValue => $"{Task.PlannedHours:0.0}h";
    public string ActualValue => IsPlan ? "—" : $"{Task.ActualSeconds / 3600:0.0}h";
    public string PlannedLabel => $"予定 {Task.PlannedHours:0.0}h";
    public string ActualLabel => IsPlan ? "NEXT DAY" : $"実績 {Task.ActualSeconds / 3600:0.0}h";
    public bool IsRunning => !IsPlan && runningId() == Task.Id;
    public string ActionLabel => IsPlan ? "本日へ" : IsRunning ? "一時停止" : "▶ 開始";
}

public sealed record SummaryRow(string Project, double PlannedHours, double ActualHours, double Scale);

public sealed class ViewState(TrackerEngine engine, string dataPath) : ObservableState
{
    public ObservableCollection<TaskRow> TodayTasks { get; } = [];
    public ObservableCollection<TaskRow> NextTasks { get; } = [];
    public ObservableCollection<SummaryRow> Summaries { get; } = [];
    public string DataPath { get; } = dataPath;
    public string VersionLabel => $"現在のバージョン：{App.VersionText}";
    public bool GuideVisible { get; set; }
    public string GuideText { get; set; } = "";
    public string GuideActionText { get; set; } = "作業一覧";
    public string Status { get; set; } = "保存済み";
    public string UndoLabel { get; set; } = "";
    public bool CanUndo => UndoLabel.Length > 0;
    public string TimerCaption => engine.Running is null ? "停止中" : "計測中";
    public string ActiveName => engine.Running?.Name ?? "計測していません";
    public string ToggleLabel => engine.Running is null ? "▶ 計測開始" : "Ⅱ 一時停止";
    public string ClockText
    {
        get
        {
            var seconds = Math.Floor(engine.Running?.ActualSeconds ?? 0);
            return $"{Math.Floor(seconds / 3600):00}:{Math.Floor(seconds / 60) % 60:00}:{seconds % 60:00}";
        }
    }
    public string TodayTabLabel => $"本日の予定  {TodayTasks.Count}件";
    public string NextTabLabel => $"次日の予定  {NextTasks.Count}件";
    public string TodayDateLabel => engine.Document.SelectedDate.ToString("M月d日（ddd）");
    public string TodayTotalsLabel => TotalsLabel;
    public string NextTotalsLabel => $"予定合計 {engine.Document.Planned(engine.Document.SelectedDate.AddDays(1)).Sum(t => t.PlannedHours):0.0}h  /  実績合計 {engine.Document.Planned(engine.Document.SelectedDate.AddDays(1)).Sum(t => t.ActualSeconds) / 3600:0.0}h";
    public string NextDateLabel => $"{engine.Document.SelectedDate.AddDays(1):M月d日（ddd）}";
    public string TotalsLabel => $"予定合計 {engine.Document.Tasks(engine.Document.SelectedDate).Sum(t => t.PlannedHours):0.0}h  /  実績合計 {engine.Document.Tasks(engine.Document.SelectedDate).Sum(t => t.ActualSeconds) / 3600:0.0}h";
    public string ReportText => Reports.Daily(engine.Document, engine.Document.SelectedDate);
    public string PeriodLabel { get; set; } = "";
}
