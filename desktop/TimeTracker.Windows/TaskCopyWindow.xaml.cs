using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using TimeTracker.Core;

namespace TimeTracker.Windows;

public sealed class CopyChoice(WorkTask task) : ObservableState
{
    public WorkTask Task { get; } = task;
    private bool selected;
    public bool IsSelected { get => selected; set { selected = value; Refresh(); } }
}

public partial class TaskCopyWindow : Window
{
    private readonly TrackerDocument? document;
    private readonly DateOnly referenceDate;
    public ObservableCollection<CopyChoice> Choices { get; } = [];
    public string SourceLabel { get; private set; }
    public IReadOnlyList<WorkTask> SelectedTasks { get; private set; } = [];
    public DateOnly TargetDate { get; private set; }
    public bool TargetIsPlan { get; private set; }
    public bool CopyNotes { get; private set; }

    public TaskCopyWindow(Window owner, IEnumerable<WorkTask> source, string label, DateOnly date, bool isPlan = false, TrackerDocument? document = null, string? sourceMode = null)
    {
        Owner = owner;
        this.document = document;
        referenceDate = document?.SelectedDate ?? (isPlan ? date.AddDays(-1) : date);
        SourceLabel = label;
        foreach (var task in source) Choices.Add(new CopyChoice(task));
        InitializeComponent();
        DataContext = this;
        if (document is not null)
        {
            SourceBar.Visibility = Visibility.Visible;
            SourceTodayButton.Content = $"本日の予定（{referenceDate:M/d}）";
            SourcePreviousButton.Content = $"前日の作業（{referenceDate.AddDays(-1):M/d}）";
            SourceNextButton.Content = $"次日の予定（{referenceDate.AddDays(1):M/d}）";
            if (!string.IsNullOrEmpty(sourceMode)) ApplySourceMode(sourceMode, setDefaultDestination: false);
        }
        TargetTodayButton.Content = $"本日の予定へ（{referenceDate:M/d}）";
        TargetNextButton.Content = $"次日の予定へ（{referenceDate.AddDays(1):M/d}）";
        DestinationDate.SelectedDate = date.ToDateTime(TimeOnly.MinValue);
        UpdateDestinationBadge();
        UpdateEmptyHint();
    }

    internal void ApplySourceMode(string mode, bool setDefaultDestination = true)
    {
        if (document is null) return;
        var (items, label, defaultTarget) = mode switch
        {
            "Previous" => ((IReadOnlyList<WorkTask>)(document.Days.GetValueOrDefault(referenceDate.AddDays(-1)) ?? []),
                $"{referenceDate.AddDays(-1):yyyy/MM/dd}（前日の作業）から選択", referenceDate),
            "Next" => (document.Planned(referenceDate.AddDays(1)),
                $"{referenceDate.AddDays(1):yyyy/MM/dd}（次日の予定）から選択", referenceDate.AddDays(1)),
            _ => (document.Tasks(referenceDate),
                $"{referenceDate:yyyy/MM/dd}（本日の予定）から選択", referenceDate.AddDays(1))
        };
        Choices.Clear();
        foreach (var task in items) Choices.Add(new CopyChoice(task));
        SourceLabel = label;
        SourceHeading.Text = label;
        InputError.Text = "";
        if (setDefaultDestination) DestinationDate.SelectedDate = defaultTarget.ToDateTime(TimeOnly.MinValue);
        UpdateDestinationBadge();
        UpdateEmptyHint();
    }

    private void ChangeSource(object sender, RoutedEventArgs e)
    {
        if (((Button)sender).Tag is string mode) ApplySourceMode(mode, setDefaultDestination: true);
    }

    private void SelectTargetToday(object sender, RoutedEventArgs e) =>
        DestinationDate.SelectedDate = referenceDate.ToDateTime(TimeOnly.MinValue);

    private void SelectTargetNext(object sender, RoutedEventArgs e) =>
        DestinationDate.SelectedDate = referenceDate.AddDays(1).ToDateTime(TimeOnly.MinValue);

    private void DestinationChanged(object? sender, SelectionChangedEventArgs e) => UpdateDestinationBadge();

    private void UpdateDestinationBadge()
    {
        if (DestinationBadge is null) return;
        if (DestinationDate.SelectedDate is not { } selected)
        {
            DestinationBadge.Text = "";
            return;
        }
        var day = DateOnly.FromDateTime(selected);
        DestinationBadge.Text = day == referenceDate
            ? "→ 本日の予定に登録します"
            : day == referenceDate.AddDays(1)
                ? "→ 次日の予定に登録します"
                : day > referenceDate
                    ? $"→ {day:M/d} の予定に登録します"
                    : $"→ {day:M/d} の作業に登録します";
    }

    private void UpdateEmptyHint()
    {
        if (EmptySourceHint is null) return;
        EmptySourceHint.Text = Choices.Count == 0 ? "この複製元にはまだ項目がありません。" : $"{Choices.Count}件から選択できます";
    }

    private void SelectAll(object sender, RoutedEventArgs e)
    {
        var select = !Choices.All(choice => choice.IsSelected);
        foreach (var choice in Choices) choice.IsSelected = select;
    }

    internal bool PrepareSelection(out string summary)
    {
        summary = "";
        var selected = Choices.Where(choice => choice.IsSelected).Select(choice => choice.Task).ToArray();
        if (selected.Length == 0) { InputError.Text = "追加する作業を1件以上選んでください。"; return false; }
        if (DestinationDate.SelectedDate is not { } date)
        { InputError.Text = "登録先の日付を選んでください。"; return false; }
        var day = DateOnly.FromDateTime(date);
        if (day == default || day == DateOnly.MaxValue)
        { InputError.Text = "登録先の日付が範囲外です。"; return false; }
        TargetDate = day;
        TargetIsPlan = day > referenceDate;
        CopyNotes = IncludeNotes.IsChecked == true;
        InputError.Text = "";
        SelectedTasks = selected;
        var targetLabel = TargetDate == referenceDate
            ? $"{TargetDate:yyyy/MM/dd}（本日の予定）"
            : TargetDate == referenceDate.AddDays(1)
                ? $"{TargetDate:yyyy/MM/dd}（次日の予定）"
                : $"{TargetDate:yyyy/MM/dd} の{(TargetIsPlan ? "予定" : "作業")}";
        summary = $"{selected.Length}件を {targetLabel}へ追加しますか？\n実績は0h、メモは{(CopyNotes ? "引き継ぎます" : "引き継ぎません")}。";
        return true;
    }

    private void Submit(object sender, RoutedEventArgs e)
    {
        if (!PrepareSelection(out var summary)) return;
        if (MessageBox.Show(this, summary, "登録内容の確認", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        SubmitButton.IsEnabled = false;
        DialogResult = true;
    }
}
