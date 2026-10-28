using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using TimeTracker.Core;

namespace TimeTracker.Windows;

public partial class MainWindow
{
    private Point dragOrigin;
    private TaskRow? dragCandidate;
    private Border? dropMarker;
    private Brush? markerBrush;
    private Thickness markerThickness;
    private DateTime lastDragScroll;

    private void DragHandleDown(object sender, MouseButtonEventArgs e)
    {
        dragOrigin = e.GetPosition(this);
        dragCandidate = (sender as FrameworkElement)?.DataContext as TaskRow;
    }

    private void DragHandleMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed) { dragCandidate = null; return; }
        if (dragCandidate is not { } row) return;
        if (inlineRow is not null) { SetStatus("並べ替える前に一覧の編集を保存または取り消してください。"); return; }
        var point = e.GetPosition(this);
        if (Math.Abs(point.X - dragOrigin.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(point.Y - dragOrigin.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        dragCandidate = null;
        try { DragDrop.DoDragDrop((DependencyObject)sender, new DataObject(typeof(TaskRow), row), DragDropEffects.Move); }
        finally { ClearDropMarker(); }
    }

    private static T? Descendant<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is T found) return found;
            if (Descendant<T>(child) is { } nested) return nested;
        }
        return null;
    }

    private bool CanReorder(ListBox list, IDataObject data, out TaskRow? row)
    {
        row = data.GetData(typeof(TaskRow)) as TaskRow;
        return inlineRow is null && row is not null && row.IsPlan == (list == NextList) &&
            (row.IsPlan ? Plans : Tasks).Contains(row.Task) && string.IsNullOrWhiteSpace(SearchBox.Text);
    }

    private (int Gap, Border? Marker, bool After) DropPosition(ListBox list, Point point)
    {
        // Only realized rows are inspected; this also works with virtualization and pixel scrolling.
        for (var index = 0; index < list.Items.Count; index++)
        {
            if (list.ItemContainerGenerator.ContainerFromIndex(index) is not ListBoxItem item) continue;
            var top = item.TranslatePoint(new Point(), list).Y;
            if (point.Y < top + item.ActualHeight / 2)
                return (index, Descendant<Border>(item), false);
            if (point.Y < top + item.ActualHeight)
                return (index + 1, Descendant<Border>(item), true);
        }
        if (list.Items.Count == 0) return (0, null, true);
        var last = list.ItemContainerGenerator.ContainerFromIndex(list.Items.Count - 1) as ListBoxItem;
        return (list.Items.Count, last is null ? null : Descendant<Border>(last), true);
    }

    private void TaskDragOver(object sender, DragEventArgs e)
    {
        var list = (ListBox)sender;
        e.Handled = true;
        e.Effects = DragDropEffects.None;
        ClearDropMarker();
        if (!CanReorder(list, e.Data, out _)) return;
        e.Effects = DragDropEffects.Move;
        var point = e.GetPosition(list);
        if ((DateTime.UtcNow - lastDragScroll).TotalMilliseconds >= 65 && Descendant<ScrollViewer>(list) is { } scroll)
        {
            if (point.Y < 32) scroll.ScrollToVerticalOffset(scroll.VerticalOffset - 22);
            else if (point.Y > list.ActualHeight - 32) scroll.ScrollToVerticalOffset(scroll.VerticalOffset + 22);
            lastDragScroll = DateTime.UtcNow;
        }
        var position = DropPosition(list, point);
        if (position.Marker is not { } border) return;
        dropMarker = border; markerBrush = border.BorderBrush; markerThickness = border.BorderThickness;
        border.SetCurrentValue(Border.BorderBrushProperty, FindResource("AccentBrush"));
        border.SetCurrentValue(Border.BorderThicknessProperty, position.After ? new Thickness(0, 0, 0, 3) : new Thickness(0, 3, 0, 0));
    }

    private void ClearDropMarker()
    {
        if (dropMarker is null) return;
        dropMarker.SetCurrentValue(Border.BorderBrushProperty, markerBrush);
        dropMarker.SetCurrentValue(Border.BorderThicknessProperty, markerThickness);
        dropMarker = null;
    }
    private void TaskDragLeave(object sender, DragEventArgs e) => ClearDropMarker();
    private void TaskDrop(object sender, DragEventArgs e)
    {
        var list = (ListBox)sender;
        e.Handled = true;
        e.Effects = DragDropEffects.None;
        ClearDropMarker();
        if (!CanReorder(list, e.Data, out var row) || row is null) return;
        var position = DropPosition(list, e.GetPosition(list));
        if (Reorder(list, row, position.Gap)) e.Effects = DragDropEffects.Move;
    }
    private bool Reorder(ListBox list, TaskRow row, int gap)
    {
        if (!TaskOrdering.Move(row.IsPlan ? Plans : Tasks, row.Task, gap)) return false;
        var scroll = Descendant<ScrollViewer>(list);
        var offset = scroll?.VerticalOffset ?? 0;
        Save(); Rebuild();
        list.SelectedItem = (row.IsPlan ? view.NextTasks : view.TodayTasks).FirstOrDefault(item => item.Task == row.Task);
        list.UpdateLayout();
        scroll?.ScrollToVerticalOffset(offset);
        return true;
    }
    private void TaskListKeyDown(object sender, KeyEventArgs e)
    {
        // Pixel virtualization can leave WPF's geometric End navigation on the penultimate row.
        // Use item indices for the list boundaries; text editors retain their normal caret keys.
        if (Keyboard.Modifiers == ModifierKeys.None && e.Key is Key.Home or Key.End &&
            Keyboard.FocusedElement is not System.Windows.Controls.Primitives.TextBoxBase)
        {
            var boundaryList = (ListBox)sender;
            if (boundaryList.Items.Count == 0) return;
            e.Handled = true;
            boundaryList.SelectedIndex = e.Key == Key.Home ? 0 : boundaryList.Items.Count - 1;
            boundaryList.ScrollIntoView(boundaryList.SelectedItem);
            boundaryList.UpdateLayout();
            (boundaryList.ItemContainerGenerator.ContainerFromIndex(boundaryList.SelectedIndex) as ListBoxItem)?.Focus();
            return;
        }
        if (Keyboard.Modifiers != ModifierKeys.Alt || e.SystemKey is not (Key.Up or Key.Down)) return;
        e.Handled = true;
        if (inlineRow is not null) { SetStatus("並べ替える前に一覧の編集を保存または取り消してください。"); return; }
        if (!string.IsNullOrWhiteSpace(SearchBox.Text)) { SetStatus("並べ替えるには検索をクリアしてください。"); return; }
        var list = (ListBox)sender;
        if (list.SelectedItem is not TaskRow row) return;
        var index = (row.IsPlan ? Plans : Tasks).IndexOf(row.Task);
        if (Reorder(list, row, e.SystemKey == Key.Up ? index - 1 : index + 2)) list.ScrollIntoView(list.SelectedItem);
    }
}
