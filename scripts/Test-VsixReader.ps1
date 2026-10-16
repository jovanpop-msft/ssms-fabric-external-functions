[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Path,
    [string]$VsixEngineDir = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\resources\app\ServiceHub\Services\Microsoft.VisualStudio.Setup.Service\VsixInstaller"
)
$ErrorActionPreference = 'Stop'
try {
    if ($PSVersionTable.PSEdition -ne 'Desktop') {
        throw 'This check must run in Windows PowerShell (.NET Framework), not pwsh.'
    }
    $engine = Join-Path $VsixEngineDir 'Microsoft.VisualStudio.ExtensionEngine.dll'
    if (!(Test-Path -LiteralPath $engine)) {
        throw "Installed VSIX reader missing: $engine. Supply -VsixEngineDir for your VSIX installer."
    }
    $assembly = [Reflection.Assembly]::LoadFrom($engine)
    $type = $assembly.GetType('Microsoft.VisualStudio.ExtensionManager.InstallableExtensionImpl', $true)
    $constructor = $type.GetConstructor(
        [Reflection.BindingFlags]'Instance,Public,NonPublic', $null,
        [type[]]@([string], [Globalization.CultureInfo]), $null)
    if ($null -eq $constructor) { throw 'The installed VSIX reader contract is unsupported.' }
    # Read package metadata only; never invoke installation or product discovery.
    $extension = $constructor.Invoke([object[]]@(
        (Resolve-Path -LiteralPath $Path).Path, [Globalization.CultureInfo]::GetCultureInfo('en-US')))
    if ($null -eq $extension) { throw 'The VSIX reader returned no package.' }
    if ($extension.PackageType.ToString() -ne 'VsixV3' -or $extension.IsExtensionManifestVersionV2) {
        throw 'Legacy VSIX format triggers the installer compatibility warning. Generate VSIX v3 setup metadata.'
    }
    Write-Host 'PASS installed Microsoft VSIX reader accepted VSIX v3 without the legacy compatibility-warning condition (no installation).'
    exit 0
} catch {
    $failure = $_.Exception
    while ($null -ne $failure) {
        [Console]::Error.WriteLine($failure.GetType().FullName + ': ' + $failure.Message)
        $failure = $failure.InnerException
    }
    exit 1
}
