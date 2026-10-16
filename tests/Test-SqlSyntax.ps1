[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$CoreAssembly,
    [string]$SsmsIdeDir = 'C:\Program Files\Microsoft SQL Server Management Studio 22\Release\Common7\IDE'
)
$ErrorActionPreference = 'Stop'
try {
    if ($PSVersionTable.PSEdition -ne 'Desktop') { throw 'Offline SQL syntax checks require Windows PowerShell (.NET Framework).' }
    [void][Reflection.Assembly]::LoadFrom((Resolve-Path -LiteralPath $CoreAssembly).Path)
    [void][Reflection.Assembly]::LoadFrom((Join-Path $SsmsIdeDir 'Extensions\Application\Microsoft.SqlServer.TransactSql.ScriptDom.dll'))
    $parser = New-Object Microsoft.SqlServer.TransactSql.ScriptDom.TSql160Parser -ArgumentList $true
    $script:checked = 0
    function CheckSyntax([string]$sql) {
        $reader = [IO.StringReader]::new($sql)
        $errors = $null
        try { [void]$parser.Parse($reader, [ref]$errors) } finally { $reader.Dispose() }
        if ($errors.Count -ne 0) {
            throw "Generated SQL does not parse: $sql`r`n$(($errors | ForEach-Object Message) -join '; ')"
        }
        $script:checked++
    }
    $names = foreach ($token in [Enum]::GetNames([Microsoft.SqlServer.TransactSql.ScriptDom.TSqlTokenType])) {
        $token
        [regex]::Replace($token, '([a-z])([A-Z])', '$1_$2')
    }
    foreach ($name in $names | Sort-Object -Unique) {
        $callee = [FabricExternalFunctions.Core.SqlText]::CallName($name, $name, $name)
        CheckSyntax "SELECT $callee(0);"
    }
    foreach ($type in @('bigint', 'bit', 'decimal(18,2)', 'float', 'varchar(max)', 'nvarchar(40)', 'date',
        'datetime2(3)', 'datetimeoffset(7)', 'time(0)', 'varbinary(max)', 'uniqueidentifier', 'json', 'sql_variant')) {
        $parameters = [FabricExternalFunctions.Core.ParameterMetadata[]]@(
            [FabricExternalFunctions.Core.ParameterMetadata]::new(0, '', 'varchar(max)'),
            [FabricExternalFunctions.Core.ParameterMetadata]::new(1, 'input', $type))
        $metadata = [FabricExternalFunctions.Core.FunctionMetadata]::new(
            42, 'schema.with ] spaces', 'select', [DateTime]::MinValue, $parameters, $null, $null)
        foreach ($kind in @('Select', 'SelectFrom', 'DeclareVariable', 'SetVariable')) {
            CheckSyntax $metadata.Script([FabricExternalFunctions.Core.FunctionScriptKind]$kind, 'DW.prod]')
        }
    }
    foreach ($identity in @(@('dbo', 'f'), @('schema.with ] spaces', "f'name"), @('select', 'stoplist'))) {
        $metadata = [FabricExternalFunctions.Core.FunctionMetadata]::new(
            42, $identity[0], $identity[1], [DateTime]::MinValue,
            [FabricExternalFunctions.Core.ParameterMetadata[]]@(), $null, $null)
        $drop = $metadata.Script([FabricExternalFunctions.Core.FunctionScriptKind]::Drop, 'DW')
        if ($drop -cne ('DROP FUNCTION IF EXISTS ' +
            [FabricExternalFunctions.Core.SqlText]::Qualified($identity[0], $identity[1]) + ';') -or
            $drop -cne [FabricExternalFunctions.Core.ProxyDeclaration]::ScriptDrop($metadata)) {
            throw 'DROP scripting and execution must share one independently quoted IF EXISTS statement.'
        }
        CheckSyntax $drop
        $replacement = [FabricExternalFunctions.Core.ProxyDeclaration]::new(
            'new schema', ($identity[1] + '_new'), 'set', 'remote', 'varchar(max)')
        foreach ($retain in @($false, $true)) {
            CheckSyntax $replacement.ScriptChange($metadata, $retain)
        }
    }
    Write-Host "PASS $script:checked generated calls, invocation, DROP and replacement/clone scripts parse with Microsoft's offline SQL parser; no SQL executed."
    exit 0
} catch {
    [Console]::Error.WriteLine($_.Exception.ToString())
    exit 1
}
