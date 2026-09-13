using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Input;

using Microsoft.Extensions.DependencyInjection;

namespace FancyWM.Utilities
{
    internal class KeyPatternChangedEventArgs(IReadOnlySet<KeyCode> keys) : EventArgs
    {
        public IReadOnlySet<KeyCode> Keys { get; } = keys ?? throw new ArgumentNullException(nameof(keys));
    }

    internal delegate void KeyPatternChangedEventHandler(object sender, KeyPatternChangedEventArgs e);

    internal interface IKeyPatternListener : IDisposable
    {
        event KeyPatternChangedEventHandler? PatternChanged;

        bool IsListening { get; }

        IReadOnlySet<KeyCode>? Pattern { get; }
    }

    internal class KeyPatternListener : IKeyPatternListener
    {
        public event KeyPatternChangedEventHandler? PatternChanged;

        public bool IsListening
        {
            get
            {
                lock (m_lifetimeLock) { return m_keyStateChangedHandler != null; }
            }
        }

        public IReadOnlySet<KeyCode>? Pattern { get; private set; }

        public UIElement EventSource { get; }

        private readonly LowLevelKeyboardHook m_llKbdHk;
        private readonly HashSet<KeyCode> m_pressedKeys = [];
        private readonly object m_lifetimeLock = new();
        private LowLevelKeyboardHook.KeyStateChangedEventHandler? m_keyStateChangedHandler;
        private long m_recordingEpoch;
        private bool m_disposed;

        public KeyPatternListener(UIElement eventSource)
            : this(eventSource, App.Current.Services.GetRequiredService<LowLevelKeyboardHook>())
        {
        }

        internal KeyPatternListener(UIElement eventSource, LowLevelKeyboardHook keyboardHook)
        {
            m_llKbdHk = keyboardHook;
            EventSource = eventSource;
            EventSource.IsKeyboardFocusWithinChanged += OnKeyboardFocusChanged;
        }

        private void OnKeyboardFocusChanged(object sender, DependencyPropertyChangedEventArgs e)
            => SynchronizeFocus(EventSource.IsKeyboardFocusWithin);

        internal void SynchronizeFocus(bool hasFocus)
        {
            lock (m_lifetimeLock)
            {
                if (m_disposed || hasFocus == (m_keyStateChangedHandler != null)) { return; }
                m_pressedKeys.Clear();
                long recordingEpoch = ++m_recordingEpoch;
                if (hasFocus)
                {
                    // A captured callback belongs to this registration even if
                    // the control loses focus and starts another recording.
                    m_keyStateChangedHandler = (object? sender, ref LowLevelKeyboardHook.KeyStateChangedEventArgs e)
                        => OnKeyStateChanged(recordingEpoch, ref e);
                    m_llKbdHk.KeyStateChanged += m_keyStateChangedHandler;
                }
                else
                {
                    m_llKbdHk.KeyStateChanged -= m_keyStateChangedHandler;
                    m_keyStateChangedHandler = null;
                }
            }
        }

        public void Dispose()
        {
            lock (m_lifetimeLock)
            {
                if (m_disposed) { return; }
                m_disposed = true;
                m_recordingEpoch++;
                m_pressedKeys.Clear();
                PatternChanged = null;
                m_llKbdHk.KeyStateChanged -= m_keyStateChangedHandler;
                m_keyStateChangedHandler = null;
            }
            EventSource.IsKeyboardFocusWithinChanged -= OnKeyboardFocusChanged;
        }

        private void OnKeyStateChanged(long recordingEpoch, ref LowLevelKeyboardHook.KeyStateChangedEventArgs e)
        {
            lock (m_lifetimeLock)
            {
                if (m_disposed || m_keyStateChangedHandler == null || recordingEpoch != m_recordingEpoch) { return; }
                // Suppression is admitted with blur/Dispose under the same gate.
                // A stale callback preserves any earlier subscriber's decision.
                e.Handled = true;
            }
            var eCopy = e;
            // Dispatcher hooks can run arbitrary code while posting. An admitted
            // post may finish after disposal; its queued body checks ownership.
            _ = EventSource.Dispatcher.InvokeAsync(() => ProcessKeyStateChanged(recordingEpoch, eCopy));
        }

        private void ProcessKeyStateChanged(long recordingEpoch, LowLevelKeyboardHook.KeyStateChangedEventArgs e)
        {
            KeyPatternChangedEventHandler? handler;
            KeyPatternChangedEventArgs changed;
            lock (m_lifetimeLock)
            {
                if (m_disposed || m_keyStateChangedHandler == null || recordingEpoch != m_recordingEpoch) { return; }
                if (e.IsPressed)
                {
                    m_pressedKeys.Add(e.KeyCode);
                    return;
                }
                if (m_pressedKeys.Count == 0) { return; }
                Pattern = m_pressedKeys.ToHashSet();
                m_pressedKeys.Clear();
                handler = PatternChanged;
                changed = new KeyPatternChangedEventArgs(Pattern);
            }
            // An already-admitted delegate list may complete across disposal.
            // Arbitrary subscribers never own the hook admission gate.
            handler?.Invoke(this, changed);
        }
    }

    public static class KeySetExtensions
    {
        public static KeyCode Normalize(this KeyCode key)
        {
            return key switch
            {
                KeyCode.RightCtrl => KeyCode.LeftCtrl,
                KeyCode.RightAlt => KeyCode.LeftAlt,
                KeyCode.RightShift => KeyCode.LeftShift,
                KeyCode.RWin => KeyCode.LWin,
                _ => key,
            };
        }

        public static IEnumerable<KeyCode> Normalize(this IEnumerable<KeyCode> keys)
        {
            return keys.Select(x => x.Normalize());
        }

        public static bool SetEqualsSideInsensitive(this IReadOnlySet<KeyCode> keys, IEnumerable<KeyCode> enumerable)
        {
            return keys.Select(x => x.Normalize()).ToHashSet().SetEquals(enumerable.Normalize());
        }

        public static string ToPrettyString(this IEnumerable<KeyCode> keys)
        {
            if (!keys.Any())
            {
                throw new ArgumentException("Empty key set!");
            }
            return string.Join(" + ", keys.Select(key => KeyDescriptions.GetDescription(key)));
        }
    }
}
