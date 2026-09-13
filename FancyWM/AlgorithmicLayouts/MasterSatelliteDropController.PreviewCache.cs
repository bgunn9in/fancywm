using System;

using FancyWM.Layouts;
using FancyWM.Layouts.Tiling;
using FancyWM.Models;

using WinMan;

namespace FancyWM.AlgorithmicLayouts
{
    internal sealed partial class MasterSatelliteDropController
    {
        // The service serializes planning and invalidation with its backend
        // lock. Retain one immutable accepted result for the current gesture.
        private PreviewCacheEntry? m_previewCache;
        private long m_previewCacheGeneration;

        internal void ClearPreviewCache()
        {
            m_previewCache = null;
            unchecked { m_previewCacheGeneration++; }
        }

        private readonly record struct PreviewAttempt(TilingWorkspace Backend, long Generation);

        private static bool HasBuiltInPreviewNodes(TilingNode? node)
        {
            if (node?.GetType() == typeof(WindowNode))
            {
                return true;
            }
            if (node?.GetType() != typeof(SplitPanelNode))
            {
                return false;
            }
            var children = ((SplitPanelNode)node).Children;
            for (int i = 0; i < children.Count; i++)
            {
                if (!HasBuiltInPreviewNodes(children[i]))
                {
                    return false;
                }
            }
            return true;
        }

        private sealed record PreviewCacheEntry(PreviewKey Key, MasterSatelliteDropPlan Plan);

        private readonly record struct WindowIdentity(IWindow? Window, IntPtr Handle)
        {
            public static WindowIdentity Capture(IWindow? window)
                => new(window, window?.Handle ?? IntPtr.Zero);

            public bool Matches(IWindow? window)
                => ReferenceEquals(Window, window) && Handle == (window?.Handle ?? IntPtr.Zero);
        }

        /// <summary>
        /// Exact pre-simulation inputs. No mutable tree or runtime state is
        /// retained, and a hash collision cannot accept a stale preview.
        /// </summary>
        private sealed class PreviewKey
        {
            private readonly TilingWorkspace m_backend;
            private readonly IVirtualDesktop m_desktop;
            private readonly MasterSatelliteLayoutSettings m_settings;
            private readonly Rectangle m_workArea;
            private readonly bool m_isActive;
            private readonly bool m_isRecovering;
            private readonly long m_revision;
            private readonly MasterSide m_masterSide;
            private readonly double m_requestedRatio;
            private readonly double m_effectiveRatio;
            private readonly SatelliteLayoutOrientation m_orientation;
            private readonly WindowIdentity m_master;
            private readonly WindowIdentity[] m_satellites;
            private readonly WindowIdentity m_source;
            private readonly WindowIdentity m_target;
            private readonly MasterSatelliteDropKind m_kind;
            private readonly int? m_fromIndex;
            private readonly int? m_toIndex;
            private readonly MasterSide? m_targetSide;
            private readonly NodeKey m_root;

            private PreviewKey(
                TilingWorkspace backend, IVirtualDesktop desktop, DesktopTree tree,
                MasterSatelliteRuntimeState state, MasterSatelliteLayoutSettings settings,
                IWindow source, IWindow? target, MasterSatelliteDropKind kind,
                int? fromIndex, int? toIndex, MasterSide? targetSide, NodeKey root)
            {
                m_backend = backend;
                m_desktop = desktop;
                m_settings = settings;
                m_workArea = tree.WorkArea;
                m_isActive = state.IsActive;
                m_isRecovering = state.IsRecovering;
                m_revision = state.Revision;
                m_masterSide = state.MasterSide;
                m_requestedRatio = state.RequestedMasterRatio;
                m_effectiveRatio = state.EffectiveMasterRatio;
                m_orientation = state.SatelliteOrientation;
                m_master = WindowIdentity.Capture(state.Master);
                m_satellites = new WindowIdentity[state.Satellites.Count];
                for (int i = 0; i < m_satellites.Length; i++)
                {
                    m_satellites[i] = WindowIdentity.Capture(state.Satellites[i]);
                }
                m_source = WindowIdentity.Capture(source);
                m_target = WindowIdentity.Capture(target);
                m_kind = kind;
                m_fromIndex = fromIndex;
                m_toIndex = toIndex;
                m_targetSide = targetSide;
                m_root = root;
            }

            public static PreviewKey? TryCapture(
                TilingWorkspace backend, IVirtualDesktop desktop, DesktopTree tree,
                MasterSatelliteRuntimeState state, MasterSatelliteLayoutSettings settings,
                IWindow source, IWindow? target, MasterSatelliteDropKind kind,
                int? fromIndex, int? toIndex, MasterSide? targetSide)
            {
                // A derived layout node/settings object can carry hidden state or
                // override Clone. Its equivalence cannot be proved by this key.
                if (settings.GetType() != typeof(MasterSatelliteLayoutSettings))
                {
                    return null;
                }
                try
                {
                    var root = NodeKey.TryCapture(tree.Root);
                    return root == null ? null : new PreviewKey(
                        backend, desktop, tree, state, settings, source, target,
                        kind, fromIndex, toIndex, targetSide, root);
                }
                catch (InvalidWindowReferenceException)
                {
                    return null;
                }
            }

            public bool Matches(
                TilingWorkspace backend, IVirtualDesktop desktop, DesktopTree tree,
                MasterSatelliteRuntimeState state, MasterSatelliteLayoutSettings settings,
                IWindow source, IWindow? target, MasterSatelliteDropKind kind,
                int? fromIndex, int? toIndex, MasterSide? targetSide)
            {
                if (!ReferenceEquals(m_backend, backend) || !ReferenceEquals(m_desktop, desktop)
                    || settings.GetType() != typeof(MasterSatelliteLayoutSettings)
                    || m_settings != settings || m_workArea != tree.WorkArea
                    || m_isActive != state.IsActive || m_isRecovering != state.IsRecovering
                    || m_revision != state.Revision || m_masterSide != state.MasterSide
                    || m_requestedRatio != state.RequestedMasterRatio
                    || m_effectiveRatio != state.EffectiveMasterRatio
                    || m_orientation != state.SatelliteOrientation
                    || m_satellites.Length != state.Satellites.Count
                    || m_kind != kind || m_fromIndex != fromIndex || m_toIndex != toIndex
                    || m_targetSide != targetSide)
                {
                    return false;
                }
                try
                {
                    if (!m_source.Matches(source) || !m_target.Matches(target)
                        || !m_master.Matches(state.Master))
                    {
                        return false;
                    }
                    for (int i = 0; i < m_satellites.Length; i++)
                    {
                        if (!m_satellites[i].Matches(state.Satellites[i]))
                        {
                            return false;
                        }
                    }
                    return m_root.Matches(tree.Root);
                }
                catch (InvalidWindowReferenceException)
                {
                    return false;
                }
            }
        }

        private sealed class NodeKey
        {
            private readonly long m_generation;
            private readonly Rectangle m_padding;
            private readonly Point m_minimum;
            private readonly Point m_maximum;
            private readonly Rectangle m_rectangle;
            private readonly WindowIdentity m_window;
            private readonly PanelOrientation m_orientation;
            private readonly int m_spacing;
            private readonly double m_containerLength;
            private readonly NodeKey[]? m_children;
            private readonly FlexConstraints[]? m_constraints;

            private NodeKey(TilingNode node, NodeKey[]? children)
            {
                m_generation = node.GenerationID;
                m_padding = node.Padding;
                m_minimum = node.ContentMinSize;
                m_maximum = node.ContentMaxSize;
                m_rectangle = node.ComputedRectangle;
                m_children = children;
                if (node is WindowNode window)
                {
                    m_window = WindowIdentity.Capture(window.WindowReference);
                }
                else if (node is SplitPanelNode split)
                {
                    m_orientation = split.Orientation;
                    m_spacing = split.Spacing;
                    m_containerLength = split.ContainerLength;
                    m_constraints = new FlexConstraints[children!.Length];
                    for (int i = 0; i < children.Length; i++)
                    {
                        m_constraints[i] = split.GetChildConstraints(split.Children[i]);
                    }
                }
            }

            public static NodeKey? TryCapture(TilingNode? node)
            {
                if (node?.GetType() == typeof(WindowNode))
                {
                    return new NodeKey(node, null);
                }
                if (node?.GetType() != typeof(SplitPanelNode))
                {
                    return null;
                }
                var panel = (SplitPanelNode)node;
                var children = new NodeKey[panel.Children.Count];
                for (int i = 0; i < children.Length; i++)
                {
                    var child = TryCapture(panel.Children[i]);
                    if (child == null)
                    {
                        return null;
                    }
                    children[i] = child;
                }
                return new NodeKey(panel, children);
            }

            public bool Matches(TilingNode? node)
            {
                if (node == null || node.GenerationID != m_generation
                    || node.Padding != m_padding || node.ContentMinSize != m_minimum
                    || node.ContentMaxSize != m_maximum || node.ComputedRectangle != m_rectangle)
                {
                    return false;
                }
                if (m_children == null)
                {
                    return node.GetType() == typeof(WindowNode)
                        && m_window.Matches(((WindowNode)node).WindowReference);
                }
                if (node.GetType() != typeof(SplitPanelNode))
                {
                    return false;
                }
                var panel = (SplitPanelNode)node;
                if (panel.Orientation != m_orientation || panel.Spacing != m_spacing
                    || panel.ContainerLength != m_containerLength || panel.Children.Count != m_children.Length)
                {
                    return false;
                }
                for (int i = 0; i < m_children.Length; i++)
                {
                    var child = panel.Children[i];
                    var actual = panel.GetChildConstraints(child);
                    var expected = m_constraints![i];
                    if (actual.Width != expected.Width || actual.MinWidth != expected.MinWidth
                        || actual.MaxWidth != expected.MaxWidth || !m_children[i].Matches(child))
                    {
                        return false;
                    }
                }
                // Fresh Arrange derives fractional geometry from this exact
                // work area, topology, padding/spacing and the full Flex widths.
                return true;
            }
        }
    }
}
