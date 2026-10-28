using System.Globalization;
using TimeTracker.Core;
using Xunit;

namespace TimeTracker.Core.Tests;

public class IssueRegressionTests
{
    [Theory]
    [InlineData("０", 0)]
    [InlineData("１．５", 1.5)]
    [InlineData("　２．２５　", 2.25)]
    [InlineData("１.5", 1.5)]
    public void FullWidthHoursWorkForPlannedAndActual(string input, double expected)
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("ja-JP");
            Assert.Equal(expected, DocumentValidation.ParseHours(input));
            Assert.Equal(expected, DocumentValidation.ParseHours(input, false));
        }
        finally { CultureInfo.CurrentCulture = previous; }
    }

    [Theory]
    [InlineData("－１")][InlineData("ＮａＮ")][InlineData("１ｅ９９９")][InlineData("１時間")]
    public void FullWidthInvalidHoursStillFail(string input) =>
        Assert.Throws<InvalidDataException>(() => DocumentValidation.ParseHours(input));

    [Fact]
    public void ReportTotalsUseSelectedDayAndNextDayAcrossAllProjects()
    {
        var day = new DateOnly(2026, 10, 22);
        var doc = new TrackerDocument();
        doc.Tasks(day).AddRange([new() { Project = "A", Name = "A", PlannedHours = 2, ActualSeconds = 5400 }, new() { Project = "B", Name = "B", PlannedHours = 5.5, ActualSeconds = 21600 }]);
        doc.Planned(day.AddDays(1)).Add(new() { Name = "明日", PlannedHours = 7.5 });
        doc.Tasks(day.AddDays(-1)).Add(new() { PlannedHours = 100, ActualSeconds = 360000 });
        doc.Planned(day.AddDays(2)).Add(new() { PlannedHours = 100 });
        var report = Reports.Daily(doc, day);
        Assert.Contains("【本日の作業】(予定合計: 7.5h / 実績合計: 7.5h)-----------------------------------", report);
        Assert.Contains("【次日の作業予定】(予定合計: 7.5h)------------------------------------------------", report);
        var parsed = TemplateParser.Parse(report);
        Assert.Equal(7.5, parsed.Today.Sum(task => task.PlannedHours));
        Assert.Equal(7.5, parsed.Next.Sum(task => task.PlannedHours));
    }

    [Fact]
    public void EmptyReportShowsZeroWithoutCreatingTasks()
    {
        var doc = new TrackerDocument();
        var report = Reports.Daily(doc, new DateOnly(2026, 10, 22));
        Assert.Contains("予定合計: 0.0h / 実績合計: 0.0h", report);
        Assert.Contains("【次日の作業予定】(予定合計: 0.0h)", report);
        Assert.Empty(doc.Days); Assert.Empty(doc.Plans);
    }
    [Theory]
    [InlineData(0)][InlineData(1)][InlineData(2)]
    public void DailyReportStartsWithTotalsAndHasOneBlankLineBeforeNextDay(int projects)
    {
        var day = new DateOnly(2026, 10, 23);
        var doc = new TrackerDocument();
        for (var index = 0; index < projects; index++)
            doc.Tasks(day).Add(new() { Project = $"案件{index}", Name = "作業", Notes = "詳細\n補足", PlannedHours = 1.5 });
        doc.Planned(day.AddDays(1)).Add(new() { Name = "次日作業", PlannedHours = 2 });
        var report = Reports.Daily(doc, day).Replace("\r\n", "\n");
        Assert.StartsWith("【本日の作業】(予定合計:", report);
        Assert.DoesNotContain("2026/10/23 作業報告", report);
        Assert.Contains("\n\n【次日の作業予定】(予定合計: 2.0h)", report);
        Assert.DoesNotContain("\n\n\n【次日の作業予定】", report);
        if (projects > 0) Assert.Contains("　　　詳細\n　　　補足", report);
    }

}
