using System;
using System.Drawing;
using System.Threading;
using System.Windows.Forms;
using Microsoft.SqlServer.Management.UI.VSIntegration.ObjectExplorer;

namespace FabricExternalFunctions.Ssms
{
    internal sealed class FolderNode : HierarchyTreeNode, INodeWithMenu
    {
        private readonly Action<PointAction> action;
        private readonly TreeNode parent;
        public FolderNode(TreeNode parent, Action<PointAction> action)
        {
            this.parent = parent;
            this.action = action; Name = "FabricExternalFunctions:Folder";
            Text = "External Functions";
            Hierarchy = (parent as HierarchyTreeNode)?.Hierarchy ??
                throw new NotSupportedException("The Object Explorer parent node does not expose its native hierarchy.");
            Nodes.Add(new TreeNode("Expand to load visible external functions"));
        }
        public override Icon Icon => ObjectExplorerImages.Folder(ObjectExplorerImages.SizeFor(this));
        public override Icon SelectedIcon => ObjectExplorerImages.OpenFolder(ObjectExplorerImages.SizeFor(this));
        public override int State => 0;
        public override bool ShowPolicyHealthState { get => false; set { } }
        public void Attach(TreeNode? after)
        {
            if (Parent != null) throw new InvalidOperationException("The External Functions folder is already attached.");
            if (after != null && after.Parent != parent)
                throw new ArgumentException("The Functions anchor is not a child of the schema.", nameof(after));
            var tree = parent.TreeView ??
                throw new InvalidOperationException("The Object Explorer parent is no longer attached.");
            parent.Nodes.Insert(after == null ? parent.Nodes.Count : after.Index + 1, this);
            try { ObjectExplorerImages.Register(tree, this); }
            catch { Remove(); throw; }
        }
        public void MoveAfter(TreeNode after)
        {
            if (Parent != parent || after.Parent != parent)
                throw new ArgumentException("The folder and Functions anchor must belong to the same schema.", nameof(after));
            if (Index == after.Index + 1) return;
            var tree = TreeView ?? throw new InvalidOperationException("The Object Explorer tree is no longer attached.");
            var selected = tree.SelectedNode;
            bool restoreSelection = false;
            for (var node = selected; node != null; node = node.Parent)
                if (node == this) { restoreSelection = true; break; }
            bool expanded = IsExpanded;
            tree.BeginUpdate();
            try
            {
                Remove();
                parent.Nodes.Insert(after.Index + 1, this);
                if (expanded) Expand();
                if (restoreSelection) tree.SelectedNode = selected;
            }
            finally { tree.EndUpdate(); }
        }
        public void ShowContextMenu(System.Drawing.Point point) => action(new PointAction(point));
        public void DoDefaultAction() => action(new PointAction(null));
    }

    internal sealed class LoadingNode : HierarchyTreeNode
    {
        public LoadingNode(IExplorerHierarchy hierarchy)
        {
            Hierarchy = hierarchy;
            Text = "Loading external functions...";
        }
        public override Icon Icon => ObjectExplorerImages.Loading(ObjectExplorerImages.SizeFor(this));
        public override Icon SelectedIcon => Icon;
        public override int State => 0;
        public override bool ShowPolicyHealthState { get => false; set { } }
    }

    internal sealed class PointAction
    {
        public PointAction(System.Drawing.Point? point) { Point = point; }
        public System.Drawing.Point? Point { get; }
    }

    internal sealed class FolderState : IDisposable
    {
        public FolderState(FolderNode node, Warehouse warehouse, IExplorerHierarchy hierarchy, string? schema)
        { Node = node; Warehouse = warehouse; Hierarchy = hierarchy; Schema = schema; }
        public FolderNode Node { get; }
        public Warehouse Warehouse { get; }
        public IExplorerHierarchy Hierarchy { get; }
        public string? Schema { get; }
        public CancellationTokenSource? Pending { get; private set; }
        public bool Loaded { get; set; }
        public bool LoadAttempted { get; private set; }
        public bool NeedsLoad => !Disposed && !Loaded && !LoadAttempted && Pending == null;
        public bool Disposed { get; private set; }
        public int Generation { get; private set; }
        public CancellationToken BeginLoad()
        {
            if (Disposed) throw new ObjectDisposedException(nameof(FolderState));
            Pending?.Cancel(); Pending?.Dispose();
            Pending = new CancellationTokenSource(); Generation++; Loaded = false; LoadAttempted = true;
            return Pending.Token;
        }
        public void EndLoad(int generation)
        {
            if (generation != Generation) return;
            Pending?.Dispose(); Pending = null;
        }
        public bool IsCurrentLoad(int generation) => !Disposed && generation == Generation &&
            Pending != null && !Pending.IsCancellationRequested;
        public bool IsCurrent(FunctionNode node) => !Disposed && Loaded && node.Parent == Node &&
            Node.TreeView is TreeView tree && !tree.IsDisposed && node.Metadata.IsInSchema(Schema) && Node.Parent != null &&
            ExternalFunctionsPackage.MatchesTarget(Node.Parent, Warehouse, Schema);
        public void Dispose()
        {
            if (Disposed) return;
            Disposed = true; Generation++; Pending?.Cancel(); Pending?.Dispose(); Pending = null;
        }
    }
}
