using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using TimeTracker.Core;

namespace TimeTracker.Windows;

internal sealed class TaskEditor : Window
{
    private readonly TextBox project;
    private readonly TextBox name;
    private readonly TextBox planned;
    private readonly TextBox actual;
    private readonly TextBox notes;
    private readonly bool plan;
    private readonly double originalSeconds;
    private readonly string initialActual;
    public WorkTask Result { get; private set; }
    public bool ActualChanged => actual.Text != initialActual;

    public TaskEditor(Window owner, WorkTask? task, bool isPlan)
    {
        SetResourceReference(StyleProperty, typeof(Window));
        Owner = owner;
        Title = task is null ? "タスクを追加" : "タスクを編集";
        Width = 500; Height = isPlan ? 560 : 620; MinWidth = 430; MinHeight = 500;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        plan = isPlan;
        Result = task is null ? new WorkTask() : new WorkTask { Id = task.Id, Project = task.Project, Name = task.Name, PlannedHours = task.PlannedHours, ActualSeconds = task.ActualSeconds, Notes = task.Notes };
        originalSeconds = Result.ActualSeconds;
        initialActual = (originalSeconds / 3600).ToString("0.########", CultureInfo.CurrentCulture);
        var root = new DockPanel { Margin = new Thickness(28) };
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 20, 0, 0) };
        var cancel = new Button { Content = "キャンセル", IsCancel = true };
        var save = new Button { Content = "保存", IsDefault = true, Style = (Style)FindResource("PrimaryButton") };
        save.Click += Save;
        actions.Children.Add(cancel); actions.Children.Add(save); DockPanel.SetDock(actions, Dock.Bottom); root.Children.Add(actions);
        var fields = new StackPanel();
        project = Field(fields, "プロジェクト", Result.Project);
        name = Field(fields, "タスク名", Result.Name);
        planned = Field(fields, "予定時間（h）・空欄は1h、0hも登録できます", Result.PlannedHours.ToString(CultureInfo.CurrentCulture));
        actual = new TextBox { Text = initialActual };
        if (!isPlan) { fields.Children.Add(new TextBlock { Text = "実績時間（h）" }); fields.Children.Add(actual); }
        notes = Field(fields, "詳細メモ", Result.Notes); notes.AcceptsReturn = true; notes.TextWrapping = TextWrapping.Wrap; notes.Height = 100; notes.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        root.Children.Add(new ScrollViewer { Content = fields, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        Content = root;
        Loaded += (_, _) => name.Focus();
    }
    private static TextBox Field(Panel parent, string label, string value)
    {
        parent.Children.Add(new TextBlock { Text = label });
        var input = new TextBox { Text = value };
        System.Windows.Automation.AutomationProperties.SetName(input, label);
        parent.Children.Add(input); return input;
    }
    private void Save(object sender, RoutedEventArgs e)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(name.Text)) throw new InvalidDataException("タスク名を入力してください。");
            var hours = DocumentValidation.ParseHours(planned.Text);
            var seconds = plan || !ActualChanged ? originalSeconds : DocumentValidation.ParseHours(actual.Text, false) * 3600;
            if (!double.IsFinite(seconds)) throw new InvalidDataException("実績時間が大きすぎます。");
            Result.Project = string.IsNullOrWhiteSpace(project.Text) ? "その他" : project.Text.Trim();
            Result.Name = name.Text.Trim(); Result.PlannedHours = hours; Result.ActualSeconds = seconds; Result.Notes = notes.Text.Trim();
            DialogResult = true;
        }
        catch (InvalidDataException exception) { MessageBox.Show(this, exception.Message, "入力内容の確認", MessageBoxButton.OK, MessageBoxImage.Information); }
    }
}
