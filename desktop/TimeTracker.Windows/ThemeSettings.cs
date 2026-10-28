using System.Windows;
using System.Windows.Controls;
using TimeTracker.Core;

namespace TimeTracker.Windows;

public partial class MainWindow
{
    private bool loadingTheme;
    private string PreviewMode => ThemeBox.SelectedIndex switch { 1 => "Light", 2 => "Dark", _ => "System" };
    private string PreviewColor => ColorThemes.Normalize(PaletteBox.SelectedValue as string);
    private bool ThemeDirty => PreviewMode != engine.Document.Settings.Theme || PreviewColor != engine.Document.Settings.ColorTheme;

    private void LoadThemeSettings()
    {
        loadingTheme = true;
        try
        {
            PaletteBox.ItemsSource = ColorThemes.Palettes;
            PaletteBox.SelectedValue = engine.Document.Settings.ColorTheme;
            ThemeBox.SelectedIndex = engine.Document.Settings.Theme switch { "Light" => 1, "Dark" => 2, _ => 0 };
            ThemePreviewStatus.Text = "選択した配色は「設定を保存」で確定します。";
        }
        finally { loadingTheme = false; }
    }
    private void ThemeSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!ready || loadingTheme || PaletteBox.SelectedItem is null) return;
        App.ApplyTheme(PreviewMode, PreviewColor);
        ThemePreviewStatus.Text = ThemeDirty ? "配色をプレビュー中（未保存）" : "保存済みの配色です。";
    }
    private void DiscardTheme(object sender, RoutedEventArgs e) => RestoreTheme();
    private void RestoreTheme()
    {
        LoadThemeSettings();
        App.ApplyTheme(engine.Document.Settings.Theme, engine.Document.Settings.ColorTheme);
    }
    private bool ConfirmThemeChange()
    {
        if (!ThemeDirty) return true;
        var answer = MessageBox.Show(this, "配色と明暗の変更が未保存です。\nはい：設定を保存\nいいえ：配色の変更を取り消す\nキャンセル：この画面に戻る", "配色の変更", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
        if (answer == MessageBoxResult.Cancel) return false;
        if (answer == MessageBoxResult.No) { RestoreTheme(); return true; }
        var saved = false;
        RunAction(() => saved = SavePreferences());
        return saved;
    }
    private bool ChangeView(int index)
    {
        if (index != Views.SelectedIndex && (!ConfirmInlineEdit() || !ConfirmThemeChange())) return false;
        if (index == Views.SelectedIndex) return true;
        Views.SelectedIndex = index;
        if (index == 3) LoadSettings();
        return true;
    }
    private void RunThemeSmokeTest()
    {
        App.TraceSmoke("Load theme settings");
        Views.SelectedIndex = 3; LoadSettings(); UpdateLayout();
        var savedColor = engine.Document.Settings.ColorTheme;
        var savedMode = engine.Document.Settings.Theme;
        var tasksBefore = System.Text.Json.JsonSerializer.Serialize(engine.Document.Days);
        ShowMini();
        var editor = new TaskEditor(this, Tasks[0], false);
        editor.Show();
        foreach (var palette in ColorThemes.Palettes)
        foreach (var mode in new[] { 1, 2 })
        {
            App.TraceSmoke($"Preview {palette.Id} mode {mode}");
            PaletteBox.SelectedValue = palette.Id;
            ThemeBox.SelectedIndex = mode;
            UpdateLayout();
            var expected = ColorThemes.Brushes(palette.Id, mode == 2);
            if (!SystemParameters.HighContrast)
            {
                foreach (var key in expected.Keys)
                {
                    var actual = (System.Windows.Media.SolidColorBrush)Application.Current.Resources[key];
                    if (actual.Color != (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(expected[key]))
                        throw new InvalidOperationException("Incorrect palette resource: " + key);
                }
                var canvas = (System.Windows.Media.SolidColorBrush)Application.Current.Resources["CanvasBrush"];
                if (((System.Windows.Media.SolidColorBrush)editor.Background).Color != canvas.Color || ((System.Windows.Media.SolidColorBrush)mini!.Background).Color != canvas.Color)
                    throw new InvalidOperationException("Open windows did not follow the theme preview.");
            }
            VerifyButtonLabelColors();
            VerifyScheduleAppearance();
            if (!Save()) throw new InvalidOperationException("Autosave failed during preview.");
            if (store.Load()!.Settings.ColorTheme != savedColor || store.Load()!.Settings.Theme != savedMode)
                throw new InvalidOperationException("Preview leaked into saved preferences.");
            if (System.Text.Json.JsonSerializer.Serialize(engine.Document.Days) != tasksBefore)
                throw new InvalidOperationException("Theme changed work records.");
        }
        App.TraceSmoke("Close preview windows and restore");
        editor.Close(); mini!.Close();
        RestoreTheme();
        if (ThemeDirty || PreviewColor != savedColor || PreviewMode != savedMode)
            throw new InvalidOperationException("Discard did not restore saved preferences.");
        App.TraceSmoke("Save Plum");
        PaletteBox.SelectedValue = "Plum"; ThemeBox.SelectedIndex = 2;
        if (!SavePreferences()) throw new InvalidOperationException("Theme save failed.");
        LoadSettings();
        if (ThemeDirty || PreviewColor != "Plum" || PreviewMode != "Dark" || store.Load()!.Settings.ColorTheme != "Plum")
            throw new InvalidOperationException("Saved theme did not reload.");
        PaletteBox.SelectedValue = savedColor;
        ThemeBox.SelectedIndex = savedMode switch { "Light" => 1, "Dark" => 2, _ => 0 };
        if (!SavePreferences()) throw new InvalidOperationException("Theme reset failed.");
        var screenshot = Environment.GetEnvironmentVariable("TIMETRACKER_SMOKE_SCREENSHOT");
        if (!string.IsNullOrEmpty(screenshot))
        {
            UpdateLayout();
            var image = new System.Windows.Media.Imaging.RenderTargetBitmap((int)ActualWidth, (int)ActualHeight, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
            image.Render(this);
            var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
            encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(image));
            using var output = File.Create(Path.Combine(Path.GetDirectoryName(screenshot)!, "theme-preview.png"));
            encoder.Save(output);
        }
    }
    private void VerifyButtonLabelColors()
    {
        var panel = new StackPanel();
        var probe = new Window { Owner = this, Width = 440, Height = 340, Content = panel, ShowInTaskbar = false };
        try
        {
            var cases = new[]
            {
                (Style: (Style)FindResource(typeof(Button)), Foreground: "TextBrush", Background: "SurfaceBrush"),
                (Style: (Style)FindResource("PrimaryButton"), Foreground: "OnPrimaryBrush", Background: "PrimaryBrush"),
                (Style: (Style)FindResource("SidebarButton"), Foreground: "OnDarkBrush", Background: "HeroBrush")
            };
            foreach (var item in cases)
            foreach (var content in new object[] { "ボタン", "_Save", new TextBlock { Text = "文字コンテンツ" } })
            {
                panel.Children.Add(new Button { Content = content, Style = item.Style, Tag = item });
            }
            probe.Show(); probe.UpdateLayout();
            foreach (Button button in panel.Children)
            {
                var item = ((Style Style, string Foreground, string Background))button.Tag;
                var label = Descendant<TextBlock>(button) ?? throw new InvalidOperationException("Button label was not rendered.");
                var text = (System.Windows.Media.SolidColorBrush)label.Foreground;
                var expected = (System.Windows.Media.SolidColorBrush)FindResource(item.Foreground);
                var frame = Descendant<Border>(button)!;
                var background = (System.Windows.Media.SolidColorBrush)frame.Background;
                var expectedBackground = (System.Windows.Media.SolidColorBrush)FindResource(item.Background);
                if (text.Color != expected.Color || background.Color != expectedBackground.Color)
                    throw new InvalidOperationException($"Rendered button label does not use {item.Foreground} on {item.Background}: {text.Color} / {background.Color}.");
            }
        }
        finally { probe.Close(); }
    }
}
