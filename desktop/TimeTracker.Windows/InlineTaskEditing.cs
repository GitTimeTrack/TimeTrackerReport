using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using TimeTracker.Core;

namespace TimeTracker.Windows;

public partial class MainWindow
{
    private TaskRow? inlineRow;
    private string previousSearch = "";
    private bool changingSearch;
    private bool changingSchedule;
    private int previousSchedule;
    private bool changingDate;

    private void BeginInlineEdit(object sender, RoutedEventArgs e)
    {
        if (((Button)sender).Tag is not TaskRow row) return;
        if (!ReferenceEquals(inlineRow, row) && !ConfirmInlineEdit()) return;
        if (row.Draft is null) { engine.Tick(); row.BeginInlineEdit(); inlineRow = row; }
        var field = ((Button)sender).CommandParameter as string ?? "Planned";
        Dispatcher.BeginInvoke(() =>
        {
            var list = row.IsPlan ? NextList : TodayList;
            list.ScrollIntoView(row); UpdateLayout();
            if (list.ItemContainerGenerator.ContainerFromItem(row) is not ListBoxItem item) return;
            var content = Descendant<ContentPresenter>(item)!;
            if (content.ContentTemplate.FindName("Draft" + field, content) is TextBox input) { input.Focus(); input.SelectAll(); }
        }, DispatcherPriority.Input);
    }
    private bool SaveInlineEdit()
    {
        var row = inlineRow;
        if (row?.Draft is null) return true;
        try { row.Draft.Apply(engine, row.Task, row.IsPlan); }
        catch (InvalidDataException error) { row.InlineError = "入力エラー：" + error.Message; row.Refresh(); return false; }
        row.EndInlineEdit(); inlineRow = null;
        var saved = Save();
        if (saved) GuideMemoSaved(row.Task, row.IsPlan);
        Rebuild();
        return saved;
    }
    private void SaveInline(object sender, RoutedEventArgs e) => RunAction(() => SaveInlineEdit());
    private void CancelInline(object sender, RoutedEventArgs e) => CancelInlineEdit();
    private void CancelInlineEdit() { inlineRow?.EndInlineEdit(); inlineRow = null; }
    private void InlineKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { e.Handled = true; CancelInlineEdit(); }
        else if (e.Key == Key.Enter && sender is TextBox { AcceptsReturn: false })
        { e.Handled = true; RunAction(() => SaveInlineEdit()); }
    }
    private bool ConfirmInlineEdit()
    {
        if (inlineRow?.Draft is null) return true;
        if (!inlineRow.Draft.IsDirty) { CancelInlineEdit(); return true; }
        var result = MessageBox.Show(this, "一覧の編集内容が未保存です。\nはい：編集を保存\nいいえ：変更を取り消す\nキャンセル：編集を続ける", "一覧の編集", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
        if (result == MessageBoxResult.Cancel) return false;
        if (result == MessageBoxResult.No) { CancelInlineEdit(); return true; }
        return SaveInlineEdit();
    }
    private void ScheduleChanged(object sender, SelectionChangedEventArgs e)
    {
        if (e.Source != ScheduleTabs || !ready || changingSchedule) return;
        if (!ConfirmInlineEdit())
        {
            changingSchedule = true; ScheduleTabs.SelectedIndex = previousSchedule; changingSchedule = false;
            return;
        }
        previousSchedule = ScheduleTabs.SelectedIndex;
        if (CopyPreviousButton is not null)
        {
            var isNextTab = ScheduleTabs.SelectedIndex == 1;
            CopyPreviousButton.Content = isNextTab ? "本日の予定から追加" : "前日の作業から追加";
            CopyPreviousButton.Tag = isNextTab ? "TodayToNext" : "Previous";
        }
    }
    private void CopyTasks(object sender, RoutedEventArgs e) => RunAction(() =>
    {
        if (!ConfirmInlineEdit()) return;
        var tag = ((Button)sender).Tag as string;
        string sourceMode;
        DateOnly sourceDay;
        List<WorkTask> source;
        DateOnly targetDate;
        bool isPlan;
        if (tag == "Previous")
        {
            sourceMode = "Previous";
            sourceDay = engine.Document.SelectedDate.AddDays(-1);
            source = engine.Document.Days.GetValueOrDefault(sourceDay) ?? [];
            targetDate = engine.Document.SelectedDate;
            isPlan = false;
        }
        else if (tag == "TodayToNext")
        {
            sourceMode = "Today";
            sourceDay = engine.Document.SelectedDate;
            source = Tasks;
            targetDate = engine.Document.SelectedDate.AddDays(1);
            isPlan = true;
        }
        else if (ScheduleTabs.SelectedIndex == 1 && Plans.Count > 0)
        {
            sourceMode = "Next";
            sourceDay = engine.Document.SelectedDate.AddDays(1);
            source = Plans;
            targetDate = engine.Document.SelectedDate.AddDays(1);
            isPlan = true;
        }
        else
        {
            sourceMode = "Today";
            sourceDay = engine.Document.SelectedDate;
            source = Tasks;
            targetDate = engine.Document.SelectedDate.AddDays(1);
            isPlan = true;
        }
        if (source.Count == 0) { SetStatus("複製する作業・予定がありません。"); return; }
        editing = true;
        try
        {
            var label = sourceMode switch
            {
                "Previous" => $"{sourceDay:yyyy/MM/dd}（前日の作業）から選択",
                "Next" => $"{sourceDay:yyyy/MM/dd}（次日の予定）から選択",
                _ => $"{sourceDay:yyyy/MM/dd}（本日の予定）から選択"
            };
            var dialog = new TaskCopyWindow(this, source, label, targetDate, isPlan, engine.Document, sourceMode);
            if (dialog.ShowDialog() != true) return;
            var count = TaskCopying.CopyTo(engine.Document, dialog.SelectedTasks, dialog.TargetDate, dialog.TargetIsPlan, dialog.CopyNotes);
            var saved = Save(); Rebuild();
            if (saved)
            {
                var targetName = dialog.TargetDate == engine.Document.SelectedDate
                    ? $"{dialog.TargetDate:yyyy/MM/dd}（本日の予定）"
                    : dialog.TargetDate == engine.Document.SelectedDate.AddDays(1)
                        ? $"{dialog.TargetDate:yyyy/MM/dd}（次日の予定）"
                        : $"{dialog.TargetDate:yyyy/MM/dd} の{(dialog.TargetIsPlan ? "予定" : "作業")}";
                SetStatus($"{targetName}へ{count}件追加しました。");
            }
        }
        finally { editing = false; }
    });
}
