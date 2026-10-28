using System.Text.Json;
using System.Windows;
using TimeTracker.Core;

namespace TimeTracker.Windows;

public partial class MainWindow
{
    private readonly OnboardingGuide guide = new(LoadGuideProgress());
    private bool assistanceStarted;
    private static string GuidePath => Path.Combine(App.DataDirectory, "onboarding.json");

    private static OnboardingProgress LoadGuideProgress()
    {
        try
        {
            if (!File.Exists(GuidePath) || new FileInfo(GuidePath).Length > 16384) return new();
            var progress = JsonSerializer.Deserialize<OnboardingProgress>(File.ReadAllText(GuidePath)) ?? new();
            progress.AutomaticDisplays = Math.Clamp(progress.AutomaticDisplays, 0, 3);
            return progress;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException) { return new(); }
    }
    private void SaveGuideProgress()
    {
        try
        {
            var temp = GuidePath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(guide.Progress));
            File.Move(temp, GuidePath, true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { SetStatus("導入ガイドの表示状態を保存できませんでした。"); }
    }
    internal void StartAssistance(bool automatic)
    {
        if (assistanceStarted) return;
        assistanceStarted = true;
        if (!automatic) return;
        IsVisibleChanged += (_, _) => { if (IsVisible) ShowAutomaticGuide(); };
        if (IsVisible) ShowAutomaticGuide();
    }
    private void ShowAutomaticGuide()
    {
        if (!guide.ShowAutomatic()) return;
        SaveGuideProgress(); RefreshDisplay();
    }
    private void RefreshGuide()
    {
        guide.Observe(engine.Running);
        view.GuideVisible = guide.Visible;
        view.GuideText = guide.Step == 0 ? "1/2　タスクを追加・選択して、［開始］で計測します。" : "2/2　［一時停止］のあと、編集から詳細メモを保存します。";
        view.GuideActionText = guide.Step == 0 && engine.Running is not null ? "次の手順" : "作業一覧";
    }
    private void ReplayGuide(object sender, RoutedEventArgs e)
    {
        if (!ChangeView(0)) return;
        guide.ShowManual(); RefreshDisplay();
    }
    private void CloseGuide(object sender, RoutedEventArgs e) { guide.Dismiss(); RefreshDisplay(); }
    private void GuideAction(object sender, RoutedEventArgs e)
    {
        if (!ChangeView(0)) return;
        if (guide.Step == 0 && engine.Running is { } running) guide.Started(running, engine.Document.SelectedDate);
        else if (guide.Step == 0 && Tasks.Count == 0) Add(false);
        RefreshDisplay();
    }
    private void GuideMemoSaved(WorkTask task, bool isPlan)
    {
        guide.Observe(engine.Running);
        if (!isPlan && guide.MemoSaved(task, engine.Document.SelectedDate)) SaveGuideProgress();
    }

}
