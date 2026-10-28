using Microsoft.Data.Sqlite;
using System.Text.Json;

namespace TimeTracker.Core;

public sealed class TrackerStore
{
    public string DatabasePath { get; }
    public TrackerStore(string databasePath)
    {
        DatabasePath = Path.GetFullPath(databasePath);
        Directory.CreateDirectory(Path.GetDirectoryName(DatabasePath)!);
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            PRAGMA journal_mode=WAL;
            CREATE TABLE IF NOT EXISTS metadata (id INTEGER PRIMARY KEY CHECK(id=1), json TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS tasks (
                day TEXT NOT NULL, is_plan INTEGER NOT NULL, position INTEGER NOT NULL, json TEXT NOT NULL,
                PRIMARY KEY(day, is_plan, position));
            """;
        command.ExecuteNonQuery();
    }

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        { DataSource = DatabasePath, DefaultTimeout = 5 }.ToString());
        connection.Open();
        return connection;
    }

    public TrackerDocument? Load()
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        using var metadata = connection.CreateCommand();
        metadata.Transaction = transaction;
        metadata.CommandText = "SELECT json FROM metadata WHERE id=1";
        if (metadata.ExecuteScalar() is not string json) return null;
        var doc = JsonSerializer.Deserialize<TrackerDocument>(json, TrackerDocument.JsonOptions)
            ?? throw new InvalidDataException("データベースの内容を読み取れません。");
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT day, is_plan, json FROM tasks ORDER BY day,is_plan,position";
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var day = DateOnly.ParseExact(reader.GetString(0), "yyyy-MM-dd");
            var task = JsonSerializer.Deserialize<WorkTask>(reader.GetString(2), TrackerDocument.JsonOptions)
                ?? throw new InvalidDataException("タスクを読み取れません。");
            (reader.GetInt32(1) == 1 ? doc.Planned(day) : doc.Tasks(day)).Add(task);
        }
        DocumentValidation.Validate(doc);
        return doc;
    }

    public void Save(TrackerDocument doc)
    {
        DocumentValidation.Validate(doc);
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        using var clear = connection.CreateCommand();
        clear.Transaction = transaction;
        clear.CommandText = "DELETE FROM tasks";
        clear.ExecuteNonQuery();
        var meta = doc.Copy();
        meta.Days.Clear();
        meta.Plans.Clear();
        using var header = connection.CreateCommand();
        header.Transaction = transaction;
        header.CommandText = "INSERT INTO metadata(id,json) VALUES(1,$json) ON CONFLICT(id) DO UPDATE SET json=excluded.json";
        header.Parameters.AddWithValue("$json", JsonSerializer.Serialize(meta, TrackerDocument.JsonOptions));
        header.ExecuteNonQuery();
        foreach (var isPlan in new[] { false, true })
        foreach (var (day, tasks) in isPlan ? doc.Plans : doc.Days)
        for (var index = 0; index < tasks.Count; index++)
        {
            using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = "INSERT INTO tasks VALUES($day,$plan,$position,$json)";
            insert.Parameters.AddWithValue("$day", day.ToString("yyyy-MM-dd"));
            insert.Parameters.AddWithValue("$plan", isPlan ? 1 : 0);
            insert.Parameters.AddWithValue("$position", index);
            insert.Parameters.AddWithValue("$json", JsonSerializer.Serialize(tasks[index], TrackerDocument.JsonOptions));
            insert.ExecuteNonQuery();
        }
        transaction.Commit();
    }

    // SQLite's online backup API includes committed WAL contents.
    public string Backup(string folder, string reason)
    {
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, $"tracker-{DateTime.Now:yyyyMMdd-HHmmss}-{reason}-{Guid.NewGuid():N}.db");
        using var source = Open();
        using var destination = new SqliteConnection($"Data Source={path}");
        destination.Open();
        source.BackupDatabase(destination);
        return path;
    }
}
