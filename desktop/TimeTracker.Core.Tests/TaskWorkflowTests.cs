using TimeTracker.Core;
using Xunit;

namespace TimeTracker.Core.Tests;

public class TaskWorkflowTests
{
    private static readonly DateOnly Day = new(2026, 10, 23);
    private static TrackerEngine Engine(TrackerDocument document, Func<DateTimeOffset>? clock = null) =>
        new(document, clock ?? (() => new DateTimeOffset(2026, 10, 23, 9, 0, 0, TimeSpan.Zero)), TimeZoneInfo.Utc);

    [Theory]
    [InlineData(true)][InlineData(false)]
    public void CopiesHaveNewIdsZeroActualAndOptionalNotes(bool notes)
    {
        var original = new WorkTask { Id = "original", Name = "作業", Project = "開発", PlannedHours = 0, ActualSeconds = 123.456, Notes = "行1\n行2" };
        var doc = new TrackerDocument { SelectedDate = Day }; doc.Tasks(Day).Add(original);
        var engine = Engine(doc); engine.Start(original.Id);
        Assert.Equal(1, TaskCopying.CopyTo(doc, [original], Day.AddDays(2), true, notes));
        var copy = doc.Planned(Day.AddDays(2)).Single();
        Assert.NotEqual(original.Id, copy.Id); Assert.Equal(original.Name, copy.Name); Assert.Equal(original.Project, copy.Project);
        Assert.Equal(0, copy.PlannedHours); Assert.Equal(0, copy.ActualSeconds); Assert.Equal(notes ? original.Notes : "", copy.Notes);
        Assert.Equal(123.456, original.ActualSeconds); Assert.Equal("行1\n行2", original.Notes); Assert.Same(original, engine.Running);
    }

    [Fact]
    public void CopyingIntoTheSourceListMaterializesBeforeAddingAndDoesNotRepeatSelection()
    {
        var doc = new TrackerDocument { SelectedDate = Day };
        doc.Tasks(Day).AddRange([new() { Name = "A" }, new() { Name = "B" }]);
        Assert.Equal(2, TaskCopying.CopyTo(doc, doc.Tasks(Day), Day, false, true));
        Assert.Equal(4, doc.Tasks(Day).Count);
        Assert.Equal(4, doc.Tasks(Day).Select(task => task.Id).Distinct().Count());
        var task = doc.Tasks(Day)[0];
        Assert.Equal(1, TaskCopying.CopyTo(doc, [task, task], Day, false, true));
    }

    [Fact]
    public void InvalidCopiesDoNotPartiallyModifyDestination()
    {
        var doc = new TrackerDocument { SelectedDate = Day };
        Assert.Throws<InvalidDataException>(() => TaskCopying.CopyTo(doc, [new() { Name = "Valid" }, new() { Name = "Bad", PlannedHours = -1 }], Day, false, true));
        Assert.Empty(doc.Days); Assert.Empty(doc.Plans);
        Assert.Throws<InvalidDataException>(() => TaskCopying.CopyTo(doc, [new() { Name = "Valid" }], default, false, true));
        Assert.Equal(0, TaskCopying.CopyTo(doc, [], Day, false, true)); Assert.Empty(doc.Days);
    }

    [Fact]
    public void UnchangedDraftActualPreservesSecondsAccumulatedWhileEditing()
    {
        var now = new DateTimeOffset(2026, 10, 23, 9, 0, 0, TimeSpan.Zero);
        var task = new WorkTask { Id = "a", Name = "A", ActualSeconds = 123.456789 };
        var doc = new TrackerDocument { SelectedDate = Day }; doc.Tasks(Day).Add(task);
        var engine = Engine(doc, () => now); engine.Start(task.Id);
        var draft = new TaskEditDraft(task) { PlannedText = "０", Notes = "編集中\nメモ" };
        now = now.AddSeconds(3.25); engine.Tick(); draft.Apply(engine, task, false);
        Assert.Equal(126.706789, task.ActualSeconds, 8); Assert.Equal(0, task.PlannedHours); Assert.Equal("編集中\nメモ", task.Notes);
        Assert.Same(task, engine.Running);
    }

    [Fact]
    public void DraftActualCorrectionRebasesRunningTimer()
    {
        var now = new DateTimeOffset(2026, 10, 23, 9, 0, 0, TimeSpan.Zero);
        var task = new WorkTask { Id = "a", Name = "A", ActualSeconds = 50 };
        var doc = new TrackerDocument { SelectedDate = Day }; doc.Tasks(Day).Add(task);
        var engine = Engine(doc, () => now); engine.Start(task.Id);
        new TaskEditDraft(task) { PlannedText = "１．５", ActualText = "０．５" }.Apply(engine, task, false);
        now = now.AddSeconds(2.75); engine.Tick();
        Assert.Equal(1802.75, task.ActualSeconds); Assert.Equal(1.5, task.PlannedHours);
    }

    [Theory]
    [InlineData("-1", "1")][InlineData("2", "invalid")][InlineData("2", "1e308")]
    public void InvalidDraftDoesNotChangeLiveTaskOrRunningTimer(string planned, string actual)
    {
        var task = new WorkTask { Id = "a", Name = "A", PlannedHours = 1, ActualSeconds = 12, Notes = "元メモ" };
        var doc = new TrackerDocument { SelectedDate = Day }; doc.Tasks(Day).Add(task);
        var engine = Engine(doc); engine.Start(task.Id);
        var draft = new TaskEditDraft(task) { PlannedText = planned, ActualText = actual, Notes = "変更" };
        Assert.Throws<InvalidDataException>(() => draft.Apply(engine, task, false));
        Assert.Equal(1, task.PlannedHours); Assert.Equal(12, task.ActualSeconds); Assert.Equal("元メモ", task.Notes); Assert.Same(task, engine.Running);
    }

    [Fact]
    public void PlanDraftDoesNotModifyActualAndUnappliedDraftDoesNotTouchModel()
    {
        var task = new WorkTask { Name = "予定", ActualSeconds = 12.345 };
        var doc = new TrackerDocument { SelectedDate = Day }; doc.Planned(Day.AddDays(1)).Add(task);
        var draft = new TaskEditDraft(task) { PlannedText = "", ActualText = "invalid", Notes = "保存後" };
        Assert.True(draft.IsDirty); Assert.Equal("", task.Notes); Assert.Equal(12.345, task.ActualSeconds);
        draft.Apply(Engine(doc), task, true);
        Assert.Equal(1, task.PlannedHours); Assert.Equal(12.345, task.ActualSeconds); Assert.Equal("保存後", task.Notes);
    }
}
