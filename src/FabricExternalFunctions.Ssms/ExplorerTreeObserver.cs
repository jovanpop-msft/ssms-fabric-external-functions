using System;
using System.Reflection;
using System.Windows.Forms;

namespace FabricExternalFunctions.Ssms
{
    internal sealed class ExplorerTreeObserver : IDisposable
    {
        private readonly TreeView tree;
        private readonly Action changed;
        private readonly EventInfo nodeAdded;
        private readonly EventInfo finishedExpanding;
        private bool queued;
        private bool disposed;

        public ExplorerTreeObserver(TreeView tree, Action changed)
        {
            this.tree = tree;
            this.changed = changed;
            nodeAdded = NativeEvent("NodeAdded");
            finishedExpanding = NativeEvent("FinishedExpanding");
            nodeAdded.AddEventHandler(tree, new TreeViewEventHandler(OnChanged));
            finishedExpanding.AddEventHandler(tree, new TreeViewEventHandler(OnChanged));
            tree.AfterExpand += OnChanged;
        }

        private EventInfo NativeEvent(string name)
        {
            var result = tree.GetType().GetEvent(name);
            if (result?.EventHandlerType != typeof(TreeViewEventHandler))
                throw new NotSupportedException("This SSMS build does not expose the expected Object Explorer " + name + " event.");
            return result;
        }

        private void OnChanged(object? sender, TreeViewEventArgs args)
        {
            if (disposed || queued || tree.IsDisposed || !tree.IsHandleCreated) return;
            queued = true;
            // Let the native batch finish adding/removing its own nodes before inserting ours.
            tree.BeginInvoke((Action)(() =>
            {
                queued = false;
                if (!disposed && !tree.IsDisposed) changed();
            }));
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            nodeAdded.RemoveEventHandler(tree, new TreeViewEventHandler(OnChanged));
            finishedExpanding.RemoveEventHandler(tree, new TreeViewEventHandler(OnChanged));
            tree.AfterExpand -= OnChanged;
        }
    }
}
