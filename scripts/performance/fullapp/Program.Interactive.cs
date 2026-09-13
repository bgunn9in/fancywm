using System.Diagnostics;
using System.Runtime.InteropServices;
using FancyWM.Models;
using FancyWM.Utilities;

internal static partial class Program
{
    private static KeybindingDictionary ValidationBindings()
    {
        var result = new KeybindingDictionary(false);
        foreach (var action in Enum.GetValues<BindableAction>()) result[action] = null;
        result[BindableAction.MoveFocusRight] = new(new HashSet<KeyCode> { KeyCode.LeftCtrl, KeyCode.LeftAlt, KeyCode.F24 }, true);
        result[BindableAction.MoveFocusDown] = new(new HashSet<KeyCode> { KeyCode.LeftCtrl, KeyCode.LeftAlt, KeyCode.F23 }, true);
        return result;
    }

    private static async Task ValidateInteractiveFocus()
    {
        var geometry = await VerifiedGeometry(10);
        var start = geometry.OrderBy(row => row.Actual.Left).ThenBy(row => row.Actual.Top).First();
        bool horizontal = geometry.Any(row => row.Actual.Left >= start.Actual.Right
            && row.Actual.Top < start.Actual.Bottom && row.Actual.Bottom > start.Actual.Top);
        var key = horizontal ? KeyCode.F24 : KeyCode.F23;
        var hooks = (Array)Field(MainWindowObject!, "m_directHks")!;
        var hook = hooks.Cast<object>().Single(h => (KeyCode)Property(h, "Key")! == key);
        int notifications = 0; long notifiedQpc = 0; uint notifiedThread = 0;
        EventHandler<EventArgs> handler = (_, _) => { notifications++; notifiedQpc = Stopwatch.GetTimestamp(); notifiedThread = GetCurrentThreadId(); };
        var pressed = hook.GetType().GetEvent("Pressed")!;
        pressed.AddEventHandler(hook, handler);
        nint foregroundBefore = GetForegroundWindow();
        if (!GetCursorPos(out var cursorBefore)) throw new System.ComponentModel.Win32Exception();
        var point = new NativePoint((start.Actual.Left + start.Actual.Right) / 2, (start.Actual.Top + start.Actual.Bottom) / 2);
        uint inputThread = GetCurrentThreadId();
        try
        {
            // Reorder only this owned target. No input is sent until the hit
            // test identifies that HWND and all relevant keys are released.
            if (GetWindowProcess(new(start.Hwnd)) != Targets!.ProcessId) throw new InvalidOperationException("Input target ownership changed.");
            if (!SetWindowPos(new(start.Hwnd), 0, 0, 0, 0, 0, 0x0213)) throw new System.ComponentModel.Win32Exception();
            await Task.Delay(50);
            await WaitUntil(() => WindowFromPoint(point) == new nint(start.Hwnd), "unobscured owned interactive target");
            int[] keys = [0x01, 0x02, 0x10, 0x11, 0x12, 0x5B, 0x5C, 0x86, 0x87];
            if (keys.Any(k => (GetAsyncKeyState(k) & 0x8000) != 0)) throw new InvalidOperationException("Interactive validation requires released mouse buttons and modifier keys.");
            int x = (int)((long)(point.X - GetSystemMetrics(76)) * 65535 / (GetSystemMetrics(78) - 1));
            int y = (int)((long)(point.Y - GetSystemMetrics(77)) * 65535 / (GetSystemMetrics(79) - 1));
            Input[] mouse = [Input.Mouse(x, y, 0xC001), Input.Mouse(0, 0, 2), Input.Mouse(0, 0, 4)];
            SendOwnedInput(mouse);
            await WaitUntil(() => GetForegroundWindow() == new nint(start.Hwnd), "owned mouse-click focus");
            // Native foreground changes precede the workspace's WinEvent
            // publication. Send the command only after the real service can
            // observe and move focus from this owned target.
            var tiling = Field(MainWindowObject!, "m_tiling")!;
            var directionValue = horizontal ? FancyWM.Layouts.Tiling.TilingDirection.Right : FancyWM.Layouts.Tiling.TilingDirection.Down;
            await WaitUntil(() => GetForegroundWindow() == new nint(start.Hwnd)
                && Property(Field(MainWindowObject!, "m_workspace")!, "FocusedWindow") is WinMan.IWindow focused
                && focused.Handle == new nint(start.Hwnd)
                && tiling.GetType().GetMethod("GetFocus", Instance)!.Invoke(tiling, null) is WinMan.IWindow backendFocus
                && backendFocus.Handle == new nint(start.Hwnd)
                && (bool)tiling.GetType().GetMethod("CanMoveFocus", Instance)!.Invoke(tiling, [directionValue])!,
                "production focus publication and directional command availability");
            long submitted = Stopwatch.GetTimestamp();
            var focusNode = TilingServices().SelectMany(Nodes).Single(node => node.WindowReference.Handle == new nint(start.Hwnd));
            var expectedFocus = focusNode.GetAdjacentWindow(directionValue)!.WindowReference.Handle;
            Observations.Add(new { Kind = "InteractiveCommandSubmitted", Qpc = submitted,
                InitialHwnd = start.Hwnd, ExpectedHwnd = expectedFocus.ToInt64(), Key = key.ToString() });
            if (GetWindowProcess(GetForegroundWindow()) != Targets.ProcessId) throw new InvalidOperationException("Foreground ownership changed before keyboard input.");
            ushort vk = (ushort)key;
            Input[] keyboard = [Input.Key(0xA2, false), Input.Key(0xA4, false), Input.Key(vk, false),
                Input.Key(vk, true), Input.Key(0xA4, true), Input.Key(0xA2, true)];
            SendOwnedInput(keyboard);
            await WaitUntil(() => notifications == 1 && GetForegroundWindow() != new nint(start.Hwnd)
                && GetWindowProcess(GetForegroundWindow()) == Targets.ProcessId, "production direct hotkey and owned focus movement");
            var end = geometry.Single(row => row.Hwnd == GetForegroundWindow().ToInt64());
            bool direction = horizontal ? end.Actual.Left > start.Actual.Left : end.Actual.Top > start.Actual.Top;
            if (!direction || notifiedThread != inputThread) throw new InvalidOperationException("Native focus direction or Dispatcher callback thread differs.");
            Observations.Add(new { Kind = "InteractiveMouseAndDirectHotkey", Passed = true, Direction = horizontal ? "Right" : "Down",
                InitialHwnd = start.Hwnd, FinalHwnd = end.Hwnd, Key = key.ToString(), SentMouseInputs = mouse.Length,
                SentKeyboardInputs = keyboard.Length, HotkeyNotifications = notifications, SubmittedQpc = submitted,
                NotificationQpc = notifiedQpc, DispatcherNativeThreadId = notifiedThread,
                InputApi = "SendInput with owned HWND hit/foreground guards; production LowLevelHotkey -> Dispatcher -> MoveFocus" });
        }
        finally
        {
            Observations.Add(new { Kind = "InteractiveAttemptFinalState", Notifications = notifications,
                NotificationQpc = notifiedQpc, NotificationNativeThreadId = notifiedThread,
                ForegroundHwnd = GetForegroundWindow().ToInt64(),
                ForegroundProcessId = GetWindowProcess(GetForegroundWindow()), Key = key.ToString(),
                HitTestHwnd = WindowFromPoint(point).ToInt64(), HitTestProcessId = GetWindowProcess(WindowFromPoint(point)),
                KeyboardHookLastActive = Field(Property(hook, "KeyboardHook")!, "m_lastActiveTimestamp"),
                HookDisposed = Field(hook, "m_disposed"), PressedModifiers = Field(hook, "m_pressedModifiers"),
                BackendFocusHwnd = (Field(MainWindowObject!, "m_tiling")!.GetType().GetMethod("GetFocus", Instance)!
                    .Invoke(Field(MainWindowObject!, "m_tiling"), null) as WinMan.IWindow)?.Handle.ToInt64(),
                FocusSequenceStopped = Field(typeof(FancyWM.MainWindow).Assembly.GetType("FancyWM.Utilities.FocusHelper")!
                    .GetField("s_requests", Static)!.GetValue(null)!, "m_stopped") });
            pressed.RemoveEventHandler(hook, handler);
            if (GetCursorPos(out var now) && Math.Abs(now.X - point.X) <= 1 && Math.Abs(now.Y - point.Y) <= 1)
                SetCursorPos(cursorBefore.X, cursorBefore.Y);
            if (GetWindowProcess(GetForegroundWindow()) == Targets!.ProcessId && foregroundBefore != 0)
                SetForegroundWindow(foregroundBefore);
        }
    }

    private static void SendOwnedInput(Input[] inputs)
    {
        if (Marshal.SizeOf<Input>() != 40) throw new InvalidOperationException("Unexpected x64 INPUT layout.");
        uint sent = SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<Input>());
        if (sent != inputs.Length)
        {
            int error = Marshal.GetLastWin32Error();
            var pressed = new HashSet<ushort>(); bool mousePressed = false;
            foreach (var input in inputs.Take((int)sent))
            {
                if (input.Type == 1)
                {
                    if ((input.KeyFlags & 2) == 0) pressed.Add(input.VirtualKey);
                    else pressed.Remove(input.VirtualKey);
                }
                else
                {
                    if ((input.MouseFlags & 2) != 0) mousePressed = true;
                    if ((input.MouseFlags & 4) != 0) mousePressed = false;
                }
            }
            var releases = pressed.Select(k => Input.Key(k, true)).ToList();
            if (mousePressed) releases.Add(Input.Mouse(0, 0, 4));
            uint released = releases.Count == 0 ? 0 : SendInput((uint)releases.Count, releases.ToArray(), Marshal.SizeOf<Input>());
            throw new System.ComponentModel.Win32Exception(error, $"SendInput accepted {sent}/{inputs.Length}; released {released}/{releases.Count} owned partial-input states.");
        }
    }
    [StructLayout(LayoutKind.Sequential)] private readonly record struct NativePoint(int X, int Y);
    [StructLayout(LayoutKind.Explicit, Size = 40)] private struct Input
    {
        [FieldOffset(0)] public uint Type;
        [FieldOffset(8)] public int X;
        [FieldOffset(12)] public int Y;
        [FieldOffset(20)] public uint MouseFlags;
        [FieldOffset(8)] public ushort VirtualKey;
        [FieldOffset(12)] public uint KeyFlags;
        public static Input Mouse(int x, int y, uint flags) => new() { Type = 0, X = x, Y = y, MouseFlags = flags };
        public static Input Key(ushort key, bool up) => new() { Type = 1, VirtualKey = key, KeyFlags = up ? 2u : 0u };
    }
    [DllImport("user32.dll", SetLastError = true)] private static extern uint SendInput(uint count, Input[] input, int size);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(nint hwnd);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out NativePoint point);
    [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] private static extern nint WindowFromPoint(NativePoint point);
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool SetWindowPos(nint hwnd, nint after, int x, int y, int width, int height, uint flags);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
}
