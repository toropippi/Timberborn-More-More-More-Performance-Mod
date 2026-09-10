using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace T3MP.Loading;

// Windows-only, non-activating status panel. No game/Unity API on this thread,
// no cross-thread window ownership or SendMessage to the blocked game window.
internal static class NativeLoadProgressWindow
{
    private const uint Child = 0x40000000, Visible = 0x10000000;
    private static int _started;
    private static volatile bool _stopping;
    private static readonly AutoResetEvent Wake = new AutoResetEvent(false);
    internal static void Start()
    {
        if (Environment.OSVersion.Platform != PlatformID.Win32NT || Interlocked.Exchange(ref _started, 1) != 0) return;
        AppDomain.CurrentDomain.ProcessExit += (_, __) => Stop();
        UnityEngine.Application.quitting += Stop;
        var thread = new Thread(Run) { IsBackground = true, Name = "T3MP load progress" };
        thread.Start();
    }
    private static void Stop() { _stopping = true; Wake.Set(); }

    internal static IntPtr FindGameWindow()
    {
        if (Environment.OSVersion.Platform != PlatformID.Win32NT) return IntPtr.Zero;
        using var process = System.Diagnostics.Process.GetCurrentProcess();
        var window = IntPtr.Zero;
        while ((window = FindWindowExW(IntPtr.Zero, window, "UnityWndClass", null)) != IntPtr.Zero)
        {
            GetWindowThreadProcessId(window, out var pid);
            if (pid == process.Id) return window;
        }
        return IntPtr.Zero;
    }

    private static void Run()
    {
        IntPtr window = IntPtr.Zero, font = IntPtr.Zero;
        try
        {
            var controls = new CommonControls { Size = 8, Classes = 0x20 };
            InitCommonControlsEx(ref controls);
            // STATIC uses the OS window procedure, so no unmanaged callbacks
            // cross into Mono while its main thread is allocating/collecting.
            window = CreateWindowExW(0x08000088, "STATIC", "T3MP Load Progress", 0x80C00006,
                0, 0, 700, 380, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
            if (window == IntPtr.Zero) throw new InvalidOperationException("CreateWindow failed: " + Marshal.GetLastWin32Error());
            double scale = 1;
            int Scale(int value) => (int)Math.Round(value * scale);
            try { scale = Math.Max(1, GetDpiForWindow(window) / 96.0); } catch (EntryPointNotFoundException) { }
            SetWindowPos(window, IntPtr.Zero, 0, 0, Scale(700), Scale(380), 0x0010 | 0x0004 | 0x0002);
            font = CreateFontW(-Scale(17), 0, 0, 0, 400, 0, 0, 0, 1, 0, 0, 5, 0, "Yu Gothic UI");
            IntPtr Label(int y, int height)
            {
                var label = CreateWindowExW(0, "STATIC", "", Child | Visible,
                    Scale(18), Scale(y), Scale(660), Scale(height), window, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
                SendMessageW(label, 0x30, font, IntPtr.Zero);
                return label;
            }
            var heading = Label(12, 27);
            var status = Label(43, 25);
            var detail = Label(70, 24);
            var bar = CreateWindowExW(0, "msctls_progress32", "", Child | Visible | 1,
                Scale(18), Scale(100), Scale(660), Scale(17), window, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
            SendMessageW(bar, 0x406, IntPtr.Zero, new IntPtr(7)); // PBM_SETRANGE32
            var rows = new IntPtr[5];
            for (var i = 0; i < rows.Length; i++) rows[i] = Label(129 + 29 * i, 26);
            var footer = Label(280, 26);
            var note = Label(309, 24);
            SetWindowTextW(note, "バーは区間数の進捗です。秒数は実時間（CPU 時間ではありません）。");
            var shown = false;
            var lastText = new string[9];
            var textHandles = new[] { heading, status, detail, rows[0], rows[1], rows[2], rows[3], rows[4], footer };
            var lastCompleted = -1;
            while (!_stopping && IsWindow(window))
            {
                while (PeekMessageW(out var message, IntPtr.Zero, 0, 0, 1))
                {
                    TranslateMessage(ref message);
                    DispatchMessageW(ref message);
                }
                var view = LoadProgress.Snapshot();
                var canShow = view != null && view.GameWindow != IntPtr.Zero && IsWindow(view.GameWindow) && !IsIconic(view.GameWindow);
                // Keep the panel with the game; never overlay another app.
                canShow &= view != null && GetForegroundWindow() == view.GameWindow;
                if (view != null)
                {
                    var strings = new[] { view.Heading, view.Status, view.Detail, view.Rows[0], view.Rows[1], view.Rows[2], view.Rows[3], view.Rows[4], view.Footer };
                    for (var i = 0; i < strings.Length; i++)
                        if (strings[i] != lastText[i]) { SetWindowTextW(textHandles[i], strings[i]); lastText[i] = strings[i]; }
                    if (lastCompleted != view.Completed) { SendMessageW(bar, 0x402, new IntPtr(view.Completed), IntPtr.Zero); lastCompleted = view.Completed; }
                }
                if (canShow && GetWindowRect(view!.GameWindow, out var rect))
                {
                    var width = Scale(700); var height = Scale(380);
                    SetWindowPos(window, new IntPtr(-1), rect.Left + Math.Max(0, (rect.Right - rect.Left - width) / 2),
                        rect.Top + Scale(50), width, height, 0x0010 | 0x0040); // NOACTIVATE | SHOWWINDOW
                    shown = true;
                }
                else if (shown) { ShowWindow(window, 0); shown = false; }
                Wake.WaitOne(view == null ? 250 : 100);
            }
        }
        catch (Exception e)
        {
            // Console is safe here; do not use Unity APIs from this thread.
            Console.Error.WriteLine("[T3MPPROGRESS] native window unavailable: " + e.Message);
        }
        finally
        {
            if (window != IntPtr.Zero) DestroyWindow(window);
            if (font != IntPtr.Zero) DeleteObject(font);
        }
    }

    [StructLayout(LayoutKind.Sequential)] private struct CommonControls { internal uint Size, Classes; }
    [StructLayout(LayoutKind.Sequential)] private struct Rect { internal int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct Message
    {
        internal IntPtr Window; internal uint Id; internal UIntPtr WParam; internal IntPtr LParam;
        internal uint Time; internal int X, Y; internal uint Private;
    }
    [DllImport("comctl32.dll")] private static extern bool InitCommonControlsEx(ref CommonControls controls);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr CreateWindowExW(uint exStyle, string className, string name, uint style, int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(IntPtr window);
    [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr window);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr window);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr window, out Rect rect);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr window, int command);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool SetWindowTextW(IntPtr window, string text);
    [DllImport("user32.dll")] private static extern IntPtr SendMessageW(IntPtr window, uint message, IntPtr wparam, IntPtr lparam);
    [DllImport("user32.dll")] private static extern bool PeekMessageW(out Message message, IntPtr window, uint min, uint max, uint remove);
    [DllImport("user32.dll")] private static extern bool TranslateMessage(ref Message message);
    [DllImport("user32.dll")] private static extern IntPtr DispatchMessageW(ref Message message);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr window);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr FindWindowExW(IntPtr parent, IntPtr after, string className, string? title);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr CreateFontW(int height, int width, int escape, int orientation, int weight, uint italic, uint underline, uint strike, uint charset, uint output, uint clip, uint quality, uint pitch, string face);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr obj);
}
