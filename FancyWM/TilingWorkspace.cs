using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Windows.Controls;
using System.Xml.Linq;

using FancyWM.Layouts;
using FancyWM.Layouts.Tiling;
using FancyWM.Utilities;

using Windows.Devices.Enumeration;

using WinMan;

namespace FancyWM
{

    internal enum TilingError
    {
        Failed,
        MissingTarget,
        InvalidTarget,
        MissingAdjacentWindow,
        CausesRecursiveNesting,
        ModifiesTopLevelPanel,
        NoValidPlacementExists,
        TargetCannotFit,
        PullsBeyondTopLevelPanel,
        NestingInStackPanel,
        UnsupportedInAlgorithmicLayout,
    }

    internal class TilingFailedException : InvalidOperationException
    {
        public TilingError FailReason { get; } = TilingError.Failed;

        public TilingFailedException(TilingError reason = TilingError.Failed)
        {
            FailReason = reason;
        }

        public TilingFailedException(string? message, TilingError reason = TilingError.Failed) : base(message)
        {
            FailReason = reason;
        }

        public TilingFailedException(string? message, Exception? innerException, TilingError reason = TilingError.Failed) : base(message, innerException)
        {
            FailReason = reason;
        }
    }

    public class NoValidPlacementExistsException : Exception
    {
    }

    public class WindowAlreadyRegisteredException : Exception
    {
    }

    internal class DesktopState
    {
        public required DesktopTree DesktopTree { get; init; }
        public TilingNode? FocusedNode { get; set; }
    }

    internal class TilingWorkspaceState
    {
        private readonly Dictionary<IVirtualDesktop, DesktopState> m_states = [];
        // DesktopState keeps its tree identity while roots are rebuilt or restored.
        private readonly Dictionary<DesktopTree, (DesktopState? State, int Count)> m_statesByTree
            = new(ReferenceEqualityComparer.Instance);
        private int m_nullStateCount;

        public IEnumerable<IVirtualDesktop> Desktops => m_states.Keys;
        public IEnumerable<DesktopState> States => m_states.Values;

        public void AddState(IVirtualDesktop virtualDesktop, DesktopState state)
        {
            m_states.Add(virtualDesktop, state);
            if (state == null)
            {
                m_nullStateCount++;
                return;
            }
            if (state.DesktopTree is DesktopTree tree)
            {
                m_statesByTree[tree] = m_statesByTree.TryGetValue(tree, out var entry)
                    ? (null, entry.Count + 1)
                    : (state, 1);
            }
        }

        public DesktopState? GetState(IVirtualDesktop virtualDesktop)
        {
            return m_states.TryGetValue(virtualDesktop, out var state) ? state : null;
        }

        public DesktopState? GetState(DesktopTree tree)
        {
            if (tree != null && m_nullStateCount == 0)
            {
                if (!m_statesByTree.TryGetValue(tree, out var entry))
                {
                    return null;
                }
                if (entry.Count == 1)
                {
                    return entry.State;
                }
            }
            return FindSingleStateForTree(tree);
        }

        private DesktopState? FindSingleStateForTree(DesktopTree? tree)
        {
            // Preserve the original ambiguity and malformed-null behavior.
            return m_states.Where(x => x.Value.DesktopTree == tree).SingleOrDefault().Value;
        }

        public void RemoveState(IVirtualDesktop virtualDesktop)
        {
            if (!m_states.Remove(virtualDesktop, out var state))
            {
                throw new ArgumentException("The specified desktop does not exist!");
            }
            if (state == null)
            {
                m_nullStateCount--;
                return;
            }
            if (state.DesktopTree is DesktopTree tree)
            {
                var entry = m_statesByTree[tree];
                if (entry.Count == 1)
                {
                    m_statesByTree.Remove(tree);
                }
                else if (entry.Count == 2)
                {
                    var remaining = m_states.Values.Single(candidate => candidate?.DesktopTree == tree);
                    m_statesByTree[tree] = (remaining, 1);
                }
                else
                {
                    m_statesByTree[tree] = (null, entry.Count - 1);
                }
            }
        }

        public DesktopState? FindByVdm(IWindow window)
        {
            IVirtualDesktop? desktop = null;
            foreach (var candidate in m_states.Keys)
            {
                if (candidate.HasWindow(window))
                {
                    desktop = candidate;
                    break;
                }
            }
            if (desktop == null)
            {
                return null;
            }
            return m_states[desktop];
        }

        public DesktopState? FindByTree(IWindow window)
        {
            foreach (var state in m_states.Values)
            {
                if (state.DesktopTree.FindNode(window) != null)
                {
                    return state;
                }
            }
            return null;
        }
    }

    internal partial class TilingWorkspace
    {
        private readonly TilingWorkspaceState m_states = new();
        private readonly Dictionary<IWindow, Rectangle> m_originalPositions
            = new(ReferenceEqualityComparer.Instance);

        public IEnumerable<DesktopTree> Trees => m_states.States.Select(x => x.DesktopTree);

        internal IReadOnlyList<IVirtualDesktop> SnapshotDesktops() =>
            m_states.Desktops.ToArray();

        public bool AutoCollapse { get; set; } = false;

        public PanelNode CreateRoot(PanelOrientation orientation)
        {
            // return new LayoutFunctionNode(new GridLayout(8));
            // return new LayoutFunctionNode(new RatioLayout(0.5, 8));
            return new SplitPanelNode { Orientation = orientation };
        }

        public void RegisterDesktop(IVirtualDesktop virtualDesktop, Rectangle workArea, PanelOrientation orientation)
        {
            var tree = new DesktopTree
            {
                Root = CreateRoot(orientation),
                WorkArea = workArea,
            };
            m_states.AddState(virtualDesktop, new DesktopState
            {
                DesktopTree = tree,
                FocusedNode = null,
            });
        }

        public void UnregisterDesktop(IVirtualDesktop virtualDesktop)
        {
            m_states.RemoveState(virtualDesktop);
        }

        public WindowNode RegisterWindow(IWindow window, int maxTreeWidth = 100)
        {
            var state = GetValidatedState(window);
            return RegisterWindow(window, state, maxTreeWidth);
        }

        /// <summary>
        /// Registers a window against an event-resolved desktop without asking the
        /// virtual-desktop manager to infer ownership again. This is required for
        /// background desktop moves where CurrentDesktop and IWindow ownership can
        /// change independently while workspace events are being reconciled.
        /// </summary>
        public WindowNode RegisterWindow(
            IWindow window,
            IVirtualDesktop virtualDesktop,
            int maxTreeWidth = 100)
        {
            ArgumentNullException.ThrowIfNull(window);
            ArgumentNullException.ThrowIfNull(virtualDesktop);
            var state = m_states.GetState(virtualDesktop)
                ?? throw new ArgumentException(
                    "Desktop not registered with backend!",
                    nameof(virtualDesktop));
            if (m_states.FindByTree(window) != null)
            {
                throw new WindowAlreadyRegisteredException();
            }
            return RegisterWindow(window, state, maxTreeWidth);
        }

        private WindowNode RegisterWindow(
            IWindow window,
            DesktopState state,
            int maxTreeWidth)
        {
            var focusedNode = state.FocusedNode;
            var parent = ResolveParent(state, focusedNode);
            parent = ResolveParentWithWidthConstraint(parent, focusedNode, window, maxTreeWidth);
            return RegisterWindow(window, parent, focusedNode as WindowNode);
        }

        public WindowNode RegisterWindow(IWindow window, PanelNode parent, WindowNode? anchor = null)
        {
            if (m_states.FindByTree(window) != null)
                throw new WindowAlreadyRegisteredException();

            var newNode = new WindowNode(window);
            AttachToParent(newNode, parent);
            m_originalPositions[window] = window.Position;
            return newNode;
        }

        private DesktopState GetValidatedState(IWindow window)
        {
            var state = m_states.FindByVdm(window)
                ?? throw new InvalidWindowReferenceException(window.Handle);

            if (state.DesktopTree.FindNode(window) is WindowNode)
                throw new WindowAlreadyRegisteredException();

            return state;
        }

        private static PanelNode ResolveParent(DesktopState state, TilingNode? focusedNode)
            => focusedNode is WindowNode focusedWindow
                ? focusedWindow.Parent ?? state.DesktopTree.Root!
                : state.DesktopTree.Root!;

        private PanelNode ResolveParentWithWidthConstraint(
            PanelNode parent, TilingNode? focusedNode, IWindow window, int maxTreeWidth)
        {
            if (!IsAtMaxWidth(parent, maxTreeWidth) || parent is not SplitPanelNode parentSplit)
                return parent;

            var nodeToSplit = SelectNodeToSplit(parent, focusedNode);

            if (nodeToSplit is WindowNode)
                TrySplitNode(nodeToSplit, parentSplit, window);

            return nodeToSplit.Parent!;
        }

        private static bool IsAtMaxWidth(PanelNode parent, int maxTreeWidth)
            => parent.Children.Count(x => x is not PlaceholderNode) >= maxTreeWidth;

        private static TilingNode SelectNodeToSplit(PanelNode parent, TilingNode? focusedNode)
            => parent.Children.Contains(focusedNode) ? focusedNode! : parent.Children.Last();

        private void TrySplitNode(TilingNode nodeToSplit, SplitPanelNode parentSplit, IWindow window)
        {
            WrapInSplitPanel(nodeToSplit, vertical: parentSplit.Orientation == PanelOrientation.Horizontal);
            ArrangeWithFallback(nodeToSplit);

            if (!CanFitLossy(nodeToSplit.Parent!, window))
                nodeToSplit.Parent!.CollapseIfSingle();
        }

        private static void ArrangeWithFallback(TilingNode node)
        {
            try
            {
                node.Desktop!.Arrange();
            }
            catch (UnsatisfiableFlexConstraintsException)
            {
                node.Parent!.CollapseIfSingle();
            }
        }

        private void AttachToParent(WindowNode newNode, PanelNode parent)
        {
            if (parent is not StackPanelNode && !CanFitLossy(parent, newNode))
                throw new NoValidPlacementExistsException();

            parent.Attach(newNode);
            parent.RemovePlaceholders();
        }

        private static bool CanFitLossy(PanelNode parent, IWindow window)
        {
            if (parent.ComputedRectangle == default)
            {
                return true;
            }

            var node = new WindowNode(window);
            node.Measure();
            return CanFitLossy(parent, node);
        }

        private static bool CanFitLossy(PanelNode parent, TilingNode node)
        {
            if (parent.ComputedRectangle == default)
            {
                return true;
            }

            var minSize = node.MinSize;
            var maxSize = parent.GetMaxSizeForInsert(node);

            return minSize.X <= maxSize.X && minSize.Y <= maxSize.Y;
        }

        public void UnregisterWindow(IWindow window)
        {
            var state = m_states.FindByTree(window) ?? throw new ArgumentException(null, nameof(window));
            if (state.FocusedNode is WindowNode node && node.WindowReference == window)
            {
                state.FocusedNode = null;
            }

            state.DesktopTree.FindNode(window)!.Remove(cleanup: true, collapse: AutoCollapse);
            m_originalPositions.Remove(window);
        }

        public Rectangle GetOriginalPosition(IWindow window)
        {
            return m_originalPositions[window];
        }

        public DesktopTree? GetTree(IVirtualDesktop desktop)
        {
            return m_states.GetState(desktop)?.DesktopTree;
        }

        public bool HasWindow(IWindow window)
        {
            var state = m_states.FindByTree(window);
            if (state == null)
            {
                return false;
            }
            return state.DesktopTree.FindNode(window) != null;
        }

        public WindowNode? FindWindow(IWindow window)
        {
            var state = m_states.FindByTree(window);
            if (state == null)
            {
                return null;
            }
            return state.DesktopTree.FindNode(window);
        }

        public TilingNode? NodeAtPoint(IVirtualDesktop currentDesktop, Point pt)
        {
            if (m_states.GetState(currentDesktop) is not DesktopState state)
                throw new ArgumentException("Desktop not registered with backend!");

            return state.DesktopTree.Root!.Windows
                .FirstOrDefault(x => x.ComputedRectangle.Contains(pt));
        }

        public void MoveNode(TilingNode node, Point pt, bool allowNesting = true)
        {
            if (node.Parent == null)
                throw new ArgumentException($"Node cannot be a top-level node!", nameof(node));

            if (node.Desktop == null)
                throw new ArgumentException($"Node must be registered with the backend!", nameof(node));

            var nodeAtPoint = FindMoveTarget(node, pt);
            if (nodeAtPoint == null || nodeAtPoint.Parent == null)
                return;

            for (TilingNode? ancestor = nodeAtPoint; ancestor != null; ancestor = ancestor.Parent)
            {
                if (EqualityComparer<TilingNode>.Default.Equals(ancestor, node))
                    throw new TilingFailedException(TilingError.CausesRecursiveNesting);
            }

            for (TilingNode? ancestor = nodeAtPoint; ancestor != null; ancestor = ancestor.Parent)
            {
                if (ancestor is not StackPanelNode)
                    continue;

                if (node is not WindowNode)
                    throw new TilingFailedException(TilingError.NestingInStackPanel);
                break;
            }

            if (nodeAtPoint.Type == TilingNodeType.Placeholder)
            {
                var oldParent = node.Parent;
                node.Parent.Detach(node);
                nodeAtPoint.Parent.Attach(node);
                nodeAtPoint.Parent.RemovePlaceholders();
                oldParent.Cleanup(collapse: AutoCollapse);
            }
            else
            {
                if (allowNesting)
                {
                    if (node.Parent != nodeAtPoint.Parent)
                    {
                        // Node moved over another node that NOT a sibling
                        var oldParent = node.Parent;
                        try
                        {
                            if (!MoveNodeTest(node, nodeAtPoint.Parent, pt))
                            {
                                return;
                            }
                            node.Parent.Detach(node);
                            var insertionIndex = FindInsertionIndex(nodeAtPoint, pt);
                            nodeAtPoint.Parent.Attach(insertionIndex, node);
                            oldParent.Cleanup(collapse: AutoCollapse);
                            node.Parent.RemovePlaceholders();
                        }
                        catch (UnsatisfiableFlexConstraintsException)
                        {
                            throw new TilingFailedException(TilingError.TargetCannotFit);
                        }
                    }
                    else if (node.Parent is not StackPanelNode) // Do not rearrange items in stack panel
                    {
                        try
                        {
                            var newPosition = TransferSize(node.ComputedRectangle, nodeAtPoint.ComputedRectangle);
                            if (newPosition.Contains(pt))
                            {
                                // Node moved over another node that IS a sibling
                                var nodeIndex = node.Parent.Children.IndexOf(node);
                                var targetIndex = node.Parent.Children.IndexOf(nodeAtPoint);

                                node.Parent.Move(nodeIndex, targetIndex);
                            }
                        }
                        catch (UnsatisfiableFlexConstraintsException)
                        {
                            throw new TilingFailedException(TilingError.Failed);
                        }
                    }
                }
                else if (node.Parent != nodeAtPoint)
                {
                    try
                    {
                        node.Swap(nodeAtPoint);
                    }
                    catch (UnsatisfiableFlexConstraintsException)
                    {
                        throw new TilingFailedException(TilingError.Failed);
                    }
                }
            }
        }

        private static TilingNode? FindMoveTarget(TilingNode source, Point pt)
        {
            if (TryFindBuiltInMoveTarget(source.Desktop!.Root!, source, pt, out var target))
            {
                return target;
            }

            return source.Desktop!.Root!.Windows
                .Where(x => x != source)
                .Concat(source.Desktop.Root.Nodes.Where(x => x.Type == TilingNodeType.Placeholder))
                .FirstOrDefault(x => x.ComputedRectangle.Contains(pt)) ?? source.Desktop.Root!.Nodes
                    .OfType<PanelNode>()
                    .Where(x => x != source)
                    .FirstOrDefault(x => Rectangle.OffsetAndSize(
                        x.ComputedRectangle.Left - x.Padding.Left,
                        x.ComputedRectangle.Top - x.Padding.Top,
                        x.ComputedRectangle.Width + x.Padding.Left + x.Padding.Right,
                        x.Padding.Top).Contains(pt));
        }

        private static bool TryFindBuiltInMoveTarget(
            TilingNode root,
            TilingNode source,
            Point pt,
            out TilingNode? target)
        {
            var pending = new Stack<TilingNode>(16);
            pending.Push(root);
            TilingNode? placeholder = null;
            while (pending.TryPop(out var node))
            {
                var type = node.GetType();
                if (type == typeof(WindowNode))
                {
                    if (node != source && node.ComputedRectangle.Contains(pt))
                    {
                        target = node;
                        return true;
                    }
                    continue;
                }
                if (type == typeof(PlaceholderNode))
                {
                    if (placeholder == null && node.ComputedRectangle.Contains(pt))
                    {
                        placeholder = node;
                    }
                    continue;
                }
                if (type != typeof(SplitPanelNode) && type != typeof(StackPanelNode))
                {
                    target = null;
                    return false;
                }

                var children = ((PanelNode)node).Children;
                for (int i = children.Count - 1; i >= 0; i--)
                {
                    pending.Push(children[i]);
                }
            }

            if (placeholder != null)
            {
                target = placeholder;
                return true;
            }

            pending.Push(root);
            while (pending.TryPop(out var node))
            {
                if (node is not PanelNode panel)
                {
                    continue;
                }
                if (panel != source
                    && Rectangle.OffsetAndSize(
                        panel.ComputedRectangle.Left - panel.Padding.Left,
                        panel.ComputedRectangle.Top - panel.Padding.Top,
                        panel.ComputedRectangle.Width + panel.Padding.Left + panel.Padding.Right,
                        panel.Padding.Top).Contains(pt))
                {
                    target = panel;
                    return true;
                }

                var children = panel.Children;
                for (int i = children.Count - 1; i >= 0; i--)
                {
                    pending.Push(children[i]);
                }
            }

            target = null;
            return true;
        }

        private static int FindInsertionIndex(TilingNode nodeAtPoint, Point pt)
        {
            Debug.Assert(nodeAtPoint.Parent != null);
            var insertionIndex = nodeAtPoint.Parent!.IndexOf(nodeAtPoint);
            if (nodeAtPoint.Parent is GridLikeNode grid)
            {
                if (grid.CanResizeInOrientation(PanelOrientation.Horizontal))
                {
                    if (nodeAtPoint.ComputedRectangle.Left + nodeAtPoint.ComputedRectangle.Width / 2 < pt.X)
                    {
                        // Right half of the window in a horizontal panel
                        return insertionIndex + 1;
                    }
                }
                else if (nodeAtPoint.ComputedRectangle.Top + nodeAtPoint.ComputedRectangle.Height / 2 < pt.Y)
                {
                    // Lower half of the window in a vertical panel
                    return insertionIndex + 1;
                }
            }
            else if (nodeAtPoint.Parent is StackPanelNode stack)
            {
                return stack.Children.Count;
            }
            return insertionIndex;
        }

        internal void WrapInSplitPanel(TilingNode node, bool vertical)
        {
            node.Parent?.RemovePlaceholders();
            var isOnlyChild = node.Parent?.Parent != null && node.Parent.Children.Count == 1;
            if (!isOnlyChild && node.Ancestors.OfType<StackPanelNode>().Any())
            {
                throw new TilingFailedException(TilingError.NestingInStackPanel);
            }

            if (node.Parent == null)
                throw new TilingFailedException(TilingError.ModifiesTopLevelPanel);
            // Parent is not the top-level panel and this is the only child
            if (isOnlyChild)
            {
                SwapPanels(node.Parent, new SplitPanelNode
                {
                    Orientation = vertical ? PanelOrientation.Vertical : PanelOrientation.Horizontal,
                });
            }
            else
            {
                node.Embed(new SplitPanelNode
                {
                    Orientation = vertical ? PanelOrientation.Vertical : PanelOrientation.Horizontal,
                });
            }
        }

        internal void WrapInStackPanel(TilingNode node)
        {
            node.Parent?.RemovePlaceholders();
            var isOnlyChild = node.Parent?.Parent != null && node.Parent.Children.Count == 1;

            if (!isOnlyChild && node.Ancestors.OfType<StackPanelNode>().Any())
            {
                throw new TilingFailedException(TilingError.NestingInStackPanel);
            }

            if (!isOnlyChild && node.Nodes.Where(x => x is not WindowNode).Any())
            {
                throw new TilingFailedException(TilingError.NestingInStackPanel);
            }

            if (node.Parent == null)
                throw new TilingFailedException(TilingError.ModifiesTopLevelPanel);

            if (isOnlyChild)
            {
                SwapPanels(node.Parent, new StackPanelNode());
            }
            else
            {
                node.Embed(new StackPanelNode());
            }
        }

        public void MoveBefore(TilingNode node, TilingNode nodeBefore)
        {
            MoveTo(node, nodeBefore, beforeAnchor: true);
        }

        public void MoveAfter(TilingNode node, TilingNode nodeAfter)
        {
            MoveTo(node, nodeAfter, beforeAnchor: false);
        }

        private void MoveTo(TilingNode node, TilingNode nodeAnchor, bool beforeAnchor)
        {
            if (node.Parent == null)
                throw new ArgumentException($"Node cannot be a top-level node!", nameof(node));

            if (node.Desktop == null)
                throw new ArgumentException($"Node must be registered with the backend!", nameof(node));

            if (nodeAnchor.Parent == null)
                throw new ArgumentException($"Node cannot be a top-level node!", nameof(nodeAnchor));

            if (nodeAnchor.Desktop == null)
                throw new ArgumentException($"Node must be registered with the backend!", nameof(nodeAnchor));

            if (node.Parent == nodeAnchor.Parent)
                throw new ArgumentException($"Nodes must have different parents!", nameof(nodeAnchor));

            var index = nodeAnchor.Parent.IndexOf(nodeAnchor);
            var oldParent = node.Parent;
            node.Parent.Detach(node);
            oldParent.Cleanup(collapse: AutoCollapse);
            nodeAnchor.Parent.Attach(beforeAnchor ? index : index + 1, node);
            nodeAnchor.Parent.RemovePlaceholders();
        }

        private bool MoveNodeTest(TilingNode node, PanelNode newParentNode, Point pt)
        {
            Debug.Assert(node.Desktop != null);
            Debug.Assert(node.Parent != null);
            Debug.Assert(newParentNode.Desktop != null);
            Debug.Assert(newParentNode.Desktop != null);
            Debug.Assert(node.Desktop == newParentNode.Desktop);

            var rootClone = (PanelNode)node.Desktop.Root!.Clone();

            if (!TryFindBuiltInCloneGenerations(
                rootClone,
                node.GenerationID,
                newParentNode.GenerationID,
                out var nodeClone,
                out var newParentCandidate))
            {
                nodeClone = rootClone.Nodes.First(x => x.GenerationID == node.GenerationID);
                newParentCandidate = rootClone.Nodes.First(x => x.GenerationID == newParentNode.GenerationID);
            }
            var newParentClone = (PanelNode)newParentCandidate;
            var testTree = new DesktopTree
            {
                Root = rootClone,
                WorkArea = node.Desktop.WorkArea,
            };

            var nodeCloneParent = nodeClone.Parent!;
            bool newParentIsAncestor = false;
            for (var ancestor = nodeCloneParent.Parent; ancestor != null; ancestor = ancestor.Parent)
            {
                if (!EqualityComparer<PanelNode>.Default.Equals(ancestor, newParentClone)) continue;
                newParentIsAncestor = true;
                break;
            }

            nodeCloneParent.Detach(nodeClone);
            nodeCloneParent.Cleanup(collapse: AutoCollapse);

            if (newParentClone.Desktop == null && newParentIsAncestor)
            {
                // The new parent node got completely detached because it was an ancestor
                // of the existing node and apparently it was a change of 1-child panels.
                return true;
            }

            newParentClone.Attach(nodeClone);
            newParentClone.RemovePlaceholders();
            testTree.Measure();
            testTree.Arrange();

            return newParentClone.ComputedRectangle.Contains(pt);
        }

        private static bool TryFindBuiltInCloneGenerations(
            TilingNode root,
            long firstGeneration,
            long secondGeneration,
            out TilingNode first,
            out TilingNode second)
        {
            TilingNode? firstMatch = null;
            TilingNode? secondMatch = null;
            var completed = TryFindBuiltInCloneGenerationsCore(
                root,
                firstGeneration,
                secondGeneration,
                ref firstMatch,
                ref secondMatch);
            first = firstMatch!;
            second = secondMatch!;
            return completed && firstMatch != null && secondMatch != null;
        }

        private static bool TryFindBuiltInCloneGenerationsCore(
            TilingNode node,
            long firstGeneration,
            long secondGeneration,
            ref TilingNode? first,
            ref TilingNode? second)
        {
            var type = node.GetType();
            if (type != typeof(SplitPanelNode)
                && type != typeof(StackPanelNode)
                && type != typeof(WindowNode)
                && type != typeof(PlaceholderNode))
            {
                return false;
            }

            if (first == null && node.GenerationID == firstGeneration)
            {
                first = node;
            }
            if (second == null && node.GenerationID == secondGeneration)
            {
                second = node;
            }
            if (first != null && second != null)
            {
                return true;
            }

            if (node is PanelNode panel)
            {
                var children = panel.Children;
                for (int i = 0; i < children.Count; i++)
                {
                    if (!TryFindBuiltInCloneGenerationsCore(
                        children[i],
                        firstGeneration,
                        secondGeneration,
                        ref first,
                        ref second))
                    {
                        return false;
                    }
                    if (first != null && second != null)
                    {
                        return true;
                    }
                }
            }
            return true;
        }

        private static Rectangle TransferSize(Rectangle a, Rectangle b)
        {
            var newCenter = b.Center;
            var width = a.Width;
            var height = a.Height;

            return new Rectangle(newCenter.X - width / 2, newCenter.Y - height / 2, newCenter.X + width / 2, newCenter.Y + height / 2);
        }

        public void MoveWindow(IWindow window, Point pt, bool allowNesting)
        {
            var state = m_states.FindByTree(window) ?? throw new ArgumentException($"Window must be registered with the backend!", nameof(window));
            var sourceNode = state.DesktopTree.FindNode(window) ?? throw new ArgumentException($"Window must be registered with the backend!", nameof(window));
            MoveNode(sourceNode, pt, allowNesting);
        }


        public (Rectangle preArrange, Rectangle postArrange) MockMoveWindow(IWindow window, Point pt, bool allowNesting)
        {
            var state = m_states.FindByTree(window) ?? throw new ArgumentException($"Window must be registered with the backend!", nameof(window));
            var sourceNode = state.DesktopTree.FindNode(window) ?? throw new ArgumentException($"Window must be registered with the backend!", nameof(window));
            return MockMoveNode(sourceNode, pt, allowNesting);
        }

        public (Rectangle preArrange, Rectangle postArrange) MockMoveNode(TilingNode sourceNode, Point pt, bool allowNesting)
        {
            var desktop = sourceNode.Desktop!;
            var rootClone = (PanelNode)desktop.Root!.Clone();

            if (!TryFindBuiltInCloneGenerations(
                rootClone,
                sourceNode.GenerationID,
                sourceNode.GenerationID,
                out var sourceNodeClone,
                out _))
            {
                sourceNodeClone = rootClone.Nodes.First(x => x.GenerationID == sourceNode.GenerationID);
            }
            var testTree = new DesktopTree
            {
                Root = rootClone,
                WorkArea = desktop.WorkArea,
            };

            MoveNode(sourceNodeClone, pt, allowNesting);

            var unconstrainedParentClone = (PanelNode)sourceNodeClone.Parent!.Clone();

            try
            {
                testTree.Arrange();
            }
            catch (UnsatisfiableFlexConstraintsException)
            {
                throw new TilingFailedException(TilingError.NoValidPlacementExists);
            }

            TilingNode? unconstrainedSourceNodeClone = null;
            bool canReuseSource = true;
            ClearPreviewConstraints(unconstrainedParentClone, sourceNode.GenerationID, ref unconstrainedSourceNodeClone, ref canReuseSource);
            unconstrainedParentClone.Padding = new();
            try
            {
                unconstrainedParentClone.Arrange(new RectangleF(unconstrainedParentClone.ComputedRectangle));
            }
            catch (UnsatisfiableFlexConstraintsException)
            {
                throw new TilingFailedException(TilingError.NoValidPlacementExists);
            }
            if (!canReuseSource || unconstrainedSourceNodeClone == null)
            {
                unconstrainedSourceNodeClone = unconstrainedParentClone.Nodes.First(x => x.GenerationID == sourceNode.GenerationID);
            }

            return (unconstrainedSourceNodeClone.ComputedRectangle, sourceNodeClone.ComputedRectangle);
        }

        private static void ClearPreviewConstraints(TilingNode node, long sourceGeneration, ref TilingNode? source, ref bool canReuseSource)
        {
            var type = node.GetType();
            if (type != typeof(SplitPanelNode) && type != typeof(StackPanelNode)
                && type != typeof(WindowNode) && type != typeof(PlaceholderNode))
            {
                // Custom Nodes enumerations can hide descendants, and layout
                // callbacks can change which generation match is first. Keep
                // their original walk and repeat the lookup after Arrange.
                canReuseSource = false;
                foreach (var descendant in node.Nodes)
                {
                    descendant.ClearConstraints();
                }
                return;
            }

            node.ClearConstraints();
            if (source == null && node.GenerationID == sourceGeneration)
            {
                source = node;
            }
            if (node is PanelNode panel)
            {
                foreach (var child in (List<TilingNode>)panel.Children)
                {
                    ClearPreviewConstraints(child, sourceGeneration, ref source, ref canReuseSource);
                }
            }
        }

        public void ResizeWindow(IWindow window, Rectangle newPosition, Rectangle oldPosition)
        {
            var state = m_states.FindByTree(window) ?? throw new ArgumentException($"Window must be registered with the backend!", nameof(window));
            var node = state.DesktopTree.FindNode(window);
            if (node != null)
            {
                ResizeNode(node, newPosition, oldPosition);
            }
        }

        public void ResizeNode(TilingNode node, Rectangle newPosition, Rectangle oldPosition)
        {
            if (newPosition.Width != oldPosition.Width)
            {
                var (p, child) = FindResizeParent(node, PanelOrientation.Horizontal);

                if (p != null)
                {
                    var leftResizeAmount = Math.Abs(newPosition.Left - oldPosition.Left);
                    var rightResizeAmount = Math.Abs(newPosition.Right - oldPosition.Right);
                    GrowDirection direction = leftResizeAmount < rightResizeAmount
                        ? GrowDirection.TowardsEnd
                        : leftResizeAmount > rightResizeAmount
                            ? GrowDirection.TowardsStart
                            : GrowDirection.Both;
                    var childIndex = p.IndexOf(child);
                    var sizeDelta = newPosition.Width - oldPosition.Width;

                    if (direction == GrowDirection.TowardsStart && childIndex == 0 && child.GetAdjacentNode(TilingDirection.Left) is TilingNode leftNode)
                    {
                        var leftNodePosition = leftNode.ComputedRectangle;
                        ResizeNode(leftNode, new Rectangle(leftNodePosition.Left, leftNodePosition.Top, leftNodePosition.Right - sizeDelta, leftNodePosition.Bottom), leftNodePosition);
                    }
                    else if (direction == GrowDirection.TowardsEnd && childIndex == p.Children.Count - 1 && child.GetAdjacentNode(TilingDirection.Right) is TilingNode rightNode)
                    {
                        var rightNodePosition = rightNode.ComputedRectangle;
                        ResizeNode(rightNode, new Rectangle(rightNodePosition.Left + sizeDelta, rightNodePosition.Top, rightNodePosition.Right, rightNodePosition.Bottom), rightNodePosition);
                    }

                    p.ResizeBy(child, sizeDelta, direction);
                }
            }

            if (newPosition.Height != oldPosition.Height)
            {
                var (p, child) = FindResizeParent(node, PanelOrientation.Vertical);

                if (p != null)
                {
                    var topResizeAmount = Math.Abs(newPosition.Top - oldPosition.Top);
                    var bottomResizeAmount = Math.Abs(newPosition.Bottom - oldPosition.Bottom);
                    GrowDirection direction = topResizeAmount < bottomResizeAmount
                        ? GrowDirection.TowardsEnd
                        : topResizeAmount > bottomResizeAmount
                            ? GrowDirection.TowardsStart
                            : GrowDirection.Both;
                    var childIndex = p.IndexOf(child);
                    var sizeDelta = newPosition.Height - oldPosition.Height;

                    if (direction == GrowDirection.TowardsStart && childIndex == 0 && child.GetAdjacentNode(TilingDirection.Up) is TilingNode topNode)
                    {
                        var topNodePosition = topNode.ComputedRectangle;
                        ResizeNode(topNode, new Rectangle(topNodePosition.Left, topNodePosition.Top, topNodePosition.Right, topNodePosition.Bottom - sizeDelta), topNodePosition);
                    }
                    else if (direction == GrowDirection.TowardsEnd && childIndex == p.Children.Count - 1 && child.GetAdjacentNode(TilingDirection.Down) is TilingNode bottomNode)
                    {
                        var bottomNodePosition = bottomNode.ComputedRectangle;
                        ResizeNode(bottomNode, new Rectangle(bottomNodePosition.Left, bottomNodePosition.Top + sizeDelta, bottomNodePosition.Right, bottomNodePosition.Bottom), bottomNodePosition);
                    }

                    p.ResizeBy(child, sizeDelta, direction);
                }
            }
        }

        private static (GridLikeNode? Panel, TilingNode Child) FindResizeParent(TilingNode node, PanelOrientation orientation)
        {
            var child = node;
            for (var parent = node.Parent; parent != null; child = parent, parent = parent.Parent)
            {
                if (parent is GridLikeNode grid && grid.CanResizeInOrientation(orientation))
                    return (grid, child);
            }
            return (null, node);
        }

        public TilingNode? GetFocus(IVirtualDesktop currentDesktop)
        {
            if (m_states.GetState(currentDesktop) is not DesktopState state)
                throw new ArgumentException("Desktop not registered with backend!");

            return state.FocusedNode;
        }

        public WindowNode GetFocusAdjacentWindow(IVirtualDesktop currentDesktop, TilingDirection direction)
        {
            var focusedNode = GetFocus(currentDesktop) ?? throw new TilingFailedException(TilingError.MissingTarget);
            WindowNode? adjacentWindow = focusedNode.GetAdjacentWindow(direction) ?? throw new TilingFailedException(TilingError.MissingAdjacentWindow);
            return adjacentWindow;
        }

        public (TilingNode, WindowNode) GetFocusAndAdjacentWindow(IVirtualDesktop currentDesktop, TilingDirection direction)
        {
            var focusedNode = GetFocus(currentDesktop) ?? throw new TilingFailedException(TilingError.MissingTarget);
            WindowNode? adjacentWindow = focusedNode.GetAdjacentWindow(direction) ?? throw new TilingFailedException(TilingError.MissingAdjacentWindow);
            return (focusedNode, adjacentWindow);
        }

        public void SetFocus(TilingNode node)
        {
            Debug.Assert(node.Parent != null);
            if (m_states.GetState(node.Desktop!) is not DesktopState state)
                throw new ArgumentException("Desktop not registered with backend!");

            state.FocusedNode = node;
        }

        public void SetFocus(IWindow window)
        {
            var state = m_states.FindByTree(window) ?? throw new ArgumentException("Window not registered with backend!");
            var node = state.DesktopTree.FindNode(window);
            Debug.Assert(node != null);

            SetFocus(node);
        }

        public void UnsetFocus(IWindow window)
        {
            var state = m_states.FindByTree(window);
            if (state == null)
                return;

            if (state.FocusedNode is WindowNode node && node.WindowReference == window)
                state.FocusedNode = null;
        }

        public void UnsetFocus(IVirtualDesktop desktop)
        {
            if (m_states.GetState(desktop) is not DesktopState state)
                throw new ArgumentException("Desktop not registered with backend!");
            state.FocusedNode = null;
        }

        public void SwapPanels(PanelNode panel, PanelNode newPanel)
        {
            if (panel.Parent == null)
                throw new TilingFailedException(TilingError.ModifiesTopLevelPanel);

            var grandparent = panel.Parent;

            grandparent.Attach(newPanel);
            panel.Swap(newPanel);

            var children = new List<TilingNode>(panel.Children);
            foreach (var node in children)
            {
                panel.Detach(node);
                newPanel.Attach(node);
            }

            panel.Cleanup(collapse: AutoCollapse);
        }

        bool CanFit(PanelNode parent, TilingNode child)
        {
            if (child.PathToRoot.Contains(parent))
            {
                try
                {
                    _ = MoveNodeTest(child, parent, new Point());
                    return true;
                }
                catch (UnsatisfiableFlexConstraintsException)
                {
                    return false;
                }
#if !DEBUG
                catch (Exception)
                {
                    return false;
                }
#endif
            }
            else
            {
                return CanFitLossy(parent, child);
            }
        }

        public void PullUp(TilingNode node)
        {
            if (node.Parent == null)
                throw new TilingFailedException(TilingError.InvalidTarget);

            if (node.Parent.Parent == null)
                throw new TilingFailedException(TilingError.PullsBeyondTopLevelPanel);

            // First grandparent that we can fit in
            var grandparent = node.Parent.PathToRoot.Skip(1).OfType<PanelNode>().FirstOrDefault(x => CanFit(x, node)) ?? throw new TilingFailedException(TilingError.TargetCannotFit);
            var oldParent = node.Parent;
            var index = grandparent.IndexOf(node.Parent);

            node.Parent.Detach(node);
            grandparent.Attach(index, node);

            oldParent.Cleanup(collapse: AutoCollapse);
        }
    }
}
