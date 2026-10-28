using TimeTracker.Core;
using Xunit;

namespace TimeTracker.Core.Tests;

public class PlannedOverrunAlarmTests
{
    [Fact]
    public void AlertsOnlyAfterBudgetAndDoesNotRepeatAfterPause()
    {
        var alarm = new PlannedOverrunAlarm();
        var task = new WorkTask { PlannedHours = 1, ActualSeconds = 3599 };
        Assert.False(alarm.ShouldNotify(task, true));
        task.ActualSeconds = 3600;
        Assert.False(alarm.ShouldNotify(task, true));
        task.ActualSeconds = 3601;
        Assert.True(alarm.ShouldNotify(task, true));
        Assert.False(alarm.ShouldNotify(task, true));
        Assert.False(alarm.ShouldNotify(null, true));
        Assert.False(alarm.ShouldNotify(task, true));
    }

    [Fact]
    public void DisabledAlarmDoesNotConsumeNotificationAndZeroHoursAreSupported()
    {
        var alarm = new PlannedOverrunAlarm();
        var task = new WorkTask { PlannedHours = 0 };
        Assert.False(alarm.ShouldNotify(task, true));
        task.ActualSeconds = 1;
        Assert.False(alarm.ShouldNotify(task, false));
        Assert.True(alarm.ShouldNotify(task, true));
        Assert.False(alarm.ShouldNotify(task, false));
        Assert.False(alarm.ShouldNotify(task, true));
    }

    [Fact]
    public void SeparateTasksAndChangedBudgetsCanAlertAgain()
    {
        var alarm = new PlannedOverrunAlarm();
        var task = new WorkTask { PlannedHours = 1, ActualSeconds = 4000 };
        Assert.True(alarm.ShouldNotify(task, true));
        Assert.True(alarm.ShouldNotify(new WorkTask { PlannedHours = 1, ActualSeconds = 4000 }, true));
        task.PlannedHours = 2;
        Assert.False(alarm.ShouldNotify(task, true));
        task.ActualSeconds = 7201;
        Assert.True(alarm.ShouldNotify(task, true));
        task.ActualSeconds = 0;
        Assert.False(alarm.ShouldNotify(task, true));
        task.ActualSeconds = 7201;
        Assert.True(alarm.ShouldNotify(task, true));
    }

    [Fact]
    public void SettingDefaultsToOffAndSurvivesBackup()
    {
        Assert.False(new Preferences().EnablePlannedOverrunAlert);
        var doc = new TrackerDocument();
        doc.Settings.EnablePlannedOverrunAlert = true;
        Assert.True(doc.Copy().Settings.EnablePlannedOverrunAlert);
    }
}
