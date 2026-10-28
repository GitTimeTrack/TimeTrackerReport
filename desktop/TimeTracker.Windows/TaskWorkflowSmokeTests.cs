using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using TimeTracker.Core;

namespace TimeTracker.Windows;

public partial class MainWindow
{
    private TextBox DraftInput(TaskRow row, string name)
    {
        var list = row.IsPlan ? NextList : TodayList;
        list.ScrollIntoView(row); UpdateLayout();
        var item = (ListBoxItem)list.ItemContainerGenerator.ContainerFromItem(row);
        var content = Descendant<ContentPresenter>(item)!;
        return (TextBox)content.ContentTemplate.FindName("Draft" + name, content);
    }
    private static KeyEventArgs InputKey(TextBox input, Key key) =>
        new(Keyboard.PrimaryDevice, PresentationSource.FromVisual(input), Environment.TickCount, key) { RoutedEvent = Keyboard.KeyDownEvent };

    private void RunTaskWorkflowSmokeTest()
    {
        App.TraceSmoke("Task workflow smoke test");
        ChangeView(0); ScheduleTabs.SelectedIndex = 0; UpdateLayout();
        var row = view.TodayTasks[0];
        row.BeginInlineEdit(); inlineRow = row;
        var planned = DraftInput(row, "Planned"); var actual = DraftInput(row, "Actual"); var notes = DraftInput(row, "Notes");
        if (!planned.IsVisible || !notes.IsVisible) throw new InvalidOperationException("Inline editing controls are hidden.");
        var originalActualText = actual.Text;
        var originalPlanned = row.Task.PlannedHours;
        planned.Text = "０"; notes.Text = "一覧で編集\n保存確認"; actual.Text = "invalid";
        planned.RaiseEvent(InputKey(planned, Key.Enter));
        if (row.Draft is null || row.InlineError.Length == 0 || row.Task.PlannedHours != originalPlanned)
            throw new InvalidOperationException("Invalid inline input was not rejected atomically.");
        actual.Text = originalActualText;
        StartTask(row.Task.Id); RefreshDisplay(); UpdateLayout();
        if (!ReferenceEquals(row, inlineRow) || row.Draft!.PlannedText != "０" || row.Draft.Notes != "一覧で編集\n保存確認")
            throw new InvalidOperationException("Timer refresh discarded the edit draft.");
        planned = DraftInput(row, "Planned");
        planned.RaiseEvent(InputKey(planned, Key.Enter)); UpdateLayout();
        if (inlineRow is not null || row.Task.PlannedHours != 0 || row.Task.Notes != "一覧で編集\n保存確認" || engine.Running?.Id != row.Task.Id)
            throw new InvalidOperationException("Inline edit did not save while timing.");
        var stored = store.Load()!.Tasks(engine.Document.SelectedDate).First(task => task.Id == row.Task.Id);
        if (stored.Notes != row.Task.Notes || stored.ActualSeconds != row.Task.ActualSeconds || stored.PlannedHours != 0)
            throw new InvalidOperationException("Inline edit was not persisted precisely.");
        engine.Pause(); Save(); Rebuild();
        row = view.TodayTasks[0]; row.BeginInlineEdit(); inlineRow = row;
        planned = DraftInput(row, "Planned"); planned.Text = "8";
        planned.RaiseEvent(InputKey(planned, Key.Escape));
        if (inlineRow is not null || row.Task.PlannedHours != 0) throw new InvalidOperationException("Escape did not cancel the draft.");
        ScheduleTabs.SelectedIndex = 1; UpdateLayout();
        var plan = view.NextTasks[0]; plan.BeginInlineEdit(); inlineRow = plan;
        planned = DraftInput(plan, "Planned"); actual = DraftInput(plan, "Actual"); notes = DraftInput(plan, "Notes");
        if (actual.IsVisible) throw new InvalidOperationException("A next-day plan exposes actual-time editing.");
        planned.Text = "０．５"; notes.Text = "次日の\nメモ";
        var enter = InputKey(notes, Key.Enter); InlineKeyDown(notes, enter);
        if (enter.Handled || plan.Draft is null) throw new InvalidOperationException("Memo Enter incorrectly committed the draft.");
        planned.RaiseEvent(InputKey(planned, Key.Enter)); UpdateLayout();
        if (plan.Task.PlannedHours != .5 || plan.Task.Notes != "次日の\nメモ") throw new InvalidOperationException("Next-day inline edit failed.");
        var sourceSnapshot = System.Text.Json.JsonSerializer.Serialize(Plans);
        var target = engine.Document.SelectedDate.AddDays(3);
        var dialog = new TaskCopyWindow(this, Plans, "予定から選択", target, true, engine.Document, "Next");
        dialog.Show(); dialog.UpdateLayout();
        if (dialog.FindName("DestinationKind") is not null)
            throw new InvalidOperationException("Redundant work/plan dropdown must not exist.");
        if (dialog.PrepareSelection(out _)) throw new InvalidOperationException("Copy accepted an empty selection.");
        dialog.ApplySourceMode("Today", setDefaultDestination: true);
        if (dialog.Choices.Count != Tasks.Count)
            throw new InvalidOperationException("Copy dialog did not switch source to today's tasks.");
        dialog.ApplySourceMode("Next", setDefaultDestination: false);
        dialog.DestinationDate.SelectedDate = target.ToDateTime(TimeOnly.MinValue);
        dialog.Choices[0].IsSelected = true;
        ((CheckBox)dialog.FindName("IncludeNotes")).IsChecked = false;
        if (!dialog.PrepareSelection(out var summary) || !summary.Contains("1件", StringComparison.Ordinal) || !dialog.TargetIsPlan)
            throw new InvalidOperationException("Copy selection or confirmation summary is invalid.");
        var count = TaskCopying.CopyTo(engine.Document, dialog.SelectedTasks, dialog.TargetDate, dialog.TargetIsPlan, dialog.CopyNotes);
        dialog.Close();
        if (count != 1 || System.Text.Json.JsonSerializer.Serialize(Plans) != sourceSnapshot)
            throw new InvalidOperationException("Copy changed the source plans.");
        var copy = engine.Document.Planned(target).Single();
        if (copy.Id == plan.Task.Id || copy.ActualSeconds != 0 || copy.PlannedHours != .5 || copy.Notes.Length != 0)
            throw new InvalidOperationException("Copy did not apply zero actual, new ID, and note options.");
        if (!Save() || store.Load()!.Planned(target).Single().Id != copy.Id)
            throw new InvalidOperationException("Copied plan was not saved.");
        var originalDate = engine.Document.SelectedDate;
        DayPicker.SelectedDate = target.ToDateTime(TimeOnly.MinValue); UpdateLayout();
        if (Tasks.Count != 1 || Tasks[0].Name != copy.Name || Tasks[0].PlannedHours != copy.PlannedHours)
            throw new InvalidOperationException("Next-day plan was not automatically carried over to today's tasks.");
        DayPicker.SelectedDate = originalDate.ToDateTime(TimeOnly.MinValue); UpdateLayout();
        var todayExtra1 = new WorkTask { Project = "その他", Name = "末尾プロジェクト", PlannedHours = 0.5 };
        var todayExtra2 = new WorkTask { Project = "00_最優先", Name = "先頭プロジェクト", PlannedHours = 1.0 };
        Tasks.Add(todayExtra1); Tasks.Add(todayExtra2);
        Rebuild(); UpdateLayout();
        SortTodayButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); UpdateLayout();
        if (Tasks[0].Project != "00_最優先" || Tasks[^1].Project != "その他")
            throw new InvalidOperationException("Project sort on today's tasks failed.");
        ScheduleTabs.SelectedIndex = 1; UpdateLayout();
        if ((string)CopyPreviousButton.Content != "本日の予定から追加" || !CopyFromTodayButton.IsVisible || !SortNextButton.IsVisible)
            throw new InvalidOperationException("Next-day tab copy/sort actions are missing.");
        var nextExtra1 = new WorkTask { Project = "その他", Name = "次日末尾", PlannedHours = 0.5 };
        var nextExtra2 = new WorkTask { Project = "00_次日先頭", Name = "次日先頭", PlannedHours = 1.0 };
        Plans.Add(nextExtra1); Plans.Add(nextExtra2);
        Rebuild(); UpdateLayout();
        SortNextButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); UpdateLayout();
        if (Plans[0].Project != "00_次日先頭" || Plans[^1].Project != "その他")
            throw new InvalidOperationException("Project sort on next-day plans failed.");
        Tasks.Remove(todayExtra1); Tasks.Remove(todayExtra2);
        Plans.Remove(nextExtra1); Plans.Remove(nextExtra2);
        Save(); Rebuild(); ScheduleTabs.SelectedIndex = 0; UpdateLayout();
    }
}
