using System.IO;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;

internal static class Program
{
    private static readonly List<Target> Windows = [];
    private static readonly List<object> Messages = [];
    private static readonly List<object> Interruptions = [];
    private static int Scenario;
    private static string Output = "";
    private static readonly bool CompactNativeTargets = Environment.GetEnvironmentVariable("FWM_FULLGRAPH_COMPACT_TARGETS") == "1";

    [STAThread]
    private static void Main(string[] args)
    {
        if (args.Length != 1 || Directory.Exists(args[0])) throw new ArgumentException("A fresh output directory is required.");
        Output = Path.GetFullPath(args[0]); Directory.CreateDirectory(Output);
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        using var control = new Control();
        _ = control.Handle;
        var reader = new Thread(() =>
        {
            string? line;
            while ((line = Console.ReadLine()) is not null)
            {
                string command = line;
                control.BeginInvoke(() => Execute(command));
            }
            if (!control.IsDisposed) control.BeginInvoke(Application.ExitThread);
        }) { IsBackground = true, Name = "Owned target command reader" };
        reader.Start();
        Console.WriteLine(JsonSerializer.Serialize(new { Ready = true, ProcessId = Environment.ProcessId,
            OwnerThreadId = GetCurrentThreadId(), Stopwatch.Frequency, CompactNativeTargets,
            Dpi = GetDpiForWindow(control.Handle), ScreenBounds = Screen.FromControl(control).Bounds,
            ScreenWorkingArea = Screen.FromControl(control).WorkingArea }));
        try { Application.Run(); }
        finally
        {
            var handles = Windows.Where(w => w.IsHandleCreated).Select(w => w.Handle.ToInt64()).ToArray();
            foreach (var window in Windows) window.Dispose();
            File.WriteAllText(Path.Combine(Output, "messages.json"), JsonSerializer.Serialize(Messages));
            File.WriteAllText(Path.Combine(Output, "interruptions.json"), JsonSerializer.Serialize(Interruptions));
            File.WriteAllText(Path.Combine(Output, "cleanup.json"), JsonSerializer.Serialize(new {
                ProcessId = Environment.ProcessId, Handles = handles,
                AllDestroyed = handles.All(h => !IsWindow(new(h))), FinishedUtc = DateTime.UtcNow }));
        }
    }

    private static void Execute(string line)
    {
        try
        {
            using var doc = JsonDocument.Parse(line); var c = doc.RootElement;
            string operation = c.GetProperty("Operation").GetString()!;
            switch (operation)
            {
                case "create":
                    if (Windows.Any(w => !w.IsDisposed)) throw new InvalidOperationException("Previous owned targets remain.");
                    Windows.Clear();
                    int count = c.GetProperty("Count").GetInt32();
                    if (count is not (1 or 10 or 50)) throw new ArgumentException("Unsupported target count.");
                    for (int i = 0; i < count; i++)
                    {
                        var form = new Target(i) { Text = $"PERF-010 owned full-app target {i}",
                            StartPosition = FormStartPosition.Manual, Bounds = new(20 + i % 10 * 40, 20 + i / 10 * 40, 200, 150),
                            FormBorderStyle = FormBorderStyle.None, MinimumSize = new(48, 48),
                            BackColor = System.Drawing.Color.FromArgb(40 + i * 3, 100, 180) };
                        Windows.Add(form); form.Show(); form.Update();
                    }
                    break;
                case "scenario": Scenario = c.GetProperty("Id").GetInt32(); break;
                case "close-all": foreach (var w in Windows.ToArray()) w.Close(); break;
                case "close": Windows[c.GetProperty("Index").GetInt32()].Close(); break;
                case "minimize": Windows[c.GetProperty("Index").GetInt32()].WindowState = FormWindowState.Minimized; break;
                case "restore": Windows[c.GetProperty("Index").GetInt32()].WindowState = FormWindowState.Normal; break;
                case "arm-interruption":
                    Windows[c.GetProperty("Index").GetInt32()].Arm(c.GetProperty("Action").GetString()!,
                        c.TryGetProperty("EventName", out var eventName) ? eventName.GetString() : null);
                    break;
                case "snapshot": break;
                case "shutdown": Application.ExitThread(); break;
                default: throw new ArgumentException(operation);
            }
            Console.WriteLine(JsonSerializer.Serialize(new { Operation = operation, Success = true, Qpc = Stopwatch.GetTimestamp(), Interruptions,
                Windows = Windows.Where(w => !w.IsDisposed).Select(w => new { w.Index, Hwnd = w.Handle.ToInt64(),
                    OwnerThreadId = GetWindowThreadProcessId(w.Handle, out _), Dpi = GetDpiForWindow(w.Handle),
                    w.Left, w.Top, w.Width, w.Height, State = w.WindowState.ToString(), w.NativeMinimumReplies, CompactNativeTargets }).ToArray() }));
        }
        catch (Exception error) { Console.WriteLine(JsonSerializer.Serialize(new { Success = false, Error = error.ToString() })); }
    }

    private sealed class Target(int index) : Form
    {
        private string? m_interruption;
        private EventWaitHandle? m_nativeSignal;
        public int Index => index;
        public int NativeMinimumReplies { get; private set; }
        public void Arm(string action, string? eventName)
        {
            if (action is not ("minimize" or "close" or "notify") || m_interruption is not null || IsDisposed)
                throw new InvalidOperationException("Invalid native interruption request.");
            m_interruption = action;
            if (eventName is not null) m_nativeSignal = EventWaitHandle.OpenExisting(eventName);
        }
        protected override void Dispose(bool disposing)
        {
            if (disposing) { m_nativeSignal?.Dispose(); m_nativeSignal = null; }
            base.Dispose(disposing);
        }
        protected override bool ShowWithoutActivation => true;
        protected override CreateParams CreateParams
        {
            get
            {
                var result = base.CreateParams;
                // Small controlled native targets retain real resize/minimize
                // contracts without the caption's 136-pixel minimum width.
                result.Style |= 0x00040000 | 0x00020000 | 0x00010000;
                return result;
            }
        }
        protected override void WndProc(ref Message message)
        {
            base.WndProc(ref message);
            if (CompactNativeTargets && message.Msg == 0x24)
            {
                // This is the minimum-size contract of this owned diagnostic
                // window. It changes neither display settings nor production
                // window sizing. Keep all real resize/minimize HWND styles.
                var limits = Marshal.PtrToStructure<MinMaxInfo>(message.LParam);
                limits.MinTrackX = 8; limits.MinTrackY = 8;
                Marshal.StructureToPtr(limits, message.LParam, false);
                message.Result = 0; NativeMinimumReplies++;
            }
            if (message.Msg == 0x47)
            {
                var position = Marshal.PtrToStructure<WindowPosition>(message.LParam);
                Messages.Add(new { Scenario, Target = index, Hwnd = Handle.ToInt64(), Qpc = Stopwatch.GetTimestamp(),
                    OwnerThreadId = GetCurrentThreadId(), position.X, position.Y, position.Width, position.Height, position.Flags });
                if (m_interruption is { } action && (position.Flags & 3) != 3)
                {
                    m_interruption = null;
                    long triggered = Stopwatch.GetTimestamp(), hwnd = Handle.ToInt64(); int scenario = Scenario;
                    if (action == "notify" && m_nativeSignal is { } signal)
                    {
                        Interruptions.Add(new { Scenario = scenario, Target = index, Hwnd = hwnd, Action = action,
                            NativePositionMessageQpc = triggered, AppliedQpc = Stopwatch.GetTimestamp(),
                            OwnerThreadId = GetCurrentThreadId(), State = WindowState.ToString(), DirectSignal = true });
                        signal.Set(); signal.Dispose(); m_nativeSignal = null;
                        return;
                    }
                    BeginInvoke(() =>
                    {
                        long applied = Stopwatch.GetTimestamp();
                        if (action == "minimize") WindowState = FormWindowState.Minimized;
                        else if (action == "close") Close();
                        Interruptions.Add(new { Scenario = scenario, Target = index, Hwnd = hwnd, Action = action,
                            NativePositionMessageQpc = triggered, AppliedQpc = applied, OwnerThreadId = GetCurrentThreadId(),
                            State = IsDisposed ? "Closed" : WindowState.ToString() });
                    });
                }
            }
        }
    }

    [StructLayout(LayoutKind.Sequential)] private struct WindowPosition { public nint Hwnd, InsertAfter; public int X, Y, Width, Height; public uint Flags; }
    [StructLayout(LayoutKind.Sequential)] private struct MinMaxInfo { public int ReservedX, ReservedY, MaxSizeX, MaxSizeY, MaxPositionX, MaxPositionY, MinTrackX, MinTrackY, MaxTrackX, MaxTrackY; }
    [DllImport("user32.dll")] private static extern bool IsWindow(nint hwnd);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(nint hwnd);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint hwnd, out uint processId);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
}
