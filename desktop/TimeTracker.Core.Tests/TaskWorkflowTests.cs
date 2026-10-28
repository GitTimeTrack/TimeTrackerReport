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

    [Fact]
    public void SelectingNextDayAutomaticallyCarriesNextDayPlansIntoTodayTasksOnce()
    {
        var doc = new TrackerDocument { SelectedDate = Day };
        doc.Planned(Day.AddDays(1)).AddRange([
            new WorkTask { Id = "plan1", Project = "開発", Name = "設計レビュー", PlannedHours = 1.5, Notes = "メモ1" },
            new WorkTask { Id = "plan2", Project = "運用", Name = "定例会", PlannedHours = 0.5, Notes = "メモ2" }
        ]);
        var engine = Engine(doc);
        engine.SelectDate(Day.AddDays(1));
        var carried = doc.Tasks(Day.AddDays(1));
        Assert.Equal(2, carried.Count);
        Assert.Equal(new[] { "設計レビュー", "定例会" }, carried.Select(t => t.Name));
        Assert.Equal(new[] { "開発", "運用" }, carried.Select(t => t.Project));
        Assert.Equal(new[] { 1.5, 0.5 }, carried.Select(t => t.PlannedHours));
        Assert.Equal(new[] { "メモ1", "メモ2" }, carried.Select(t => t.Notes));
        Assert.All(carried, t => Assert.Equal(0, t.ActualSeconds));
        Assert.Equal(2, doc.Planned(Day.AddDays(1)).Count);
        // Re-selecting the same date must not duplicate already-carried tasks.
        engine.SelectDate(Day);
        engine.SelectDate(Day.AddDays(1));
        Assert.Equal(2, doc.Tasks(Day.AddDays(1)).Count);
    }

    [Fact]
    public void CopyingToPlannedDateAlsoUpdatesInitializedDayTasks()
    {
        var doc = new TrackerDocument { SelectedDate = Day };
        var engine = Engine(doc);
        doc.Planned(Day.AddDays(1)).Add(new WorkTask { Id = "p1", Project = "A", Name = "先行予定", PlannedHours = 1 });
        engine.SelectDate(Day.AddDays(1));
        Assert.Single(doc.Tasks(Day.AddDays(1)));
        engine.SelectDate(Day);
        var added = new WorkTask { Id = "today1", Project = "B", Name = "本日の予定から追加", PlannedHours = 2, Notes = "引継ぎ" };
        doc.Tasks(Day).Add(added);
        Assert.Equal(1, TaskCopying.CopyTo(doc, [added], Day.AddDays(1), true, true));
        Assert.Equal(2, doc.Planned(Day.AddDays(1)).Count);
        Assert.Equal(2, doc.Tasks(Day.AddDays(1)).Count);
        Assert.Equal("本日の予定から追加", doc.Tasks(Day.AddDays(1))[1].Name);
    }
}
