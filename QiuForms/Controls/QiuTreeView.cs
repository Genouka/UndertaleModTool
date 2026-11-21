// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Collections.Generic;

namespace QiuForms;

/// <summary>A node of a <see cref="QiuTreeView"/>.</summary>
public sealed class QiuTreeNode
{
    /// <summary>Creates a node with the given text.</summary>
    public QiuTreeNode(string text)
    {
        Text = text ?? string.Empty;
    }

    /// <summary>The text shown in the tree.</summary>
    public string Text { get; set; }

    /// <summary>Child nodes.</summary>
    public List<QiuTreeNode> Children { get; } = [];

    /// <summary>Whether the node is expanded. Defaults to <see langword="true"/>.</summary>
    public bool IsExpanded { get; set; } = true;

    /// <summary>Optional caller data.</summary>
    public object? Tag { get; set; }
}

/// <summary>A tree view with one root node and flat children below it.</summary>
/// <remarks>
/// This is the shape the bundled sprite pickers need (a single "Sprites" root whose children are
/// filtered by a search box). It is intentionally not a general tree editor.
/// </remarks>
public sealed class QiuTreeView : QiuElement
{
    private readonly QiuTreeNode root;
    private QiuTreeNode? selectedNode;

    /// <summary>Creates a tree with the given root title.</summary>
    public QiuTreeView(string rootTitle)
    {
        root = new QiuTreeNode(rootTitle) { IsExpanded = true };
    }

    /// <summary>Optional caption shown above the tree.</summary>
    public string? Caption { get; set; }

    /// <summary>Preferred height in device-independent units, or <c>0</c> for the host default.</summary>
    public double Height { get; set; }

    /// <summary>Preferred width in device-independent units, or <c>0</c> for the host default.</summary>
    public double Width { get; set; }

    /// <summary>The root node.</summary>
    public QiuTreeNode Root => root;

    /// <summary>The root's children.</summary>
    public IReadOnlyList<QiuTreeNode> Nodes => root.Children;

    /// <summary>The selected node, or <see langword="null"/>.</summary>
    public QiuTreeNode? SelectedNode
    {
        get => selectedNode;
        set
        {
            if (ReferenceEquals(selectedNode, value))
                return;

            selectedNode = value;
            SelectionChanged?.Invoke(this);
        }
    }

    /// <summary>The selected node's text, or <see langword="null"/>.</summary>
    public string? SelectedText => selectedNode?.Text;

    /// <summary>Raised after <see cref="SetItems"/> changed the tree.</summary>
    public event Action<QiuTreeView>? ItemsChanged;

    /// <summary>Raised after the selection changed, including changes made in the host.</summary>
    public event Action<QiuTreeView>? SelectionChanged;

    /// <summary>Raised when the user double-clicks a node or presses Enter on it.</summary>
    public event Action<QiuTreeView>? ItemActivated;

    /// <summary>Replaces the root's children with one node per text.</summary>
    public void SetItems(IEnumerable<string> texts)
    {
        ArgumentNullException.ThrowIfNull(texts);

        root.Children.Clear();
        foreach (string text in texts)
            root.Children.Add(new QiuTreeNode(text));

        selectedNode = null;
        ItemsChanged?.Invoke(this);
    }

    /// <summary>Raises <see cref="ItemActivated"/>. Called by hosts.</summary>
    public void RaiseItemActivated() => ItemActivated?.Invoke(this);

    /// <summary>
    /// Raises <see cref="ItemsChanged"/> after the caller edited <see cref="Root"/> directly
    /// (for example to build a category tree). Hosts rebuild their view.
    /// </summary>
    public void Refresh() => ItemsChanged?.Invoke(this);
}
