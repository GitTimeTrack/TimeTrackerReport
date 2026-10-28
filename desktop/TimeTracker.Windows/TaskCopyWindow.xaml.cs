using System.Windows;
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
    public IReadOnlyList<CopyChoice> Choices { get; }
    public string SourceLabel { get; }
    public IReadOnlyList<WorkTask> SelectedTasks { get; private set; } = [];
    public DateOnly TargetDate { get; private set; }
    public bool TargetIsPlan { get; private set; }
    public bool CopyNotes { get; private set; }
    public TaskCopyWindow(Window owner, IEnumerable<WorkTask> source, string label, DateOnly date, bool isPlan)
    {
        Owner = owner; Choices = source.Select(task => new CopyChoice(task)).ToArray(); SourceLabel = label;
        InitializeComponent(); DataContext = this;
        DestinationDate.SelectedDate = date.ToDateTime(TimeOnly.MinValue);
        DestinationKind.SelectedIndex = isPlan ? 1 : 0;
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
        TargetDate = day; TargetIsPlan = DestinationKind.SelectedIndex == 1; CopyNotes = IncludeNotes.IsChecked == true;
        InputError.Text = "";
        SelectedTasks = selected;
        summary = $"{selected.Length}件を {TargetDate:yyyy/MM/dd} の{(TargetIsPlan ? "予定" : "作業")}へ追加しますか？\n実績は0h、メモは{(CopyNotes ? "引き継ぎます" : "引き継ぎません")}。";
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
