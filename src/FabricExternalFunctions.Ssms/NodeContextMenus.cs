using System;
using System.Drawing;
using System.Windows.Forms;
using FabricExternalFunctions.Core;

namespace FabricExternalFunctions.Ssms
{
    internal sealed class NodeContextMenus : IDisposable
    {
        private ContextMenuStrip? current;
        private bool disposed;

        public static Point ScreenPoint(TreeNode node, Point treePoint) =>
            (node.TreeView ?? throw new InvalidOperationException("The Object Explorer node is no longer attached."))
                .PointToScreen(treePoint);

        public ContextMenuStrip Create()
        {
            if (disposed) throw new ObjectDisposedException(nameof(NodeContextMenus));
            // Closed can fire inside a modal click handler; the menu must outlive that dispatch.
            current?.Dispose();
            current = new ContextMenuStrip();
            return current;
        }

        public ContextMenuStrip CreateFolderMenu(Action createFunction, Action template, Action refresh)
        {
            var menu = Create();
            menu.Items.Add("New External Function...", null, (_, __) => createFunction());
            menu.Items.Add("New (from template)...", null, (_, __) => template());
            menu.Items.Add("Refresh", null, (_, __) => refresh());
            return menu;
        }

        public ContextMenuStrip CreateFunctionMenu(FunctionMetadata metadata, Action<FunctionScriptKind> script,
            Action drop, Action properties, Action edit)
        {
            var menu = Create();
            var scripts = new ToolStripMenuItem("Script External Function as");
            scripts.DropDownItems.Add("CREATE", null, (_, __) => script(FunctionScriptKind.Create));
            scripts.DropDownItems.Add("CREATE OR ALTER", null, (_, __) => script(FunctionScriptKind.CreateOrAlter));
            scripts.DropDownItems.Add("DROP", null, (_, __) => script(FunctionScriptKind.Drop));
            scripts.DropDownItems.Add(new ToolStripSeparator());
            scripts.DropDownItems.Add("SELECT", null, (_, __) => script(FunctionScriptKind.Select));
            scripts.DropDownItems.Add(new ToolStripMenuItem("SELECT FROM", null, (_, __) => script(FunctionScriptKind.SelectFrom))
            {
                Enabled = metadata.CanScriptSelectFrom,
                ToolTipText = "Requires a varchar(max) return type."
            });
            scripts.DropDownItems.Add("Declare Variable", null, (_, __) => script(FunctionScriptKind.DeclareVariable));
            scripts.DropDownItems.Add("SET Variable", null, (_, __) => script(FunctionScriptKind.SetVariable));
            menu.Items.Add(scripts);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Drop", null, (_, __) => drop());
            menu.Items.Add("Properties", null, (_, __) => properties());
            menu.Items.Add("Edit", null, (_, __) => edit());
            return menu;
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            current?.Dispose();
            current = null;
        }
    }
}
