using System.Text.Json;

namespace TimeTracker.Core;

public static class BackupCodec
{
    public static TrackerDocument Decode(string json)
    {
        using var parsed = JsonDocument.Parse(json);
        var root = parsed.RootElement;
        if (root.ValueKind != JsonValueKind.Object) throw new InvalidDataException("JSONの形式が不正です。");
        if (root.TryGetProperty("version", out var version) && version.ValueKind == JsonValueKind.Number)
        {
            foreach (var key in new[] { "version", "selectedDate", "days", "plans", "settings", "savedAt" })
                if (!root.TryGetProperty(key, out _)) throw new InvalidDataException("バックアップの必須項目がありません。");
            var native = JsonSerializer.Deserialize<TrackerDocument>(json, TrackerDocument.JsonOptions)
                ?? throw new InvalidDataException("バックアップが空です。");
            DocumentValidation.Validate(native);
            return native;
        }
        return DecodeLegacy(root);
    }

    private static TrackerDocument DecodeLegacy(JsonElement root)
    {
        var day = DateOnly.ParseExact(root.GetProperty("selectedDate").GetString()!, "yyyy-MM-dd");
        var doc = new TrackerDocument { SelectedDate = day };
        if (root.TryGetProperty("version", out var version) && version.GetString() != "v6")
            throw new InvalidDataException("対応していないバックアップです。");
        if (root.TryGetProperty("savedAt", out var saved)) doc.SavedAt = DateTimeOffset.FromUnixTimeMilliseconds(saved.GetInt64());
        if (root.TryGetProperty("history", out var history))
            foreach (var record in history.EnumerateObject())
                doc.Days[DateOnly.ParseExact(record.Name, "yyyy-MM-dd")] = ReadTasks(record.Value.GetProperty("tasks"));
        var tasksJson = root.GetProperty("tasks");
        doc.Days[day] = ReadTasks(tasksJson);
        doc.Plans[day.AddDays(1)] = ReadTasks(root.GetProperty("nextDayTasks"));
        foreach (var task in tasksJson.EnumerateArray())
        {
            if (task.TryGetProperty("isRunning", out var running) && running.GetBoolean())
                doc.RecoveredTimer = true; // Saved actual seconds are used; no offline time is added.
        }
        if (root.TryGetProperty("settings", out var settings))
        {
            if (settings.ValueKind != JsonValueKind.Object) throw new InvalidDataException("設定の形式が不正です。");
            if (settings.TryGetProperty("enableLongAlert", out var enabledLong)) doc.Settings.EnableLongAlert = enabledLong.GetBoolean();
            if (settings.TryGetProperty("longAlertMinutes", out var minutesLong)) doc.Settings.LongAlertMinutes = minutesLong.GetInt32();
            if (settings.TryGetProperty("enableIdleAlert", out var enabledIdle)) doc.Settings.EnableIdleAlert = enabledIdle.GetBoolean();
            if (settings.TryGetProperty("idleAlertMinutes", out var minutesIdle)) doc.Settings.IdleAlertMinutes = minutesIdle.GetInt32();
        }
        DocumentValidation.Validate(doc);
        return doc;
    }

    private static List<WorkTask> ReadTasks(JsonElement array)
    {
        var result = new List<WorkTask>();
        foreach (var task in array.EnumerateArray())
        {
            result.Add(new WorkTask
            {
                Id = task.GetProperty("id").GetString() ?? throw new InvalidDataException("タスクIDがありません。"),
                Name = task.GetProperty("name").GetString() ?? "",
                Project = task.TryGetProperty("project", out var project) ? project.GetString() ?? "" : "その他",
                PlannedHours = task.TryGetProperty("plannedHours", out var hours) ? hours.GetDouble() : 1,
                ActualSeconds = task.TryGetProperty("actualSeconds", out var actual) ? actual.GetDouble() : 0,
                Notes = task.TryGetProperty("subtasks", out var notes) ? notes.GetString() ?? "" : ""
            });
        }
        return result;
    }

    public static string Encode(TrackerDocument document)
    {
        DocumentValidation.Validate(document);
        var copy = document.Copy();
        copy.Days = copy.Days.OrderBy(pair => pair.Key).ToDictionary();
        copy.Plans = copy.Plans.OrderBy(pair => pair.Key).ToDictionary();
        return JsonSerializer.Serialize(copy, TrackerDocument.JsonOptions);
    }
}
