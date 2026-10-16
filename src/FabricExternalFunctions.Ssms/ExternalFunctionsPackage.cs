using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using EnvDTE;
using FabricExternalFunctions.Core;
using Microsoft.SqlServer.Management.Sdk.Sfc;
using Microsoft.SqlServer.Management.UI.VSIntegration;
using Microsoft.SqlServer.Management.UI.VSIntegration.Editors;
using Microsoft.SqlServer.Management.UI.VSIntegration.ObjectExplorer;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Task = System.Threading.Tasks.Task;

namespace FabricExternalFunctions.Ssms
{
    [PackageRegistration(UseManagedResourcesOnly = true, AllowsBackgroundLoading = true)]
    [Guid(PackageId)]
    [ProvideAutoLoad("d114938f-591c-46cf-a785-500a82d97410", PackageAutoLoadFlags.BackgroundLoad)]
    public sealed class ExternalFunctionsPackage : AsyncPackage
    {
        public const string PackageId = "bde8af5e-30f5-46b9-85a6-047aac74a170";
        private readonly CatalogReader catalog = new CatalogReader();
        private readonly NodeContextMenus contextMenus = new NodeContextMenus();
        private readonly List<FolderState> folders = new List<FolderState>();
        private readonly HashSet<string> diagnosedWarehouses = new HashSet<string>();
        private readonly HashSet<string> discoveryDiagnostics = new HashSet<string>();
        private TreeView? tree;
        private ExplorerTreeObserver? treeObserver;
        private System.Windows.Forms.Timer? discovery;
        private bool disposed;
        private bool scanning;
        private string? discoveryFailure;

        protected override async Task InitializeAsync(CancellationToken cancellationToken, IProgress<ServiceProgressData> progress)
        {
            await JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
            try
            {
                var hostVersion = FileVersionInfo.GetVersionInfo(
                    System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "SqlWorkbench.Interfaces.dll"));
                if (hostVersion.FileMajorPart != 22 || !Environment.Is64BitProcess)
                    throw new NotSupportedException("This development package targets separately installed SSMS 22 x64 only.");
                ActivityLog.LogInformation("Fabric External Functions", "Initialized " + GetType().Assembly.GetName().Version +
                    "; schema-based and classic Functions discovery enabled.");
                discovery = new System.Windows.Forms.Timer { Interval = 2000 };
                discovery.Tick += DiscoveryTick;
                discovery.Start();
                Discover();
            }
            catch (Exception error) { Report(error); }
        }

        private void DiscoveryTick(object? sender, EventArgs args)
        {
            DiscoveryTickOnTreeChange();
        }

        private void Discover()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (disposed || scanning) return;
            scanning = true;
            try
            {
                if (tree == null || tree.IsDisposed)
                {
                    treeObserver?.Dispose(); treeObserver = null;
                    var service = GetService(typeof(IObjectExplorerService)) as IObjectExplorerService;
                    if (service == null)
                    {
                        LogDiscovery("Waiting for Object Explorer service.", true);
                        return;
                    }
                    LogDiscovery("Service resolved: " + service.GetType().FullName + ".");
                    var property = service.GetType().GetProperty("Tree", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                    if (property == null) throw new NotSupportedException("This SSMS build does not expose the expected Object Explorer tree.");
                    tree = property.GetValue(service) as TreeView;
                    if (tree == null)
                    {
                        LogDiscovery("Waiting for Object Explorer tree.", true);
                        return;
                    }
                    tree.BeforeExpand += BeforeExpand;
                    tree.ItemDrag += ItemDrag;
                    treeObserver = new ExplorerTreeObserver(tree, DiscoveryTickOnTreeChange);
                    LogDiscovery("Tree attached: " + tree.GetType().FullName + ".");
                }
                foreach (var old in folders.Where(f => !IsAttached(f)).ToArray())
                {
                    LogDiscovery("Removing folder: detached tree, parent, or changed target.", true);
                    old.Dispose(); old.Node.Remove(); folders.Remove(old);
                }
                foreach (var existing in folders.Where(f => f.Schema != null))
                {
                    var anchor = FunctionsAnchor(existing.Node.Parent!);
                    if (anchor != null) existing.Node.MoveAfter(anchor);
                }
                int nativeNodes = 0, expandingNodes = 0, warehouseNodes = 0, schemaNodes = 0;
                foreach (var node in Enumerate(tree.Nodes).ToArray())
                {
                    if (node is FolderNode || node is FunctionNode || folders.Any(f => f.Node.Parent == node)) continue;
                    var info = Information(node);
                    if (info == null)
                    {
                        LogDiscovery("Node has no INodeInformation: " + node.GetType().FullName + ".");
                        continue;
                    }
                    nativeNodes++;
                    if (node is ILazyLoadingNode lazy && lazy.Expanding)
                    {
                        expandingNodes++;
                        continue;
                    }
                    var connection = info.Connection;
                    if (connection == null) continue;
                    bool warehouseEndpoint = WarehouseEndpoint.IsSupported(connection.ServerName);
                    if (!warehouseEndpoint) continue;
                    warehouseNodes++;
                    var warehouse = TargetFor(node, info, out string? schema);
                    if (schema != null) schemaNodes++;
                    if (discoveryDiagnostics.Count < 48 && node.IsExpanded)
                    {
                        string? queryHint = info["QueryHint"] as string;
                        LogDiscovery("Expanded node shape: urn=" + info.UrnPath +
                            "; nodeType=" + node.GetType().FullName +
                            "; name=" + info.Name +
                            "; context=" + info.Context +
                            "; server=" + connection.ServerName +
                            "; database=" + connection.DatabaseName +
                            "; warehouseEndpoint=" + warehouseEndpoint +
                            "; schemaFilter=" + (info["Schema"] as string ?? "(none)") +
                            "; queryHint=" + (queryHint ?? "(none)") +
                            "; tableHint=" + HasHint(queryHint, "Table") +
                            "; viewHint=" + HasHint(queryHint, "View") +
                            "; functionHint=" + HasHint(queryHint, "UserDefinedFunction") +
                            "; schemaResolved=" + (schema != null) +
                            "; targetResolved=" + (warehouse != null) + ".");
                    }
                    if (warehouse == null) continue;
                    var anchor = schema == null ? null : FunctionsAnchor(node);
                    FolderState? state = null;
                    var folder = new FolderNode(node, action =>
                    {
                        if (state == null) return;
                        if (action.Point.HasValue) FolderMenu(state, action.Point.Value);
                        else EnsureLoaded(state);
                    });
                    state = new FolderState(folder, warehouse, folder.Hierarchy, schema);
                    folder.ToolTipText = warehouse.DisplayName + (schema == null ? "" : " / schema " + schema);
                    folder.Attach(anchor); folders.Add(state);
                    LogDiscovery("Folder attached; placement=" + (schema == null ? "classic Functions" : "schema") + ".");
                }
                LogDiscovery("Scan: nativeNodes=" + nativeNodes + "; expandingNodes=" + expandingNodes +
                    "; warehouseNodes=" + warehouseNodes + "; schemaNodes=" + schemaNodes +
                    "; attachedFolders=" + folders.Count + ".");
                foreach (var state in folders.Where(f => f.Node.Parent?.IsExpanded == true).ToArray())
                    EnsureLoaded(state);
                foreach (var node in Enumerate(tree.Nodes).Where(n => n.IsExpanded).ToArray())
                {
                    var info = Information(node);
                    if (info?.UrnPath != "Server/Database") continue;
                    var warehouse = WarehouseFor(info);
                    if (warehouse == null || folders.Any(f => f.Warehouse.Server == warehouse.Server &&
                        f.Warehouse.Database == warehouse.Database)) continue;
                    if (diagnosedWarehouses.Add(warehouse.Server + "\0" + warehouse.Database))
                        ActivityLog.LogWarning("Fabric External Functions", "No supported schema or Functions container is currently materialized for " +
                            warehouse.DisplayName + ". Expand a schema in the schema-based view, or Functions in the classic view.");
                }
            }
            finally { scanning = false; }
        }

        private void DiscoveryTickOnTreeChange()
        {
            try { Discover(); discoveryFailure = null; }
            catch (Exception error)
            {
                if (error is NotSupportedException) discovery?.Stop();
                string failure = error.ToString();
                if (failure == discoveryFailure) return;
                discoveryFailure = failure;
                Report(error);
            }
        }

        private void EnsureLoaded(FolderState state)
        {
            if (state.NeedsLoad) Run(() => RefreshAsync(state));
        }

        private void LogDiscovery(string message, bool warning = false)
        {
            if (discoveryDiagnostics.Count >= 48 || !discoveryDiagnostics.Add(message)) return;
            if (warning) ActivityLog.LogWarning("Fabric External Functions", "Discovery: " + message);
            else ActivityLog.LogInformation("Fabric External Functions", "Discovery: " + message);
        }
        private static bool HasHint(string? hint, string name) =>
            hint != null && hint.Split(';').Any(value => value.Trim() == name);

        private static IEnumerable<TreeNode> Enumerate(TreeNodeCollection nodes)
        {
            foreach (TreeNode node in nodes)
            {
                yield return node;
                if (!(node is FolderNode) && !(node is FunctionNode))
                    foreach (var child in Enumerate(node.Nodes)) yield return child;
            }
        }
        private static INodeInformation? Information(TreeNode node) =>
            (node as IServiceProvider)?.GetService(typeof(INodeInformation)) as INodeInformation;
        private static string? SchemaFor(INodeInformation info)
        {
            if (string.IsNullOrWhiteSpace(info.Context)) return null;
            var context = new Urn(info.Context);
            return ExplorerSchema.Resolve(info.UrnPath, context.Type,
                context.Type == "Schema" ? context.GetAttribute("Name", "Schema") : null,
                info.Name, info["Schema"] as string, info["QueryHint"] as string);
        }
        private static Warehouse? TargetFor(TreeNode node, INodeInformation info, out string? schema)
        {
            schema = SchemaFor(info);
            if (schema != null) return CatalogReader.IsUserSchema(schema) ? WarehouseFor(info) : null;
            if (!IsFunctionsContainer(node, info)) return null;
            for (var parent = node.Parent; parent != null; parent = parent.Parent)
            {
                var parentInfo = Information(parent);
                if (parentInfo != null && SchemaFor(parentInfo) != null) return null;
            }
            return WarehouseFor(info);
        }
        private bool IsAttached(FolderState state)
        {
            if (disposed || state.Disposed || tree == null || tree.IsDisposed ||
                state.Node.TreeView != tree || state.Node.Parent == null) return false;
            return MatchesTarget(state.Node.Parent, state.Warehouse, state.Schema);
        }
        internal static bool MatchesTarget(TreeNode parent, Warehouse target, string? expectedSchema)
        {
            var info = Information(parent);
            if (info == null || !WarehouseEndpoint.IsSupported(info.Connection?.ServerName)) return false;
            var warehouse = TargetFor(parent, info, out string? schema);
            return warehouse != null &&
                string.Equals(warehouse.Server, target.Server, StringComparison.OrdinalIgnoreCase) &&
                warehouse.Database == target.Database && schema == expectedSchema;
        }
        private static Warehouse? WarehouseFor(INodeInformation info)
        {
            var connection = info.Connection;
            if (connection == null || !WarehouseEndpoint.IsSupported(connection.ServerName)) return null;
            string server = connection.ServerName;
            string database = new Urn(info.Context).GetAttribute("Name", "Database");
            if (string.IsNullOrWhiteSpace(database)) database = connection.DatabaseName;
            return new Warehouse(server, database);
        }
        private static bool IsFunctionsContainer(TreeNode node, INodeInformation info)
        {
            string path = info.UrnPath;
            return path == "Server/Database/FunctionsFolder" || path == "Server/Database/UserDefinedFunctionsFolder" ||
                node.Nodes.Cast<TreeNode>().Select(Information).Any(n =>
                    n?.UrnPath == "Server/Database/Scalar-valuedFunctionsFolder" ||
                    n?.UrnPath == "Server/Database/Table-valuedFunctionsFolder");
        }
        private static TreeNode? FunctionsAnchor(TreeNode parent) =>
            parent.Nodes.Cast<TreeNode>().FirstOrDefault(child =>
            {
                var info = Information(child);
                return info != null && (info.UrnPath == "Server/Database/Schema/UsrDbFunctionsFolder" ||
                    IsFunctionsContainer(child, info));
            });
        private void BeforeExpand(object? sender, TreeViewCancelEventArgs args)
        {
            if (args.Cancel) return;
            if (args.Node is FunctionNode) return;
            if (args.Node is FolderNode)
            {
                var state = folders.FirstOrDefault(f => f.Node == args.Node);
                if (state != null) EnsureLoaded(state);
            }
            else
            {
                try
                {
                    Discover();
                    foreach (var state in folders.Where(f => f.Node.Parent == args.Node).ToArray())
                        EnsureLoaded(state);
                }
                catch (Exception error) { Report(error); }
            }
        }

        private void ItemDrag(object? sender, ItemDragEventArgs args)
        {
            if (args.Item is FunctionNode node)
                ActivityLog.LogInformation("Fabric External Functions", "Function drag started: " +
                    node.Metadata.DisplayName + "; canDrag=" + node.CanDrag + "; signatureAvailable=" + node.Metadata.HasSignature + ".");
        }

        private async Task RefreshAsync(FolderState state)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (!IsAttached(state)) throw new InvalidOperationException("The schema or warehouse node is no longer attached. Refresh Object Explorer.");
            var token = state.BeginLoad(); int generation = state.Generation;
            try
            {
                state.Node.Nodes.Clear();
                var loading = new LoadingNode(state.Hierarchy);
                state.Node.Nodes.Add(loading);
                ObjectExplorerImages.Register(tree ?? throw new InvalidOperationException("The Object Explorer tree is no longer attached."), loading);
                var functions = await catalog.ReadAsync(state.Warehouse, null, token, state.Schema);
                await JoinableTaskFactory.SwitchToMainThreadAsync();
                if (!state.IsCurrentLoad(generation) || !IsAttached(state)) return;
                if (functions.Any(f => !f.IsInSchema(state.Schema)))
                    throw new InvalidOperationException("Catalog results do not match this folder's schema. No function nodes were attached.");
                state.Node.Nodes.Clear();
                foreach (var function in functions)
                {
                    var node = new FunctionNode(state, function, FunctionAction);
                    state.Node.Nodes.Add(node);
                    node.RegisterImages(tree ??
                        throw new InvalidOperationException("The Object Explorer tree is no longer attached."));
                }
                state.Loaded = true;
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            catch (Exception error)
            {
                await JoinableTaskFactory.SwitchToMainThreadAsync();
                if (!state.IsCurrentLoad(generation) || !IsAttached(state))
                {
                    ActivityLog.LogWarning("Fabric External Functions",
                        "Ignored an obsolete metadata load failure after refresh or detachment: " + error);
                    return;
                }
                state.Node.Nodes.Clear();
                state.Node.Nodes.Add(new TreeNode("Metadata load failed. Use Refresh to retry."));
                throw;
            }
            finally
            {
                await JoinableTaskFactory.SwitchToMainThreadAsync();
                state.EndLoad(generation);
            }
        }

        private void FolderMenu(FolderState state, Point point)
        {
            var menu = contextMenus.CreateFolderMenu(
                () => Run(() => EditAsync(state, null), true), () => Run(() => TemplateAsync(state)),
                () => Run(() => RefreshAsync(state)));
            menu.Show(NodeContextMenus.ScreenPoint(state.Node, point));
        }

        private void FunctionAction(FunctionNode node, Point? point)
        {
            if (!node.Owner.IsCurrent(node) || !IsAttached(node.Owner)) { Report(new InvalidOperationException("This function node is stale. Refresh the folder.")); return; }
            if (!point.HasValue) { Run(() => DetailsAsync(node)); return; }
            var menu = contextMenus.CreateFunctionMenu(
                node.Metadata, kind => Run(() => ScriptAsync(node, kind)), () => Run(() => DropAsync(node), true),
                () => Run(() => DetailsAsync(node)), () => Run(() => EditAsync(node.Owner, node.Metadata), true));
            menu.Show(NodeContextMenus.ScreenPoint(node, point.Value));
        }

        private Task TemplateAsync(FolderState state)
        {
            if (!IsAttached(state))
                throw new InvalidOperationException("This folder node is stale. Refresh Object Explorer.");
            OpenQuery(SqlText.ExternalFunctionTemplate, state);
            return Task.CompletedTask;
        }

        private Task ScriptAsync(FunctionNode node, FunctionScriptKind kind)
        {
            if (!node.Owner.IsCurrent(node) || !IsAttached(node.Owner))
                throw new InvalidOperationException("This function node is stale. Refresh the folder.");
            OpenQuery(node.Metadata.Script(kind, node.Owner.Warehouse.Database), node.Owner);
            return Task.CompletedTask;
        }

        private async Task DropAsync(FunctionNode node)
        {
            await Task.Yield();
            await JoinableTaskFactory.SwitchToMainThreadAsync(DisposalToken);
            var state = node.Owner;
            if (!state.IsCurrent(node) || !IsAttached(state))
                throw new InvalidOperationException("This function node is stale. Refresh the folder.");
            if (!Dialogs.Delete(node.Metadata, state.Warehouse)) return;
            if (!state.IsCurrent(node) || !IsAttached(state))
                throw new InvalidOperationException("The function or warehouse context changed before execution.");
            await catalog.ExecuteDropAsync(state.Warehouse, node.Metadata, DisposalToken);
            await JoinableTaskFactory.SwitchToMainThreadAsync();
            await RefreshAfterMutationAsync(async () =>
            {
                foreach (var folder in folders.Where(f => IsAttached(f) &&
                    string.Equals(f.Warehouse.Server, state.Warehouse.Server, StringComparison.OrdinalIgnoreCase) &&
                    f.Warehouse.Database == state.Warehouse.Database &&
                    (f.Schema == null || f.Schema == node.Metadata.Schema)).ToArray())
                    await RefreshAsync(folder);
            }, "External function " + node.Metadata.DisplayName + " was deleted");
        }

        private async Task DetailsAsync(FunctionNode node)
        {
            await Task.Yield();
            await JoinableTaskFactory.SwitchToMainThreadAsync(DisposalToken);
            if (!node.Owner.IsCurrent(node) || !IsAttached(node.Owner))
                throw new InvalidOperationException("This function node is stale. Refresh the folder.");
            Dialogs.Details(node.Metadata, node.Owner.Warehouse);
        }

        private async Task EditAsync(FolderState state, FunctionMetadata? existing)
        {
            // Do not enter a modal editor from an unfinished native context-menu click.
            await Task.Yield();
            await JoinableTaskFactory.SwitchToMainThreadAsync(DisposalToken);
            if (!IsAttached(state)) throw new InvalidOperationException("The schema or warehouse node is no longer attached.");
            if (existing != null && !existing.IsInSchema(state.Schema)) throw new InvalidOperationException("Function metadata does not belong to this schema.");
            var schemas = await catalog.ReadSchemasAsync(state.Warehouse, DisposalToken);
            await JoinableTaskFactory.SwitchToMainThreadAsync();
            if (!IsAttached(state)) throw new InvalidOperationException("The schema or warehouse context changed while loading schemas.");
            async Task ExecuteDeclarationAsync(ProxyDeclaration proposed, bool retain)
            {
                await JoinableTaskFactory.SwitchToMainThreadAsync(DisposalToken);
                if (!IsAttached(state)) throw new InvalidOperationException("The schema or warehouse context changed before execution.");
                await catalog.ExecuteAsync(state.Warehouse, proposed, existing, retain, DisposalToken);
            }
            var declaration = Dialogs.Edit(state.Warehouse, existing, state.Schema, schemas,
                ExecuteDeclarationAsync, (error, showDialog) => Report(error, true, showDialog),
                sql => OpenQuery(sql, state),
                out bool retainExisting, out bool applied);
            if (declaration == null) return;
            if (!applied)
                await ExecuteDeclarationAsync(declaration, retainExisting);
            await JoinableTaskFactory.SwitchToMainThreadAsync();
            await RefreshAfterMutationAsync(async () =>
            {
                var affected = folders.Where(f => IsAttached(f) &&
                    string.Equals(f.Warehouse.Server, state.Warehouse.Server, StringComparison.OrdinalIgnoreCase) &&
                    f.Warehouse.Database == state.Warehouse.Database &&
                    (f.Schema == null || f.Schema == declaration.Schema || (existing != null && f.Schema == existing.Schema))).ToArray();
                foreach (var folder in affected) await RefreshAsync(folder);
            }, "External function " + SqlText.CallName(declaration.Schema, declaration.Name) + " was saved");
        }

        private static async Task RefreshAfterMutationAsync(Func<Task> refresh, string completedOperation)
        {
            try { await refresh().ConfigureAwait(false); }
            catch (Exception error) when (!(error is OperationCanceledException))
            {
                throw new InvalidOperationException(completedOperation +
                    ", but Object Explorer could not refresh. Use Refresh; do not repeat the warehouse change.", error);
            }
        }

        private void OpenQuery(string sql, FolderState state)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (!IsAttached(state))
                throw new InvalidOperationException("The selected Object Explorer folder is stale. Refresh Object Explorer.");
            var source = Information(state.Node.Parent!) ??
                throw new NotSupportedException("The selected Object Explorer connection is unavailable.");
            var connection = QueryConnection.ForNode(source, state.Warehouse);
            var dte = GetService(typeof(DTE)) as DTE ?? throw new NotSupportedException("SSMS editor automation unavailable; use Copy or Save.");
            var before = dte.ActiveDocument;
            ServiceCache.ScriptFactory.CreateNewBlankScript(ScriptType.Sql, connection, null);
            var document = dte.ActiveDocument;
            if (document == null || document == before || !(document.Selection is TextSelection selection))
                throw new NotSupportedException("Could not identify the newly opened SQL buffer. No text inserted; use Copy or Save.");
            selection.Insert(sql);
        }

        private void Run(Func<Task> action, bool mayExecuteDdl = false)
        {
            JoinableTaskFactory.RunAsync(async () =>
            {
                try { await action(); }
                catch (OperationCanceledException) when (disposed) { }
                catch (Exception error)
                {
                    await JoinableTaskFactory.SwitchToMainThreadAsync();
                    Report(error, mayExecuteDdl);
                }
            });
        }
        private void Report(Exception error, bool mayExecuteDdl = false, bool showDialog = true)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (GetService(typeof(SVsActivityLog)) is IVsActivityLog log)
                log.LogEntry((uint)__ACTIVITYLOG_ENTRYTYPE.ALE_ERROR, "Fabric External Functions", error.ToString());
            string guidance = mayExecuteDdl
                ? "No automatic DDL retry is performed. If execution timed out, check warehouse state before retrying."
                : "No DDL was requested by this operation. See the SSMS Activity Log for details.";
            if (!disposed && showDialog) MessageBox.Show(error.Message + "\r\n\r\n" + guidance,
                "Fabric External Functions", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                disposed = true;
                if (discovery != null) { discovery.Stop(); discovery.Tick -= DiscoveryTick; discovery.Dispose(); }
                if (tree != null) { tree.BeforeExpand -= BeforeExpand; tree.ItemDrag -= ItemDrag; }
                treeObserver?.Dispose(); treeObserver = null;
                contextMenus.Dispose();
                foreach (var state in folders) { state.Dispose(); state.Node.Remove(); }
                folders.Clear();
            }
            base.Dispose(disposing);
        }
    }
}
