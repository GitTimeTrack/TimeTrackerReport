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
    }
    private void CopyTasks(object sender, RoutedEventArgs e) => RunAction(() =>
    {
        if (!ConfirmInlineEdit()) return;
        var previous = ((Button)sender).Tag as string == "Previous";
        var plan = !previous && ScheduleTabs.SelectedIndex == 1;
        var sourceDay = previous ? engine.Document.SelectedDate.AddDays(-1) : plan ? engine.Document.SelectedDate.AddDays(1) : engine.Document.SelectedDate;
        var source = previous ? engine.Document.Days.GetValueOrDefault(sourceDay) ?? [] : plan ? Plans : Tasks;
        if (source.Count == 0) { SetStatus("複製する作業・予定がありません。"); return; }
        editing = true;
        try
        {
            var dialog = new TaskCopyWindow(this, source, $"{sourceDay:yyyy/MM/dd}の{(plan ? "予定" : "作業")}から選択", plan ? engine.Document.SelectedDate.AddDays(1) : engine.Document.SelectedDate, plan);
            if (dialog.ShowDialog() != true) return;
            var count = TaskCopying.CopyTo(engine.Document, dialog.SelectedTasks, dialog.TargetDate, dialog.TargetIsPlan, dialog.CopyNotes);
            var saved = Save(); Rebuild();
            if (saved) SetStatus($"{dialog.TargetDate:yyyy/MM/dd} の{(dialog.TargetIsPlan ? "予定" : "作業")}へ{count}件追加しました。");
        }
        finally { editing = false; }
    });
}
