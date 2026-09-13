using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Threading;

namespace FancyWM.Utilities
{
    internal class LowLevelHotkey : IDisposable
    {
        public event EventHandler<EventArgs>? Pressed;

        public Dispatcher Dispatcher { get; }
        public LowLevelKeyboardHook KeyboardHook { get; }
        public IReadOnlyCollection<KeyCode> ModifierKeys => m_modifiers;
        public KeyCode Key { get; }
        public required bool ScanOnRelease { get; init; }
        public required bool HideKeyPress { get; init; }
        public required bool ClearModifiersOnMiss { get; init; }
        public required bool SideAgnostic { get; set; }

        private readonly KeyCode[] m_modifiers;
        private readonly bool[] m_pressedModifiers;
        private readonly object m_lifetimeLock = new();
        private volatile bool m_disposed;
        private bool m_keyDirty = false;

        public LowLevelHotkey(LowLevelKeyboardHook keyboardHook, IReadOnlyCollection<KeyCode> modifierKeys, KeyCode key)
        {
            Dispatcher = Dispatcher.CurrentDispatcher;
            KeyboardHook = keyboardHook ?? throw new ArgumentNullException(nameof(keyboardHook));
            Key = key;

            m_modifiers = modifierKeys.ToArray() ?? throw new ArgumentNullException(nameof(modifierKeys)); ;
            m_pressedModifiers = new bool[modifierKeys.Count];
            KeyboardHook.KeyStateChanged += OnLowLevelKeyStateChanged;
        }

        private KeyCode RemapKeyCode(KeyCode k)
        {
            if (!SideAgnostic)
            {
                return k;
            }
            return k switch
            {
                KeyCode.RightShift => KeyCode.LeftShift,
                KeyCode.RightCtrl => KeyCode.LeftCtrl,
                KeyCode.RWin => KeyCode.LWin,
                KeyCode.RightAlt => KeyCode.LeftAlt,
                _ => k,
            };
        }

        private void OnLowLevelKeyStateChanged(object? sender, ref LowLevelKeyboardHook.KeyStateChangedEventArgs e)
        {
            if (m_disposed)
            {
                return;
            }
            var inputKeyCode = RemapKeyCode(e.KeyCode);
            var mainKeyCode = RemapKeyCode(Key);

            if (e.IsPressed)
            {
                // 1. Unless we want the trigger on release.
                // 2. Check if the hotkey is triggered (requires main key).
                // 3. And if so, if we need to hide the main key, do so.
                if (!ScanOnRelease && Scan(inputKeyCode, mainKeyCode) && HideKeyPress)
                {
                    SuppressKey(isPressed: true, ref e);
                }

                int modifierIndex = Array.IndexOf(m_modifiers, e.KeyCode);
                if (modifierIndex != -1)
                {
                    m_pressedModifiers[modifierIndex] = true;
                }
                else if (inputKeyCode != mainKeyCode && ClearModifiersOnMiss)
                {
                    // A non-modifier, non-main key was pressed, in which case
                    // we reset the state, to allow other hotkeys to trigger.
                    Array.Fill(m_pressedModifiers, false);
                }
            }
            else
            {
                int modifierIndex = Array.IndexOf(m_modifiers, e.KeyCode);
                if (modifierIndex != -1)
                {
                    m_pressedModifiers[modifierIndex] = false;
                }

                // Handle the dirty key.
                if (inputKeyCode == mainKeyCode && m_keyDirty)
                {
                    SuppressKey(isPressed: false, ref e);
                }

                // 1. If we want to trigger on release.
                // 2. Check if the hotkey is triggered (requires main key).
                if (ScanOnRelease)
                {
                    Scan(inputKeyCode, mainKeyCode);
                }
            }
        }

        private bool Scan(KeyCode inputKeyCode, KeyCode mainKeyCode)
        {
            if (!m_pressedModifiers.All(x => x) || inputKeyCode != mainKeyCode)
            {
                return false;
            }
            lock (m_lifetimeLock)
            {
                if (m_disposed)
                {
                    return false;
                }
            }
            // Posting can re-enter through Dispatcher.Hooks. An admitted post
            // may finish after Dispose; its notification still checks lifetime.
            Dispatcher.BeginInvoke(new Action(NotifyPressed));
            return true;
        }

        private void SuppressKey(bool isPressed, ref LowLevelKeyboardHook.KeyStateChangedEventArgs e)
        {
            lock (m_lifetimeLock)
            {
                if (!m_disposed)
                {
                    m_keyDirty = isPressed;
                    e.Handled = true;
                }
            }
        }

        private void NotifyPressed()
        {
            EventHandler<EventArgs>? handler;
            lock (m_lifetimeLock)
            {
                if (m_disposed)
                {
                    return;
                }
                handler = Pressed;
            }
            // Already-claimed user code may complete across disposal. Never
            // hold the owner gate while invoking arbitrary application handlers.
            handler?.Invoke(this, new EventArgs());
        }

        public void Dispose()
        {
            lock (m_lifetimeLock)
            {
                m_disposed = true;
                Pressed = null;
                KeyboardHook.KeyStateChanged -= OnLowLevelKeyStateChanged;
            }
        }
    }
}
