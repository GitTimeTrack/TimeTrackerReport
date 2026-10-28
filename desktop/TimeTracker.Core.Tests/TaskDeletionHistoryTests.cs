using TimeTracker.Core;
using Xunit;

namespace TimeTracker.Core.Tests;

public class TaskDeletionHistoryTests
{
    private static readonly DateOnly Day = new(2026, 10, 23);
    private static TrackerEngine Engine(TrackerDocument doc, Func<DateTimeOffset>? clock = null) =>
        new(doc, clock ?? (() => new DateTimeOffset(2026, 10, 23, 9, 0, 0, TimeSpan.Zero)), TimeZoneInfo.Utc);

    [Fact]
    public void RunningTaskRestoresItsDetailsAndSecondsAtOriginalPositionWithoutResuming()
    {
        var now = new DateTimeOffset(2026, 10, 23, 9, 0, 0, TimeSpan.Zero);
        var doc = new TrackerDocument { SelectedDate = Day };
        doc.Tasks(Day).AddRange([new() { Id = "a", Name = "A" }, new() { Id = "b", Project = "開発", Name = "B", PlannedHours = 0, ActualSeconds = 12.345, Notes = "行1\n行2" }, new() { Id = "c", Name = "C" }]);
        var engine = Engine(doc, () => now); var history = new TaskDeletionHistory();
        engine.Start("b"); now = now.AddSeconds(3.5);
        Assert.True(history.Delete(engine, "b", false));
        Assert.Null(engine.Running);
        Assert.Equal(new DeletedTaskInfo("B", Day, false), history.Pending(doc));
        now = now.AddHours(1);
        Assert.True(history.Undo(doc));
        Assert.Equal(new[] { "a", "b", "c" }, doc.Tasks(Day).Select(task => task.Id));
        var restored = doc.Tasks(Day)[1];
        Assert.Equal(15.845, restored.ActualSeconds, 8);
        Assert.Equal(0, restored.PlannedHours); Assert.Equal("開発", restored.Project); Assert.Equal("行1\n行2", restored.Notes);
        engine.Tick(); Assert.Equal(15.845, restored.ActualSeconds, 8); Assert.Null(engine.Running);
        Assert.False(history.Undo(doc)); Assert.Null(history.Pending(doc));
    }

    [Fact]
    public void PlansRestoreToOriginalDateAfterNavigationAndClampTheirPosition()
    {
        var doc = new TrackerDocument { SelectedDate = Day };
        doc.Planned(Day.AddDays(1)).AddRange([new() { Id = "a", Name = "A" }, new() { Id = "b", Name = "B", Notes = "明日" }]);
        var engine = Engine(doc); var history = new TaskDeletionHistory();
        history.Delete(engine, "b", true);
        doc.Planned(Day.AddDays(1)).Clear();
        engine.SelectDate(Day.AddDays(5));
        Assert.True(history.Undo(doc));
        Assert.Equal("b", doc.Planned(Day.AddDays(1)).Single().Id);
        Assert.Empty(doc.Planned(Day.AddDays(6)));
        Assert.Equal(Day.AddDays(5), doc.SelectedDate);
    }

    [Fact]
    public void OnlyLatestDeletionCanBeRestoredAndMissingDeleteDoesNotReplaceIt()
    {
        var doc = new TrackerDocument { SelectedDate = Day };
        doc.Tasks(Day).AddRange([new() { Id = "a", Name = "A" }, new() { Id = "b", Name = "B" }]);
        var engine = Engine(doc); var history = new TaskDeletionHistory();
        history.Delete(engine, "a", false); history.Delete(engine, "b", false);
        Assert.False(history.Delete(engine, "missing", false));
        Assert.Equal("B", history.Pending(doc)!.Name);
        Assert.True(history.Undo(doc));
        Assert.Equal("b", doc.Tasks(Day).Single().Id);
        Assert.False(history.Undo(doc));
    }

    [Fact]
    public void RestoringDoesNotStopAnUnrelatedRunningTask()
    {
        var doc = new TrackerDocument { SelectedDate = Day };
        doc.Tasks(Day).AddRange([new() { Id = "a", Name = "A" }, new() { Id = "b", Name = "B" }]);
        var engine = Engine(doc); var history = new TaskDeletionHistory();
        history.Delete(engine, "a", false); engine.Start("b");
        history.Undo(doc);
        Assert.Equal("b", engine.Running!.Id);
    }

    [Fact]
    public void DuplicateIdsAreNotOverwrittenAndHistoryRemainsAvailable()
    {
        var doc = new TrackerDocument { SelectedDate = Day };
        doc.Tasks(Day).Add(new() { Id = "a", Name = "Original" });
        var history = new TaskDeletionHistory(); history.Delete(Engine(doc), "a", false);
        doc.Tasks(Day).Add(new() { Id = "a", Name = "Existing" });
        Assert.Throws<InvalidOperationException>(() => history.Undo(doc));
        Assert.Equal("Existing", doc.Tasks(Day).Single().Name);
        Assert.NotNull(history.Pending(doc));
    }

    [Fact]
    public void BackupReplacementDoesNotResurrectTasksFromOldDocument()
    {
        var doc = new TrackerDocument { SelectedDate = Day }; doc.Tasks(Day).Add(new() { Id = "a", Name = "A" });
        var engine = Engine(doc); var history = new TaskDeletionHistory(); history.Delete(engine, "a", false);
        var replacement = new TrackerDocument { SelectedDate = Day }; engine.Replace(replacement);
        Assert.Null(history.Pending(replacement)); Assert.False(history.Undo(replacement)); Assert.Empty(replacement.Tasks(Day));
        history.Clear(); Assert.Null(history.Pending(doc));
    }
}
