using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

using FancyWM.Utilities;

namespace FancyWM.Controls
{
    /// <summary>
    /// Interaction logic for KeyPressBox.xaml
    /// </summary>
    public partial class KeyPressBox : UserControl
    {
        internal event KeyPatternChangedEventHandler? PatternChanged;

        public static readonly DependencyProperty PatternProperty = DependencyProperty.Register(
            nameof(Pattern),
            typeof(IReadOnlySet<KeyCode>),
            typeof(KeyPressBox),
            new PropertyMetadata(null));

        public IReadOnlySet<KeyCode>? Pattern
        {
            get => (IReadOnlySet<KeyCode>?)GetValue(PatternProperty);
            set => SetValue(PatternProperty, value);
        }

        public static readonly DependencyProperty InitialPatternProperty = DependencyProperty.Register(
            nameof(InitialPattern),
            typeof(string),
            typeof(KeyPressBox),
            new PropertyMetadata(null));

        public string InitialPattern
        {
            get => (string)GetValue(InitialPatternProperty);
            set => SetValue(InitialPatternProperty, value);
        }

        private const string EmptyPlaceholder = "<None>";

        private readonly Func<UIElement, KeyPatternListener> m_createListener;
        private readonly Func<bool>? m_readFocus;
        private KeyPatternListener? m_patternListener;
        private LayoutInvalidationQueue? m_recordingQueue;
        private bool m_unloaded;
        private bool m_acquiringListener;
        private bool m_reconcileRecording;
        private long m_lifetime;

        public KeyPressBox()
            : this(static input => new KeyPatternListener(input))
        {
        }

        internal KeyPressBox(Func<UIElement, KeyPatternListener> createListener, Func<bool>? readFocus = null)
        {
            m_createListener = createListener;
            m_readFocus = readFocus;
            InitializeComponent();

            InputBox.Text = EmptyPlaceholder;
            InputBox.TextChanged += OnTextChanged;

            InputBox.IsKeyboardFocusWithinChanged += OnRecordingFocusChanged;
            Loaded += OnLoaded;
            Unloaded += OnUnloaded;
        }

        internal void SynchronizeRecordingFocus()
        {
            if (m_unloaded) { return; }
            if (m_acquiringListener)
            {
                m_reconcileRecording = true;
                return;
            }

            long lifetime = m_lifetime;
            bool hasFocus = m_readFocus?.Invoke() ?? InputBox.IsKeyboardFocusWithin;
            if (m_unloaded || lifetime != m_lifetime) { return; }
            if (m_patternListener != null)
            {
                m_patternListener.SynchronizeFocus(hasFocus);
                return;
            }
            if (!hasFocus) { return; }

            m_acquiringListener = true;
            KeyPatternListener? acquired = null;
            try
            {
                acquired = m_createListener(InputBox);
                hasFocus = m_readFocus?.Invoke() ?? InputBox.IsKeyboardFocusWithin;
                if (m_unloaded || lifetime != m_lifetime || !hasFocus) { return; }
                m_patternListener = acquired;
                acquired.PatternChanged += OnPatternChanged;
                acquired.SynchronizeFocus(true);
                acquired = null;
            }
            finally
            {
                acquired?.Dispose();
                m_acquiringListener = false;
                if (m_reconcileRecording && !m_unloaded)
                {
                    m_reconcileRecording = false;
                    // Only reentrant acquisition needs deferred reconciliation.
                    // Yield one turn per retry and retain the latest Loaded request.
                    m_recordingQueue ??= new LayoutInvalidationQueue(
                        callback => Dispatcher.BeginInvoke(DispatcherPriority.Background, callback),
                        () => !m_unloaded && !m_acquiringListener,
                        () => { SynchronizeRecordingFocus(); return Task.CompletedTask; },
                        exception => throw exception);
                    m_recordingQueue.Invalidate();
                }
            }
        }

        private void OnRecordingFocusChanged(object sender, DependencyPropertyChangedEventArgs e)
            => SynchronizeRecordingFocus();

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            m_unloaded = false;
            SynchronizeRecordingFocus();
        }

        private void OnUnloaded(object sender, RoutedEventArgs e)
        {
            m_unloaded = true;
            m_lifetime++;
            m_reconcileRecording = false;
            var listener = m_patternListener;
            m_patternListener = null;
            if (listener != null)
            {
                listener.PatternChanged -= OnPatternChanged;
                listener.Dispose();
            }
        }

        private void OnPatternChanged(object sender, KeyPatternChangedEventArgs e)
        {
            if (m_unloaded || !ReferenceEquals(sender, m_patternListener)) { return; }
            long lifetime = m_lifetime;
            e = new KeyPatternChangedEventArgs(e.Keys.Normalize().ToHashSet());
            Pattern = e.Keys;
            // Setting the bound property may synchronously unload/reload this
            // control. A normal pattern-induced blur still completes its event.
            if (m_unloaded || lifetime != m_lifetime || !ReferenceEquals(sender, m_patternListener)) { return; }
            PatternChanged?.Invoke(sender, e);
        }

        private void OnTextChanged(object sender, TextChangedEventArgs e)
        {
            if (string.IsNullOrEmpty(InputBox.Text))
            {
                long lifetime = m_lifetime;
                InputBox.Text = EmptyPlaceholder;
                // Text and binding observers may synchronously replace this
                // lifetime, just as they can during a recorded pattern update.
                if (m_unloaded || lifetime != m_lifetime) { return; }
                Pattern = null;
                if (m_unloaded || lifetime != m_lifetime) { return; }
                PatternChanged?.Invoke(this, new KeyPatternChangedEventArgs(new HashSet<KeyCode>()));
            }
        }

        protected override void OnPropertyChanged(DependencyPropertyChangedEventArgs e)
        {
            base.OnPropertyChanged(e);
            if (e.Property == PatternProperty)
            {
                if (Pattern != null)
                {
                    InputBox.Text = Pattern.OrderByDescending(x => (int)x).ToPrettyString();
                    Keyboard.ClearFocus();
                }
                else
                {
                    InputBox.Text = EmptyPlaceholder;
                }
            }
            else if (e.Property == InitialPatternProperty)
            {
                if (string.IsNullOrEmpty(InitialPattern))
                {
                    InputBox.Text = EmptyPlaceholder;
                }
                else
                {
                    InputBox.Text = FormatPattern(InitialPattern);
                }
            }
        }

        private static string FormatPattern(string pattern)
        {
            return pattern.Split(',')
                .Select(Enum.Parse<KeyCode>)
                .ToPrettyString();
        }
    }
}
