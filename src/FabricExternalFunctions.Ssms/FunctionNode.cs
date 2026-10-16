using System;
using System.Drawing;
using System.Windows.Forms;
using FabricExternalFunctions.Core;
using Microsoft.SqlServer.Management.UI.VSIntegration.ObjectExplorer;

namespace FabricExternalFunctions.Ssms
{
    internal sealed class FunctionNode : HierarchyTreeNode, INodeWithMenu, INodeWithDrag
    {
        private readonly FolderState owner;
        private readonly Action<FunctionNode, Point?> show;
        public FunctionNode(FolderState owner, FunctionMetadata metadata, Action<FunctionNode, Point?> show)
        {
            this.owner = owner; Metadata = metadata; this.show = show;
            Text = (owner.Schema == null ? metadata.DisplayName : metadata.Name) +
                ": " + (metadata.ReturnType ?? "unavailable");
            Name = "FabricXF:" + metadata.ObjectId;
            ToolTipText = metadata.Signature; Hierarchy = owner.Hierarchy;
            foreach (var parameter in metadata.Inputs) Nodes.Add(new ParameterNode(parameter, owner.Hierarchy));
        }
        public FunctionMetadata Metadata { get; }
        public FolderState Owner => owner;
        public override Icon Icon => ObjectExplorerImages.Function(ObjectExplorerImages.SizeFor(this));
        public override Icon SelectedIcon => Icon;
        public override int State => 0;
        public override bool ShowPolicyHealthState { get => false; set { } }
        public void RegisterImages(TreeView tree)
        {
            ObjectExplorerImages.Register(tree, this);
            foreach (TreeNode parameter in Nodes) ObjectExplorerImages.Register(tree, parameter);
        }
        public bool CanDrag => Metadata.HasSignature && owner.IsCurrent(this);
        public void UpdateData(DataObject data)
        {
            if (!CanDrag) throw new InvalidOperationException("Function metadata is stale or missing. Refresh the folder.");
            data.SetData(CallDragSession.Format, false, CallDragSession.Register(this));
        }
        public void ShowContextMenu(Point treePoint) => show(this, treePoint);
        public void DoDefaultAction() => show(this, null);
    }

    internal sealed class ParameterNode : HierarchyTreeNode
    {
        public ParameterNode(ParameterMetadata parameter, IExplorerHierarchy hierarchy)
        {
            Hierarchy = hierarchy;
            Name = "FabricXF:Parameter:" + parameter.Ordinal;
            Text = parameter.Name.TrimStart('@') + " (" + parameter.SqlType + ")";
            ToolTipText = Text;
        }
        public override Icon Icon => ObjectExplorerImages.Parameter(ObjectExplorerImages.SizeFor(this));
        public override Icon SelectedIcon => Icon;
        public override int State => 0;
        public override bool ShowPolicyHealthState { get => false; set { } }
    }
}
