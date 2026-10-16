[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$PackageAssembly,
    [string]$SsmsIdeDir = 'C:\Program Files\Microsoft SQL Server Management Studio 22\Release\Common7\IDE'
)
$ErrorActionPreference = 'Stop'
try {
    if ($PSVersionTable.PSEdition -ne 'Desktop') {
        throw 'Native hierarchy checks require Windows PowerShell (.NET Framework).'
    }
    Add-Type -AssemblyName System.Windows.Forms, System.Drawing
    foreach ($name in @('Microsoft.VisualStudio.Shell.Framework', 'Microsoft.VisualStudio.Interop',
        'Microsoft.VisualStudio.Shell.Interop', 'Microsoft.VisualStudio.OLE.Interop', 'Microsoft.VisualStudio.Shell.15.0')) {
        [void][Reflection.Assembly]::LoadFrom((Join-Path $SsmsIdeDir ('PublicAssemblies\' + $name + '.dll')))
    }
    [void][Reflection.Assembly]::LoadFrom((Join-Path $SsmsIdeDir 'PublicAssemblies\Microsoft.VisualStudio.Threading.17.x\Microsoft.VisualStudio.Threading.dll'))
    $connectionInfoAssembly = [Reflection.Assembly]::LoadFrom((Join-Path $SsmsIdeDir 'Microsoft.SqlServer.ConnectionInfo.dll'))
    [void][Reflection.Assembly]::LoadFrom((Join-Path $SsmsIdeDir 'Microsoft.SqlServer.Management.Sdk.Sfc.dll'))
    $interfaces = Join-Path $SsmsIdeDir 'SqlWorkbench.Interfaces.dll'
    [void][Reflection.Assembly]::LoadFrom($interfaces)
    $nativeAssembly = [Reflection.Assembly]::LoadFrom((Join-Path $SsmsIdeDir 'ObjectExplorer.dll'))
    $contextType = $nativeAssembly.GetType(
        'Microsoft.SqlServer.Management.UI.VSIntegration.ObjectExplorer.NodeContext', $true)
    # Reproduce the metadata getter without creating a connection or invoking host services.
    $context = [Runtime.Serialization.FormatterServices]::GetUninitializedObject($contextType)
    $reproduced = $false
    try { [void]$contextType.GetProperty('Hierarchy').GetValue($context, $null) }
    catch {
        $failure = $_.Exception
        while ($null -ne $failure.InnerException) { $failure = $failure.InnerException }
        if ($failure -isnot [InvalidOperationException]) { throw }
        $reproduced = $true
    }
    if (!$reproduced) { throw 'Installed NodeContext.Hierarchy did not reproduce the reported failure.' }
    Write-Host 'PASS actual SSMS metadata Hierarchy getter reproduces InvalidOperationException.'

    Add-Type -ReferencedAssemblies @($interfaces,
        [System.Windows.Forms.TreeNode].Assembly.Location,
        [System.Drawing.Icon].Assembly.Location) -TypeDefinition @'
using System;
using System.Drawing;
using System.Runtime.Remoting.Messaging;
using System.Runtime.Remoting.Proxies;
using Microsoft.SqlServer.Management.UI.VSIntegration.ObjectExplorer;

public sealed class NativeHierarchyFixture : IExplorerHierarchy
{
    public HierarchyTreeNode Root { get; set; }
    public bool IsBuilding { get { return false; } }
    public event EventHandler BeginAsynchronousUpdate { add { } remove { } }
    public event EventHandler EndAsynchronousUpdate { add { } remove { } }
    public void PerformAsynchronousBuild(IExplorerHierarchyNode node, BuildChildren builder, EventHandler finished)
    { throw new InvalidOperationException("The fixture must not start asynchronous host work."); }
    public void StopAllAsynchronousOperations()
    { throw new InvalidOperationException("The fixture must not change host work."); }
}

public sealed class NativeHierarchyNodeFixture : HierarchyTreeNode, IServiceProvider
{
    public INodeInformation Information { get; set; }
    public override Icon Icon { get { return SystemIcons.Application; } }
    public override Icon SelectedIcon { get { return Icon; } }
    public override int State { get { return 0; } }
    public override bool ShowPolicyHealthState { get; set; }
    public object GetService(Type serviceType)
    { return serviceType == typeof(INodeInformation) ? Information : null; }
}

public sealed class NativeInformationFixture : RealProxy
{
    private readonly object connection;
    private readonly string context, name;
    public NativeInformationFixture(object connection, string context, string name) : base(typeof(INodeInformation))
    { this.connection = connection; this.context = context; this.name = name; }
    public override IMessage Invoke(IMessage message)
    {
        var method = (IMethodCallMessage)message;
        object result;
        switch (method.MethodName)
        {
            case "GetType": result = typeof(INodeInformation); break;
            case "ToString": result = name; break;
            case "get_Connection": result = connection; break;
            case "get_Context": result = context; break;
            case "get_Name": result = name; break;
            case "get_UrnPath": result = "Server/Database/Schema"; break;
            case "get_Item": result = null; break;
            default: return new ReturnMessage(new Exception("Unexpected metadata access: " + method.MethodName), method);
        }
        return new ReturnMessage(result, null, 0, null, method);
    }
}

public sealed class ImageRegistrationTreeFixture : System.Windows.Forms.TreeView
{
    public event System.Windows.Forms.TreeViewEventHandler NodeAdded;
    public event System.Windows.Forms.TreeViewEventHandler FinishedExpanding;
    public void NotifyNodeAdded(System.Windows.Forms.TreeNode node)
    { if (NodeAdded != null) NodeAdded(this, new System.Windows.Forms.TreeViewEventArgs(node)); }
    public void NotifyFinishedExpanding(System.Windows.Forms.TreeNode node)
    { if (FinishedExpanding != null) FinishedExpanding(this, new System.Windows.Forms.TreeViewEventArgs(node)); }
    public ImageRegistrationTreeFixture()
    {
        ImageList = new System.Windows.Forms.ImageList();
        ImageList.ColorDepth = System.Windows.Forms.ColorDepth.Depth32Bit;
    }
    public void AddRootNode(System.Windows.Forms.TreeNode node)
    { Nodes.Add(node); AddIconToImageMap(node); }
    public void AddIconToImageMap(System.Windows.Forms.TreeNode node)
    {
        ImageList.Images.Add(((INodeWithIcon)node).Icon);
        node.ImageIndex = ImageList.Images.Count - 1;
        node.SelectedImageIndex = node.ImageIndex;
    }
}
'@
    $hierarchy = New-Object NativeHierarchyFixture
    $parent = New-Object NativeHierarchyNodeFixture
    $parent.Information = $context
    $parent.Hierarchy = $hierarchy
    $hierarchy.Root = $parent
    $assembly = [Reflection.Assembly]::LoadFrom((Resolve-Path -LiteralPath $PackageAssembly).Path)
    $folderType = $assembly.GetType('FabricExternalFunctions.Ssms.FolderNode', $true)
    $constructor = $folderType.GetConstructors() | Where-Object {
        $_.GetParameters()[0].ParameterType -eq [System.Windows.Forms.TreeNode]
    } | Select-Object -First 1
    if ($null -eq $constructor) { throw 'FolderNode must receive the actual parent TreeNode, not metadata.Hierarchy.' }
    $folder = $constructor.Invoke([object[]]@($parent.PSObject.BaseObject, $null))
    if (![object]::ReferenceEquals($folder.Hierarchy.PSObject.BaseObject, $hierarchy.PSObject.BaseObject) -or
        ![object]::ReferenceEquals($folder.Root.PSObject.BaseObject, $parent.PSObject.BaseObject)) {
        throw 'The extension folder did not preserve its native parent hierarchy/root.'
    }
    [void]$parent.Nodes.Add($folder)
    if ($folder.Parent -ne $parent -or $folder.Text -ne 'External Functions' -or $folder.Nodes.Count -ne 1) {
        throw 'The extension folder failed to attach with its loading placeholder.'
    }
    Write-Host 'PASS production FolderNode attaches with native hierarchy while metadata.Hierarchy throws.'

    foreach ($invalidParent in @((New-Object System.Windows.Forms.TreeNode),
        (New-Object NativeHierarchyNodeFixture))) {
        $rejected = $false
        try { [void]$constructor.Invoke([object[]]@($invalidParent.PSObject.BaseObject, $null)) }
        catch {
            $failure = $_.Exception
            while ($null -ne $failure.InnerException) { $failure = $failure.InnerException }
            if ($failure -isnot [NotSupportedException]) { throw }
            $rejected = $true
        }
        if (!$rejected) { throw 'An unsupported parent without a native hierarchy was accepted.' }
    }
    Write-Host 'PASS non-native and missing-hierarchy parents fail explicitly. No SSMS launch or SQL execution.'

    $folder.Remove()
    $nativeTreeType = $nativeAssembly.GetType(
        'Microsoft.SqlServer.Management.UI.VSIntegration.ObjectExplorer.LazyTreeView', $true)
    if ($null -eq $nativeTreeType.GetMethod('AddIconToImageMap', [type[]]@([System.Windows.Forms.TreeNode]))) {
        throw 'Installed SSMS does not expose the expected public image-registration method.'
    }
    foreach ($name in @('NodeAdded', 'FinishedExpanding')) {
        if ($nativeTreeType.GetEvent($name).EventHandlerType -ne [System.Windows.Forms.TreeViewEventHandler]) {
            throw "The installed native $name event does not match the observer contract."
        }
    }
    # Native LazyTreeView requires live VS theme services; exercise its registration contract without a shell.
    $tree = New-Object ImageRegistrationTreeFixture
    $nativeFolder = $null
    try {
        $tree.AddRootNode($parent)
        [void]$tree.Handle
        $placeholder = New-Object System.Windows.Forms.TreeNode -ArgumentList 'Native lazy placeholder'
        [void]$parent.Nodes.Add($placeholder)
        $folder.Attach($null)
        if ($folder.Parent -ne $parent -or $parent.IsExpanded -or $placeholder.Parent -ne $parent) {
            throw 'The folder must attach before schema expansion without removing the native lazy placeholder.'
        }
        $imageIndex = $folder.ImageIndex
        $placeholder.Remove()
        foreach ($label in @('Tables', 'Views', 'Functions', 'Stored Procedures')) {
            [void]$parent.Nodes.Add((New-Object System.Windows.Forms.TreeNode -ArgumentList $label))
        }
        $anchor = $parent.Nodes | Where-Object Text -eq 'Functions' | Select-Object -First 1
        $folder.MoveAfter($anchor)
        $order = ($parent.Nodes | ForEach-Object Text) -join ','
        if ($order -ne 'Tables,Views,Functions,External Functions,Stored Procedures') {
            throw "External Functions is not immediately after Functions: $order"
        }
        if ($folder.ImageIndex -lt 0 -or $folder.ImageIndex -eq $parent.ImageIndex) {
            throw 'Folder image was not registered through the native image-map adapter.'
        }
        if ($folder.ImageIndex -ne $imageIndex -or $folder.IsExpanded) {
            throw 'Repositioning after native categories arrive must preserve the icon and collapsed state.'
        }
        $notifications = [int[]]@(0)
        $changed = [Action]({ $notifications[0]++ }.GetNewClosure())
        $observerType = $assembly.GetType('FabricExternalFunctions.Ssms.ExplorerTreeObserver', $true)
        $observer = $observerType.GetConstructors()[0].Invoke([object[]]@($tree.PSObject.BaseObject, $changed))
        try {
            $tree.NotifyNodeAdded($parent)
            $tree.NotifyNodeAdded($anchor)
            $tree.NotifyFinishedExpanding($parent)
            if ($notifications[0] -ne 0) { throw 'Discovery must not mutate the tree inside a native batch.' }
            [System.Windows.Forms.Application]::DoEvents()
            if ($notifications[0] -ne 1) { throw 'Native node batches must schedule one immediate follow-up discovery.' }
            $tree.NotifyNodeAdded($parent)
            $observer.Dispose()
            [System.Windows.Forms.Application]::DoEvents()
            if ($notifications[0] -ne 1) { throw 'Disposed observers must suppress queued work.' }
            $tree.NotifyFinishedExpanding($parent)
            [System.Windows.Forms.Application]::DoEvents()
            if ($notifications[0] -ne 1) { throw 'Disposed observers must unsubscribe from native events.' }
        } finally { $observer.Dispose() }
        Write-Host 'PASS eager collapsed-schema folder attachment, native ordering and deferred/coalesced tree events.'
        $stream = $nativeAssembly.GetManifestResourceStream(
            'Microsoft.SqlServer.Management.UI.VSIntegration.ObjectExplorer.folder.ico')
        try { $nativeFolder = New-Object System.Drawing.Icon -ArgumentList $stream, $tree.ImageList.ImageSize }
        finally { $stream.Dispose() }
        $expectedBitmap = $nativeFolder.ToBitmap()
        $actualBitmap = $folder.Icon.ToBitmap()
        try {
            for ($x = 0; $x -lt $expectedBitmap.Width; $x++) {
                for ($y = 0; $y -lt $expectedBitmap.Height; $y++) {
                    if ($actualBitmap.GetPixel($x, $y) -ne $expectedBitmap.GetPixel($x, $y)) {
                        throw 'The folder icon pixels do not match the native SSMS resource.'
                    }
                }
            }
        } finally { $expectedBitmap.Dispose(); $actualBitmap.Dispose() }
        Write-Host 'PASS production folder ordering and native image-registration adapter.'
        $tree.Location = New-Object System.Drawing.Point -ArgumentList 130, 210
        $menuType = $assembly.GetType('FabricExternalFunctions.Ssms.NodeContextMenus', $true)
        foreach ($clientPoint in @((New-Object System.Drawing.Point -ArgumentList 25, 42),
            (New-Object System.Drawing.Point -ArgumentList 110, 85))) {
            $expected = $tree.PointToScreen($clientPoint)
            $actual = $menuType.GetMethod('ScreenPoint').Invoke($null,
                [object[]]@($folder.PSObject.BaseObject, $clientPoint.PSObject.BaseObject))
            if ($actual -ne $expected -or $actual -eq $clientPoint) {
                throw 'Native tree-client context-menu positions must be converted to screen coordinates exactly once.'
            }
        }
        Write-Host 'PASS mouse/keyboard menu coordinates include the Object Explorer window offset.'

        $core = [Reflection.Assembly]::LoadFrom((Join-Path (Split-Path $PackageAssembly -Parent) 'FabricExternalFunctions.Core.dll'))
        $parameterType = $core.GetType('FabricExternalFunctions.Core.ParameterMetadata', $true)
        $parameters = [Array]::CreateInstance($parameterType, 1)
        $parameter = $parameterType.GetConstructors()[0].Invoke([object[]]@(0, '', 'int'))
        $parameters.SetValue($parameter.PSObject.BaseObject, 0)
        $metadataType = $core.GetType('FabricExternalFunctions.Core.FunctionMetadata', $true)
        $metadata = $metadataType.GetConstructors()[0].Invoke(
            [object[]]@(1, 'dbo', 'test_function', [DateTime]::MinValue, $parameters, $null, $null))
        $warehouse = $assembly.GetType('FabricExternalFunctions.Ssms.Warehouse', $true).GetConstructors()[0].Invoke(
            [object[]]@('example.datawarehouse.fabric.microsoft.com', 'test_database'))
        $state = $assembly.GetType('FabricExternalFunctions.Ssms.FolderState', $true).GetConstructors()[0].Invoke(
            [object[]]@($folder.PSObject.BaseObject, $warehouse.PSObject.BaseObject, $hierarchy.PSObject.BaseObject, 'dbo'))
        $function = $assembly.GetType('FabricExternalFunctions.Ssms.FunctionNode', $true).GetConstructors()[0].Invoke(
            [object[]]@($state.PSObject.BaseObject, $metadata.PSObject.BaseObject, $null))
        [void]$folder.Nodes.Add($function)
        $function.RegisterImages($tree)
        if ($function.ImageIndex -lt 0 -or $function.ImageIndex -eq $folder.ImageIndex -or
            $function.Icon.Size -ne $nativeFolder.Size) {
            throw 'Function must use its own registered Fabric icon at the native icon size.'
        }
        Write-Host 'PASS production function node uses embedded Fabric icon and native image-map registration.'
        if ($function.Text -ne 'test_function: int' -or $function.Name -ne 'FabricXF:1') {
            throw 'Function labels must include the effective SQL return type without changing node identity.'
        }
        foreach ($scope in @('dbo', $null)) {
            $labelState = $state.GetType().GetConstructors()[0].Invoke(
                [object[]]@($folder.PSObject.BaseObject, $warehouse.PSObject.BaseObject, $hierarchy.PSObject.BaseObject, $scope))
            try {
                foreach ($type in @('float', 'decimal(20,4)', 'varchar(max)', $null)) {
                    foreach ($definition in @($null, 'CREATE FUNCTION dbo.s2 AS EXTERNAL FUNCTION item.remote;',
                        'CREATE FUNCTION dbo.s2 RETURNS bigint AS EXTERNAL FUNCTION item.remote;')) {
                        $returnParameters = [Array]::CreateInstance($parameterType, $(if ($null -eq $type) { 0 } else { 1 }))
                        if ($null -ne $type) {
                            $returnParameters.SetValue($parameterType.GetConstructors()[0].Invoke([object[]]@(0, '', $type)), 0)
                        }
                        $labelMetadata = $metadataType.GetConstructors()[0].Invoke(
                            [object[]]@(3, 'dbo', 's2', [DateTime]::MinValue, $returnParameters, $definition, $null))
                        $labelNode = $function.GetType().GetConstructors()[0].Invoke(
                            [object[]]@($labelState.PSObject.BaseObject, $labelMetadata.PSObject.BaseObject, $null))
                        $expectedName = if ($null -eq $scope) { 'dbo.s2' } else { 's2' }
                        $expectedType = if ($null -eq $type) { 'unavailable' } else { $type }
                        if ($labelNode.Text -ne "${expectedName}: $expectedType" -or
                            $labelNode.Metadata.Name -ne 's2' -or $labelNode.Name -ne 'FabricXF:3') {
                            throw 'Schema/classic labels must show the actual catalog type, never an inferred marker or guessed declaration type.'
                        }
                    }
                }
            } finally { $labelState.Dispose() }
        }
        Write-Host 'PASS function labels show effective catalog return types in schema/classic views, with honest missing-metadata handling.'
        if ($function.Nodes.Count -ne 0) { throw 'Zero-input functions must not invent parameter children.' }
        $inputs = [Array]::CreateInstance($parameterType, 4)
        $inputs.SetValue($parameterType.GetConstructors()[0].Invoke([object[]]@(3, 'odd name', 'varchar(max)')), 0)
        $inputs.SetValue($parameterType.GetConstructors()[0].Invoke([object[]]@(0, '', 'int')), 1)
        $inputs.SetValue($parameterType.GetConstructors()[0].Invoke([object[]]@(2, '@amount', 'decimal(20,4)')), 2)
        $inputs.SetValue($parameterType.GetConstructors()[0].Invoke([object[]]@(1, 'latitude', 'float')), 3)
        $inputMetadata = $metadataType.GetConstructors()[0].Invoke(
            [object[]]@(2, 'dbo', 'with_parameters', [DateTime]::MinValue, $inputs, $null, $null))
        $withParameters = $function.GetType().GetConstructors()[0].Invoke(
            [object[]]@($state.PSObject.BaseObject, $inputMetadata.PSObject.BaseObject, $null))
        [void]$folder.Nodes.Add($withParameters)
        $withParameters.RegisterImages($tree)
        if ($withParameters.IsExpanded -or $withParameters.Nodes.Count -ne 3 -or
            (($withParameters.Nodes | ForEach-Object Text) -join '|') -ne
                'latitude (float)|amount (decimal(20,4))|odd name (varchar(max))') {
            throw 'Function children must start collapsed and show ordered input names/types without @ prefixes, excluding return metadata.'
        }
        foreach ($child in $withParameters.Nodes) {
            if ($child -isnot [Microsoft.SqlServer.Management.UI.VSIntegration.ObjectExplorer.HierarchyTreeNode] -or
                ![object]::ReferenceEquals($child.Hierarchy.PSObject.BaseObject, $hierarchy.PSObject.BaseObject) -or
                $child.ImageIndex -lt 0 -or $child.ImageIndex -eq $withParameters.ImageIndex -or
                $child.Icon.Size -ne $tree.ImageList.ImageSize -or $child.Nodes.Count -ne 0 -or
                $child -is [Microsoft.SqlServer.Management.UI.VSIntegration.ObjectExplorer.INodeWithDrag] -or
                $child -is [Microsoft.SqlServer.Management.UI.VSIntegration.ObjectExplorer.INodeWithMenu]) {
                throw 'Parameter children must be native, registered, read-only leaf nodes with the input-parameter icon.'
            }
        }
        $parameterStream = $nativeAssembly.GetManifestResourceStream(
            'Microsoft.SqlServer.Management.UI.VSIntegration.ObjectExplorer.input_parameter.ico')
        try { $parameterIcon = New-Object System.Drawing.Icon -ArgumentList $parameterStream, $tree.ImageList.ImageSize }
        finally { $parameterStream.Dispose() }
        $expectedParameter = $parameterIcon.ToBitmap()
        $actualParameter = $withParameters.Nodes[0].Icon.ToBitmap()
        try {
            for ($x = 0; $x -lt $expectedParameter.Width; $x++) {
                for ($y = 0; $y -lt $expectedParameter.Height; $y++) {
                    if ($actualParameter.GetPixel($x, $y) -ne $expectedParameter.GetPixel($x, $y)) {
                        throw 'Parameter icon pixels must match the native SSMS input-parameter resource.'
                    }
                }
            }
        } finally { $expectedParameter.Dispose(); $actualParameter.Dispose(); $parameterIcon.Dispose() }
        foreach ($attempt in 1..2) {
            $withParameters.Expand()
            if (!$withParameters.IsExpanded -or $withParameters.GetNodeCount($false) -ne 3) {
                throw 'Expanding a function must reveal its cached parameters without placeholders or duplicates.'
            }
            $withParameters.Collapse()
            if ($withParameters.IsExpanded -or $withParameters.Nodes.Count -ne 3) {
                throw 'Collapsing must hide parameters without discarding them.'
            }
        }
        if ($inputMetadata.Inputs[0].Name -ne 'latitude' -or $inputMetadata.Inputs[1].Name -ne '@amount' -or
            $inputMetadata.CallTemplate('test_database') -ne "test_database.dbo.with_parameters(0, 0, '')") {
            throw 'Parameter display must preserve raw names, while drag/script calls use type-based literals.'
        }
        $withParameters.Remove()
        Write-Host 'PASS native parameter children: ordered names/types without @ prefixes, native icons, repeated expand/collapse, no return child or drag/menu actions.'
        $metadataConnection = [Activator]::CreateInstance($connectionInfoAssembly.GetType(
            'Microsoft.SqlServer.Management.Common.SqlConnectionInfo', $true))
        $metadataConnection.ServerName = 'example.datawarehouse.fabric.microsoft.com'
        $metadataConnection.DatabaseName = 'test_database'
        $nativeContext = New-Object NativeInformationFixture -ArgumentList $metadataConnection,
            "Server[@Name='example.datawarehouse.fabric.microsoft.com']/Database[@Name='test_database']/Schema[@Name='dbo']", 'dbo'
        $parent.Information = $nativeContext.GetTransparentProxy()
        $state.Loaded = $true
        if (!$function.CanDrag) { throw 'An attached function with a known signature cannot start a native drag.' }
        $data = New-Object System.Windows.Forms.DataObject
        $function.UpdateData($data)
        $dragType = $assembly.GetType('FabricExternalFunctions.Ssms.CallDragSession', $true)
        $format = $dragType.GetField('Format').GetRawConstantValue()
        if (!$data.GetDataPresent($format) -or $data.GetDataPresent([System.Windows.Forms.DataFormats]::UnicodeText)) {
            throw 'The native drag must contain the owned format, not an uncontrolled text fallback.'
        }
        $resolved = $dragType.GetMethod('Resolve').Invoke($null, [object[]]@($data.GetData($format)))
        if (![object]::ReferenceEquals($resolved.PSObject.BaseObject, $function.PSObject.BaseObject)) {
            throw 'The native drag payload did not resolve to its source function.'
        }
        $state.Loaded = $false
        if ($function.CanDrag -or $null -ne $dragType.GetMethod('Resolve').Invoke($null, [object[]]@($data.GetData($format)))) {
            throw 'Stale source metadata remained draggable.'
        }
        $dragType.GetMethod('Consume').Invoke($null, @())
        Write-Host 'PASS native drag payload resolves attached function metadata and rejects a stale source.'
        if (!$state.NeedsLoad) { throw 'An unrequested folder must be eligible for schema-expansion prefetch.' }
        $token = $state.BeginLoad()
        $generation = $state.Generation
        if (!$state.IsCurrentLoad($generation)) { throw 'The active load must own its completion.' }
        if ($state.NeedsLoad -or !$state.LoadAttempted -or $null -eq $state.Pending) {
            throw 'In-flight preload must prevent duplicate requests.'
        }
        $newToken = $state.BeginLoad()
        $newGeneration = $state.Generation
        if ($state.IsCurrentLoad($generation) -or !$state.IsCurrentLoad($newGeneration)) {
            throw 'A superseded load must not publish results or errors for the new request.'
        }
        if (!$token.IsCancellationRequested -or $newToken.IsCancellationRequested) {
            throw 'A replacement refresh must cancel only the old request.'
        }
        $state.EndLoad($generation)
        if ($null -eq $state.Pending) { throw 'A stale completion must not clear the newer request.' }
        $state.EndLoad($newGeneration)
        if ($null -ne $state.Pending -or $state.NeedsLoad) {
            throw 'Completed/failed preload must clear pending work without automatic retry loops.'
        }
        $loading = $assembly.GetType('FabricExternalFunctions.Ssms.LoadingNode', $true).GetConstructors()[0].Invoke(
            [object[]]@($hierarchy.PSObject.BaseObject))
        [void]$folder.Nodes.Add($loading)
        $assembly.GetType('FabricExternalFunctions.Ssms.ObjectExplorerImages', $true).GetMethod('Register').Invoke(
            $null, [object[]]@($tree.PSObject.BaseObject, $loading.PSObject.BaseObject))
        if ($folder.IsExpanded -or $loading.ImageIndex -lt 0 -or $loading.ImageIndex -eq $folder.ImageIndex) {
            throw 'Loading must use its own registered indicator without expanding the folder.'
        }
        $loadingBitmap = $loading.Icon.ToBitmap()
        try {
            if ($loadingBitmap.GetPixel(0, 0).A -ne 0) { throw 'The loading icon must retain a transparent background.' }
        } finally { $loadingBitmap.Dispose() }
        $loading.Remove()
        $folder.Nodes.Clear()
        $state.Loaded = $true
        if ($folder.Nodes.Count -ne 0 -or $folder.GetNodeCount($false) -ne 0 -or $state.NeedsLoad -or
            $folder.Parent -ne $parent -or $folder.Text -ne 'External Functions') {
            throw 'A loaded empty folder must remain attached with no message/loading child or automatic reload.'
        }
        Write-Host 'PASS an empty loaded folder has no placeholder children and remains available for New/Refresh.'
        [void]$folder.Nodes.Add($function)
        $state.Dispose()
        if ($state.IsCurrentLoad($newGeneration)) { throw 'Disposed folders must discard all load completions.' }
        if ($state.NeedsLoad) { throw 'Disposed folders must not queue metadata loading.' }
        Write-Host 'PASS preload lifecycle, stale generation protection, cancellation and hidden loading indicator.'
        foreach ($size in @(16, 20, 24, 32, 40, 48, 64)) {
            $tree.ImageList.ImageSize = New-Object System.Drawing.Size -ArgumentList $size, $size
            if ($function.Icon.Size -ne $tree.ImageList.ImageSize) {
                throw "The embedded Fabric icon does not provide the requested ${size}px frame."
            }
            $bitmap = $function.Icon.ToBitmap()
            try {
                if ($bitmap.GetPixel(0, 0).A -ne 0) { throw "The ${size}px Fabric icon lost corner transparency." }
            } finally { $bitmap.Dispose() }
        }
        Write-Host 'PASS Fabric icon supplies transparent 16/20/24/32/40/48/64px frames at tree image size.'
    } finally {
        if ($null -ne $nativeFolder) { $nativeFolder.Dispose() }
        $tree.Dispose()
    }

    $connectionAssembly = [Reflection.Assembly]::LoadFrom((Join-Path $SsmsIdeDir 'Microsoft.SqlServer.RegSvrEnum.dll'))
    $uiConnectionType = $connectionAssembly.GetType('Microsoft.SqlServer.Management.Smo.RegSvrEnum.UIConnectionInfo', $true)
    $queryConnection = $assembly.GetType('FabricExternalFunctions.Ssms.QueryConnection', $true)
    $nativeConnectionMethod = $queryConnection.GetMethod('ResolveNativeMethod').Invoke($null, [object[]]@($nativeAssembly))
    if (!$nativeConnectionMethod.IsStatic -or $nativeConnectionMethod.ReturnType -ne $uiConnectionType -or
        $nativeConnectionMethod.GetParameters().Count -ne 1 -or
        $nativeConnectionMethod.GetParameters()[0].ParameterType.FullName -ne
            'Microsoft.SqlServer.Management.UI.VSIntegration.ObjectExplorer.INodeInformation') {
        throw 'Query connection must use the native Object Explorer New Query connection-context contract.'
    }
    $rejected = $false
    try { [void]$queryConnection.GetMethod('ResolveNativeMethod').Invoke($null, [object[]]@($assembly)) }
    catch {
        $failure = $_.Exception
        while ($null -ne $failure.InnerException) { $failure = $failure.InnerException }
        if ($failure -isnot [NotSupportedException]) { throw }
        $rejected = $true
    }
    if (!$rejected) { throw 'A missing native query-connection helper must be an explicit unsupported-host error.' }
    $queryTarget = $assembly.GetType('FabricExternalFunctions.Ssms.Warehouse', $true).GetConstructors()[0].Invoke(
        [object[]]@('example.datawarehouse.fabric.microsoft.com', 'test_database'))
    $queryInfo = [Activator]::CreateInstance($uiConnectionType)
    $queryInfo.ServerName = 'EXAMPLE.DATAWAREHOUSE.FABRIC.MICROSOFT.COM'
    $queryInfo.AdvancedOptions['DATABASE'] = 'test_database'
    $queryInfo.AdvancedOptions['ApplicationIntent'] = 'ReadOnly'
    $validated = $queryConnection.GetMethod('ValidateTarget').Invoke(
        $null, [object[]]@($queryInfo.PSObject.BaseObject, $queryTarget.PSObject.BaseObject))
    if (![object]::ReferenceEquals($validated.PSObject.BaseObject, $queryInfo.PSObject.BaseObject) -or
        $queryInfo.AdvancedOptions['ApplicationIntent'] -ne 'ReadOnly' -or
        $queryInfo.AdvancedOptions['DATABASE'] -ne 'test_database') {
        throw 'Native query context must be passed through intact, without rebuilding or mutating connection options.'
    }
    foreach ($invalid in @(
        @('other.datawarehouse.fabric.microsoft.com', 'test_database'),
        @('example.datawarehouse.fabric.microsoft.com', 'other_database'),
        @('example.datawarehouse.fabric.microsoft.com', 'TEST_DATABASE'),
        @('example.datawarehouse.fabric.microsoft.com', $null),
        @($null, $null))) {
        $badInfo = if ($null -eq $invalid[0]) { $null } else {
            $value = [Activator]::CreateInstance($uiConnectionType)
            $value.ServerName = $invalid[0]
            $value.AdvancedOptions['DATABASE'] = $invalid[1]
            $value
        }
        $rejected = $false
        try {
            [void]$queryConnection.GetMethod('ValidateTarget').Invoke(
                $null, [object[]]@($badInfo, $queryTarget.PSObject.BaseObject))
        } catch {
            $failure = $_.Exception
            while ($null -ne $failure.InnerException) { $failure = $failure.InnerException }
            if ($failure -isnot [InvalidOperationException]) { throw }
            $rejected = $true
        }
        if (!$rejected) { throw 'Missing or mismatched native query context must not open a disconnected/wrong-database fallback.' }
    }
    Write-Host 'PASS native New Query connection-helper contract and intact source-target validation, without invoking host services or connecting.'

    $refreshAfterMutation = $assembly.GetType('FabricExternalFunctions.Ssms.ExternalFunctionsPackage', $true).GetMethod(
        'RefreshAfterMutationAsync', [Reflection.BindingFlags]'Static,NonPublic')
    if ($null -eq $refreshAfterMutation) { throw 'Post-mutation refresh must distinguish a committed change from a failed refresh.' }
    foreach ($verb in @('saved', 'deleted')) {
        $completed = "External function dbo.f was $verb"
        $refreshFailure = [InvalidOperationException]::new('Simulated metadata refresh failure.')
        $attempts = [int[]]@(0)
        $refresh = [Func[Threading.Tasks.Task]]{
            $attempts[0]++
            [Threading.Tasks.Task]::FromException($refreshFailure)
        }
        $pending = $refreshAfterMutation.Invoke($null, [object[]]@($refresh, $completed))
        $rejected = $false
        try { [void]$pending.GetAwaiter().GetResult() }
        catch {
            $failure = $_.Exception
            while ($null -ne $failure -and !($failure -is [InvalidOperationException] -and
                $failure.Message.StartsWith("$completed, but"))) { $failure = $failure.InnerException }
            if ($null -eq $failure -or ![object]::ReferenceEquals($failure.InnerException, $refreshFailure) -or
                $failure.Message -notmatch 'do not repeat') { throw }
            $rejected = $true
        }
        if (!$rejected -or $attempts[0] -ne 1) { throw 'A failed post-DDL refresh must not obscure success or retry the change.' }
    }
    $refresh = [Func[Threading.Tasks.Task]]{ [Threading.Tasks.Task]::CompletedTask }
    $pending = $refreshAfterMutation.Invoke($null, [object[]]@($refresh, 'External function dbo.f was saved'))
    [void]$pending.GetAwaiter().GetResult()
    $refresh = [Func[Threading.Tasks.Task]]{
        [Threading.Tasks.Task]::FromCanceled([Threading.CancellationToken]::new($true))
    }
    $pending = $refreshAfterMutation.Invoke($null, [object[]]@($refresh, 'External function dbo.f was saved'))
    $cancelled = $false
    try { [void]$pending.GetAwaiter().GetResult() }
    catch {
        $failure = $_.Exception
        while ($null -ne $failure.InnerException) { $failure = $failure.InnerException }
        if ($failure -isnot [OperationCanceledException]) { throw }
        $cancelled = $true
    }
    if (!$cancelled) { throw 'Shutdown cancellation must not be relabeled as a mutation failure.' }
    Write-Host 'PASS post-DDL refresh distinguishes saved/deleted state, retains the failure cause, runs once and preserves cancellation.'

    $menus = [Activator]::CreateInstance($assembly.GetType('FabricExternalFunctions.Ssms.NodeContextMenus', $true), $true)
    try {
        $previous = $null
        $closed = [System.Windows.Forms.ToolStripDropDown].GetMethod('OnClosed', [Reflection.BindingFlags]'Instance,NonPublic')
        $closedArgs = New-Object System.Windows.Forms.ToolStripDropDownClosedEventArgs -ArgumentList (
            [System.Windows.Forms.ToolStripDropDownCloseReason]::ItemClicked)
        foreach ($label in @('Properties', 'Script definition', 'Edit Properties')) {
            $menu = $menus.Create()
            if ($null -ne $previous -and !$previous.IsDisposed) { throw 'Replaced menu was not disposed.' }
            [void]$menu.Items.Add($label)
            $menu.Items[0].PerformClick()
            [void]$closed.Invoke($menu.PSObject.BaseObject, [object[]]@($closedArgs.PSObject.BaseObject))
            if ($menu.IsDisposed) { throw 'Menu was disposed while click/close dispatch could still access it.' }
            # WinForms accesses the menu handle after Closed when resuming modal click dispatch.
            [void]$menu.Handle
            $previous = $menu
        }
        Write-Host 'PASS production menus survive click/Closed dispatch; replaced menus are disposed.'
        $calls = New-Object 'System.Collections.Generic.List[string]'
        $menu = $menus.CreateFolderMenu([Action]{ $calls.Add('new') }, [Action]{ $calls.Add('template') },
            [Action]{ $calls.Add('refresh') })
        if ($menu.Items.Count -ne 3 -or $menu.Items[0].Text -ne 'New External Function...' -or
            $menu.Items[1].Text -ne 'New (from template)...' -or $menu.Items[2].Text -ne 'Refresh') {
            throw 'Folder menus must contain New External Function, New (from template) and Refresh in that order.'
        }
        $menu.Items[0].PerformClick(); $menu.Items[1].PerformClick(); $menu.Items[2].PerformClick()
        if (($calls -join ',') -ne 'new,template,refresh') { throw 'Folder menu actions were not correctly wired.' }
        $calls.Clear()
        $factory = $menus.GetType().GetMethod('CreateFunctionMenu')
        $menuParameters = [Array]::CreateInstance($parameterType, 1)
        $menuParameters.SetValue($parameterType.GetConstructors()[0].Invoke([object[]]@(0, '', 'varchar(max)')), 0)
        $menuMetadata = $metadataType.GetConstructors()[0].Invoke(
            [object[]]@(4, 'dbo', 'json_function', [DateTime]::MinValue, $menuParameters, $null, $null))
        $scriptCallback = [Management.Automation.LanguagePrimitives]::ConvertTo(
            [scriptblock]{ param($kind) $calls.Add("script:$kind") }, $factory.GetParameters()[1].ParameterType)
        $menu = $factory.Invoke($menus, [object[]]@($menuMetadata.PSObject.BaseObject, $scriptCallback,
            [Action]{ $calls.Add('drop') }, [Action]{ $calls.Add('properties') }, [Action]{ $calls.Add('edit') }))
        if ($menu.Items.Count -ne 5 -or $menu.Items[0].Text -ne 'Script External Function as' -or
            $menu.Items[1] -isnot [Windows.Forms.ToolStripSeparator] -or
            $menu.Items[2].Text -ne 'Drop' -or $menu.Items[3].Text -ne 'Properties' -or $menu.Items[4].Text -ne 'Edit') {
            throw 'Function menu must contain Script External Function as, separator, Drop, Properties and Edit, without a template action.'
        }
        $scripts = $menu.Items[0].DropDownItems
        if ($scripts.Count -ne 8 -or !$scripts[5].Enabled -or $scripts[3] -isnot [Windows.Forms.ToolStripSeparator] -or
            ((($scripts | Where-Object { $_ -is [Windows.Forms.ToolStripMenuItem] } | ForEach-Object Text) -join ',') -ne
                'CREATE,CREATE OR ALTER,DROP,SELECT,SELECT FROM,Declare Variable,SET Variable')) {
            throw 'The script submenu must offer CREATE/CREATE OR ALTER/DROP, a separator, SELECT, SELECT FROM, Declare Variable and SET Variable.'
        }
        foreach ($item in $scripts | Where-Object { $_ -is [Windows.Forms.ToolStripMenuItem] }) { $item.PerformClick() }
        foreach ($index in @(2, 3, 4)) { $menu.Items[$index].PerformClick() }
        if (($calls -join ',') -ne 'script:Create,script:CreateOrAlter,script:Drop,script:Select,script:SelectFrom,script:DeclareVariable,script:SetVariable,drop,properties,edit') {
            throw 'The script-only actions must remain distinct from Drop, Properties and Edit.'
        }
        Write-Host 'PASS native-style function/folder menu order and distinct script versus mutation callbacks, with no Rename or auth-testing item.'
        foreach ($returnType in @('varchar(max)', 'VARCHAR(MAX)', 'varchar(100)', 'nvarchar(max)', 'float',
            'decimal(20,4)', '[types].[varchar(max)]', 'unavailable', $null)) {
            foreach ($definition in @($null, 'CREATE FUNCTION dbo.f AS EXTERNAL FUNCTION item.remote;',
                'CREATE FUNCTION dbo.f RETURNS varchar(max) AS EXTERNAL FUNCTION item.remote;',
                'CREATE FUNCTION dbo.f RETURNS float AS EXTERNAL FUNCTION item.remote;')) {
                $menuParameters = [Array]::CreateInstance($parameterType, $(if ($null -eq $returnType) { 0 } else { 1 }))
                if ($null -ne $returnType) {
                    $menuParameters.SetValue($parameterType.GetConstructors()[0].Invoke([object[]]@(0, '', $returnType)), 0)
                }
                $menuMetadata = $metadataType.GetConstructors()[0].Invoke(
                    [object[]]@(4, 'dbo', 'f', [DateTime]::MinValue, $menuParameters, $definition, $null))
                $calls.Clear()
                $menu = $factory.Invoke($menus, [object[]]@($menuMetadata.PSObject.BaseObject, $scriptCallback,
                    [Action]{ $calls.Add('drop') }, [Action]{ $calls.Add('properties') }, [Action]{ $calls.Add('edit') }))
                $scripts = $menu.Items[0].DropDownItems
                $selectFrom = $scripts[5]
                $eligible = $returnType -ieq 'varchar(max)'
                if ($selectFrom.Text -ne 'SELECT FROM' -or $selectFrom.Enabled -ne $eligible -or
                    $selectFrom.ToolTipText -notmatch 'varchar\(max\)' -or !$scripts[4].Enabled) {
                    throw 'Only effective varchar(max) returns may enable SELECT FROM; ordinary SELECT remains unchanged.'
                }
                $selectFrom.PerformClick()
                $expected = if ($eligible) { 'script:SelectFrom' } else { '' }
                if (($calls -join ',') -ne $expected) {
                    throw 'Disabled SELECT FROM must not invoke scripting; eligible SELECT FROM must retain its callback.'
                }
            }
        }
        Write-Host 'PASS SELECT FROM menu gating uses the effective catalog varchar(max) return type for inferred and explicit definitions.'
    } finally { $menus.Dispose() }
    if (!$menu.IsDisposed) { throw 'The last context menu was not disposed with its owner.' }
    exit 0
} catch {
    [Console]::Error.WriteLine($_.Exception.ToString())
    exit 1
}
