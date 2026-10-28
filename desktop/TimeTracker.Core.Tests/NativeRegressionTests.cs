using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using TimeTracker.Core;
using Xunit;

namespace TimeTracker.Core.Tests;

public sealed class NativeRegressionTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "TimeTrackerTests-" + Guid.NewGuid().ToString("N"));
    private readonly DateOnly today = new(2026, 10, 21);
    private DateTimeOffset now = new(2026, 10, 21, 9, 0, 0, TimeSpan.Zero);
    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(directory)) Directory.Delete(directory, true);
    }
    private TrackerDocument Document() => new()
    {
        SelectedDate = today, Days = { [today] = [new WorkTask { Id = "a", Project = "constructor", Name = "作業A", PlannedHours = 0, Notes = "メモ" }, new WorkTask { Id = "b", Name = "作業B" }] }
    };
    private TrackerEngine Engine(TrackerDocument? doc = null) => new(doc ?? Document(), () => now, TimeZoneInfo.Utc);
    private TrackerStore Store() => new(Path.Combine(directory, "tracker.db"));
    private string Legacy(object? tasks = null, object? settings = null) => JsonSerializer.Serialize(new
    {
        version = "v6", selectedDate = "2026-10-21", savedAt = now.ToUnixTimeMilliseconds(),
        tasks = tasks ?? new[] { new { id = "old", project = "__proto__", name = "旧作業", plannedHours = 0, actualSeconds = 37.25, subtasks = "引継ぎ\nメモ", isRunning = true } },
        nextDayTasks = new[] { new { id = "next", project = "constructor", name = "翌日の作業", plannedHours = 0, subtasks = "" } },
        history = new Dictionary<string, object> { ["2026-10-20"] = new { tasks = new[] { new { id = "past", project = "過去", name = "履歴", plannedHours = 1.5, actualSeconds = 123.456, subtasks = "" } } } },
        settings = settings ?? new { enableLongAlert = false, longAlertMinutes = 30, enableIdleAlert = true, idleAlertMinutes = 5 }
    });

    [Theory]
    [InlineData("0", 0)]
    [InlineData("0.0", 0)]
    [InlineData("0.5", 0.5)]
    [InlineData("", 1)]
    public void HoursPreserveZeroAndDefaultOnlyBlank(string text, double expected)
    {
        var original = CultureInfo.CurrentCulture;
        try { CultureInfo.CurrentCulture = CultureInfo.InvariantCulture; Assert.Equal(expected, DocumentValidation.ParseHours(text)); }
        finally { CultureInfo.CurrentCulture = original; }
    }
    [Theory]
    [InlineData("-1")][InlineData("NaN")][InlineData("Infinity")][InlineData("1e999")][InlineData("invalid")]
    public void InvalidHoursAreRejected(string text) => Assert.Throws<InvalidDataException>(() => DocumentValidation.ParseHours(text));

    [Fact]
    public void SwitchingTasksKeepsOnlyOneTimerAndAccurateSeconds()
    {
        var engine = Engine(); engine.Start("a"); now = now.AddSeconds(61.75); engine.Tick(); engine.Start("b");
        now = now.AddSeconds(2.25); engine.Pause();
        Assert.Equal(61.75, engine.Document.Tasks(today)[0].ActualSeconds);
        Assert.Equal(2.25, engine.Document.Tasks(today)[1].ActualSeconds);
        Assert.Null(engine.Running);
    }
    [Fact]
    public void IdleCutoffExcludesTheWholeIdleInterval()
    {
        var engine = Engine(); engine.Start("a"); now = now.AddMinutes(20); engine.Pause(now.AddMinutes(-15));
        Assert.Equal(300, engine.Document.Tasks(today)[0].ActualSeconds);
        now = now.AddMinutes(10); engine.Start("a"); now = now.AddSeconds(5); engine.Pause();
        Assert.Equal(305, engine.Document.Tasks(today)[0].ActualSeconds);
    }
    [Fact]
    public void ActualCorrectionDoesNotRecountEarlierElapsedTime()
    {
        var engine = Engine(); engine.Start("a"); now = now.AddSeconds(20); engine.SetActual(engine.Running!, 3600);
        now = now.AddSeconds(3.5); engine.Tick(); Assert.Equal(3603.5, engine.Running!.ActualSeconds);
    }
    [Fact]
    public void CrashRecoveryNeverAddsOfflineHours()
    {
        var engine = Engine(); engine.Start("a"); now = now.AddSeconds(12.5); var checkpoint = engine.Checkpoint();
        var store = Store(); store.Save(checkpoint); now = now.AddDays(2);
        var recovered = Engine(store.Load());
        Assert.Null(recovered.Running); Assert.True(recovered.Document.RecoveredTimer);
        Assert.Equal(12.5, recovered.Document.Tasks(today)[0].ActualSeconds);
    }
    [Fact]
    public void MidnightStopsAtTheDayBoundary()
    {
        now = new DateTimeOffset(2026, 10, 21, 23, 59, 50, TimeSpan.Zero);
        var engine = Engine(); engine.Start("a"); now = now.AddMinutes(10); engine.Tick();
        Assert.Null(engine.Running); Assert.Equal(10, engine.Document.Tasks(today)[0].ActualSeconds);
    }
    [Fact]
    public void LocalTimeZoneDeterminesWhichDayCanRun()
    {
        now = new DateTimeOffset(2026, 10, 20, 16, 0, 0, TimeSpan.Zero);
        var engine = new TrackerEngine(Document(), () => now, TimeZoneInfo.FindSystemTimeZoneById("Asia/Tokyo"));
        engine.Start("a"); Assert.Equal(today, engine.Today);
        engine.SelectDate(today.AddDays(-1)); Assert.Null(engine.Running);
        Assert.Throws<InvalidOperationException>(() => engine.Start("a"));
    }
    [Fact]
    public void BackwardClockDoesNotCreateNegativeActualTime()
    {
        var engine = Engine(); engine.Start("a"); now = now.AddDays(-1); engine.Tick();
        Assert.Null(engine.Running); Assert.Equal(0, engine.Document.Tasks(today)[0].ActualSeconds);
    }
    [Fact]
    public void SqliteRoundTripPreservesHistoryPlansPrecisionAndOrder()
    {
        var doc = Document(); doc.Plans[today.AddDays(1)] = [new WorkTask { Name = "次日", PlannedHours = 0 }];
        doc.Days[today.AddDays(-1)] = [new WorkTask { Name = "履歴", ActualSeconds = 123.456789 }];
        doc.Settings.Theme = "Dark"; doc.Settings.PauseOnIdle = false;
        var store = Store(); store.Save(doc); var loaded = store.Load()!;
        Assert.Equal(BackupCodec.Encode(doc), BackupCodec.Encode(loaded));
        Assert.Equal(new[] { "a", "b" }, loaded.Tasks(today).Select(t => t.Id));
        Assert.Equal(0, loaded.Planned(today.AddDays(1))[0].PlannedHours);
    }
    [Fact]
    public void FailedTransactionKeepsAllPreviousRowsAndMetadata()
    {
        var store = Store(); var original = Document(); store.Save(original);
        using (var connection = new SqliteConnection($"Data Source={store.DatabasePath}"))
        {
            connection.Open(); using var command = connection.CreateCommand();
            command.CommandText = "CREATE TRIGGER reject_test BEFORE INSERT ON tasks WHEN NEW.json LIKE '%BROKEN%' BEGIN SELECT RAISE(ABORT, 'disk failure simulation'); END";
            command.ExecuteNonQuery();
        }
        var candidate = Document(); candidate.Tasks(today)[1].Name = "BROKEN"; candidate.Settings.Theme = "Dark";
        Assert.Throws<SqliteException>(() => store.Save(candidate));
        Assert.Equal(BackupCodec.Encode(original), BackupCodec.Encode(store.Load()!));
    }
    [Fact]
    public void OnlineBackupIncludesWalDataAndCanBeOpened()
    {
        var store = Store(); var doc = Document(); doc.Tasks(today)[0].Name = "直前の変更"; store.Save(doc);
        var backup = store.Backup(Path.Combine(directory, "backups"), "test");
        var restored = new TrackerStore(backup).Load()!;
        Assert.Equal(BackupCodec.Encode(doc), BackupCodec.Encode(restored));
    }
    [Fact]
    public void InvalidRestoreCannotStopOrReplaceLiveTimer()
    {
        var engine = Engine(); engine.Start("a"); var invalid = Document(); invalid.Tasks(today)[0].PlannedHours = -1;
        Assert.Throws<InvalidDataException>(() => engine.Replace(invalid)); Assert.Equal("a", engine.Running!.Id);
    }
    [Fact]
    public void LegacyMigrationPreservesZeroNotesHistoryAndSettings()
    {
        var doc = BackupCodec.Decode(Legacy());
        Assert.Equal(0, doc.Tasks(today)[0].PlannedHours); Assert.Equal(37.25, doc.Tasks(today)[0].ActualSeconds);
        Assert.Equal("引継ぎ\nメモ", doc.Tasks(today)[0].Notes); Assert.Equal(123.456, doc.Tasks(today.AddDays(-1))[0].ActualSeconds);
        Assert.Equal(0, doc.Planned(today.AddDays(1))[0].PlannedHours);
        Assert.False(doc.Settings.EnableLongAlert); Assert.Equal(30, doc.Settings.LongAlertMinutes);
        Assert.True(doc.RecoveredTimer); Assert.Null(doc.RunningTaskId);
        var store = Store(); store.Save(doc); Assert.Equal(BackupCodec.Encode(doc), BackupCodec.Encode(store.Load()!));
    }
    [Theory]
    [InlineData("[]")][InlineData("null")][InlineData("{}")][InlineData("{\"version\":1}")]
    public void InvalidBackupShapesAreRejected(string json) => Assert.ThrowsAny<Exception>(() => BackupCodec.Decode(json));
    [Fact]
    public void MalformedLegacyTaskTypesAndNegativeHoursAreRejected()
    {
        Assert.ThrowsAny<Exception>(() => BackupCodec.Decode(Legacy(new[] { new { id = "bad", project = 123, name = "型不正", plannedHours = 1 } })));
        Assert.ThrowsAny<Exception>(() => BackupCodec.Decode(Legacy(new[] { new { id = "bad", project = "正常", name = "負数", plannedHours = -1 } })));
        Assert.ThrowsAny<Exception>(() => BackupCodec.Decode(Legacy(settings: new { enableLongAlert = "false" })));
    }
    [Fact]
    public void NativeBackupRoundTripPreservesAllData()
    {
        var doc = Document(); Assert.Equal(BackupCodec.Encode(doc), BackupCodec.Encode(BackupCodec.Decode(BackupCodec.Encode(doc))));
    }
    [Fact]
    public void ReportsSafelyGroupSpecialNamesAndQuoteMultilineCsv()
    {
        var doc = Document(); doc.Tasks(today)[0].Name = "toString,\"確認\""; doc.Tasks(today)[0].Notes = "行1\n行2,\"注記\"";
        doc.Tasks(today)[1].Project = "__proto__"; doc.Tasks(today)[1].ActualSeconds = 3600;
        doc.Plans[today.AddDays(1)] = [new WorkTask { Project = "constructor", Name = "次日作業", PlannedHours = 0 }];
        var report = Reports.Daily(doc, today);
        Assert.Contains("constructor", report); Assert.Contains("__proto__", report); Assert.Contains("次日作業", report);
        var csv = Reports.Csv(doc, today, today);
        Assert.Contains("\"toString,\"\"確認\"\"\"", csv); Assert.Contains("\"行1\n行2,\"\"注記\"\"\"", csv);
        Assert.Equal(2, Reports.Summarize(doc, today, today).Count);
    }
    [Fact]
    public void TemplatePreservesZeroAndDefaultsOnlyMissingHours()
    {
        var parsed = TemplateParser.Parse("【本日の作業】\n◆ゼロ（予定 0h）\n・A\n・B\n◆未指定\n・C\n◆小数（予定 0.2h）\n・D\n・E\n【次日の作業予定】\n◆翌日（予定 0h）\n・F");
        Assert.Equal(new[] { 0d, 0d, 1d, 0.1, 0.1 }, parsed.Today.Select(t => t.PlannedHours));
        Assert.Equal(0, parsed.Next.Single().PlannedHours);
        Assert.All(parsed.Today, task => Assert.Equal(0, task.ActualSeconds));
    }
    [Fact]
    public void MonthlySummariesIncludeMonthEndAndExcludeOtherMonths()
    {
        var doc = Document(); doc.Days.Clear();
        doc.Days[new DateOnly(2024, 2, 29)] = [new WorkTask { Name = "閏日", ActualSeconds = 3600 }];
        doc.Days[new DateOnly(2024, 3, 1)] = [new WorkTask { Name = "翌月", ActualSeconds = 7200 }];
        Assert.Equal(1, Reports.Summarize(doc, new DateOnly(2024, 2, 1), new DateOnly(2024, 2, 29)).Single().ActualHours);
    }
    [Fact]
    public void ShippedWebTemplateImportsWithoutDemoActualTime()
    {
        var parsed = TemplateParser.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "DailyReportFormat.txt")));
        Assert.Equal(7, parsed.Today.Count); Assert.Equal(4, parsed.Next.Count);
        Assert.Equal(7.5, parsed.Today.Sum(task => task.PlannedHours));
        Assert.Equal(7.5, parsed.Next.Sum(task => task.PlannedHours));
        Assert.All(parsed.Today, task => Assert.Equal(0, task.ActualSeconds));
    }
    [Fact]
    public void DeletingTaskRemovesItsStoredRowAndKeepsOtherDays()
    {
        var store = Store(); var doc = Document();
        doc.Days[today.AddDays(-1)] = [new WorkTask { Name = "過去の作業" }];
        store.Save(doc); doc.Tasks(today).RemoveAt(0); store.Save(doc);
        var saved = store.Load()!;
        Assert.Equal("b", saved.Tasks(today).Single().Id);
        Assert.Equal("過去の作業", saved.Tasks(today.AddDays(-1)).Single().Name);
    }

}
