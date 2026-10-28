using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using Microsoft.Win32;

namespace TimeTracker.Windows;

internal static partial class NativeMethods
{
    [StructLayout(LayoutKind.Sequential)]
    internal struct LastInput { public uint Size; public uint Tick; }
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetLastInputInfo(ref LastInput input);
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool RegisterHotKey(IntPtr window, int id, uint modifiers, uint key);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool UnregisterHotKey(IntPtr window, int id);

    internal static TimeSpan IdleTime()
    {
        var input = new LastInput { Size = (uint)Marshal.SizeOf<LastInput>() };
        if (!GetLastInputInfo(ref input)) throw new Win32Exception(Marshal.GetLastWin32Error());
        return TimeSpan.FromMilliseconds(unchecked((uint)Environment.TickCount - input.Tick));
    }
}

internal sealed class GlobalShortcuts : IDisposable
{
    private readonly HwndSource source;
    private readonly Action toggle;
    private readonly Action show;
    public bool Registered { get; }
    public GlobalShortcuts(IntPtr handle, Action toggle, Action show)
    {
        this.toggle = toggle;
        this.show = show;
        source = HwndSource.FromHwnd(handle) ?? throw new InvalidOperationException("ウィンドウを取得できません。");
        source.AddHook(HandleMessage);
        var timer = NativeMethods.RegisterHotKey(handle, 1, 0x4003, 0x54); // no-repeat, Ctrl+Alt+T
        var activate = NativeMethods.RegisterHotKey(handle, 2, 0x4003, 0x20);
        Registered = timer && activate;
    }
    private IntPtr HandleMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message == 0x0312)
        {
            if (wParam.ToInt32() == 1) toggle();
            else if (wParam.ToInt32() == 2) show();
            handled = true;
        }
        return IntPtr.Zero;
    }
    public void Dispose()
    {
        NativeMethods.UnregisterHotKey(source.Handle, 1);
        NativeMethods.UnregisterHotKey(source.Handle, 2);
        source.RemoveHook(HandleMessage);
    }
}

internal static class StartupRegistration
{
    private const string Key = @"Software\Microsoft\Windows\CurrentVersion\Run";
    public static void Set(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(Key);
        if (enabled)
        {
            var executable = Environment.ProcessPath ?? throw new InvalidOperationException("実行ファイルの場所が不明です。");
            if (!executable.EndsWith("TimeTrackerReport.exe", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("自動起動はビルド済みのTimeTrackerReport.exeから設定してください。");
            key.SetValue("TimeTrackerReport", $"\"{executable}\" --tray");
        }
        else key.DeleteValue("TimeTrackerReport", false);
    }
}
