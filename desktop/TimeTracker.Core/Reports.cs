using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace TimeTracker.Core;

public sealed record ProjectSummary(string Project, double PlannedHours, double ActualHours);

public static class Reports
{
    public static IReadOnlyList<ProjectSummary> Summarize(TrackerDocument doc, DateOnly from, DateOnly to) =>
        doc.Days.Where(pair => pair.Key >= from && pair.Key <= to).SelectMany(pair => pair.Value)
            .GroupBy(task => task.Project, StringComparer.Ordinal)
            .Select(group => new ProjectSummary(group.Key, group.Sum(t => t.PlannedHours), group.Sum(t => t.ActualSeconds) / 3600))
            .OrderByDescending(p => p.ActualHours).ToList();

    public static string Daily(TrackerDocument doc, DateOnly day)
    {
        var today = doc.Days.GetValueOrDefault(day) ?? [];
        var next = doc.Plans.GetValueOrDefault(day.AddDays(1)) ?? [];
        var text = new StringBuilder();
        text.AppendLine(FormattableString.Invariant($"【本日の作業】(予定合計: {today.Sum(t => t.PlannedHours):0.0}h / 実績合計: {today.Sum(t => t.ActualSeconds) / 3600:0.0}h)-----------------------------------"));
        Append(today, false);
        text.AppendLine();
        text.AppendLine(FormattableString.Invariant($"【次日の作業予定】(予定合計: {next.Sum(t => t.PlannedHours):0.0}h)------------------------------------------------"));
        Append(next, true);
        return text.ToString();
        void Append(List<WorkTask> tasks, bool plan)
        {
            var firstProject = true;
            foreach (var project in tasks.GroupBy(t => t.Project, StringComparer.Ordinal))
            {
                if (!firstProject) text.AppendLine();
                firstProject = false;
                text.Append($"　◆{project.Key}（予定 {project.Sum(t => t.PlannedHours):0.0}h");
                if (!plan) text.Append($"/実績 {project.Sum(t => t.ActualSeconds) / 3600:0.0}h");
                text.AppendLine("）");
                foreach (var task in project)
                {
                    text.AppendLine($"　　・{task.Name}");
                    foreach (var line in task.Notes.Replace("\r", "").Split('\n').Where(line => line.Length > 0))
                        text.AppendLine($"　　　{line}");
                }
            }
        }
    }

    public static string Csv(TrackerDocument doc, DateOnly from, DateOnly to)
    {
        var lines = new List<string> { "日付,プロジェクト,タスク名,予定時間(h),実績時間(h),詳細" };
        foreach (var (day, tasks) in doc.Days.OrderBy(pair => pair.Key).Where(pair => pair.Key >= from && pair.Key <= to))
        foreach (var task in tasks)
            lines.Add(string.Join(",", new[] { day.ToString("yyyy-MM-dd"), task.Project, task.Name,
                task.PlannedHours.ToString("0.###", CultureInfo.InvariantCulture),
                (task.ActualSeconds / 3600).ToString("0.###", CultureInfo.InvariantCulture), task.Notes }.Select(Quote)));
        return string.Join("\r\n", lines) + "\r\n";
    }
    private static string Quote(string value) => "\"" + value.Replace("\"", "\"\"") + "\"";
}

public static class TemplateParser
{
    private sealed class Project(string name, double? hours)
    {
        public string Name { get; } = name;
        public double? Hours { get; } = hours;
        public List<WorkTask> Tasks { get; } = [];
    }

    public static (List<WorkTask> Today, List<WorkTask> Next) Parse(string text)
    {
        var today = new List<Project>();
        var next = new List<Project>();
        var section = today;
        Project? project = null;
        WorkTask? current = null;
        foreach (var raw in text.Replace("\r", "").Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;
            if (line.Contains("【本日の作業】", StringComparison.Ordinal)) { section = today; project = null; current = null; continue; }
            if (line.Contains("【次日の作業予定】", StringComparison.Ordinal)) { section = next; project = null; current = null; continue; }
            if (line[0] is '◆' or '■' or '★')
            {
                var heading = line[1..].Trim();
                var match = Regex.Match(heading, @"^(.*?)[（(]\s*予定\s*([\d.]+)\s*h", RegexOptions.CultureInvariant);
                double? hours = match.Success ? double.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture) : null;
                if (hours is not null && (!double.IsFinite(hours.Value) || hours < 0)) throw new InvalidDataException("テンプレートの時間が不正です。");
                project = new Project(match.Success ? match.Groups[1].Value.Trim() : heading, hours);
                section.Add(project);
                current = null;
            }
            else if (line[0] is '・' or '•')
            {
                if (project is null) throw new InvalidDataException("タスクの前に◆プロジェクト名を指定してください。");
                current = new WorkTask { Project = project.Name, Name = line[1..].Trim() };
                project.Tasks.Add(current);
            }
            else if (current is not null) current.Notes += (current.Notes.Length > 0 ? "\n" : "") + line;
        }
        if (today.Count == 0 && next.Count == 0) throw new InvalidDataException("◆プロジェクト名と・タスク名を含むテンプレートを選択してください。");
        return (Build(today), Build(next));
    }

    private static List<WorkTask> Build(List<Project> projects)
    {
        var result = new List<WorkTask>();
        foreach (var project in projects)
        {
            if (project.Tasks.Count == 0) project.Tasks.Add(new WorkTask { Project = project.Name, Name = "作業予定" });
            foreach (var task in project.Tasks)
            {
                task.PlannedHours = project.Hours.HasValue ? project.Hours.Value / project.Tasks.Count : 1;
                if (string.IsNullOrWhiteSpace(task.Name) || string.IsNullOrWhiteSpace(task.Project)) throw new InvalidDataException("プロジェクト名・タスク名が空です。");
                result.Add(task);
            }
        }
        return result;
    }
}
