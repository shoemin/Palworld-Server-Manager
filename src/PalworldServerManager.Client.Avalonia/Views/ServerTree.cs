using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;

namespace PalworldServerManager.Client.Avalonia.Views;

// Native tree semantics with explicit activation. Directional focus never selects a server.
public sealed class ServerTree : TreeView, ICustomKeyboardNavigation
{
    protected override Type StyleKeyOverride => typeof(TreeView);
    public TreeViewItem? Inspected { get; private set; }
    public event Action<TreeViewItem>? Inspect;
    public event Action<TreeViewItem>? Activate;
    public ServerTree() => AddHandler(KeyDownEvent, Navigate, RoutingStrategies.Tunnel);
    private TreeViewItem[] VisibleNodes() => Items.OfType<TreeViewItem>()
        .SelectMany(group => group.IsExpanded ? new[] { group }.Concat(group.Items.OfType<TreeViewItem>()) : [group]).ToArray();
    protected override void OnGotFocus(GotFocusEventArgs e)
    {
        if (Node(e.Source) is { } node) { Inspected = node; node.BringIntoView(); Inspect?.Invoke(node); }
    }
    private static TreeViewItem? Node(object? source) => source is Visual visual ?
        visual.GetSelfAndVisualAncestors().OfType<TreeViewItem>().FirstOrDefault() : null;
    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        if (Node(e.Source) is { } node && e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        { node.Focus(NavigationMethod.Pointer); Activate?.Invoke(node); e.Handled = true; }
    }
    protected override void OnKeyDown(KeyEventArgs e) { } // explicit tree pattern below
    private void Navigate(object? sender, KeyEventArgs e)
    {
        var nodes = VisibleNodes(); if (nodes.Length == 0) return;
        var current = Node(e.Source) ?? Inspected ?? nodes[0]; var index = Array.IndexOf(nodes, current);
        TreeViewItem? next = null;
        switch (e.Key)
        {
            case Key.Down: next = nodes[Math.Min(nodes.Length - 1, index + 1)]; break;
            case Key.Up: next = nodes[Math.Max(0, index - 1)]; break;
            case Key.Home: next = nodes[0]; break;
            case Key.End: next = nodes[^1]; break;
            case Key.Right:
                if (current.Items.Count > 0) { if (!current.IsExpanded) current.IsExpanded = true; else next = (TreeViewItem)current.Items[0]!; }
                break;
            case Key.Left:
                if (current.Items.Count > 0 && current.IsExpanded) current.IsExpanded = false;
                else next = Items.OfType<TreeViewItem>().FirstOrDefault(group => group.Items.Contains(current));
                break;
            case Key.Enter: Activate?.Invoke(current); break;
            default: return;
        }
        next?.Focus(NavigationMethod.Directional); e.Handled = true;
    }
    (bool handled, IInputElement? next) ICustomKeyboardNavigation.GetNext(IInputElement element, NavigationDirection direction)
    {
        if (direction is not NavigationDirection.Next and not NavigationDirection.Previous) return (false, null);
        if (element is Visual visual && this.IsVisualAncestorOf(visual)) return (true, null);
        var nodes = VisibleNodes(); var next = Inspected is not null && nodes.Contains(Inspected) ? Inspected : nodes.FirstOrDefault();
        return (next is not null, next);
    }
}
