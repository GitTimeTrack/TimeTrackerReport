using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace TimeTracker.Windows;

public partial class MainWindow
{
    private void VerifyBranding()
    {
        UpdateLayout();
        var text = new FormattedText(BrandName.Text, System.Globalization.CultureInfo.CurrentCulture,
            BrandName.FlowDirection, new Typeface(BrandName.FontFamily, BrandName.FontStyle, BrandName.FontWeight, BrandName.FontStretch),
            BrandName.FontSize, BrandName.Foreground, VisualTreeHelper.GetDpi(BrandName).PixelsPerDip);
        if (Title != "TimeTrackerReport" || BrandName.Text != Title || text.WidthIncludingTrailingWhitespace > BrandName.ActualWidth ||
            BrandIcon.Source is not DrawingImage || Icon is null)
            throw new InvalidOperationException("App branding is inconsistent or does not fit the sidebar.");
    }

    private void VerifyVectorClock()
    {
        if (BrandIcon.Source is not DrawingImage clock || clock.Drawing.Bounds != new Rect(0, 0, 256, 256))
            throw new InvalidOperationException("The sidebar clock must use resolution-independent vector geometry.");
        foreach (var scale in new[] { 1d, 1.5, 2, 3 })
        {
            var visual = new DrawingVisual();
            using (var drawing = visual.RenderOpen()) drawing.DrawImage(clock, new Rect(0, 0, 48, 48));
            var size = (int)(48 * scale);
            var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(size, size, 96 * scale, 96 * scale, PixelFormats.Pbgra32);
            bitmap.Render(visual);
            var pixels = new byte[size * size * 4]; bitmap.CopyPixels(pixels, size * 4, 0);
            if (pixels[(size / 2 * size + size / 2) * 4 + 3] != 255)
                throw new InvalidOperationException("The vector clock did not render at display scale " + scale);
            var screenshot = Environment.GetEnvironmentVariable("TIMETRACKER_SMOKE_SCREENSHOT");
            if (string.IsNullOrEmpty(screenshot)) continue;
            var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
            encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
            using var output = File.Create(Path.Combine(Path.GetDirectoryName(screenshot)!, $"clock-preview-{scale * 100:0}.png"));
            encoder.Save(output);
        }
    }

    private void VerifyScheduleAppearance()
    {
        var previousView = Views.SelectedIndex;
        var previousTab = ScheduleTabs.SelectedIndex;
        Views.SelectedIndex = 0;
        var tabs = new[] { TodayScheduleTab, NextScheduleTab };
        foreach (var selected in new[] { 0, 1 })
        {
            ScheduleTabs.SelectedIndex = selected; UpdateLayout();
            for (var index = 0; index < tabs.Length; index++)
            {
                var tab = tabs[index];
                var frame = (Border)tab.Template.FindName("ScheduleTabFrame", tab);
                var mark = (Border)tab.Template.FindName("SelectedMark", tab);
                var expectedBackground = (SolidColorBrush)FindResource(index == selected ? "SurfaceBrush" : tab.IsMouseOver ? "HoverBrush" : "CanvasBrush");
                var expectedText = (SolidColorBrush)FindResource(index == selected ? "AccentBrush" : tab.IsMouseOver ? "TextBrush" : "MutedBrush");
                var label = ((StackPanel)tab.Header).Children.OfType<TextBlock>().Last();
                if (((SolidColorBrush)frame.Background).Color != expectedBackground.Color ||
                    ((SolidColorBrush)label.Foreground).Color != expectedText.Color ||
                    (mark.Visibility == Visibility.Visible) != (selected == index))
                    throw new InvalidOperationException($"Schedule tab color mismatch: {PreviewColor}/{PreviewMode}, tab {index}, selected {selected}, hover {tab.IsMouseOver}, background {frame.Background} expected {expectedBackground}, text {label.Foreground} expected {expectedText}, marker {mark.Visibility}.");
                if (frame.CornerRadius != new CornerRadius(10, 10, 0, 0) ||
                    selected == index && AutomationProperties.GetItemStatus(tab) != "選択中")
                    throw new InvalidOperationException("Schedule tab appearance or accessibility is incorrect.");
            }
            var surface = (Border)ScheduleTabs.Template.FindName("ScheduleFrame", ScheduleTabs);
            var headers = (TabPanel)ScheduleTabs.Template.FindName("ScheduleHeaders", ScheduleTabs);
            var selectedFrame = (Border)tabs[selected].Template.FindName("ScheduleTabFrame", tabs[selected]);
            var frameTop = surface.TranslatePoint(new Point(), ScheduleTabs).Y;
            var tabBottom = selectedFrame.TranslatePoint(new Point(0, selectedFrame.ActualHeight), ScheduleTabs).Y;
            if (Math.Abs(tabBottom - frameTop - 1) > .5 || headers.Margin.Bottom != -1 ||
                selectedFrame.BorderThickness.Bottom != 0 ||
                ((SolidColorBrush)selectedFrame.Background).Color != ((SolidColorBrush)surface.Background).Color)
                throw new InvalidOperationException("Selected schedule tab is not joined to the list surface.");
            CaptureAssistancePreview($"schedule-preview-{PreviewColor}-{PreviewMode}-{selected}.png");
        }
        var outer = (Border)ScheduleTabs.Template.FindName("ScheduleFrame", ScheduleTabs);
        var input = (Border)SearchBox.Template.FindName("InputFrame", SearchBox);
        if (outer.CornerRadius.TopLeft != 14 || input.CornerRadius.TopLeft != 8 ||
            SearchBox.Template.FindName("PART_ContentHost", SearchBox) is not ScrollViewer ||
            !view.TodayTabLabel.StartsWith("本日の予定", StringComparison.Ordinal))
            throw new InvalidOperationException("Rounded schedule frame, search editor or tab name is incorrect.");
        ScheduleTabs.SelectedIndex = previousTab; Views.SelectedIndex = previousView; UpdateLayout();
    }

    private static void VerifyHourDisplayPrecision()
    {
        foreach (var hours in new[] { 0, 1, 1.234, 12.987 })
        {
            var task = new TimeTracker.Core.WorkTask { PlannedHours = hours, ActualSeconds = hours * 3600 };
            var row = new TaskRow(task, false, () => null);
            var formatted = hours.ToString("0.0") + "h";
            if (row.PlannedValue != formatted || row.ActualValue != formatted ||
                row.PlannedLabel != "予定 " + formatted || row.ActualLabel != "実績 " + formatted ||
                task.PlannedHours != hours || task.ActualSeconds != hours * 3600)
                throw new InvalidOperationException("Hour display changed precision or underlying work values.");
        }
    }

    private void VerifyNavigationSelection()
    {
        var buttons = new[] { Nav0, Nav1, Nav2, Nav3 };
        foreach (var selected in new[] { 0, 1, 2, 3, 0 })
        {
            Views.SelectedIndex = selected;
            UpdateLayout();
            for (var index = 0; index < buttons.Length; index++)
            {
                var marker = (Border)buttons[index].Template.FindName("CurrentMarker", buttons[index]);
                if (Selector.GetIsSelected(buttons[index]) != (selected == index) ||
                    (marker.Visibility == Visibility.Visible) != (selected == index))
                    throw new InvalidOperationException("Navigation selection is out of sync.");
                var label = Descendant<TextBlock>(buttons[index])!;
                var foreground = (SolidColorBrush)label.Foreground;
                var expected = (SolidColorBrush)FindResource(selected == index ? "OnPrimaryBrush" : "OnDarkBrush");
                if (foreground.Color != expected.Color) throw new InvalidOperationException("Navigation label lost its contrasting color.");
                if (selected == index && AutomationProperties.GetItemStatus(buttons[index]) != "表示中")
                    throw new InvalidOperationException("Navigation selection is not accessible.");
            }
        }
    }

    private void VerifyRunningRow()
    {
        var first = Tasks[0]; var second = Tasks[1];
        TodayList.SelectedItem = view.TodayTasks[1];
        if (!view.TodayTasks[0].IsRunning || view.TodayTasks[1].IsRunning || view.NextTasks.Any(row => row.IsRunning))
            throw new InvalidOperationException("Row selection changed the running indicator.");
        TodayList.ScrollIntoView(view.TodayTasks[0]); UpdateLayout();
        var item = (ListBoxItem)TodayList.ItemContainerGenerator.ContainerFromIndex(0);
        var content = Descendant<ContentPresenter>(item)!;
        var badge = (TextBlock)content.ContentTemplate.FindName("RunningBadge", content);
        if (badge.Visibility != Visibility.Visible || badge.Text != "● 計測中")
            throw new InvalidOperationException("Running badge is not visible.");
        StartTask(second.Id);
        if (view.TodayTasks[0].IsRunning || !view.TodayTasks[1].IsRunning)
            throw new InvalidOperationException("Running badge did not follow task switching.");
        engine.Pause(); RefreshDisplay();
        if (view.TodayTasks.Any(row => row.IsRunning)) throw new InvalidOperationException("Paused task is still marked running.");
        TodayList.ScrollIntoView(view.TodayTasks[1]); UpdateLayout();
        item = (ListBoxItem)TodayList.ItemContainerGenerator.ContainerFromIndex(1);
        content = Descendant<ContentPresenter>(item)!;
        badge = (TextBlock)content.ContentTemplate.FindName("RunningBadge", content);
        if (badge.Visibility != Visibility.Collapsed) throw new InvalidOperationException("Paused badge is still visible.");
        StartTask(first.Id);
        SearchBox.Text = "Windows smoke test"; UpdateLayout();
        if (view.TodayTasks.Count != 1 || !view.TodayTasks[0].IsRunning || view.ActiveName != first.Name)
            throw new InvalidOperationException("Filtered running indicator is incorrect.");
        SearchBox.Text = "次日の予定";
        if (view.TodayTasks.Count != 0 || view.ActiveName != first.Name) throw new InvalidOperationException("Hidden running task was lost.");
        SearchBox.Clear(); UpdateLayout();
    }

    private static void RaiseListKey(ListBox list, Key key)
    {
        var preview = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(list), Environment.TickCount, key) { RoutedEvent = Keyboard.PreviewKeyDownEvent };
        list.RaiseEvent(preview);
        if (!preview.Handled)
            list.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(list), Environment.TickCount, key) { RoutedEvent = Keyboard.KeyDownEvent });
    }

    private void VerifyModernScrollbar(ListBox list)
    {
        var scroll = Descendant<ScrollViewer>(list)!;
        scroll.ScrollToTop(); UpdateLayout();
        var bar = (ScrollBar)scroll.Template.FindName("PART_VerticalScrollBar", scroll);
        var track = bar.Track ?? throw new InvalidOperationException("Scrollbar track is missing.");
        var thumb = track.Thumb;
        var grip = (Border)thumb.Template.FindName("Grip", thumb);
        if (Math.Abs(bar.ActualWidth - 16) > .1 || Math.Abs(grip.ActualWidth - 6) > .1 || grip.CornerRadius.TopLeft != 3)
            throw new InvalidOperationException("Scrollbar proportions do not match the design.");
        if (!bar.IsEnabled || thumb.ActualHeight < 28 || grip.Background is not SolidColorBrush)
            throw new InvalidOperationException("Scrollbar thumb is not usable.");
        ScrollBar.PageDownCommand.Execute(null, bar); UpdateLayout();
        if (scroll.VerticalOffset <= 0) throw new InvalidOperationException("Scrollbar page-down command failed.");
        var afterPage = scroll.VerticalOffset;
        ScrollBar.PageUpCommand.Execute(null, bar); UpdateLayout();
        if (scroll.VerticalOffset >= afterPage) throw new InvalidOperationException("Scrollbar page-up command failed.");
        scroll.ScrollToTop(); UpdateLayout();
        thumb.RaiseEvent(new DragStartedEventArgs(0, 0) { RoutedEvent = Thumb.DragStartedEvent });
        thumb.RaiseEvent(new DragDeltaEventArgs(0, 50) { RoutedEvent = Thumb.DragDeltaEvent });
        thumb.RaiseEvent(new DragCompletedEventArgs(0, 50, false) { RoutedEvent = Thumb.DragCompletedEvent });
        UpdateLayout();
        if (scroll.VerticalOffset <= 0) throw new InvalidOperationException("Scrollbar thumb drag did not scroll the list.");
        scroll.ScrollToTop(); UpdateLayout();
        scroll.RaiseEvent(new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, -120) { RoutedEvent = UIElement.MouseWheelEvent });
        UpdateLayout();
        if (scroll.VerticalOffset <= 0) throw new InvalidOperationException("Mouse wheel did not scroll the list.");
        list.Focus();
        RaiseListKey(list, Key.End);
        UpdateLayout();
        if (list.SelectedIndex != list.Items.Count - 1) throw new InvalidOperationException($"Keyboard End did not select the final task: {list.Name}, selected {list.SelectedIndex}/{list.Items.Count}, focus {Keyboard.FocusedElement?.GetType().Name}.");
        RaiseListKey(list, Key.Home);
        UpdateLayout();
        if (list.SelectedIndex != 0) throw new InvalidOperationException("Keyboard Home did not select the first task.");
        scroll.ScrollToBottom(); UpdateLayout();
        if (Math.Abs(scroll.VerticalOffset - scroll.ScrollableHeight) > 1)
            throw new InvalidOperationException("Scrollbar cannot reach the bottom.");
        scroll.ScrollToTop(); UpdateLayout();
    }
}
