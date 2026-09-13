using System.Diagnostics;
using System.Runtime.InteropServices;
using Forms = System.Windows.Forms;
using Rectangle = WinMan.Rectangle;

namespace FancyWM.AnimationNativeHarness;

internal sealed class NativeWindows : IDisposable
{
    private readonly List<Owner> m_owners = [];
    public nint[] Handles { get; private set; } = [];
    public Rectangle[] Originals { get; private set; } = [];
    public Rectangle[] Destinations { get; private set; } = [];
    public uint[] OwnerThreadIds { get; }
    public uint[] OwnerProcessIds { get; }
    public long[] ExtendedStylesBeforeVisibilityFix { get; private set; } = [];
    public uint Dpi { get; private set; }
    public System.Drawing.Rectangle WorkArea { get; private set; }
    public nint ForegroundBefore { get; } = GetForegroundWindow();

    public NativeWindows(int count, Recording recording, int ownerLimit = 1)
    {
        if (count is < 1 or > 50 || ownerLimit is < 1 or > 50)
            throw new ArgumentOutOfRangeException(nameof(count));
        int owners = Math.Min(count, ownerLimit);
        Handles = new nint[count];
        OwnerThreadIds = new uint[count];
        OwnerProcessIds = new uint[count];
        WorkArea = Forms.Screen.PrimaryScreen!.WorkingArea;
        int cellWidth = WorkArea.Width / 10, cellHeight = WorkArea.Height / 5;
        if (cellWidth < 80 || cellHeight < 70) throw new InvalidOperationException("Display is too small for the fixed grid.");
        try
        {
            for (int ownerIndex = 0; ownerIndex < owners; ownerIndex++)
            {
                var indices = Enumerable.Range(0, count).Where(i => i % owners == ownerIndex).ToArray();
                var owner = new Owner(ownerIndex, forms =>
                {
                    foreach (int i in indices)
                    {
                        var form = new TargetForm(recording, i)
                        {
                            StartPosition = Forms.FormStartPosition.Manual,
                            Bounds = new(WorkArea.Left + i % 10 * cellWidth + 4,
                                WorkArea.Top + i / 10 * cellHeight + 4, cellWidth / 2, cellHeight / 2),
                            FormBorderStyle = Forms.FormBorderStyle.None,
                            ShowInTaskbar = false,
                            TopMost = true,
                            Text = $"FancyWM native measurement {i + 1}",
                            BackColor = System.Drawing.Color.FromArgb(40 + i * 3, 100, 180),
                        };
                        forms.Add(form);
                        form.Show();
                        form.Update();
                        Handles[i] = form.Handle;
                        OwnerThreadIds[i] = GetWindowThreadProcessId(form.Handle, out uint processId);
                        OwnerProcessIds[i] = processId;
                        if (OwnerThreadIds[i] != GetCurrentThreadId() || processId != Environment.ProcessId)
                            throw new InvalidOperationException("HWND ownership differs from the creating thread/process.");
                    }
                });
                m_owners.Add(owner);
                owner.Start();
            }
            Task.WhenAll(m_owners.Select(owner => owner.Ready)).WaitAsync(TimeSpan.FromSeconds(20)).GetAwaiter().GetResult();
            ExtendedStylesBeforeVisibilityFix = Handles.Select(handle => GetWindowLongPtr(handle, -20).ToInt64()).ToArray();
            // The first Form on each newly started message loop can lose the
            // native TOPMOST bit despite its managed TopMost property. D9/D11
            // demonstrate the occlusion and this correction using hit tests
            // and actual screen pixels. Establish the intended native style
            // after every owner loop is running, outside measured transitions.
            PrepareVisibility();
            if (OwnerThreadIds.Distinct().Count() != owners)
                throw new InvalidOperationException("Actual HWND owner count differs from the topology.");
            Dpi = GetDpiForWindow(Handles[0]);
            if (Handles.Any(handle => GetDpiForWindow(handle) != Dpi))
                throw new InvalidOperationException("Target DPI differs within the fixed grid.");
            Originals = Handles.Select(ReadRectangle).ToArray();
            Destinations = Originals.Select(rect => Rectangle.OffsetAndSize(
                rect.Left + cellWidth / 3, rect.Top + cellHeight / 3, rect.Width, rect.Height)).ToArray();
        }
        catch (Exception constructionFailure)
        {
            try { Dispose(); }
            catch (Exception cleanupFailure) { throw new AggregateException(constructionFailure, cleanupFailure); }
            throw;
        }
    }

    private sealed class Owner
    {
        private readonly Thread m_thread;
        private readonly TaskCompletionSource m_ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly List<TargetForm> m_forms = [];
        private Exception? m_failure;
        private bool m_started;
        public Task Ready => m_ready.Task;

        public Owner(int index, Action<List<TargetForm>> initialize)
        {
            m_thread = new Thread(() =>
            {
                try
                {
                    initialize(m_forms);
                    m_forms[0].BeginInvoke(() => m_ready.TrySetResult());
                    Forms.Application.Run();
                }
                catch (Exception error)
                {
                    m_failure = error;
                    m_ready.TrySetException(error);
                }
                finally
                {
                    foreach (var form in m_forms) form.Dispose();
                }
            }) { Name = $"Native target HWND owner {index}", IsBackground = true };
            m_thread.SetApartmentState(ApartmentState.STA);
        }

        public void Start()
        {
            m_thread.Start();
            m_started = true;
        }

        public void CloseAndJoin()
        {
            if (!m_started) return;
            // Ready is published by the owner's message loop, not by initialization.
            if (m_thread.IsAlive && m_ready.Task.IsCompletedSuccessfully)
            {
                m_forms[0].BeginInvoke(() =>
                {
                    foreach (var form in m_forms) form.Close();
                    Forms.Application.ExitThread();
                });
            }
            if (!m_thread.Join(TimeSpan.FromSeconds(10)))
                throw new TimeoutException("An owned HWND thread did not exit.");
            if (m_failure is not null) throw new InvalidOperationException("Native HWND owner failed.", m_failure);
        }
    }

    public async Task VerifyAsync(Rectangle[] expected)
    {
        var deadline = Stopwatch.StartNew();
        while (true)
        {
            bool correct = true;
            for (int i = 0; i < Handles.Length; i++)
                correct &= ReadRectangle(Handles[i]) == expected[i];
            if (correct) return;
            if (deadline.Elapsed > TimeSpan.FromSeconds(5))
                throw new InvalidOperationException("The actual HWND rectangles did not reach every target.");
            await Task.Delay(1);
        }
    }

    internal sealed record Visibility(long Hwnd, long ExtendedStyle, bool Visible, uint Cloaked, long[] HitHwnds);

    public void PrepareVisibility()
    {
        // Setup only: call before the existing 100 ms settling delay and CPU/QPC
        // sample boundaries. Never repair visibility during a measured transition.
        // NOOWNERZORDER is required for the hidden WinForms taskbar owners:
        // D12 shows ordinary reordering demotes other owner groups, while
        // preserving owner order retains native TOPMOST on every target.
        foreach (nint handle in Handles)
            if (!SetWindowPos(handle, new(-1), 0, 0, 0, 0, 0x0213))
                throw new System.ComponentModel.Win32Exception();
    }

    public Visibility[] VerifyVisibility()
    {
        return Handles.Select(handle =>
        {
            var rect = ReadRectangle(handle);
            var points = new[] { new NativePoint(rect.Left + 1, rect.Top + 1),
                new NativePoint(rect.Right - 2, rect.Top + 1), new NativePoint(rect.Left + 1, rect.Bottom - 2),
                new NativePoint(rect.Right - 2, rect.Bottom - 2),
                new NativePoint((rect.Left + rect.Right) / 2, (rect.Top + rect.Bottom) / 2) };
            long style = GetWindowLongPtr(handle, -20).ToInt64();
            bool visible = IsWindowVisible(handle);
            Marshal.ThrowExceptionForHR(DwmGetWindowAttribute(handle, 14, out uint cloaked, sizeof(uint)));
            long[] hit = points.Select(point => WindowFromPoint(point).ToInt64()).ToArray();
            if ((style & 8) == 0 || !visible || cloaked != 0 || hit.Any(value => value != handle.ToInt64()))
                throw new InvalidOperationException($"An owned target is obscured: HWND={handle}, style={style:X}, visible={visible}, cloaked={cloaked}, hits={string.Join(',', hit)}");
            return new Visibility(handle.ToInt64(), style, visible, cloaked, hit);
        }).ToArray();
    }

    public void Dispose()
    {
        var failures = new List<Exception>();
        foreach (var owner in m_owners)
        {
            try { owner.CloseAndJoin(); }
            catch (Exception error) { failures.Add(error); }
        }
        if (Handles.Any(IsWindow)) failures.Add(new InvalidOperationException("An owned HWND survived cleanup."));
        if (failures.Count != 0) throw new AggregateException(failures);
    }

    internal static Rectangle ReadRectangle(nint handle)
    {
        if (!GetWindowRect(handle, out var rect)) throw new System.ComponentModel.Win32Exception();
        return new(rect.Left, rect.Top, rect.Right, rect.Bottom);
    }

    private sealed class TargetForm(Recording recording, int target) : Forms.Form
    {
        protected override bool ShowWithoutActivation => true;
        protected override Forms.CreateParams CreateParams
        {
            get
            {
                var result = base.CreateParams;
                result.ExStyle |= 0x08000080; // NOACTIVATE | TOOLWINDOW
                return result;
            }
        }

        protected override void WndProc(ref Forms.Message message)
        {
            base.WndProc(ref message);
            if (message.Msg == 0x0047 && Volatile.Read(ref recording.Transition) != 0) // WM_WINDOWPOSCHANGED
            {
                var position = Marshal.PtrToStructure<WindowPosition>(message.LParam);
                recording.Add(Recording.Operation.NativeApplied, Stopwatch.GetTimestamp(), target,
                    position.X, position.Y, position.Width, position.Height);
            }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct NativePoint(int X, int Y);
    [StructLayout(LayoutKind.Sequential)]
    private struct WindowPosition { public nint Handle, InsertAfter; public int X, Y, Width, Height; public uint Flags; }
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(nint handle, out NativeRect rect);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindow(nint handle);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(nint handle);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern nint GetWindowLongPtr(nint handle, int index);
    [DllImport("user32.dll")]
    private static extern nint WindowFromPoint(NativePoint point);
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(nint handle, nint after, int x, int y, int width, int height, uint flags);
    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(nint handle, int attribute, out uint value, int bytes);
    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(nint handle);
    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint handle, out uint processId);
    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();
    [DllImport("dwmapi.dll")]
    internal static extern int DwmFlush();
}
