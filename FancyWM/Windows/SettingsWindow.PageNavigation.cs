using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Threading;

using FancyWM.Pages.Settings;

namespace FancyWM.Windows
{
    public partial class SettingsWindow
    {
        // SettingsWindow owns navigation; the adapter keeps browser/native UI out
        // of lifecycle regression tests while using the real Dispatcher path.
        internal sealed class PageNavigation(
            Dispatcher dispatcher,
            Func<Type, UIElement> createPage,
            Action<UIElement?> displayPage,
            Action<Exception> reportError) : IDisposable
        {
            private readonly HashSet<UIElement> m_owned = new(ReferenceEqualityComparer.Instance);
            private DispatcherOperation? m_pending;
            private UIElement? m_current;
            private UIElement? m_help;
            private Type? m_currentType;
            private long m_generation;
            private bool m_disposed;

            public void Navigate(Type pageType)
            {
                dispatcher.VerifyAccess();
                ArgumentNullException.ThrowIfNull(pageType);
                if (m_disposed) return;
                long generation = ++m_generation;
                m_pending?.Abort();
                m_pending = null;
                if (m_currentType == pageType) return;
                // Construct only after this request reaches the existing idle turn.
                // Superseded requests never initialize a browser or page bindings.
                m_pending = dispatcher.InvokeAsync(() => Apply(pageType, generation), DispatcherPriority.ContextIdle);
            }

            private void Apply(Type pageType, long generation)
            {
                if (m_disposed || generation != m_generation) return;
                m_pending = null;
                UIElement? page = null;
                UIElement? previous = m_current;
                Type? previousType = m_currentType;
                bool created = false;
                try
                {
                    page = pageType == typeof(HelpPage) ? m_help : null;
                    if (page == null)
                    {
                        page = createPage(pageType);
                        created = true;
                        m_owned.Add(page);
                    }
                    if (m_disposed || generation != m_generation)
                    {
                        if (created) Release(page);
                        return;
                    }
                    // Admit ownership before invoking WPF setters, which can run
                    // reentrant navigation or close callbacks synchronously.
                    m_current = page;
                    m_currentType = pageType;
                    if (pageType == typeof(HelpPage)) m_help = page;
                    displayPage(page);
                }
                catch (Exception error)
                {
                    if (!m_disposed && page != null && ReferenceEquals(m_current, page))
                    {
                        m_current = previous;
                        m_currentType = previousType;
                        if (created && ReferenceEquals(m_help, page)) m_help = null;
                        try { displayPage(previous); }
                        catch (Exception restoreError) { reportError(restoreError); }
                    }
                    reportError(error);
                }
                finally
                {
                    // Release removes ownership before invoking Dispose, including
                    // when a reentrant close already drained this same page.
                    if (page != null && !ReferenceEquals(page, m_current) && !ReferenceEquals(page, m_help)) Release(page);
                    if (previous != null && !ReferenceEquals(previous, m_current) && !ReferenceEquals(previous, m_help)) Release(previous);
                }
            }

            private void Release(UIElement page)
            {
                if (!m_owned.Remove(page)) return;
                try { (page as IDisposable)?.Dispose(); }
                catch (Exception error) { reportError(error); }
            }

            public void Dispose()
            {
                dispatcher.VerifyAccess();
                if (m_disposed) return;
                m_disposed = true;
                m_generation++;
                m_pending?.Abort();
                m_pending = null;
                var pages = m_owned.ToArray();
                m_owned.Clear();
                bool hadContent = m_current != null;
                m_current = m_help = null;
                m_currentType = null;
                List<Exception>? failures = null;
                try { if (hadContent) displayPage(null); }
                catch (Exception error) { (failures ??= []).Add(error); }
                foreach (var page in pages)
                {
                    try { (page as IDisposable)?.Dispose(); }
                    catch (Exception error) { (failures ??= []).Add(error); }
                }
                if (failures != null) throw new AggregateException("Failed to release settings pages.", failures);
            }
        }
    }
}
