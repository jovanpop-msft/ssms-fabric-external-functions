[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$PackageAssembly,
    [string]$SsmsIdeDir = 'C:\Program Files\Microsoft SQL Server Management Studio 22\Release\Common7\IDE'
)
$ErrorActionPreference = 'Stop'
try {
    if ($PSVersionTable.PSEdition -ne 'Desktop') { throw 'Editor checks require Windows PowerShell (.NET Framework).' }
    Add-Type -AssemblyName WindowsBase, PresentationCore, PresentationFramework, System.Windows.Forms, System.Drawing
    [void][Reflection.Assembly]::LoadFrom((Join-Path $SsmsIdeDir 'SqlWorkbench.Interfaces.dll'))
    [void][Reflection.Assembly]::LoadFrom((Join-Path $SsmsIdeDir 'ObjectExplorer.dll'))
    foreach ($name in @('Microsoft.VisualStudio.Shell.Framework', 'Microsoft.VisualStudio.Interop',
        'Microsoft.VisualStudio.Shell.Interop', 'Microsoft.VisualStudio.OLE.Interop', 'Microsoft.VisualStudio.Shell.15.0')) {
        [void][Reflection.Assembly]::LoadFrom((Join-Path $SsmsIdeDir ('PublicAssemblies\' + $name + '.dll')))
    }
    [void][Reflection.Assembly]::LoadFrom((Join-Path $SsmsIdeDir 'PublicAssemblies\Microsoft.VisualStudio.Threading.17.x\Microsoft.VisualStudio.Threading.dll'))
    [void][Reflection.Assembly]::LoadFrom((Join-Path $SsmsIdeDir 'Microsoft.SqlServer.Management.Sdk.Sfc.dll'))
    [void][Reflection.Assembly]::LoadFrom((Join-Path $SsmsIdeDir 'Microsoft.SqlServer.ConnectionInfo.dll'))
    $editorDir = Join-Path $SsmsIdeDir 'CommonExtensions\Microsoft\Editor'
    foreach ($name in @('Microsoft.VisualStudio.CoreUtility', 'Microsoft.VisualStudio.Text.Data',
        'Microsoft.VisualStudio.Text.Logic', 'Microsoft.VisualStudio.Text.UI', 'Microsoft.VisualStudio.Text.UI.Wpf')) {
        [void][Reflection.Assembly]::LoadFrom((Join-Path $editorDir ($name + '.dll')))
    }
    [void][Reflection.Assembly]::LoadFrom((Join-Path (Split-Path $PackageAssembly -Parent) 'FabricExternalFunctions.Core.dll'))
    $assembly = [Reflection.Assembly]::LoadFrom((Resolve-Path -LiteralPath $PackageAssembly).Path)
    $netstandard = [Reflection.Assembly]::Load('netstandard, Version=2.0.0.0, Culture=neutral, PublicKeyToken=cc7b13ffcd2ddd51')
    Add-Type -ReferencedAssemblies @(
        $netstandard.Location,
        (Join-Path (Split-Path $PackageAssembly -Parent) 'FabricExternalFunctions.Core.dll'),
        (Join-Path $SsmsIdeDir 'SqlWorkbench.Interfaces.dll'),
        [System.Windows.Forms.TreeNode].Assembly.Location,
        [System.Drawing.Icon].Assembly.Location,
        [Microsoft.VisualStudio.Text.ITextSnapshot].Assembly.Location,
        [Microsoft.VisualStudio.Text.VirtualSnapshotPoint].Assembly.Location,
        [Microsoft.VisualStudio.Text.Editor.IWpfTextView].Assembly.Location,
        [Microsoft.VisualStudio.Text.Editor.ITextView].Assembly.Location,
        [Microsoft.VisualStudio.Text.Operations.IEditorOperations].Assembly.Location,
        [Microsoft.VisualStudio.Utilities.IContentType].Assembly.Location,
        [System.Windows.Point].Assembly.Location,
        [System.Windows.DragDropEffects].Assembly.Location,
        [System.Windows.FrameworkElement].Assembly.Location) -TypeDefinition @'
using System;
using System.Reflection;
using System.Runtime.Remoting.Messaging;
using System.Runtime.Remoting.Proxies;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Text.Editor.DragDrop;
using Microsoft.VisualStudio.Text.Operations;
using Microsoft.VisualStudio.Utilities;
using FabricExternalFunctions.Core;
using Microsoft.SqlServer.Management.UI.VSIntegration.ObjectExplorer;

public static class DropDestinationChecks
{
    private sealed class SourceNode : HierarchyTreeNode, IServiceProvider
    {
        public INodeInformation Information { get; set; }
        public object GetService(Type serviceType) { return serviceType == typeof(INodeInformation) ? Information : null; }
        public override System.Drawing.Icon Icon { get { return System.Drawing.SystemIcons.Application; } }
        public override System.Drawing.Icon SelectedIcon { get { return Icon; } }
        public override int State { get { return 0; } }
        public override bool ShowPolicyHealthState { get; set; }
    }
    private sealed class Proxy : RealProxy
    {
        private readonly Func<IMethodCallMessage, object> call;
        public Proxy(Type type, Func<IMethodCallMessage, object> call) : base(type) { this.call = call; }
        public override IMessage Invoke(IMessage message)
        {
            var method = (IMethodCallMessage)message;
            try { return new ReturnMessage(call(method), null, 0, null, method); }
            catch (Exception error) { return new ReturnMessage(error, method); }
        }
    }
    public static void Check(Assembly package)
    {
        int readOnlyChecks = 0;
        bool readOnly = false;
        var contentType = (IContentType)new Proxy(typeof(IContentType), method => {
            if (method.MethodName == "IsOfType") return true;
            throw new Exception("Unexpected content-type call: " + method.MethodName);
        }).GetTransparentProxy();
        var buffer = (ITextBuffer)new Proxy(typeof(ITextBuffer), method => {
            if (method.MethodName == "get_ContentType") return contentType;
            if (method.MethodName == "IsReadOnly") {
                readOnlyChecks++;
                if ((int)method.GetArg(0) > 1) throw new Exception("Stale position reached the current shorter buffer.");
                return readOnly;
            }
            throw new Exception("Unexpected buffer call: " + method.MethodName);
        }).GetTransparentProxy();
        Func<int, ITextSnapshot> snapshot = length => (ITextSnapshot)new Proxy(typeof(ITextSnapshot), method => {
            if (method.MethodName == "get_Length") return length;
            if (method.MethodName == "get_TextBuffer") return buffer;
            throw new Exception("Unexpected snapshot call: " + method.MethodName);
        }).GetTransparentProxy();
        var oldSnapshot = snapshot(100);
        var currentSnapshot = snapshot(1);
        ITextCaret editorCaret = null;
        ITextSelection editorSelection = null;
        var view = (IWpfTextView)new Proxy(typeof(IWpfTextView), method => {
            if (method.MethodName == "get_IsClosed") return false;
            if (method.MethodName == "get_TextBuffer") return buffer;
            if (method.MethodName == "get_TextSnapshot") return currentSnapshot;
            if (method.MethodName == "get_Caret") return editorCaret;
            if (method.MethodName == "get_Selection") return editorSelection;
            throw new Exception("Unexpected view call: " + method.MethodName);
        }).GetTransparentProxy();
        using (var sourceTree = new System.Windows.Forms.TreeView())
        {
            var parent = new SourceNode();
            var connectionAssembly = Array.Find(AppDomain.CurrentDomain.GetAssemblies(),
                candidate => candidate.GetName().Name == "Microsoft.SqlServer.ConnectionInfo");
            var connectionType = connectionAssembly.GetType("Microsoft.SqlServer.Management.Common.SqlConnectionInfo", true);
            var connection = Activator.CreateInstance(connectionType);
            connectionType.GetProperty("ServerName").SetValue(connection, "example.datawarehouse.fabric.microsoft.com", null);
            connectionType.GetProperty("DatabaseName").SetValue(connection, "DW", null);
            string nativeDatabase = "DW", nativeSchema = "dbo";
            parent.Information = (INodeInformation)new Proxy(typeof(INodeInformation), method => {
                if (method.MethodName == "get_Connection") return connection;
                if (method.MethodName == "get_Context")
                    return "Server[@Name='example.datawarehouse.fabric.microsoft.com']/Database[@Name='" +
                        nativeDatabase + "']/Schema[@Name='" + nativeSchema + "']";
                if (method.MethodName == "get_UrnPath") return "Server/Database/Schema";
                if (method.MethodName == "get_Name") return nativeSchema;
                if (method.MethodName == "get_Item") return null;
                throw new Exception("Unexpected native-context access: " + method.MethodName);
            }).GetTransparentProxy();
            var hierarchy = (IExplorerHierarchy)new Proxy(typeof(IExplorerHierarchy), method => {
                if (method.MethodName == "get_Root") return parent;
                if (method.MethodName == "get_IsBuilding") return false;
                throw new Exception("Unexpected native hierarchy call: " + method.MethodName);
            }).GetTransparentProxy();
            parent.Hierarchy = hierarchy;
            sourceTree.Nodes.Add(parent);
            var targetFor = package.GetType("FabricExternalFunctions.Ssms.ExternalFunctionsPackage", true)
                .GetMethod("TargetFor", BindingFlags.Static | BindingFlags.NonPublic);
            var warehouseFor = package.GetType("FabricExternalFunctions.Ssms.ExternalFunctionsPackage", true)
                .GetMethod("WarehouseFor", BindingFlags.Static | BindingFlags.NonPublic);
            var disconnected = (INodeInformation)new Proxy(typeof(INodeInformation), method => {
                if (method.MethodName == "get_Connection") return null;
                throw new Exception("Disconnected metadata must not be parsed as a warehouse: " + method.MethodName);
            }).GetTransparentProxy();
            if (warehouseFor.Invoke(null, new object[] { disconnected }) != null)
                throw new Exception("A native node without connection metadata must not be a warehouse.");
            foreach (string schema in new[] { "sys", "SYS", "INFORMATION_SCHEMA", "guest", "queryinsights", "db_owner" })
            {
                var information = (INodeInformation)new Proxy(typeof(INodeInformation), method => {
                    if (method.MethodName == "get_Context")
                        return "Server[@Name='example.datawarehouse.fabric.microsoft.com']/Database[@Name='DW']/Schema[@Name='" + schema + "']";
                    if (method.MethodName == "get_UrnPath") return "Server/Database/Schema";
                    if (method.MethodName == "get_Name") return schema;
                    if (method.MethodName == "get_Item") return null;
                    throw new Exception("System-schema discovery must not access connection services: " + method.MethodName);
                }).GetTransparentProxy();
                if (targetFor.Invoke(null, new object[] { parent, information, null }) != null)
                    throw new Exception("A system schema must not receive an External Functions folder.");
            }
            var folder = (System.Windows.Forms.TreeNode)package.GetType("FabricExternalFunctions.Ssms.FolderNode", true)
                .GetConstructors()[0].Invoke(new object[] { parent, null });
            parent.Nodes.Add(folder);
            var warehouse = package.GetType("FabricExternalFunctions.Ssms.Warehouse", true)
                .GetConstructors()[0].Invoke(new object[] { "example.datawarehouse.fabric.microsoft.com", "DW" });
            var stateType = package.GetType("FabricExternalFunctions.Ssms.FolderState", true);
            var state = stateType.GetConstructors()[0].Invoke(new object[] { folder, warehouse, hierarchy, "dbo" });
            try
            {
                stateType.GetProperty("Loaded").SetValue(state, true, null);
                var metadata = new FunctionMetadata(42, "dbo", "f", new DateTime(2026, 10, 15),
                    new[] { new ParameterMetadata(0, "", "int") }, null);
                var source = (System.Windows.Forms.TreeNode)package.GetType("FabricExternalFunctions.Ssms.FunctionNode", true)
                    .GetConstructors()[0].Invoke(new object[] { state, metadata, null });
                folder.Nodes.Add(source);
                var dragType = package.GetType("FabricExternalFunctions.Ssms.CallDragSession", true);
                string format = (string)dragType.GetField("Format").GetRawConstantValue();
                var data = new System.Windows.DataObject();
                data.SetData(format, dragType.GetMethod("Register").Invoke(null, new object[] { source }));
                var type = package.GetType("FabricExternalFunctions.Ssms.CallDropHandler", true);
                Exception reportedError = null;
                bool? reportedInserted = null;
                int reports = 0;
                Action<Exception, bool> reportFailure = (error, inserted) => {
                    reportedError = error; reportedInserted = inserted; reports++;
                };
                var handler = type.GetConstructors()[0].Invoke(new object[] { view, null, reportFailure });
                Func<DragDropInfo> liveInfo = () => new DragDropInfo(new System.Windows.Point(),
                    System.Windows.DragDropKeyStates.None, data, false, null, System.Windows.DragDropEffects.Copy,
                    new VirtualSnapshotPoint(currentSnapshot, 1));
                var check = type.GetMethod("IsDropEnabled");
                var info = new DragDropInfo(new System.Windows.Point(), System.Windows.DragDropKeyStates.None,
                    data, false, null, System.Windows.DragDropEffects.Copy,
                    new VirtualSnapshotPoint(oldSnapshot, 50));
                if ((bool)check.Invoke(handler, new object[] { info }) || readOnlyChecks != 0)
                    throw new Exception("An old-snapshot drop must be rejected before accessing its stale position.");
                info = new DragDropInfo(new System.Windows.Point(), System.Windows.DragDropKeyStates.None,
                    data, false, null, System.Windows.DragDropEffects.Copy,
                    new VirtualSnapshotPoint(currentSnapshot, 1));
                if (!(bool)check.Invoke(handler, new object[] { info }) || readOnlyChecks != 1)
                    throw new Exception("A live source at a current writable position must enable the drop.");
                readOnly = true;
                if ((bool)check.Invoke(handler, new object[] { info }) || readOnlyChecks != 2)
                    throw new Exception("A current-snapshot drop must still honor the editor's read-only check.");
                readOnly = false;
                nativeDatabase = "other_database";
                if ((bool)check.Invoke(handler, new object[] { info }))
                    throw new Exception("A live drag must become invalid immediately when the native database changes.");
                nativeDatabase = "DW"; nativeSchema = "api";
                if ((bool)check.Invoke(handler, new object[] { info }))
                    throw new Exception("A live drag must become invalid immediately when the native schema changes.");
                nativeSchema = "dbo";
                connectionType.GetProperty("ServerName").SetValue(connection, "other.datawarehouse.fabric.microsoft.com", null);
                if ((bool)check.Invoke(handler, new object[] { info }))
                    throw new Exception("A live drag must become invalid immediately when the native server changes.");
                connectionType.GetProperty("ServerName").SetValue(connection, "example.datawarehouse.fabric.microsoft.com", null);
                if (!(bool)check.Invoke(handler, new object[] { info }))
                    throw new Exception("A matching native context must still accept the original live drag.");
                dragType.GetMethod("Consume").Invoke(null, null);
                if ((bool)check.Invoke(handler, new object[] { info }))
                    throw new Exception("A consumed source token must not enable another drop.");
                var callMetadata = new FunctionMetadata(43, "dbo", "with_parameters", new DateTime(2026, 10, 15),
                    new[] { new ParameterMetadata(0, "", "varchar(max)"), new ParameterMetadata(1, "startDate", "date"),
                        new ParameterMetadata(2, "count", "int"), new ParameterMetadata(3, "currency", "nvarchar(10)"),
                        new ParameterMetadata(4, "payload", "varbinary(max)") }, null);
                var callSource = (System.Windows.Forms.TreeNode)source.GetType().GetConstructors()[0]
                    .Invoke(new object[] { state, callMetadata, null });
                folder.Nodes.Add(callSource);
                object token = dragType.GetMethod("Register").Invoke(null, new object[] { callSource });
                data.SetData(format, token);
                int insertions = 0, caret = 1, selectionClears = 0, insertionBaseline = 0, insertionAttempts = 0;
                bool tokenSurvivedInsertion = false;
                bool failBeforeInsertion = false, failAfterInsertion = false, rejectInsertion = false;
                var beforeFailure = new InvalidOperationException("Synthetic pre-insertion caret failure.");
                var afterFailure = new InvalidOperationException("Synthetic post-insertion scrolling failure.");
                string insertedText = null;
                var mappingPoint = (IMappingPoint)new Proxy(typeof(IMappingPoint), method => {
                    throw new Exception("No projection mapping is needed for this insertion.");
                }).GetTransparentProxy();
                Func<CaretPosition> caretPosition = () => new CaretPosition(
                    new VirtualSnapshotPoint(currentSnapshot, caret), mappingPoint, PositionAffinity.Successor);
                editorCaret = (ITextCaret)new Proxy(typeof(ITextCaret), method => {
                    if (method.MethodName == "get_Position") return caretPosition();
                    if (method.MethodName == "MoveTo") {
                        if (failBeforeInsertion) throw beforeFailure;
                        if (insertions > insertionBaseline && dragType.GetMethod("Resolve").Invoke(null, new[] { token }) != null)
                            tokenSurvivedInsertion = true;
                        object position = method.GetArg(0);
                        caret = position is VirtualSnapshotPoint
                            ? ((VirtualSnapshotPoint)position).Position.Position : ((SnapshotPoint)position).Position;
                        return caretPosition();
                    }
                    if (method.MethodName == "EnsureVisible") {
                        if (dragType.GetMethod("Resolve").Invoke(null, new[] { token }) != null)
                            tokenSurvivedInsertion = true;
                        if (failAfterInsertion) throw afterFailure;
                        return null;
                    }
                    throw new Exception("Unexpected caret call: " + method.MethodName);
                }).GetTransparentProxy();
                editorSelection = (ITextSelection)new Proxy(typeof(ITextSelection), method => {
                    if (method.MethodName == "Clear") { selectionClears++; return null; }
                    throw new Exception("Unexpected selection call: " + method.MethodName);
                }).GetTransparentProxy();
                var operations = (IEditorOperations)new Proxy(typeof(IEditorOperations), method => {
                    if (method.MethodName == "InsertText") {
                        insertionAttempts++;
                        if (rejectInsertion) return false;
                        insertedText = (string)method.GetArg(0); insertions++;
                        currentSnapshot = snapshot(currentSnapshot.Length + insertedText.Length);
                        caret += insertedText.Length;
                        return true;
                    }
                    throw new Exception("Unexpected editor operation: " + method.MethodName);
                }).GetTransparentProxy();
                caretPosition();
                handler = type.GetConstructors()[0].Invoke(new object[] { view, operations, reportFailure });
                var drop = type.GetMethod("HandleDataDropped");
                var effect = (DragDropPointerEffects)drop.Invoke(handler, new object[] { info });
                const string expected = "DW.dbo.with_parameters('', 0, N'', 0x)";
                if (effect != DragDropPointerEffects.Copy || insertions != 1 || selectionClears != 1 || reports != 0 ||
                    insertedText != expected || caret != 1 + "DW.dbo.with_parameters(".Length)
                    throw new Exception("A real drop must insert the typed call once and place the caret at its first argument.");
                if (tokenSurvivedInsertion)
                    throw new Exception("A successful insertion must consume its token before caret or scrolling can fail.");
                info = liveInfo();
                if ((DragDropPointerEffects)drop.Invoke(handler, new object[] { info }) != DragDropPointerEffects.None ||
                    insertions != 1 || selectionClears != 1)
                    throw new Exception("The completed drop must not be inserted twice.");

                token = dragType.GetMethod("Register").Invoke(null, new object[] { callSource });
                data.SetData(format, token); info = liveInfo(); insertionBaseline = insertions;
                failBeforeInsertion = true;
                effect = (DragDropPointerEffects)drop.Invoke(handler, new object[] { info });
                if (effect != DragDropPointerEffects.None || insertions != 1 || insertionAttempts != 1 ||
                    reports != 1 || reportedInserted != false || !ReferenceEquals(reportedError, beforeFailure) ||
                    dragType.GetMethod("Resolve").Invoke(null, new[] { token }) == null)
                    throw new Exception("A pre-insertion failure must report failure without inserting or consuming its token.");

                failBeforeInsertion = false; failAfterInsertion = true;
                effect = (DragDropPointerEffects)drop.Invoke(handler, new object[] { info });
                if (effect != DragDropPointerEffects.Copy || insertions != 2 || insertionAttempts != 2 ||
                    reports != 2 || reportedInserted != true || !ReferenceEquals(reportedError, afterFailure) ||
                    dragType.GetMethod("Resolve").Invoke(null, new[] { token }) != null || tokenSurvivedInsertion)
                    throw new Exception("A post-insertion failure must report the inserted call, return Copy and consume its token.");
                info = liveInfo();
                if ((DragDropPointerEffects)drop.Invoke(handler, new object[] { info }) != DragDropPointerEffects.None ||
                    insertions != 2 || reports != 2)
                    throw new Exception("A call inserted before presentation failed must not be replayed.");

                failAfterInsertion = false; rejectInsertion = true; insertionBaseline = insertions;
                token = dragType.GetMethod("Register").Invoke(null, new object[] { callSource });
                data.SetData(format, token); info = liveInfo();
                effect = (DragDropPointerEffects)drop.Invoke(handler, new object[] { info });
                if (effect != DragDropPointerEffects.None || insertions != 2 || insertionAttempts != 3 ||
                    reports != 3 || reportedInserted != false || reportedError == null ||
                    reportedError.Message != "The SQL editor rejected the insertion." ||
                    dragType.GetMethod("Resolve").Invoke(null, new[] { token }) == null)
                    throw new Exception("A rejected insertion must remain an explicit failure, not a completed copy.");
                token = dragType.GetMethod("Register").Invoke(null, new object[] { callSource });
                sourceTree.Dispose();
                if (dragType.GetMethod("Resolve").Invoke(null, new[] { token }) != null)
                    throw new Exception("Disposing the native tree must invalidate a drag immediately, even if managed nodes remain attached.");
                dragType.GetMethod("Consume").Invoke(null, null);
            }
            finally { ((IDisposable)state).Dispose(); }
        }
    }
}
'@
    [void][Microsoft.VisualStudio.Shell.ThreadHelper].GetMethod(
        'SetUIThread', [Reflection.BindingFlags]'Static,Public,NonPublic').Invoke($null, @())
    [DropDestinationChecks]::Check($assembly)
    Write-Host 'PASS stale snapshots are rejected before accessing shortened buffers; live tokens enable writable drops only, and consumed tokens are rejected.'
    Write-Host 'PASS disconnected native metadata is ignored; actual typed call insertion consumes its token before presentation and occurs only once.'
    Write-Host 'PASS live drag tokens revalidate native server, database and schema without network access.'
    Write-Host 'PASS disposing the native tree invalidates existing drag tokens immediately.'
    Write-Host 'PASS pre-insertion, rejected-insertion and post-insertion failures report the correct outcome without replay.'
    exit 0
} catch {
    [Console]::Error.WriteLine($_.Exception.ToString())
    exit 1
}
