namespace TimeTracker.Core;

public sealed record ColorPalette(string Id, string Name, string Sidebar, string Hero, string Primary, string Hover,
    string Soft, string Canvas, string Text, string DarkCanvas, string DarkSurface);

public static class ColorThemes
{
    public static IReadOnlyList<ColorPalette> Palettes { get; } = Array.AsReadOnly(new[]
    {
        new ColorPalette("Navy", "ディープネイビー", "#101D3A", "#172C52", "#284F96", "#1F3F7A", "#ABC6F3", "#F2F5FA", "#1D2D49", "#101A2E", "#1B2B48"),
        new ColorPalette("Teal", "ティール", "#102C32", "#173B42", "#096D65", "#07574F", "#9EDCD3", "#F1F7F6", "#183631", "#102725", "#1B3532"),
        new ColorPalette("Slate", "スレート", "#202B3B", "#2B3B50", "#465F80", "#354A68", "#BECFE5", "#F3F5F8", "#26364B", "#1B2430", "#283545"),
        new ColorPalette("Plum", "プラム", "#302039", "#442E50", "#75458B", "#5C346F", "#DEC0EC", "#F8F3FA", "#392540", "#291D30", "#3A2B43")
    });
    public static string Normalize(string? id) => Palettes.Any(p => p.Id == id) ? id! : "Slate";
    public static ColorPalette Get(string? id) => Palettes.First(p => p.Id == Normalize(id));
    public static IReadOnlyDictionary<string, string> Brushes(string? id, bool dark)
    {
        var p = Get(id);
        return new Dictionary<string, string>
        {
            ["CanvasBrush"] = dark ? p.DarkCanvas : p.Canvas,
            ["SurfaceBrush"] = dark ? p.DarkSurface : "#FFFFFF",
            ["TextBrush"] = dark ? "#EDF2FA" : p.Text,
            ["MutedBrush"] = dark ? "#BAC8DA" : "#526176",
            ["LineBrush"] = dark ? "#8293AA" : "#7C8CA3",
            ["AccentBrush"] = dark ? p.Soft : p.Primary,
            ["SidebarBrush"] = p.Sidebar, ["HeroBrush"] = p.Hero,
            ["OnDarkBrush"] = "#FFFFFF", ["OnDarkMutedBrush"] = p.Soft,
            ["PrimaryBrush"] = p.Primary, ["PrimaryHoverBrush"] = p.Hover,
            ["OnPrimaryBrush"] = "#FFFFFF",
            ["HoverBrush"] = dark ? p.Hero : p.Canvas,
            ["ScrollThumbBrush"] = dark ? "#8293AA" : "#7C8CA3",
            ["ScrollThumbHoverBrush"] = dark ? p.Soft : p.Primary,
            ["ScrollThumbPressedBrush"] = dark ? "#FFFFFF" : p.Hover,
            ["ScrollTrackBrush"] = dark ? p.DarkCanvas : p.Canvas,
            ["RunningSurfaceBrush"] = dark ? p.Hero : p.Canvas
        };
    }
}
