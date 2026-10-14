using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using Helide.Persistence;
using Helide.Terminal;
using Helide.Theme;
using Cursors = System.Windows.Input.Cursors;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using Panel = System.Windows.Controls.Panel;
using Point = System.Windows.Point;
using Size = System.Windows.Size;

namespace Helide.Shell;

/// <summary>Where on a pane a dragged tab is let go, and so what happens to it.</summary>
internal enum DropEdge
{
    /// <summary>Into the pane itself, in place of what is there.</summary>
    Center,
    Left,
    Right,
    Top,
    Bottom,
}

/// <summary>A pane, the part of it the pointer is over, and the area that part claims.</summary>
internal readonly record struct DropTarget(NativeTerminalHost Anchor, DropEdge Edge, Rect Preview);

/// <summary>
/// Shows terminal hosts side by side and one above another, in as many splits as
/// there are tabs to fill them.
/// </summary>
/// <remarks>
/// Every host is a permanent child of this panel and is only ever shown, hidden and
/// moved -- never taken out of the tree and put back somewhere else. Each one is a
/// native window, and reparenting a native window under WPF is how a closed pane keeps
/// painting over a live one. A split is therefore not a nesting of WPF containers: it
/// is a tree of rectangles, and this panel arranges the hosts into the leaves.
/// <para>
/// A leaf shows exactly one host. The hosts that are in no leaf are the tabs that are
/// open but not on screen; they stay alive and are collapsed.
/// </para>
/// <para>
/// The drop highlight is a popup, not a shape drawn in the tree. A native window is
/// painted over everything WPF draws in the same window, so only another window can
/// sit on top of a pane.
/// </para>
/// </remarks>
internal sealed class PaneSplitPanel : Panel
{
    // The strip between two panes. Wide enough to take hold of, with a one pixel line in it.
    private const double GapSize = 5;

    // Never dragged smaller than this, so a pane cannot be lost under its neighbour.
    private const double MinimumPane = 90;

    private abstract class Node
    {
        public Branch? Parent;

        // Share of the parent's room, relative to its siblings.
        public double Weight = 1;

        // The size it was last arranged at, along the parent's axis.
        public double Length;
    }

    private sealed class Leaf : Node
    {
        public NativeTerminalHost Host = null!;
    }

    private sealed class Branch : Node
    {
        // Side by side when true, stacked when false.
        public bool Row;
        public readonly List<Node> Items = [];
        public readonly List<Bar> Bars = [];
    }

    private readonly List<NativeTerminalHost> _hosts = [];
    private readonly Dictionary<Leaf, Rect> _rects = [];
    private readonly KeyboardFocusChangedEventHandler _focusHandler;

    private Node? _root;
    private Leaf? _focused;

    private System.Windows.Controls.Primitives.Popup? _preview;
    private Border? _previewBox;

    public PaneSplitPanel()
    {
        _focusHandler = (sender, _) =>
        {
            if (sender is NativeTerminalHost host && FindLeaf(host) is { } leaf)
                SetFocused(leaf);
        };
    }

    /// <summary>
    /// Raised when the arrangement changes by anything a person did -- a split, a tab
    /// moved or closed, a divider let go -- and so is worth saving.
    /// </summary>
    public event Action? LayoutChanged;

    private void NotifyLayout() => LayoutChanged?.Invoke();

    /// <summary>Raised when a different host becomes the one that has focus.</summary>
    public event Action<NativeTerminalHost?>? FocusedHostChanged;

    /// <summary>The host in the pane that has focus, if there is one.</summary>
    public NativeTerminalHost? FocusedHost => _focused?.Host;

    /// <summary>How many panes are on screen.</summary>
    public int PaneCount => _rects.Count > 0 ? _rects.Count : CountLeaves(_root);

    /// <summary>Whether this host is in a pane, rather than open but out of sight.</summary>
    public bool IsShown(NativeTerminalHost host) => FindLeaf(host) is not null;

    // ----------------------------------------------------------------- hosts

    /// <summary>Takes a host in. It is not shown until <see cref="Show"/> says where.</summary>
    /// <param name="startWhenShown">
    /// Keep it out of the window until it is first on screen. A host starts its program
    /// when it enters the window, so a tab restored behind others costs nothing at launch
    /// and starts when it is first looked at.
    /// </param>
    public void Add(NativeTerminalHost host, bool startWhenShown = false)
    {
        if (_hosts.Contains(host))
            return;

        _hosts.Add(host);
        host.Visibility = Visibility.Collapsed;
        host.AddHandler(Keyboard.GotKeyboardFocusEvent, _focusHandler, handledEventsToo: true);

        if (startWhenShown)
            _notStarted.Add(host);
        else
            Children.Add(host);
    }

    // Hosts added to start when shown, and not shown yet.
    private readonly HashSet<NativeTerminalHost> _notStarted = [];

    // While a set of tabs is being restored each is shown in turn as it is made; only
    // the ones still on screen at the end are started.
    private bool _holdingStarts;

    public void HoldStarts() => _holdingStarts = true;

    public void ReleaseStarts()
    {
        _holdingStarts = false;
        Refresh();
    }

    /// <summary>
    /// Puts a host on screen and gives it focus: where it already is, or else in the
    /// focused pane in place of what that pane was showing.
    /// </summary>
    public void Show(NativeTerminalHost host)
    {
        if (!_hosts.Contains(host))
            Add(host);

        var leaf = FindLeaf(host);
        var changed = leaf is null;
        if (leaf is null)
        {
            if (_root is null)
            {
                leaf = new Leaf { Host = host };
                _root = leaf;
            }
            else
            {
                leaf = _focused ?? FirstLeaf(_root);
                leaf.Host = host;
            }
        }

        SetFocused(leaf);
        Refresh();

        if (changed)
            NotifyLayout();
    }

    /// <summary>
    /// Takes a host out. The pane that was showing it shows <paramref name="replacement"/>
    /// instead, or goes away when there is none and the others take its room.
    /// </summary>
    public void Remove(NativeTerminalHost host, NativeTerminalHost? replacement = null)
    {
        var leaf = FindLeaf(host);

        _hosts.Remove(host);
        _notStarted.Remove(host);
        host.RemoveHandler(Keyboard.GotKeyboardFocusEvent, _focusHandler);
        host.Visibility = Visibility.Collapsed;
        Children.Remove(host);

        if (leaf is not null)
        {
            if (replacement is not null && _hosts.Contains(replacement) && FindLeaf(replacement) is null)
            {
                leaf.Host = replacement;
                if (ReferenceEquals(_focused, leaf))
                    FocusedHostChanged?.Invoke(replacement);
            }
            else
            {
                RemoveLeaf(leaf);
            }
        }

        Refresh();
        NotifyLayout();
    }

    /// <summary>Empties the panel: every host, every split.</summary>
    public void Clear()
    {
        foreach (var host in _hosts)
        {
            host.RemoveHandler(Keyboard.GotKeyboardFocusEvent, _focusHandler);
            host.Visibility = Visibility.Collapsed;
        }

        _hosts.Clear();
        _notStarted.Clear();
        Children.Clear();
        _root = null;
        _focused = null;
        _rects.Clear();
        HideDropPreview();
        FocusedHostChanged?.Invoke(null);
    }

    // ---------------------------------------------------------------- splits

    /// <summary>
    /// Puts a host, already added, in a new pane beside the one showing
    /// <paramref name="anchor"/>.
    /// </summary>
    public void SplitBeside(NativeTerminalHost anchor, DropEdge edge, NativeTerminalHost host)
    {
        if (!_hosts.Contains(host))
            Add(host);

        var target = FindLeaf(anchor);
        if (target is null || edge == DropEdge.Center || FindLeaf(host) is not null)
        {
            Show(host);
            return;
        }

        var added = Insert(target, edge, host);
        SetFocused(added);
        Refresh();
        NotifyLayout();
    }

    /// <summary>
    /// Moves a host to where it was dropped. <paramref name="fill"/> is what the pane it
    /// leaves shows afterwards; with none, that pane is closed up.
    /// </summary>
    public void MoveTo(NativeTerminalHost moving, DropTarget target, NativeTerminalHost? fill)
    {
        var destination = FindLeaf(target.Anchor);
        if (destination is null || !_hosts.Contains(moving))
            return;

        var source = FindLeaf(moving);

        if (target.Edge == DropEdge.Center)
        {
            if (ReferenceEquals(source, destination))
                return;

            if (source is null)
                destination.Host = moving;
            else
                (source.Host, destination.Host) = (destination.Host, moving);

            SetFocused(FindLeaf(moving)!);
            Refresh();
            NotifyLayout();
            return;
        }

        if (ReferenceEquals(source, destination))
        {
            // Splitting a pane around the one thing in it needs something else to stay.
            if (fill is null || FindLeaf(fill) is not null)
                return;

            destination.Host = fill;
        }
        else if (source is not null)
        {
            if (fill is not null && FindLeaf(fill) is null)
                source.Host = fill;
            else
                RemoveLeaf(source);
        }

        var added = Insert(destination, target.Edge, moving);
        SetFocused(added);
        Refresh();
        NotifyLayout();
    }

    /// <summary>
    /// Closes the pane showing a host, leaving the host open but out of sight. Does
    /// nothing for the last pane.
    /// </summary>
    public void Unsplit(NativeTerminalHost host)
    {
        var leaf = FindLeaf(host);
        if (leaf is null || ReferenceEquals(_root, leaf))
            return;

        RemoveLeaf(leaf);
        Refresh();
        NotifyLayout();
    }

    // ------------------------------------------------------------------ saving

    /// <summary>
    /// The arrangement as saved state. Each pane names its tab by whatever
    /// <paramref name="indexOf"/> says it is; a pane whose host has no index is left out.
    /// </summary>
    public PaneNodeState? Export(Func<NativeTerminalHost, int> indexOf) => ExportNode(_root, indexOf);

    private static PaneNodeState? ExportNode(Node? node, Func<NativeTerminalHost, int> indexOf)
    {
        switch (node)
        {
            case Leaf leaf:
                var index = indexOf(leaf.Host);
                return index < 0 ? null : new PaneNodeState { Tab = index };

            case Branch branch:
                var kept = new List<(PaneNodeState State, double Weight)>();
                foreach (var item in branch.Items)
                    if (ExportNode(item, indexOf) is { } state)
                        kept.Add((state, item.Weight));

                if (kept.Count == 0)
                    return null;
                if (kept.Count == 1)
                    return kept[0].State;

                // Fractions rather than pixels, so the shape survives a different window.
                var total = kept.Sum(k => k.Weight);
                if (total <= 0)
                    total = kept.Count;
                foreach (var (state, weight) in kept)
                    state.Weight = (weight > 0 ? weight : 1) / total;

                return new PaneNodeState { Row = branch.Row, Children = kept.Select(k => k.State).ToList() };

            default:
                return null;
        }
    }

    /// <summary>
    /// Puts saved panes back. <paramref name="hostAt"/> turns a saved tab position into a
    /// host that exists now; a pane whose tab does not exist is dropped and the panes
    /// beside it take its room. Returns false, and changes nothing, when nothing could
    /// be placed.
    /// </summary>
    public bool Import(PaneNodeState? state, Func<int, NativeTerminalHost?> hostAt, NativeTerminalHost? focus)
    {
        if (state is null)
            return false;

        var used = new HashSet<NativeTerminalHost>();
        var root = ImportNode(state, hostAt, used);
        if (root is null)
            return false;

        _root = root;
        _focused = (focus is not null ? FindLeaf(focus) : null) ?? FirstLeaf(root);
        Refresh();
        FocusedHostChanged?.Invoke(_focused.Host);
        return true;
    }

    private Node? ImportNode(PaneNodeState state, Func<int, NativeTerminalHost?> hostAt, HashSet<NativeTerminalHost> used)
    {
        if (state.Tab is { } tab)
        {
            var host = hostAt(tab);

            // One host cannot be in two panes, so a layout that says so keeps the first.
            if (host is null || !_hosts.Contains(host) || !used.Add(host))
                return null;

            return new Leaf { Host = host };
        }

        var branch = new Branch { Row = state.Row };
        foreach (var child in state.Children ?? [])
        {
            if (ImportNode(child, hostAt, used) is not { } node)
                continue;

            node.Weight = double.IsFinite(child.Weight) && child.Weight > 0.02 ? child.Weight : 1;
            node.Parent = branch;
            branch.Items.Add(node);
        }

        if (branch.Items.Count == 0)
            return null;

        if (branch.Items.Count == 1)
        {
            var only = branch.Items[0];
            only.Parent = null;
            return only;
        }

        return branch;
    }

    // ------------------------------------------------------------- drop target

    /// <summary>
    /// What a drop at this screen position would do, or null when the position is not
    /// over a pane.
    /// </summary>
    public DropTarget? HitTestDrop(Point screen)
    {
        if (_rects.Count == 0)
            return null;

        Point local;
        try
        {
            local = PointFromScreen(screen);
        }
        catch (InvalidOperationException)
        {
            return null;
        }

        foreach (var (leaf, rect) in _rects)
        {
            if (!rect.Contains(local) || rect.Width < 1 || rect.Height < 1)
                continue;

            // The edge zones are a width in pixels, not a share of the pane. As a share they
            // grow with the pane, and on a wide pane the "edge" is most of it, which is how
            // a drop in the middle of a pane ended up splitting it.
            var zoneX = Math.Clamp(rect.Width * 0.25, 36, 150);
            var zoneY = Math.Clamp(rect.Height * 0.25, 36, 150);

            // How far into its zone the pointer is: under 1 is inside it, 0 at the edge.
            var fromLeft = (local.X - rect.Left) / zoneX;
            var fromRight = (rect.Right - local.X) / zoneX;
            var fromTop = (local.Y - rect.Top) / zoneY;
            var fromBottom = (rect.Bottom - local.Y) / zoneY;

            // The nearest edge wins, so a corner is decided by which side the pointer
            // is closer to rather than by whichever test happens to come first.
            var nearest = Math.Min(Math.Min(fromLeft, fromRight), Math.Min(fromTop, fromBottom));
            var edge = DropEdge.Center;
            if (nearest < 1)
            {
                edge = nearest == fromLeft ? DropEdge.Left
                    : nearest == fromRight ? DropEdge.Right
                    : nearest == fromTop ? DropEdge.Top
                    : DropEdge.Bottom;
            }

            var preview = edge switch
            {
                DropEdge.Left => new Rect(rect.Left, rect.Top, rect.Width / 2, rect.Height),
                DropEdge.Right => new Rect(rect.Left + rect.Width / 2, rect.Top, rect.Width / 2, rect.Height),
                DropEdge.Top => new Rect(rect.Left, rect.Top, rect.Width, rect.Height / 2),
                DropEdge.Bottom => new Rect(rect.Left, rect.Top + rect.Height / 2, rect.Width, rect.Height / 2),
                _ => rect,
            };

            return new DropTarget(leaf.Host, edge, preview);
        }

        return null;
    }

    /// <summary>Lights up the area a drop would take, or hides it when there is none.</summary>
    public void ShowDropPreview(DropTarget? target)
    {
        if (target is not { } drop)
        {
            HideDropPreview();
            return;
        }

        if (_preview is null)
        {
            _previewBox = new Border { IsHitTestVisible = false };
            _preview = new System.Windows.Controls.Primitives.Popup
            {
                AllowsTransparency = true,

                // Not Relative: that placement is mirrored on a system set to align
                // menus to the right, and a highlight that lands a pane's width to the
                // left of where it was asked for is worse than none.
                //
                // A custom placement is not mirrored, but it is not given the offsets
                // as the answer either: the point returned is where the popup starts
                // and HorizontalOffset/VerticalOffset are then added to it. Returning
                // the offsets from here as well counted them twice, which put the
                // highlight twice as far right and down as it should be -- invisible
                // on a left or top edge, where the offset is nothing, and wrong on a
                // right or bottom one. Measured, not assumed: asked for (120, 90) that
                // landed at (240, 180), and returning the origin landed exactly.
                Placement = PlacementMode.Custom,
                CustomPopupPlacementCallback = (_, _, _) =>
                [
                    new CustomPopupPlacement(new Point(0, 0), PopupPrimaryAxis.None),
                ],
                PlacementTarget = this,
                PopupAnimation = PopupAnimation.None,
                StaysOpen = true,
                Focusable = false,
                IsHitTestVisible = false,
                Child = _previewBox,
            };
        }

        // The theme's accent, as a tint over what is underneath and a line around it,
        // never as a fill. A solid or near-solid accent hides the terminal it lands
        // on, and on a theme whose accent is white it is a grey slab. Taken from the
        // palette each time, so a theme change is picked up.
        var accent = ThemePalette.Color(ThemePalette.AccentBrush);
        _previewBox!.Background = new SolidColorBrush(
            System.Windows.Media.Color.FromArgb(0x2A, accent.R, accent.G, accent.B));
        _previewBox.BorderBrush = new SolidColorBrush(
            System.Windows.Media.Color.FromArgb(0xE0, accent.R, accent.G, accent.B));
        _previewBox.BorderThickness = new Thickness(2);
        _previewBox.Width = Math.Max(1, drop.Preview.Width);
        _previewBox.Height = Math.Max(1, drop.Preview.Height);

        _preview.HorizontalOffset = drop.Preview.Left;
        _preview.VerticalOffset = drop.Preview.Top;
        _preview.IsOpen = true;
    }

    public void HideDropPreview()
    {
        if (_preview is not null)
            _preview.IsOpen = false;
    }

    // ------------------------------------------------------------------ tree

    private Leaf? FindLeaf(NativeTerminalHost host) => FindLeaf(_root, host);

    private static Leaf? FindLeaf(Node? node, NativeTerminalHost host)
    {
        switch (node)
        {
            case Leaf leaf:
                return ReferenceEquals(leaf.Host, host) ? leaf : null;
            case Branch branch:
                foreach (var child in branch.Items)
                    if (FindLeaf(child, host) is { } found)
                        return found;
                return null;
            default:
                return null;
        }
    }

    private static Leaf FirstLeaf(Node node) =>
        node is Branch branch ? FirstLeaf(branch.Items[0]) : (Leaf)node;

    private static int CountLeaves(Node? node) => node switch
    {
        Leaf => 1,
        Branch branch => branch.Items.Sum(CountLeaves),
        _ => 0,
    };

    // A new pane for a host, beside an existing one.
    private Leaf Insert(Leaf target, DropEdge edge, NativeTerminalHost host)
    {
        var row = edge is DropEdge.Left or DropEdge.Right;
        var before = edge is DropEdge.Left or DropEdge.Top;
        var added = new Leaf { Host = host };

        // Beside a pane that is already in a row of the same kind, the new one joins that
        // row and takes half of the room the pane had. Nesting a row in a row would give
        // the same picture and a tree that is harder to drag about.
        if (target.Parent is { } parent && parent.Row == row)
        {
            var index = parent.Items.IndexOf(target) + (before ? 0 : 1);
            var half = target.Weight / 2;
            target.Weight = half;
            added.Weight = half;
            added.Parent = parent;
            parent.Items.Insert(index, added);
            return added;
        }

        var branch = new Branch { Row = row, Weight = target.Weight, Parent = target.Parent };
        if (target.Parent is { } grandparent)
            grandparent.Items[grandparent.Items.IndexOf(target)] = branch;
        else
            _root = branch;

        target.Weight = 1;
        target.Parent = branch;
        added.Weight = 1;
        added.Parent = branch;

        if (before)
        {
            branch.Items.Add(added);
            branch.Items.Add(target);
        }
        else
        {
            branch.Items.Add(target);
            branch.Items.Add(added);
        }

        return added;
    }

    // Closes a pane up. A row left with one pane in it is no row: that pane takes its
    // place.
    private void RemoveLeaf(Leaf leaf)
    {
        var parent = leaf.Parent;
        var wasFocused = ReferenceEquals(_focused, leaf);

        if (parent is null)
        {
            _root = null;
            _focused = null;
            if (wasFocused)
                FocusedHostChanged?.Invoke(null);
            return;
        }

        var position = parent.Items.IndexOf(leaf);
        parent.Items.RemoveAt(position);
        leaf.Parent = null;

        Node survivor;
        if (parent.Items.Count == 1)
        {
            survivor = parent.Items[0];
            survivor.Weight = parent.Weight;
            survivor.Parent = parent.Parent;

            if (parent.Parent is { } grandparent)
                grandparent.Items[grandparent.Items.IndexOf(parent)] = survivor;
            else
                _root = survivor;
        }
        else
        {
            // The room it had goes to whoever was next to it.
            var next = parent.Items[Math.Min(position, parent.Items.Count - 1)];
            next.Weight += leaf.Weight;
            survivor = next;
        }

        if (wasFocused)
        {
            _focused = FirstLeaf(survivor);
            FocusedHostChanged?.Invoke(_focused.Host);
        }
    }

    private void SetFocused(Leaf leaf)
    {
        if (ReferenceEquals(_focused, leaf))
            return;

        _focused = leaf;
        FocusedHostChanged?.Invoke(leaf.Host);
    }

    // Shows what is in a pane and hides the rest, and rebuilds the strips between panes.
    private void Refresh()
    {
        var shown = new HashSet<NativeTerminalHost>();
        CollectHosts(_root, shown);

        if (!_holdingStarts)
        {
            foreach (var host in shown)
            {
                if (_notStarted.Remove(host))
                    Children.Add(host);
            }
        }

        foreach (var host in _hosts)
            host.Visibility = shown.Contains(host) ? Visibility.Visible : Visibility.Collapsed;

        // The bars are children like the hosts, so they are swapped here and never in
        // the middle of a layout pass.
        for (var index = Children.Count - 1; index >= 0; index--)
            if (Children[index] is Bar)
                Children.RemoveAt(index);

        RebuildBars(_root);

        if (_root is null)
            _rects.Clear();

        InvalidateMeasure();
        InvalidateArrange();
    }

    private static void CollectHosts(Node? node, HashSet<NativeTerminalHost> into)
    {
        switch (node)
        {
            case Leaf leaf:
                into.Add(leaf.Host);
                break;
            case Branch branch:
                foreach (var child in branch.Items)
                    CollectHosts(child, into);
                break;
        }
    }

    private void RebuildBars(Node? node)
    {
        if (node is not Branch branch)
            return;

        branch.Bars.Clear();
        for (var index = 0; index < branch.Items.Count - 1; index++)
        {
            var bar = new Bar(this, branch, index);
            branch.Bars.Add(bar);
            Children.Add(bar);
        }

        foreach (var child in branch.Items)
            RebuildBars(child);
    }

    // ---------------------------------------------------------------- layout

    protected override Size MeasureOverride(Size availableSize)
    {
        foreach (UIElement child in Children)
            child.Measure(availableSize);

        return new Size(
            double.IsInfinity(availableSize.Width) ? 0 : availableSize.Width,
            double.IsInfinity(availableSize.Height) ? 0 : availableSize.Height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var whole = new Rect(0, 0, finalSize.Width, finalSize.Height);
        _rects.Clear();

        // Anything that is neither a host nor a bar -- the panel that stands in for a
        // pane that failed to start -- fills the panel.
        foreach (UIElement child in Children)
            if (child is not NativeTerminalHost && child is not Bar)
                child.Arrange(whole);

        if (_root is not null)
            ArrangeNode(_root, whole);

        return finalSize;
    }

    private void ArrangeNode(Node node, Rect bounds)
    {
        switch (node)
        {
            case Leaf leaf:
                leaf.Host.Arrange(bounds);
                _rects[leaf] = bounds;
                break;

            case Branch branch:
                var gaps = (branch.Items.Count - 1) * GapSize;
                var room = Math.Max(0, (branch.Row ? bounds.Width : bounds.Height) - gaps);
                var total = branch.Items.Sum(item => item.Weight);
                if (total <= 0)
                    total = branch.Items.Count;

                var offset = branch.Row ? bounds.Left : bounds.Top;
                for (var index = 0; index < branch.Items.Count; index++)
                {
                    var item = branch.Items[index];
                    var length = room * (item.Weight > 0 ? item.Weight : 1) / total;
                    item.Length = length;

                    var slot = branch.Row
                        ? new Rect(offset, bounds.Top, length, bounds.Height)
                        : new Rect(bounds.Left, offset, bounds.Width, length);
                    ArrangeNode(item, slot);

                    offset += length;
                    if (index < branch.Bars.Count)
                    {
                        branch.Bars[index].Arrange(branch.Row
                            ? new Rect(offset, bounds.Top, GapSize, bounds.Height)
                            : new Rect(bounds.Left, offset, bounds.Width, GapSize));
                        offset += GapSize;
                    }
                }

                break;
        }
    }

    // Moves the strip between item `index` and the one after it by `delta` pixels.
    private void Resize(Branch branch, int index, double delta)
    {
        if (index < 0 || index + 1 >= branch.Items.Count)
            return;

        // Weights are made to match what is on screen first, so that moving one strip
        // changes the two panes beside it and nothing else.
        foreach (var item in branch.Items)
            item.Weight = Math.Max(1, item.Length);

        var before = branch.Items[index];
        var after = branch.Items[index + 1];
        var sum = before.Weight + after.Weight;
        var minimum = Math.Min(MinimumPane, sum / 2);

        var moved = Math.Clamp(before.Weight + delta, minimum, sum - minimum);
        before.Weight = moved;
        after.Weight = sum - moved;

        InvalidateArrange();
    }

    // The strip between two panes: a wide target to take hold of, a hairline drawn in it.
    private sealed class Bar : FrameworkElement
    {
        private readonly PaneSplitPanel _panel;
        private readonly Branch _branch;
        private readonly int _index;
        private Point _last;

        public Bar(PaneSplitPanel panel, Branch branch, int index)
        {
            _panel = panel;
            _branch = branch;
            _index = index;
            Cursor = branch.Row ? Cursors.SizeWE : Cursors.SizeNS;
        }

        protected override void OnRender(DrawingContext context)
        {
            // Transparent is not nothing: it is what makes the whole strip hit-testable.
            context.DrawRectangle(System.Windows.Media.Brushes.Transparent, null, new Rect(RenderSize));

            var line = ThemePalette.Brush(ThemePalette.BorderBrush);
            context.DrawRectangle(
                line,
                null,
                _branch.Row
                    ? new Rect(Math.Floor(RenderSize.Width / 2), 0, 1, RenderSize.Height)
                    : new Rect(0, Math.Floor(RenderSize.Height / 2), RenderSize.Width, 1));
        }

        protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
        {
            _last = e.GetPosition(_panel);
            CaptureMouse();
            e.Handled = true;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            if (!IsMouseCaptured)
                return;

            var now = e.GetPosition(_panel);
            _panel.Resize(_branch, _index, _branch.Row ? now.X - _last.X : now.Y - _last.Y);
            _last = now;
        }

        protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
        {
            if (IsMouseCaptured)
            {
                ReleaseMouseCapture();
                _panel.NotifyLayout();
            }

            e.Handled = true;
        }
    }
}
