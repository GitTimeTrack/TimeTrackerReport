using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Windows;
using Microsoft.Win32;
using TimeTracker.Core;

namespace TimeTracker.Windows;

public partial class App : Application
{
    private Mutex? instanceMutex;
    private CancellationTokenSource? pipeCancellation;
    internal bool Exiting { get; set; }
    private static string? smokeDirectory;
    private static bool smoke;
    internal static string VersionText => typeof(App).Assembly.GetName().Version!.ToString(3);
    public static string DataDirectory => smokeDirectory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TimeTrackerReport");
    private static string InstanceName => (smoke ? "TimeTrackerSmoke-" : "TimeTrackerReport-") + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Environment.UserName)))[..16];

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        smoke = e.Args.Contains("--smoke-test", StringComparer.OrdinalIgnoreCase);
        if (smoke)
        {
            TraceSmoke("Startup");
            DispatcherUnhandledException += (_, error) =>
            {
                TraceSmoke(error.Exception.ToString());
                error.Handled = true;
                Exiting = true;
                Shutdown(1);
            };
        }
        if (smoke) smokeDirectory = Path.Combine(Path.GetTempPath(), "TimeTrackerSmoke-" + Guid.NewGuid().ToString("N"));
        instanceMutex = new Mutex(true, @"Global\" + InstanceName, out var first);
        if (!first)
        {
            try
            {
                using var pipe = new NamedPipeClientStream(".", InstanceName, PipeDirection.Out);
                await pipe.ConnectAsync(2500);
                await pipe.WriteAsync(new byte[] { 1 });
            }
            catch (IOException) { MessageBox.Show("アプリは既に起動しています。タスクトレイの時計アイコンから開いてください。"); }
            catch (TimeoutException) { MessageBox.Show("アプリは既に起動しています。タスクトレイから開いてください。"); }
            Shutdown();
            return;
        }
        try
        {
            Directory.CreateDirectory(DataDirectory);
            var store = new TrackerStore(Path.Combine(DataDirectory, "tracker.db"));
            var document = store.Load();
            var status = "保存済み";
            if (document is null)
            {
                document = new TrackerDocument();
                var legacy = Path.Combine(AppContext.BaseDirectory, "data", "tracker_data.json");
                if (File.Exists(legacy) && MessageBox.Show("旧Web版のデータが見つかりました。元ファイルを残して移行しますか？", "データ移行", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
                {
                    document = ReadBackup(legacy);
                    status = "旧Web版のデータを移行しました。元のJSONファイルはそのまま残しています。";
                }
                store.Save(document);
            }
            var backups = Path.Combine(DataDirectory, "backups");
            Directory.CreateDirectory(backups);
            if (!Directory.EnumerateFiles(backups, $"tracker-{DateTime.Now:yyyyMMdd}-*-startup-*.db").Any())
            {
                store.Backup(backups, "startup");
                foreach (var old in new DirectoryInfo(backups).GetFiles("*-startup-*.db").OrderByDescending(f => f.Name).Skip(14)) old.Delete();
            }
            var engine = new TrackerEngine(document);
            if (document.RecoveredTimer)
            {
                status = "前回の計測を最終保存時点まで復元しました。停止中の時間は実績に加算していません。";
                document.RecoveredTimer = false;
            }
            engine.SelectDate(engine.Today);
            store.Save(engine.Checkpoint());
            ApplyTheme(document.Settings.Theme, document.Settings.ColorTheme);
            TraceSmoke("Construct main window");
            var window = new MainWindow(engine, store, status);
            MainWindow = window;
            // Create the HWND even when starting hidden, so shortcuts are registered.
            TraceSmoke("Show main window");
            window.Show();
            if (e.Args.Contains("--tray", StringComparer.OrdinalIgnoreCase)) window.Hide();
            window.StartAssistance(!smoke);
            pipeCancellation = new CancellationTokenSource();
            _ = ListenForActivation(window, pipeCancellation.Token);
            if (smoke)
            {
                await window.Dispatcher.InvokeAsync(window.RunSmokeTest, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                Shutdown(0);
            }
        }
        catch (Exception exception)
        {
            if (smoke)
            {
                var log = Environment.GetEnvironmentVariable("TIMETRACKER_SMOKE_LOG");
                if (!string.IsNullOrEmpty(log)) File.WriteAllText(log, exception.ToString());
                Exiting = true;
                Shutdown(1);
                return;
            }
            MessageBox.Show("起動できませんでした。既存データを削除せず、保存先の空き容量・権限を確認してください。\n\n" + exception.Message, "TimeTrackerReport", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    internal static TrackerDocument ReadBackup(string path)
    {
        if (new FileInfo(path).Length > 64 * 1024 * 1024) throw new InvalidDataException("64MB以下のバックアップを選択してください。");
        return BackupCodec.Decode(File.ReadAllText(path));
    }

    private static async Task ListenForActivation(MainWindow window, CancellationToken cancellation)
    {
        while (!cancellation.IsCancellationRequested)
        {
            try
            {
                using var pipe = new NamedPipeServerStream(InstanceName, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await pipe.WaitForConnectionAsync(cancellation);
                var buffer = new byte[1];
                await pipe.ReadExactlyAsync(buffer, cancellation);
                await window.Dispatcher.InvokeAsync(window.ShowFromTray);
            }
            catch (OperationCanceledException) { return; }
            catch (IOException) { await Task.Delay(500, cancellation); }
        }
    }

    internal static void ApplyTheme(string theme, string colorTheme = "Slate")
    {
        var dark = theme == "Dark" || theme == "System" && (int?)Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "AppsUseLightTheme", 1) == 0;
        foreach (var (key, value) in ColorThemes.Brushes(colorTheme, dark))
            Current.Resources[key] = new System.Windows.Media.SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(value));
        if (SystemParameters.HighContrast)
        {
            foreach (var key in new[] { "CanvasBrush", "SurfaceBrush", "SidebarBrush", "HeroBrush", "HoverBrush", "ScrollTrackBrush", "RunningSurfaceBrush" }) Current.Resources[key] = SystemColors.WindowBrush;
            foreach (var key in new[] { "TextBrush", "MutedBrush", "OnDarkBrush", "OnDarkMutedBrush" }) Current.Resources[key] = SystemColors.WindowTextBrush;
            foreach (var key in new[] { "AccentBrush", "PrimaryBrush", "PrimaryHoverBrush", "LineBrush", "ScrollThumbBrush", "ScrollThumbHoverBrush", "ScrollThumbPressedBrush" }) Current.Resources[key] = SystemColors.HighlightBrush;
            Current.Resources["OnPrimaryBrush"] = SystemColors.HighlightTextBrush;
        }
        // Keep native controls (date picker, scrollbars, etc.) on the same semantic colors.
        foreach (var (key, brush) in new (ResourceKey, string)[]
        {
            (SystemColors.WindowBrushKey, "SurfaceBrush"), (SystemColors.WindowTextBrushKey, "TextBrush"),
            (SystemColors.ControlBrushKey, "SurfaceBrush"), (SystemColors.ControlTextBrushKey, "TextBrush"),
            (SystemColors.HighlightBrushKey, "PrimaryBrush"), (SystemColors.HighlightTextBrushKey, "OnPrimaryBrush"),
            (SystemColors.GrayTextBrushKey, "MutedBrush"), (SystemColors.ControlDarkBrushKey, "LineBrush")
        }) Current.Resources[key] = Current.Resources[brush];
    }

    internal static void TraceSmoke(string message)
    {
        if (!smoke) return;
        var log = Environment.GetEnvironmentVariable("TIMETRACKER_SMOKE_LOG");
        if (!string.IsNullOrEmpty(log)) File.AppendAllText(log, message + Environment.NewLine);
    }

    protected override void OnSessionEnding(SessionEndingCancelEventArgs e)
    {
        if (MainWindow is MainWindow window && !window.SaveBeforeSessionEnd())
        {
            e.Cancel = true;
            window.ShowFromTray();
        }
        base.OnSessionEnding(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        pipeCancellation?.Cancel();
        pipeCancellation?.Dispose();
        instanceMutex?.Dispose();
        if (smokeDirectory is not null)
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { Directory.Delete(smokeDirectory, true); } catch (IOException) { }
        }
        base.OnExit(e);
    }
}
