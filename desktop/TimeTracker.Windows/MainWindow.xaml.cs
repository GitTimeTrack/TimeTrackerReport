using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;
using Microsoft.Win32;
using TimeTracker.Core;
using Forms = System.Windows.Forms;

namespace TimeTracker.Windows;

public partial class MainWindow : Window
{
    private readonly TrackerEngine engine;
    private readonly TrackerStore store;
    private readonly ViewState view;
    private readonly DispatcherTimer timer;
    private readonly Forms.NotifyIcon tray;
    private GlobalShortcuts? shortcuts;
    private Window? mini;
    private readonly PlannedOverrunAlarm plannedOverrunAlarm = new();
    private readonly TaskDeletionHistory deletionHistory = new();
    private bool ready;
    private bool saveFailed;
    private bool sessionLocked;
    private bool suspended;
    private bool presentingReturn;
    private bool editing;
    private string? selectedId;
    private DateTimeOffset lastSave;
    private DateTimeOffset untrackedSince;
    private bool idleNotified;
    private string? longNotifiedId;
    private DateOnly observedToday;
    private Absence? absence;
    private record Absence(string Id, DateOnly Day, DateTimeOffset From, string Reason);

    public MainWindow(TrackerEngine engine, TrackerStore store, string status)
    {
        this.engine = engine;
        this.store = store;
        view = new ViewState(engine, store.DatabasePath) { Status = status };
        InitializeComponent();
        DataContext = view;
        DayPicker.SelectedDate = engine.Document.SelectedDate.ToDateTime(TimeOnly.MinValue);
        untrackedSince = lastSave = engine.Now;
        observedToday = engine.Today;
        tray = new Forms.NotifyIcon
        {
            Icon = new System.Drawing.Icon(Application.GetResourceStream(new Uri("pack://application:,,,/Assets/app.ico")).Stream),
            Text = "TimeTrackerReport", Visible = true
        };
        tray.DoubleClick += (_, _) => Dispatcher.Invoke(ShowFromTray);
        tray.BalloonTipClicked += (_, _) => Dispatcher.Invoke(ShowFromTray);
        SourceInitialized += (_, _) =>
        {
            shortcuts = new GlobalShortcuts(new WindowInteropHelper(this).Handle, ToggleSelected, ShowFromTray);
            if (!shortcuts.Registered) SetStatus("一部のショートカットが他のアプリで使用されています。トレイから操作できます。");
        };
        SystemEvents.SessionSwitch += SessionChanged;
        SystemEvents.PowerModeChanged += PowerChanged;
        SystemEvents.UserPreferenceChanged += PreferenceChanged;
        timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        timer.Tick += Tick;
        ready = true;
        LoadSettings();
        Rebuild();
        timer.Start();
    }

    private List<WorkTask> Tasks => engine.Document.Tasks(engine.Document.SelectedDate);
    private List<WorkTask> Plans => engine.Document.Planned(engine.Document.SelectedDate.AddDays(1));
    private string Backups => Path.Combine(App.DataDirectory, "backups");
    private void SetStatus(string text)
    {
        view.Status = saveFailed ? "未保存：保存先の容量・権限を確認し、JSONバックアップを出力してください。" : text;
        view.Refresh();
    }

    private bool Save()
    {
        try
        {
            store.Save(engine.Checkpoint());
            lastSave = engine.Now;
            saveFailed = false;
            SetStatus($"保存済み · {DateTime.Now:HH:mm:ss}" + (engine.Document.Settings.CloseToTray ? " · 閉じてもトレイで計測を続けられます" : ""));
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException or InvalidDataException)
        {
            var first = !saveFailed;
            saveFailed = true;
            SetStatus("");
            if (first) Notify("保存できません", "画面を閉じる前に、設定画面からJSONバックアップを出力してください。");
            return false;
        }
    }

    private void Rebuild()
    {
        var query = SearchBox.Text.Trim();
        bool Match(WorkTask task) => string.IsNullOrEmpty(query) || (task.Project + " " + task.Name + " " + task.Notes).Contains(query, StringComparison.CurrentCultureIgnoreCase);
        var existing = view.TodayTasks.Concat(view.NextTasks).ToDictionary(row => row.Task);
        TaskRow Row(WorkTask task, bool plan) => existing.TryGetValue(task, out var row) && row.IsPlan == plan ? row : new TaskRow(task, plan, () => engine.Document.RunningTaskId);
        view.TodayTasks.Clear(); view.NextTasks.Clear();
        foreach (var task in Tasks.Where(Match)) view.TodayTasks.Add(Row(task, false));
        foreach (var task in Plans.Where(Match)) view.NextTasks.Add(Row(task, true));
        TodayList.SelectedItem = view.TodayTasks.FirstOrDefault(row => row.Task.Id == selectedId);
        RefreshAnalytics();
        RefreshDisplay();
        RebuildTray();
    }

    private void RefreshDisplay()
    {
        var pending = deletionHistory.Pending(engine.Document);
        view.UndoLabel = pending is null ? "" : $"削除しました：{pending.Day:yyyy/MM/dd} {(pending.IsPlan ? "予定" : "作業")}「{pending.Name}」";
        foreach (var row in view.TodayTasks.Concat(view.NextTasks))
        {
            // Do not rebind draft TextBoxes while typing or composing full-width input with an IME.
            if (row.IsInlineEditing) row.Refresh(nameof(TaskRow.ActualValue), nameof(TaskRow.ActualLabel), nameof(TaskRow.ActionLabel), nameof(TaskRow.IsRunning));
            else row.Refresh();
        }
        RefreshGuide();
        view.Refresh();
        var text = engine.Running is { } task ? $"{view.ClockText} · {task.Name}" : "TimeTrackerReport · 計測停止中";
        tray.Text = text.Length > 63 ? text[..63] : text;
        if (Views.SelectedIndex == 2) RefreshAnalytics();
    }

    private void RebuildTray()
    {
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("アプリを開く", null, (_, _) => Dispatcher.Invoke(ShowFromTray));
        menu.Items.Add(engine.Running is null ? "選択タスクの計測開始" : "計測を一時停止", null, (_, _) => Dispatcher.Invoke(ToggleSelected));
        var tasks = new Forms.ToolStripMenuItem("タスクを切り替える");
        foreach (var task in Tasks)
        {
            var id = task.Id;
            tasks.DropDownItems.Add(task.Name, null, (_, _) => Dispatcher.Invoke(() => RunAction(() => StartTask(id))));
        }
        tasks.Enabled = engine.Document.SelectedDate == engine.Today && Tasks.Count > 0;
        menu.Items.Add(tasks);
        menu.Items.Add("ミニタイマー", null, (_, _) => Dispatcher.Invoke(ShowMini));
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("終了", null, (_, _) => Dispatcher.Invoke(ExitApplication));
        var old = tray.ContextMenuStrip;
        tray.ContextMenuStrip = menu;
        old?.Dispose();
    }

    private void Tick(object? sender, EventArgs e)
    {
        try
        {
            engine.Tick();
            if (observedToday != engine.Today)
            {
                observedToday = engine.Today;
                Notify("日付が変わりました", "前日の計測を終了しました。「今日」へ移動して予定を引き継げます。");
                Save(); Rebuild();
            }
            var idle = NativeMethods.IdleTime();
            var settings = engine.Document.Settings;
            if (engine.Running is not null && settings.PauseOnIdle && idle.TotalMinutes >= settings.IdleAlertMinutes && !editing)
                Interrupt("離席", engine.Now - idle);
            if (absence is not null && !sessionLocked && !suspended && idle.TotalSeconds < 3 && !presentingReturn && !editing && inlineRow is null)
                ConfirmReturn();
            if (engine.Running is { } running)
            {
                untrackedSince = engine.Now; idleNotified = false;
                if (plannedOverrunAlarm.ShouldNotify(running, settings.EnablePlannedOverrunAlert))
                {
                    Notify("予定時間を超えました", $"「{running.Name}」予定 {running.PlannedHours:0.0}h / 実績 {running.ActualSeconds / 3600:0.0}h");
                    System.Media.SystemSounds.Exclamation.Play();
                }
                if (settings.EnableLongAlert && engine.Document.RunStartedAt is { } start
                    && (engine.Now - start).TotalMinutes >= settings.LongAlertMinutes && longNotifiedId != running.Id)
                {
                    longNotifiedId = running.Id;
                    Notify("そろそろひと息", $"「{running.Name}」の計測が{settings.LongAlertMinutes}分を超えました。停止忘れがないか確認しましょう。");
                }
            }
            else if (absence is null && settings.EnableIdleAlert && !idleNotified
                && (engine.Now - untrackedSince).TotalMinutes >= settings.IdleAlertMinutes)
            {
                idleNotified = true;
                Notify("作業を計測しますか？", "タイマーが停止しています。クリックしてタスクを選択できます。");
            }
            if ((engine.Now - lastSave).TotalSeconds >= 10) Save();
            RefreshDisplay();
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException or ArgumentException)
        {
            SetStatus("Windows連携を確認できませんでした：" + exception.Message);
        }
    }

    private void Interrupt(string reason, DateTimeOffset? cutoff = null)
    {
        if (engine.Running is not { } task) return;
        var from = cutoff ?? engine.Now;
        if (engine.Document.RunStartedAt is { } start && from < start) from = start;
        absence = new Absence(task.Id, engine.Document.SelectedDate, from, reason);
        engine.Pause(from);
        Save(); Rebuild();
        Notify("計測を一時停止しました", reason + "を検知しました。復帰時に、この間の時間を含めるか確認します。");
    }

    private void ConfirmReturn()
    {
        var pending = absence;
        if (pending is null) return;
        if (pending.Day != engine.Today || engine.Document.SelectedDate != pending.Day)
        {
            absence = null;
            Notify("前日の計測は停止中です", "日付が変わったため、自動再開していません。必要な実績はタスクの編集で補正できます。");
            return;
        }
        var task = Tasks.FirstOrDefault(t => t.Id == pending.Id);
        if (task is null) { absence = null; return; }
        presentingReturn = true;
        try
        {
            ShowFromTray();
            var minutes = Math.Max(0, (engine.Now - pending.From).TotalMinutes);
            var answer = MessageBox.Show(this,
                $"{pending.Reason}中の約{minutes:0}分をどう扱いますか？\n\nはい：この時間を除外して計測を再開\nいいえ：作業時間に含めて再開\nキャンセル：停止したままにする",
                "おかえりなさい", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
            absence = null;
            if (answer != MessageBoxResult.Cancel)
            {
                if (answer == MessageBoxResult.No) engine.SetActual(task, task.ActualSeconds + Math.Max(0, (engine.Now - pending.From).TotalSeconds));
                StartTask(task.Id);
            }
            Save(); Rebuild();
        }
        finally { presentingReturn = false; }
    }

    private void SessionChanged(object sender, SessionSwitchEventArgs e) => Dispatcher.BeginInvoke(() =>
    {
        if (e.Reason == SessionSwitchReason.SessionLock) { sessionLocked = true; if (engine.Document.Settings.PauseOnLock) Interrupt("画面ロック"); }
        if (e.Reason == SessionSwitchReason.SessionUnlock) sessionLocked = false;
    });
    private void PowerChanged(object sender, PowerModeChangedEventArgs e) => Dispatcher.Invoke(() =>
    {
        if (e.Mode == PowerModes.Suspend) { suspended = true; Interrupt("スリープ"); Save(); }
        if (e.Mode == PowerModes.Resume) suspended = false;
    });
    private void PreferenceChanged(object sender, UserPreferenceChangedEventArgs e) => Dispatcher.BeginInvoke(() => App.ApplyTheme(PreviewMode, PreviewColor));

    private void Notify(string title, string text)
    {
        tray.BalloonTipTitle = title; tray.BalloonTipText = text; tray.BalloonTipIcon = Forms.ToolTipIcon.Info;
        tray.ShowBalloonTip(7000);
    }
    public void ShowFromTray()
    {
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
    }
    private void StartTask(string id)
    {
        if (editing || presentingReturn && absence is not null) return;
        absence = null; longNotifiedId = null; selectedId = id;
        engine.Start(id); guide.Started(engine.Running!, engine.Document.SelectedDate); Save(); Rebuild();
    }
    private void ToggleSelected() => RunAction(() =>
    {
        if (editing) return;
        if (engine.Running is not null) { engine.Pause(); untrackedSince = engine.Now; Save(); Rebuild(); }
        else
        {
            var id = Tasks.FirstOrDefault(task => task.Id == selectedId)?.Id ?? Tasks.FirstOrDefault()?.Id;
            if (id is null) { ShowFromTray(); if (ChangeView(0)) Add(false); }
            else StartTask(id);
        }
    });
    private void GlobalToggle(object sender, RoutedEventArgs e) => ToggleSelected();
    private void TaskSelected(object sender, SelectionChangedEventArgs e) { if (TodayList.SelectedItem is TaskRow row) selectedId = row.Task.Id; }
    private void ToggleTask(object sender, RoutedEventArgs e) => RunAction(() =>
    {
        if (((Button)sender).Tag is not TaskRow row) return;
        if (row.IsPlan) { Tasks.Add(row.Task.AsPlan()); Save(); Rebuild(); }
        else if (engine.Document.RunningTaskId == row.Task.Id) { engine.Pause(); untrackedSince = engine.Now; Save(); Rebuild(); }
        else StartTask(row.Task.Id);
    });
    private void Add(bool plan) => RunAction(() =>
    {
        if (!ConfirmInlineEdit()) return;
        editing = true;
        try
        {
            var editor = new TaskEditor(this, null, plan);
            if (editor.ShowDialog() == true) { (plan ? Plans : Tasks).Add(editor.Result); if (!plan) selectedId = editor.Result.Id; Save(); Rebuild(); }
        }
        finally { editing = false; }
    });
    private void AddToday(object sender, RoutedEventArgs e) => Add(false);
    private void AddNext(object sender, RoutedEventArgs e) => Add(true);
    private void EditTask(object sender, RoutedEventArgs e) => RunAction(() =>
    {
        if (!ConfirmInlineEdit()) return;
        if (((Button)sender).Tag is not TaskRow row) return;
        editing = true;
        try
        {
            engine.Tick();
            var editor = new TaskEditor(this, row.Task, row.IsPlan);
            if (editor.ShowDialog() != true) return;
            row.Task.Project = editor.Result.Project; row.Task.Name = editor.Result.Name;
            row.Task.PlannedHours = editor.Result.PlannedHours; row.Task.Notes = editor.Result.Notes;
            if (!row.IsPlan && editor.ActualChanged) engine.SetActual(row.Task, editor.Result.ActualSeconds);
            var saved = Save();
            if (saved) GuideMemoSaved(row.Task, row.IsPlan);
            Rebuild();
        }
        finally { editing = false; }
    });
    private void DeleteTask(object sender, RoutedEventArgs e) => RunAction(() =>
    {
        if (!ConfirmInlineEdit()) return;
        if (((Button)sender).Tag is not TaskRow row || MessageBox.Show(this, $"「{row.Name}」を削除しますか？", "タスクの削除", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        DeleteRow(row);
    });
    private void DeleteRow(TaskRow row)
    {
        if (!deletionHistory.Delete(engine, row.Task.Id, row.IsPlan)) return;
        if (!row.IsPlan)
        {
            if (absence?.Id == row.Task.Id) absence = null;
            if (selectedId == row.Task.Id) selectedId = null;
            if (engine.Running is null) untrackedSince = engine.Now;
        }
        Save(); Rebuild();
    }
    private void UndoDelete(object sender, RoutedEventArgs e) => RunAction(() =>
    {
        var pending = deletionHistory.Pending(engine.Document);
        if (pending is null || !deletionHistory.Undo(engine.Document)) return;
        var saved = Save(); Rebuild();
        if (saved) SetStatus($"{pending.Day:yyyy/MM/dd}の{(pending.IsPlan ? "予定" : "作業")}「{pending.Name}」を元に戻しました。");
    });
    private void CarryPlans(object sender, RoutedEventArgs e) => RunAction(() =>
    {
        if (!ConfirmInlineEdit()) return;
        var source = engine.Document.Planned(engine.Document.SelectedDate);
        if (source.Count == 0) { SetStatus("この日付に引き継げる予定がありません。前日の「次日の作業予定」に登録できます。"); return; }
        if (MessageBox.Show(this, $"予定{source.Count}件を本日のタスクに追加しますか？", "予定の引き継ぎ", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
        Tasks.AddRange(source.Select(task => task.AsPlan()));
        Save(); Rebuild();
    });
    private void DateChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!ready || changingDate || DayPicker.SelectedDate is not { } date) return;
        if (!ConfirmInlineEdit())
        {
            changingDate = true; DayPicker.SelectedDate = engine.Document.SelectedDate.ToDateTime(TimeOnly.MinValue); changingDate = false;
            return;
        }
        RunAction(() => { engine.SelectDate(DateOnly.FromDateTime(date)); absence = null; selectedId = null; Save(); Rebuild(); });
    }
    private void GoToday(object sender, RoutedEventArgs e) => DayPicker.SelectedDate = engine.Today.ToDateTime(TimeOnly.MinValue);
    private void SearchChanged(object sender, TextChangedEventArgs e)
    {
        if (!ready || changingSearch) return;
        if (!ConfirmInlineEdit()) { changingSearch = true; SearchBox.Text = previousSearch; changingSearch = false; return; }
        previousSearch = SearchBox.Text; Rebuild();
    }
    private void Navigate(object sender, RoutedEventArgs e)
    {
        if (!ChangeView(int.Parse((string)((Button)sender).Tag, CultureInfo.InvariantCulture))) return;
        engine.Tick(); RefreshDisplay();
    }
    private void PeriodChanged(object sender, SelectionChangedEventArgs e) { if (ready) RefreshAnalytics(); }
    private (DateOnly From, DateOnly To) Range()
    {
        var day = engine.Document.SelectedDate;
        return PeriodBox.SelectedIndex switch
        {
            1 => (day.AddDays(-((int)day.DayOfWeek + 6) % 7), day.AddDays(6 - ((int)day.DayOfWeek + 6) % 7)),
            2 => (new DateOnly(day.Year, day.Month, 1), new DateOnly(day.Year, day.Month, DateTime.DaysInMonth(day.Year, day.Month))),
            _ => (day, day)
        };
    }
    private void RefreshAnalytics()
    {
        var (from, to) = Range();
        view.PeriodLabel = $"{from:yyyy/MM/dd} — {to:MM/dd}";
        var totals = Reports.Summarize(engine.Document, from, to);
        var max = Math.Max(1, totals.SelectMany(p => new[] { p.PlannedHours, p.ActualHours }).DefaultIfEmpty().Max());
        view.Summaries.Clear();
        foreach (var item in totals) view.Summaries.Add(new SummaryRow(item.Project, item.PlannedHours, item.ActualHours, max));
        view.Refresh();
    }
    private void LoadSettings()
    {
        var p = engine.Document.Settings;
        PlannedOverrunAlert.IsChecked = p.EnablePlannedOverrunAlert;
        LongAlert.IsChecked = p.EnableLongAlert; LongMinutes.Text = p.LongAlertMinutes.ToString(CultureInfo.CurrentCulture);
        IdleAlert.IsChecked = p.EnableIdleAlert; IdleMinutes.Text = p.IdleAlertMinutes.ToString(CultureInfo.CurrentCulture);
        PauseIdle.IsChecked = p.PauseOnIdle; PauseLock.IsChecked = p.PauseOnLock;
        CloseTray.IsChecked = p.CloseToTray; Startup.IsChecked = p.StartWithWindows;
        LoadThemeSettings();
    }
    private void SaveSettings(object sender, RoutedEventArgs e) => RunAction(() => SavePreferences());
    private bool SavePreferences()
    {
        if (!int.TryParse(LongMinutes.Text, out var longMinutes) || longMinutes is < 15 or > 360
            || !int.TryParse(IdleMinutes.Text, out var idleMinutes) || idleMinutes is < 5 or > 120)
            throw new InvalidDataException("通知時間は表示された範囲の整数で入力してください。");
        var previous = engine.Document.Settings;
        var start = Startup.IsChecked == true;
        if (start != engine.Document.Settings.StartWithWindows) StartupRegistration.Set(start);
        engine.Document.Settings = new Preferences
        {
            EnablePlannedOverrunAlert = PlannedOverrunAlert.IsChecked == true,
            EnableLongAlert = LongAlert.IsChecked == true, LongAlertMinutes = longMinutes,
            EnableIdleAlert = IdleAlert.IsChecked == true, IdleAlertMinutes = idleMinutes,
            PauseOnIdle = PauseIdle.IsChecked == true, PauseOnLock = PauseLock.IsChecked == true,
            CloseToTray = CloseTray.IsChecked == true, StartWithWindows = start,
            Theme = PreviewMode, ColorTheme = PreviewColor
        };
        longNotifiedId = null; idleNotified = false; untrackedSince = engine.Now;
        if (!Save())
        {
            engine.Document.Settings = previous;
            if (start != previous.StartWithWindows) StartupRegistration.Set(previous.StartWithWindows);
            return false;
        }
        App.ApplyTheme(engine.Document.Settings.Theme, engine.Document.Settings.ColorTheme);
        LoadThemeSettings();
        return true;
    }
    private void CopyReport(object sender, RoutedEventArgs e) => RunAction(() => { engine.Tick(); Clipboard.SetText(view.ReportText); SetStatus("日報をクリップボードにコピーしました。"); });
    private void ExportReport(object sender, RoutedEventArgs e) => WriteExport("作業報告", "txt", () => Reports.Daily(engine.Checkpoint(), engine.Document.SelectedDate));
    private void ExportCsv(object sender, RoutedEventArgs e) => WriteExport("作業実績", "csv", () => Reports.Csv(engine.Checkpoint(), engine.Document.SelectedDate, engine.Document.SelectedDate));
    private void ExportAnalytics(object sender, RoutedEventArgs e) { var (from, to) = Range(); WriteExport("作業分析", "csv", () => Reports.Csv(engine.Checkpoint(), from, to)); }
    private void ExportBackup(object sender, RoutedEventArgs e) => WriteExport("TimeTrackerReport_Backup", "json", () => BackupCodec.Encode(engine.Checkpoint()));
    private void WriteExport(string name, string extension, Func<string> content) => RunAction(() =>
    {
        var dialog = new SaveFileDialog { FileName = $"{name}_{engine.Document.SelectedDate:yyyyMMdd}.{extension}", Filter = $"{extension.ToUpperInvariant()}ファイル|*.{extension}", DefaultExt = extension };
        if (dialog.ShowDialog(this) != true) return;
        var temp = dialog.FileName + ".tmp";
        File.WriteAllText(temp, content(), new UTF8Encoding(extension == "csv"));
        File.Move(temp, dialog.FileName, true);
        SetStatus("出力しました：" + dialog.FileName);
    });
    private void ImportBackup(object sender, RoutedEventArgs e) => RunAction(() =>
    {
        if (!ConfirmInlineEdit() || !ConfirmThemeChange()) return;
        var dialog = new OpenFileDialog { Filter = "バックアップJSON|*.json" };
        if (dialog.ShowDialog(this) != true) return;
        // All parsing and validation finishes before confirmation or live-state changes.
        var candidate = App.ReadBackup(dialog.FileName);
        if (MessageBox.Show(this, "現在のデータをバックアップして、選択したJSONで置き換えますか？", "データの復元", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        if (!Save()) throw new IOException("現在のデータを保存できないため復元を中止しました。JSONバックアップを先に出力してください。");
        store.Backup(Backups, "before-restore");
        candidate.RunningTaskId = null; candidate.RunStartedAt = null;
        candidate.Settings.StartWithWindows = engine.Document.Settings.StartWithWindows;
        // Commit first. An I/O error leaves the active document untouched.
        store.Save(candidate);
        engine.Replace(candidate);
        deletionHistory.Clear();
        absence = null; selectedId = null;
        DayPicker.SelectedDate = candidate.SelectedDate.ToDateTime(TimeOnly.MinValue);
        App.ApplyTheme(candidate.Settings.Theme, candidate.Settings.ColorTheme);
        LoadSettings(); Rebuild(); Save();
        SetStatus("バックアップを復元しました。計測は停止状態です。");
    });
    private void ImportTemplate(object sender, RoutedEventArgs e) => RunAction(() =>
    {
        var dialog = new OpenFileDialog { Title = "ひな型を読み込む", Filter = "日報のひな型|*.txt" };
        if (dialog.ShowDialog(this) != true) return;
        var parsed = TemplateParser.Parse(File.ReadAllText(dialog.FileName));
        if (MessageBox.Show(this, $"本日{parsed.Today.Count}件、次日{parsed.Next.Count}件を追加しますか？既存タスクは残ります。", "テンプレート", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
        Tasks.AddRange(parsed.Today); Plans.AddRange(parsed.Next); Save(); Rebuild();
    });
    private void OpenDataFolder(object sender, RoutedEventArgs e) => RunAction(() => Process.Start(new ProcessStartInfo("explorer.exe", App.DataDirectory) { UseShellExecute = true }));
    private void OpenMini(object sender, RoutedEventArgs e) => ShowMini();
    private void ShowMini()
    {
        if (mini is not null) { mini.Show(); mini.Activate(); return; }
        mini = new Window { Title = "ミニタイマー", Width = 340, Height = 210, MinWidth = 300, MinHeight = 190, Topmost = true, DataContext = view, Owner = this, WindowStartupLocation = WindowStartupLocation.CenterScreen };
        var root = new StackPanel { Margin = new Thickness(18) };
        var name = new TextBlock { TextTrimming = TextTrimming.CharacterEllipsis };
        name.SetBinding(TextBlock.TextProperty, nameof(ViewState.ActiveName));
        var clock = new TextBlock { FontSize = 32, Margin = new Thickness(0, 8, 0, 12) };
        clock.SetBinding(TextBlock.TextProperty, nameof(ViewState.ClockText));
        var toggle = new Button { Content = "開始 / 一時停止" }; toggle.Click += GlobalToggle;
        root.Children.Add(name); root.Children.Add(clock); root.Children.Add(toggle);
        mini.Content = root; mini.Closed += (_, _) => mini = null; mini.Show();
    }
    private void HideToTray(object sender, RoutedEventArgs e) { if (!ConfirmInlineEdit() || !ConfirmThemeChange()) return; Hide(); Notify("トレイで動作しています", "時計アイコンをダブルクリックすると戻れます。終了はトレイメニューから選べます。"); }
    private void WindowClosing(object? sender, CancelEventArgs e)
    {
        if (((App)Application.Current).Exiting) return;
        e.Cancel = true;
        if (!ConfirmInlineEdit() || !ConfirmThemeChange()) return;
        if (engine.Document.Settings.CloseToTray) { Hide(); Notify("トレイに常駐しています", "タイマーと通知は引き続き動作しています。"); }
        else ExitApplication();
    }
    private void ExitApplication()
    {
        if (!ConfirmInlineEdit() || !ConfirmThemeChange()) return;
        if (MessageBox.Show(this, "計測を停止してアプリを終了しますか？", "終了", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
        engine.Pause();
        if (!Save())
        {
            ShowFromTray(); Views.SelectedIndex = 3;
            MessageBox.Show(this, "保存に失敗したため、終了を中止しました。JSONバックアップを出力してください。", "未保存のデータがあります");
            return;
        }
        ((App)Application.Current).Exiting = true;
        DisposeIntegration();
        Application.Current.Shutdown();
    }
    internal void RunSmokeTest()
    {
        App.TraceSmoke("RunSmokeTest");
        if (!IsLoaded || new WindowInteropHelper(this).Handle == IntPtr.Zero || !tray.Visible)
            throw new InvalidOperationException("Native window or tray failed to initialize.");
        var task = new WorkTask { Name = "Windows smoke test", Project = "constructor", PlannedHours = 0 };
        Tasks.Add(task); selectedId = task.Id; Rebuild();
        selectedId = "a-deleted-or-next-day-task";
        ToggleSelected();
        if (engine.Running?.Id != task.Id || view.TodayTasks.Count != 1) throw new InvalidOperationException("Timer UI failed.");
        for (var index = 1; index <= 40; index++)
        {
            Tasks.Add(new WorkTask { Name = $"作業 {index:00} · 進捗確認と次のアクションを整理", Project = "プロジェクト " + (index % 3 + 1), PlannedHours = index % 4 * 0.5 });
            Plans.Add(new WorkTask { Name = $"次日の予定 {index:00}", Project = "開発", PlannedHours = 0.5 });
        }
        Rebuild(); UpdateLayout();
        if (!TemplateButton.IsVisible) throw new InvalidOperationException("Workspace template action is hidden.");
        task.Notes = "確認事項\n担当者との相談\n次のアクション";
        Plans[0].Notes = "次日の詳細メモ";
        Rebuild(); UpdateLayout();
        if (!TodayTotals.IsVisible || !TodayTotals.Text.Contains("予定合計", StringComparison.Ordinal) || !TodayTotals.Text.Contains("実績合計", StringComparison.Ordinal))
            throw new InvalidOperationException("Today totals are missing.");
        var unfilteredTotals = view.TodayTotalsLabel;
        SearchBox.Text = "Windows smoke test";
        if (view.TodayTotalsLabel != unfilteredTotals) throw new InvalidOperationException("Search changed full-day totals.");
        SearchBox.Clear(); UpdateLayout();
        VerifyMemoExpansion(TodayList, view.TodayTasks[0]);
        ScheduleTabs.SelectedIndex = 1; UpdateLayout();
        if (!NextTotals.IsVisible || !NextTotals.Text.Contains("予定合計", StringComparison.Ordinal) || !NextTotals.Text.Contains("実績合計", StringComparison.Ordinal))
            throw new InvalidOperationException("Next-day totals are missing.");
        VerifyMemoExpansion(NextList, view.NextTasks[0]);
        ScheduleTabs.SelectedIndex = 0; UpdateLayout();
        VerifyBranding();
        VerifyVectorClock();
        VerifyHourDisplayPrecision();
        VerifyNavigationSelection();
        VerifyRunningRow();
        VerifyModernScrollbar(TodayList);
        var todayScroll = Descendant<ScrollViewer>(TodayList)!;
        if (todayScroll.ScrollableHeight <= 0 || todayScroll.ViewportHeight < 150)
            throw new InvalidOperationException("Task list does not have a usable scrolling viewport.");
        var moved = view.TodayTasks[0];
        Reorder(TodayList, moved, Tasks.Count);
        if (Tasks[^1].Id != task.Id || engine.Running?.Id != task.Id)
            throw new InvalidOperationException("Reordering changed the active timer.");
        Reorder(TodayList, view.TodayTasks[^1], 0);
        ScheduleTabs.SelectedIndex = 1; UpdateLayout();
        VerifyModernScrollbar(NextList);
        var nextScroll = Descendant<ScrollViewer>(NextList)!;
        if (nextScroll.ScrollableHeight <= 0) throw new InvalidOperationException("Next-day list cannot scroll.");
        var next = view.NextTasks[0];
        Reorder(NextList, next, Plans.Count);
        if (Plans[^1] != next.Task) throw new InvalidOperationException("Next-day order failed.");
        nextScroll.ScrollToBottom(); UpdateLayout();
        if (nextScroll.VerticalOffset <= 0) throw new InvalidOperationException("Pixel scrolling failed.");
        var firstRow = view.NextTasks[0];
        var dragData = new DataObject(typeof(TaskRow), firstRow);
        if (CanReorder(TodayList, dragData, out _)) throw new InvalidOperationException("Cross-list drops must be rejected.");
        SearchBox.Text = "次日の予定";
        if (CanReorder(NextList, dragData, out _)) throw new InvalidOperationException("Filtered drops must be rejected.");
        SearchBox.Clear();
        ScheduleTabs.SelectedIndex = 0; UpdateLayout();
        Width = 920; Height = 660; UpdateLayout();
        if (Descendant<ScrollViewer>(TodayList)!.ViewportHeight < 120)
            throw new InvalidOperationException($"Minimum-size list is too small: {Descendant<ScrollViewer>(TodayList)!.ViewportHeight}.");
        Width = 1240; Height = 840; UpdateLayout();
        var screenshot = Environment.GetEnvironmentVariable("TIMETRACKER_SMOKE_SCREENSHOT");
        if (!string.IsNullOrEmpty(screenshot))
        {
            UpdateLayout();
            var image = new System.Windows.Media.Imaging.RenderTargetBitmap((int)ActualWidth, (int)ActualHeight, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
            image.Render(this);
            var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
            encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(image));
            using var output = File.Create(screenshot);
            encoder.Save(output);
        }
        engine.Pause();
        foreach (var index in new[] { 0, 1, 2, 3 }) { Views.SelectedIndex = index; UpdateLayout(); }
        if (!view.ReportText.Contains(task.Name, StringComparison.Ordinal)) throw new InvalidOperationException("Report binding failed.");
        var editor = new TaskEditor(this, task, false);
        if (editor.ActualChanged || editor.Result.PlannedHours != 0 || editor.Result.ActualSeconds != task.ActualSeconds) throw new InvalidOperationException("Editor precision failed.");
        editor.Show(); editor.Close();
        PlannedOverrunAlert.IsChecked = true;
        SaveSettings(this, new RoutedEventArgs()); LoadSettings();
        if (!engine.Document.Settings.EnablePlannedOverrunAlert || PlannedOverrunAlert.IsChecked != true || store.Load()?.Settings.EnablePlannedOverrunAlert != true)
            throw new InvalidOperationException("Planned overrun alert setting was not saved.");
        PlannedOverrunAlert.IsChecked = false;
        SaveSettings(this, new RoutedEventArgs());
        if (engine.Document.Settings.EnablePlannedOverrunAlert) throw new InvalidOperationException("Planned overrun alert setting cannot be disabled.");
        App.TraceSmoke("RunThemeSmokeTest");
        RunThemeSmokeTest();
        ShowMini();
        if (mini is not { IsVisible: true }) throw new InvalidOperationException("Mini timer failed.");
        mini.Close();
        if (!Save() || store.Load()?.Tasks(engine.Today).First().Name != task.Name)
            throw new InvalidOperationException("Windows SQLite persistence failed.");
        if (store.Load()?.Planned(engine.Today.AddDays(1)).Last().Id != next.Task.Id)
            throw new InvalidOperationException("Reordered plans were not persisted.");
        RunUndoDeletionSmokeTest();
        RunTaskWorkflowSmokeTest();
        RunAssistanceSmokeTest();
        ((App)Application.Current).Exiting = true;
        DisposeIntegration();
    }

    private void VerifyMemoExpansion(ListBox list, TaskRow row)
    {
        list.ScrollIntoView(row); UpdateLayout();
        var container = (ListBoxItem)list.ItemContainerGenerator.ContainerFromItem(row);
        var expander = Descendant<Expander>(container) ?? throw new InvalidOperationException("Memo control is missing.");
        if (!expander.IsVisible || expander.IsExpanded) throw new InvalidOperationException("Memo must start collapsed.");
        var collapsedHeight = container.ActualHeight;
        expander.IsExpanded = true; UpdateLayout();
        if (!row.IsNotesExpanded || container.ActualHeight <= collapsedHeight)
            throw new InvalidOperationException("Memo does not expand in the list.");
        Rebuild(); UpdateLayout();
        var replacement = (row.IsPlan ? view.NextTasks : view.TodayTasks).First(item => item.Task == row.Task);
        if (!replacement.IsNotesExpanded) throw new InvalidOperationException("Memo expansion was lost on refresh.");
        list.ScrollIntoView(replacement); UpdateLayout();
        container = (ListBoxItem)list.ItemContainerGenerator.ContainerFromItem(replacement);
        expander = Descendant<Expander>(container)!;
        if (!expander.IsExpanded) throw new InvalidOperationException("Memo expansion binding was lost.");
        expander.IsExpanded = false; UpdateLayout();
        if (replacement.IsNotesExpanded || container.ActualHeight != collapsedHeight)
            throw new InvalidOperationException("Memo does not collapse correctly.");
    }

    internal bool SaveBeforeSessionEnd()
    {
        engine.Pause();
        return Save();
    }

    private void DisposeIntegration()
    {
        timer.Stop(); shortcuts?.Dispose(); tray.Visible = false; tray.Icon?.Dispose(); tray.Dispose();
        SystemEvents.SessionSwitch -= SessionChanged;
        SystemEvents.PowerModeChanged -= PowerChanged;
        SystemEvents.UserPreferenceChanged -= PreferenceChanged;
    }

    private void RunAction(Action action)
    {
        try { action(); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or InvalidOperationException or ArgumentException or System.Text.Json.JsonException or Microsoft.Data.Sqlite.SqliteException or System.Runtime.InteropServices.COMException or FormatException or KeyNotFoundException or Win32Exception)
        { MessageBox.Show(this, exception.Message, "処理を完了できませんでした", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }
}
