using System.Text.Json.Nodes;
using TimeTracker.Core;
using Xunit;

namespace TimeTracker.Core.Tests;

public class ColorThemeTests
{
    [Theory]
    [InlineData("Navy")][InlineData("Teal")][InlineData("Slate")][InlineData("Plum")]
    public void ColorIsIndependentFromModeAndSurvivesStorage(string color)
    {
        var directory = Path.Combine(Path.GetTempPath(), "theme-tests-" + Guid.NewGuid());
        Directory.CreateDirectory(directory);
        try
        {
            foreach (var mode in new[] { "System", "Light", "Dark" })
            {
                var doc = new TrackerDocument { Settings = new Preferences { ColorTheme = color, Theme = mode, EnablePlannedOverrunAlert = true } };
                doc.Tasks(doc.SelectedDate).Add(new WorkTask { Name = "作業", ActualSeconds = 123.456 });
                var restored = BackupCodec.Decode(BackupCodec.Encode(doc));
                var store = new TrackerStore(Path.Combine(directory, "tracker.db"));
                store.Save(restored);
                var loaded = store.Load()!;
                Assert.Equal(color, loaded.Settings.ColorTheme);
                Assert.Equal(mode, loaded.Settings.Theme);
                Assert.True(loaded.Settings.EnablePlannedOverrunAlert);
                Assert.Equal(123.456, loaded.Tasks(doc.SelectedDate)[0].ActualSeconds);
            }
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Directory.Delete(directory, true);
        }
    }

    [Theory]
    [InlineData(null)][InlineData("Unknown")][InlineData("")]
    public void UnknownColorFallsBackWithoutLosingOtherSettings(string? value)
    {
        var doc = new TrackerDocument { Settings = new Preferences { Theme = "Dark", LongAlertMinutes = 75 } };
        var json = JsonNode.Parse(BackupCodec.Encode(doc))!;
        json["settings"]!["colorTheme"] = value;
        var restored = BackupCodec.Decode(json.ToJsonString());
        Assert.Equal("Slate", restored.Settings.ColorTheme);
        Assert.Equal("Dark", restored.Settings.Theme);
        Assert.Equal(75, restored.Settings.LongAlertMinutes);
        json["settings"]!.AsObject().Remove("colorTheme");
        Assert.Equal("Slate", BackupCodec.Decode(json.ToJsonString()).Settings.ColorTheme);
    }

    [Theory]
    [InlineData("Navy")][InlineData("Teal")][InlineData("Slate")][InlineData("Plum")]
    public void PaletteContrastMeetsReadableTextAndBorderTargets(string color)
    {
        foreach (var dark in new[] { false, true })
        {
            var brushes = ColorThemes.Brushes(color, dark);
            foreach (var surface in new[] { "SurfaceBrush", "CanvasBrush" })
            {
                Assert.True(Contrast(brushes["TextBrush"], brushes[surface]) >= 4.5);
                Assert.True(Contrast(brushes["MutedBrush"], brushes[surface]) >= 4.5);
                Assert.True(Contrast(brushes["AccentBrush"], brushes[surface]) >= 4.5);
                Assert.True(Contrast(brushes["LineBrush"], brushes[surface]) >= 3);
            }
            foreach (var background in new[] { "SidebarBrush", "HeroBrush" })
            {
                Assert.True(Contrast(brushes["OnDarkBrush"], brushes[background]) >= 4.5);
                Assert.True(Contrast(brushes["OnDarkMutedBrush"], brushes[background]) >= 4.5);
            }
            Assert.True(Contrast(brushes["OnPrimaryBrush"], brushes["PrimaryBrush"]) >= 4.5);
            Assert.True(Contrast(brushes["OnPrimaryBrush"], brushes["PrimaryHoverBrush"]) >= 4.5);
        }
    }
    private static double Contrast(string first, string second)
    {
        static double Luminance(string hex)
        {
            var rgb = new[] { 1, 3, 5 }.Select(offset => Convert.ToInt32(hex.Substring(offset, 2), 16) / 255d)
                .Select(value => value <= .04045 ? value / 12.92 : Math.Pow((value + .055) / 1.055, 2.4)).ToArray();
            return rgb[0] * .2126 + rgb[1] * .7152 + rgb[2] * .0722;
        }
        var a = Luminance(first); var b = Luminance(second);
        return (Math.Max(a, b) + .05) / (Math.Min(a, b) + .05);
    }
}
