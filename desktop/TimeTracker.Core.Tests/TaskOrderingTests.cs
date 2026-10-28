using TimeTracker.Core;
using Xunit;

namespace TimeTracker.Core.Tests;

public class TaskOrderingTests
{
    [Theory]
    [InlineData(0, 3, "BCA")]
    [InlineData(2, 0, "CAB")]
    [InlineData(0, 2, "BAC")]
    [InlineData(2, 1, "ACB")]
    [InlineData(1, 1, "ABC")]
    [InlineData(1, 2, "ABC")]
    [InlineData(0, -1, "ABC")]
    [InlineData(2, 4, "ABC")]
    public void MoveUsesInsertionGap(int source, int gap, string expected)
    {
        var tasks = "ABC".Select(name => new WorkTask { Name = name.ToString(), PlannedHours = 0, ActualSeconds = 42 }).ToList();
        var original = tasks.ToArray();
        TaskOrdering.Move(tasks, tasks[source], gap);
        Assert.Equal(expected, string.Concat(tasks.Select(task => task.Name)));
        Assert.All(original, task => { Assert.Contains(task, tasks); Assert.Equal(42, task.ActualSeconds); Assert.Equal(0, task.PlannedHours); });
    }

    [Fact]
    public void UnknownTaskDoesNotModifyList()
    {
        var tasks = new List<WorkTask> { new() };
        Assert.False(TaskOrdering.Move(tasks, new WorkTask(), 0));
        Assert.Single(tasks);
    }
}
