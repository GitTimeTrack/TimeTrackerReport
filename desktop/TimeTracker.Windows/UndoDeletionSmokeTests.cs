using System.Windows;
using System.Windows.Controls;
using TimeTracker.Core;

namespace TimeTracker.Windows;

public partial class MainWindow
{
    private void RunUndoDeletionSmokeTest()
    {
        App.TraceSmoke("Undo deletion smoke test");
        ChangeView(0); UpdateLayout();
        var originalDate = engine.Document.SelectedDate;
        var originalOrder = Tasks.Select(task => task.Id).ToArray();
        var original = Tasks[0];
        StartTask(original.Id);
        DeleteRow(view.TodayTasks.First(row => row.Task.Id == original.Id)); UpdateLayout();
        if (engine.Running is not null || Tasks.Any(task => task.Id == original.Id) || !view.CanUndo || !UndoNotice.IsVisible || !UndoButton.IsEnabled)
            throw new InvalidOperationException("Deleted running task cannot be undone from the notice.");
        if (!view.UndoLabel.Contains(original.Name, StringComparison.Ordinal))
            throw new InvalidOperationException("Undo notice does not identify the deleted task.");
        var savedDeletion = store.Load()!;
        if (savedDeletion.Tasks(originalDate).Any(task => task.Id == original.Id))
            throw new InvalidOperationException("Task deletion was not saved.");
        var screenshot = Environment.GetEnvironmentVariable("TIMETRACKER_SMOKE_SCREENSHOT");
        if (!string.IsNullOrEmpty(screenshot))
        {
            var image = new System.Windows.Media.Imaging.RenderTargetBitmap((int)ActualWidth, (int)ActualHeight, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
            image.Render(this);
            var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
            encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(image));
            using var output = File.Create(Path.Combine(Path.GetDirectoryName(screenshot)!, "undo-preview.png"));
            encoder.Save(output);
        }
        UndoButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); UpdateLayout();
        var restored = Tasks.First(task => task.Id == original.Id);
        if (!Tasks.Select(task => task.Id).SequenceEqual(originalOrder) || engine.Running is not null || view.CanUndo || UndoNotice.IsVisible)
            throw new InvalidOperationException("Undo did not restore order and clear the notice.");
        if (restored.ActualSeconds != original.ActualSeconds || restored.Notes != original.Notes || restored.PlannedHours != original.PlannedHours)
            throw new InvalidOperationException("Undo lost task precision or notes.");
        if (store.Load()!.Tasks(originalDate).First().Id != original.Id || !view.ReportText.Contains(original.Name, StringComparison.Ordinal))
            throw new InvalidOperationException("Restored task was not reflected in persistence or report.");
        UndoButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        if (Tasks.Count != originalOrder.Length) throw new InvalidOperationException("Repeated undo duplicated the task.");
        var planOrder = Plans.Select(task => task.Id).ToArray();
        var plan = Plans[0];
        DeleteRow(view.NextTasks.First(row => row.Task.Id == plan.Id));
        DayPicker.SelectedDate = originalDate.AddDays(2).ToDateTime(TimeOnly.MinValue);
        UpdateLayout();
        if (!view.CanUndo || !UndoNotice.IsVisible) throw new InvalidOperationException("Navigation discarded undo history.");
        UndoButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); UpdateLayout();
        var restoredPlans = engine.Document.Planned(originalDate.AddDays(1));
        if (!restoredPlans.Select(task => task.Id).SequenceEqual(planOrder) || engine.Document.SelectedDate != originalDate.AddDays(2))
            throw new InvalidOperationException("Plan was not restored to its original date and order.");
        if (store.Load()!.Planned(originalDate.AddDays(1)).First().Id != plan.Id)
            throw new InvalidOperationException("Restored plan was not saved.");
        DayPicker.SelectedDate = originalDate.ToDateTime(TimeOnly.MinValue); UpdateLayout();
    }
}
