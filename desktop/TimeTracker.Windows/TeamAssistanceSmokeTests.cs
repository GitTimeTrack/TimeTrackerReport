using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using TimeTracker.Core;

namespace TimeTracker.Windows;

public partial class MainWindow
{
    private void CaptureAssistancePreview(string name)
    {
        var screenshot = Environment.GetEnvironmentVariable("TIMETRACKER_SMOKE_SCREENSHOT");
        if (string.IsNullOrEmpty(screenshot)) return;
        UpdateLayout();
        var image = new RenderTargetBitmap((int)ActualWidth, (int)ActualHeight, 96, 96, PixelFormats.Pbgra32);
        image.Render(this);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
        using var output = File.Create(Path.Combine(Path.GetDirectoryName(screenshot)!, name)); encoder.Save(output);
    }
    private void RunAssistanceSmokeTest()
    {
        App.TraceSmoke("Team assistance smoke test");
        ChangeView(0); ScheduleTabs.SelectedIndex = 0;
        var count = Tasks.Count;
        ShowAutomaticGuide(); UpdateLayout();
        if (!GuideBanner.IsVisible || guide.Progress.AutomaticDisplays != 1 || LoadGuideProgress().AutomaticDisplays != 1)
            throw new InvalidOperationException("Automatic guide was not displayed and persisted.");
        CaptureAssistancePreview("guide-preview.png");
        CloseGuide(this, new RoutedEventArgs()); ShowAutomaticGuide();
        if (guide.Visible || guide.Progress.AutomaticDisplays != 1) throw new InvalidOperationException("Guide was shown twice in the same run.");
        ReplayGuide(this, new RoutedEventArgs());
        var task = Tasks[0]; StartTask(task.Id); UpdateLayout();
        if (guide.Step != 1 || !view.GuideText.StartsWith("2/2", StringComparison.Ordinal))
            throw new InvalidOperationException("Guide did not follow a real timer start.");
        engine.Pause(); Rebuild();
        var row = view.TodayTasks.First(item => item.Task.Id == task.Id);
        row.BeginInlineEdit(); inlineRow = row; row.Draft!.Notes = "導入ガイドのメモ保存確認";
        if (!SaveInlineEdit() || guide.Visible || !LoadGuideProgress().Completed || Tasks.Count != count)
            throw new InvalidOperationException("Guide did not complete after stop and memo save, or created fake work records.");
        ReplayGuide(this, new RoutedEventArgs());
        if (!guide.Visible || guide.Step != 0 || !guide.Progress.Completed || guide.Progress.AutomaticDisplays != 1)
            throw new InvalidOperationException("Manual guide replay reset progress.");
        CloseGuide(this, new RoutedEventArgs());
        ChangeView(3); LoadSettings();
        VersionText.BringIntoView(); UpdateLayout();
        if (!VersionText.IsVisible || VersionText.Text != $"現在のバージョン：{App.VersionText}" || !Version.TryParse(App.VersionText, out _))
            throw new InvalidOperationException("Current app version is not visible or does not match the assembly.");
        CaptureAssistancePreview("version-preview.png");
    }
}
