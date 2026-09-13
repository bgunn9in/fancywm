using System;
using System.Collections.Generic;
using System.Windows.Threading;

namespace FancyWM.Utilities
{
    internal class LowLevelKeyPatternListener : IKeyPatternListener
    {
        private enum ListenerState { Listening, Draining, Detached }

        public Dispatcher Dispatcher { get; }

        public LowLevelKeyboardHook KeyboardHook { get; }

        public bool IsListening
        {
            get
            {
                lock (m_gate) { return m_state == ListenerState.Listening; }
            }
        }

        public IReadOnlySet<KeyCode>? Pattern
        {
            get
            {
                lock (m_gate) { return m_pattern; }
            }
        }

        private readonly object m_gate = new();
        private readonly HashSet<KeyCode> m_pressedKeys = [];
        private readonly HashSet<KeyCode> m_pressedKeyCodes = [];
        private ListenerState m_state = ListenerState.Listening;
        private IReadOnlySet<KeyCode>? m_pattern;

        public event KeyPatternChangedEventHandler? PatternChanged;

        public LowLevelKeyPatternListener(LowLevelKeyboardHook keyboardHook)
        {
            Dispatcher = Dispatcher.CurrentDispatcher;
            KeyboardHook = keyboardHook;
            KeyboardHook.KeyStateChanged += OnKeyStateChanged;
        }

        public void Dispose()
        {
            lock (m_gate)
            {
                if (m_state == ListenerState.Detached) { return; }
                m_state = ListenerState.Draining;
                m_pressedKeys.Clear();
                PatternChanged = null;
                DetachIfDrained();
            }
        }

        private void DetachIfDrained()
        {
            if (m_state == ListenerState.Draining && m_pressedKeyCodes.Count == 0)
            {
                // This sealed hook's field-like event cannot invoke user code.
                // Commit detachment after removal so a failure remains retryable.
                KeyboardHook.KeyStateChanged -= OnKeyStateChanged;
                m_state = ListenerState.Detached;
            }
        }

        private void OnKeyStateChanged(object? sender, ref LowLevelKeyboardHook.KeyStateChangedEventArgs e)
        {
            IReadOnlySet<KeyCode>? pattern = null;
            lock (m_gate)
            {
                if (m_state == ListenerState.Detached) { return; }
                if (m_state == ListenerState.Draining)
                {
                    // Keep this same handler for callbacks captured before Dispose.
                    // Repeat-down must not consume ownership of the matching key-up.
                    if (m_pressedKeyCodes.Contains(e.KeyCode))
                    {
                        e.Handled = true;
                        if (!e.IsPressed) { m_pressedKeyCodes.Remove(e.KeyCode); }
                    }
                    DetachIfDrained();
                    return;
                }

                if (e.IsPressed)
                {
                    e.Handled = true;
                    m_pressedKeyCodes.Add(e.KeyCode);
                    m_pressedKeys.Add(e.KeyCode);
                    return;
                }

                if (m_pressedKeyCodes.Remove(e.KeyCode))
                {
                    e.Handled = true;
                }

                // Preserve recording on the first release, including a release
                // whose key was already down before this listener subscribed.
                if (m_pressedKeys.Count > 0)
                {
                    pattern = new HashSet<KeyCode>(m_pressedKeys);
                    m_pattern = pattern;
                    m_pressedKeys.Clear();
                }
            }

            if (pattern != null) { PostPatternChanged(pattern); }
        }

        private void PostPatternChanged(IReadOnlySet<KeyCode> pattern)
        {
            // Dispatcher hooks can reenter Dispose while posting. Each queued
            // notification owns a snapshot that later input never changes.
            Dispatcher.BeginInvoke(new Action(() => NotifyPatternChanged(pattern)));
        }

        private void NotifyPatternChanged(IReadOnlySet<KeyCode> pattern)
        {
            KeyPatternChangedEventHandler? handler;
            lock (m_gate)
            {
                if (m_state != ListenerState.Listening) { return; }
                handler = PatternChanged;
            }
            // An admitted delegate list may finish across Dispose; arbitrary
            // subscribers never hold the hook admission gate.
            handler?.Invoke(this, new KeyPatternChangedEventArgs(pattern));
        }
    }
}
