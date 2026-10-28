using TimeTracker.Core;
using Xunit;

namespace TimeTracker.Core.Tests;

public class TeamAssistanceTests
{
    private static readonly DateOnly Day = new(2026, 10, 23);
    private static readonly WorkTask TaskItem = new() { Id = "a", Name = "A", Notes = "メモ" };

    [Fact]
    public void AutomaticGuideCountsOncePerRunAndStopsAfterThreeRuns()
    {
        var progress = new OnboardingProgress();
        for (var count = 1; count <= 3; count++)
        {
            var guide = new OnboardingGuide(progress);
            Assert.True(guide.ShowAutomatic()); guide.Dismiss();
            Assert.False(guide.ShowAutomatic()); Assert.Equal(count, progress.AutomaticDisplays);
        }
        Assert.False(new OnboardingGuide(progress).ShowAutomatic());
    }
    [Fact]
    public void GuideCompletesOnlyForTheStartedStoppedTaskAndSavedMemo()
    {
        var guide = new OnboardingGuide(new()); guide.ShowAutomatic(); guide.Started(TaskItem, Day);
        guide.Observe(TaskItem); Assert.False(guide.MemoSaved(TaskItem, Day));
        guide.Observe(null);
        Assert.False(guide.MemoSaved(new WorkTask { Id = "other", Notes = "メモ" }, Day));
        Assert.False(guide.MemoSaved(TaskItem, Day.AddDays(1)));
        Assert.False(guide.MemoSaved(new WorkTask { Id = "a", Notes = "" }, Day));
        Assert.True(guide.MemoSaved(TaskItem, Day)); Assert.True(guide.Progress.Completed); Assert.False(guide.Visible);
        Assert.False(new OnboardingGuide(guide.Progress).ShowAutomatic());
    }
    [Fact]
    public void ReplayDoesNotResetSavedCompletionOrAutomaticCount()
    {
        var progress = new OnboardingProgress { AutomaticDisplays = 3, Completed = true };
        var guide = new OnboardingGuide(progress); guide.ShowManual();
        Assert.True(guide.Visible); Assert.Equal(0, guide.Step); Assert.Equal(3, progress.AutomaticDisplays); Assert.True(progress.Completed);
        guide.Dismiss(); Assert.False(guide.Visible); Assert.False(guide.ShowAutomatic());
    }
}
