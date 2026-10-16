using System;
using System.ComponentModel.Composition;
using System.Windows.Forms;
using FabricExternalFunctions.Core;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Text.Editor.DragDrop;
using Microsoft.VisualStudio.Text.Operations;
using Microsoft.VisualStudio.Utilities;

namespace FabricExternalFunctions.Ssms
{
    internal static class CallDragSession
    {
        public const string Format = "FabricExternalFunctions.Call.v1";
        private static string? currentId;
        private static WeakReference<FunctionNode>? currentNode;
        public static string Register(FunctionNode node)
        {
            currentId = Guid.NewGuid().ToString("D");
            currentNode = new WeakReference<FunctionNode>(node);
            return currentId;
        }
        public static FunctionNode? Resolve(object? id)
        {
            if (!(id is string value) || value != currentId || currentNode == null ||
                !currentNode.TryGetTarget(out var node) || !node.CanDrag) return null;
            return node;
        }
        public static void Consume() { currentId = null; currentNode = null; }
    }

    [Export(typeof(IDropHandlerProvider))]
    [Name("FabricExternalFunctions.CallDropHandler")]
    [ContentType("SQL")]
    [ContentType("T-SQL")]
    [DropFormat(CallDragSession.Format)]
    public sealed class CallDropProvider : IDropHandlerProvider
    {
        private readonly IEditorOperationsFactoryService operations;
        [ImportingConstructor]
        public CallDropProvider(IEditorOperationsFactoryService operations) { this.operations = operations; }
        public IDropHandler GetAssociatedDropHandler(IWpfTextView view)
        {
            ActivityLog.LogInformation("Fabric External Functions",
                "SQL editor drop handler attached; contentType=" + view.TextBuffer.ContentType.TypeName + ".");
            return new CallDropHandler(view, operations.GetEditorOperations(view), ReportFailure);
        }
        private static void ReportFailure(Exception error, bool inserted)
        {
            ActivityLog.LogError("Fabric External Functions",
                (inserted ? "Call inserted; caret update failed. " : "") + error.GetType().Name + ": " + error.Message);
            MessageBox.Show(inserted ? "Call inserted, but caret update failed.\r\n\r\n" + error.Message : error.Message,
                inserted ? "Call inserted" : "External function drop failed", MessageBoxButtons.OK,
                inserted ? MessageBoxIcon.Warning : MessageBoxIcon.Error);
        }
    }

    internal sealed class CallDropHandler : IDropHandler
    {
        private readonly IWpfTextView view;
        private readonly IEditorOperations operations;
        private readonly Action<Exception, bool> reportFailure;
        public CallDropHandler(IWpfTextView view, IEditorOperations operations, Action<Exception, bool> reportFailure)
        { this.view = view; this.operations = operations; this.reportFailure = reportFailure; }
        public bool IsDropEnabled(DragDropInfo info)
        {
            if (view.IsClosed || (!view.TextBuffer.ContentType.IsOfType("SQL") &&
                !view.TextBuffer.ContentType.IsOfType("T-SQL"))) return false;
            var position = info.VirtualBufferPosition.Position;
            return position.Snapshot == view.TextSnapshot &&
                position.Snapshot.TextBuffer == view.TextBuffer &&
                !view.TextBuffer.IsReadOnly(position.Position) &&
                CallDragSession.Resolve(info.Data.GetData(CallDragSession.Format)) != null;
        }
        public DragDropPointerEffects HandleDragStarted(DragDropInfo info) => Effect(info);
        public DragDropPointerEffects HandleDraggingOver(DragDropInfo info) => Effect(info);
        public void HandleDragCanceled() { }
        private DragDropPointerEffects Effect(DragDropInfo info) => IsDropEnabled(info)
            ? DragDropPointerEffects.Copy | DragDropPointerEffects.Track : DragDropPointerEffects.None;
        public DragDropPointerEffects HandleDataDropped(DragDropInfo info)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            bool inserted = false;
            try
            {
                if (!IsDropEnabled(info)) return DragDropPointerEffects.None;
                var node = CallDragSession.Resolve(info.Data.GetData(CallDragSession.Format));
                if (node == null) throw new InvalidOperationException("Function metadata changed during the drag. Refresh and try again.");
                string database = node.Owner.Warehouse.Database;
                string text = node.Metadata.CallTemplate(database);
                view.Selection.Clear();
                view.Caret.MoveTo(info.VirtualBufferPosition);
                if (!operations.InsertText(text)) throw new InvalidOperationException("The SQL editor rejected the insertion.");
                inserted = true;
                CallDragSession.Consume();
                if (node.Metadata.Inputs.Count > 0)
                {
                    int end = view.Caret.Position.BufferPosition.Position;
                    int firstSlot = end - text.Length + SqlText.CallName(database, node.Metadata.Schema, node.Metadata.Name).Length + 1;
                    view.Caret.MoveTo(new SnapshotPoint(view.TextSnapshot, firstSlot));
                }
                view.Caret.EnsureVisible();
                return DragDropPointerEffects.Copy;
            }
            catch (Exception error)
            {
                reportFailure(error, inserted);
                return inserted ? DragDropPointerEffects.Copy : DragDropPointerEffects.None;
            }
        }
    }
}
