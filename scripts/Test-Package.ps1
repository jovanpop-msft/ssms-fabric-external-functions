[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Path,
    [string]$VsixEngineDir = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\resources\app\ServiceHub\Services\Microsoft.VisualStudio.Setup.Service\VsixInstaller"
)
$ErrorActionPreference = 'Stop'
$zip = [IO.Compression.ZipFile]::OpenRead((Resolve-Path -LiteralPath $Path).Path)
try {
    $expected = @('extension.vsixmanifest', '[Content_Types].xml', 'manifest.json', 'catalog.json', 'FabricExternalFunctions.pkgdef',
        'FabricExternalFunctions.Ssms.dll', 'FabricExternalFunctions.Core.dll', 'LICENSE', 'THIRD-PARTY-NOTICES.txt')
    $actual = @($zip.Entries | ForEach-Object FullName)
    if (@(Compare-Object ($expected | Sort-Object) ($actual | Sort-Object)).Count -ne 0) {
        throw 'Unexpected package content. Host DLLs must not be redistributed.'
    }
    $licenseReader = [IO.StreamReader]::new($zip.GetEntry('LICENSE').Open())
    try { $license = $licenseReader.ReadToEnd() } finally { $licenseReader.Dispose() }
    $expectedLicense = Get-Content -Raw -LiteralPath (Join-Path (Split-Path $PSScriptRoot -Parent) 'LICENSE')
    if ($license.Replace("`r`n", "`n").Trim() -cne $expectedLicense.Replace("`r`n", "`n").Trim()) {
        throw 'Package license does not match the source LICENSE.'
    }
    $reader = [IO.StreamReader]::new($zip.GetEntry('extension.vsixmanifest').Open())
    try { [xml]$manifest = $reader.ReadToEnd() } finally { $reader.Dispose() }
    $ns = [Xml.XmlNamespaceManager]::new($manifest.NameTable)
    $ns.AddNamespace('v', 'http://schemas.microsoft.com/developer/vsx-schema/2011')
    if ($manifest.SelectNodes('//v:Prerequisites/v:Prerequisite[@Id="Microsoft.VisualStudio.Component.CoreEditor"]', $ns).Count -ne 1) {
        throw 'VSIX v3 must declare its core editor prerequisite and include generated setup metadata.'
    }
    $prerequisite = $manifest.SelectSingleNode('//v:Prerequisite[@Id="Microsoft.VisualStudio.Component.CoreEditor"]', $ns)
    if ($prerequisite.Version -ne '[18.0,)') { throw 'Core editor prerequisite must allow the baseline editor used by SSMS 22.' }
    $targets = $manifest.SelectNodes('//v:InstallationTarget', $ns)
    if ($targets.Count -ne 1 -or $targets[0].Id -ne 'Microsoft.VisualStudio.Ssms' -or
        $targets[0].Version -ne '[22.0,23.0)' -or $targets[0].ProductArchitecture -ne 'amd64') {
        throw 'Package must exclusively target separately installed SSMS 22 x64.'
    }
    $assets = $manifest.SelectNodes('//v:Asset', $ns)
    if ($assets.Count -ne 2 -or
        $manifest.SelectNodes('//v:Asset[@Type="Microsoft.VisualStudio.VsPackage"][@Path="FabricExternalFunctions.pkgdef"]', $ns).Count -ne 1 -or
        $manifest.SelectNodes('//v:Asset[@Type="Microsoft.VisualStudio.MefComponent"][@Path="FabricExternalFunctions.Ssms.dll"]', $ns).Count -ne 1) {
        throw 'Both the package registration and the editor MEF component must be declared exactly once.'
    }
    foreach ($asset in $assets) {
        if (!$zip.GetEntry($asset.Path)) { throw "Missing declared VSIX asset: $($asset.Path)" }
    }
    $setupReader = [IO.StreamReader]::new($zip.GetEntry('manifest.json').Open())
    try { $setupManifest = $setupReader.ReadToEnd() | ConvertFrom-Json } finally { $setupReader.Dispose() }
    $catalogReader = [IO.StreamReader]::new($zip.GetEntry('catalog.json').Open())
    try { $catalog = $catalogReader.ReadToEnd() | ConvertFrom-Json } finally { $catalogReader.Dispose() }
    $identity = $manifest.SelectSingleNode('//v:Identity', $ns)
    $registrationReader = [IO.StreamReader]::new($zip.GetEntry('FabricExternalFunctions.pkgdef').Open())
    try { $registration = $registrationReader.ReadToEnd() } finally { $registrationReader.Dispose() }
    $packageSection = [regex]::Match($registration,
        '(?ms)^\[\$RootKey\$\\Packages\\\{bde8af5e-30f5-46b9-85a6-047aac74a170\}\]\r?\n(?<values>.*?)(?=^\[|\z)')
    if (!$packageSection.Success -or $packageSection.Groups['values'].Value -notmatch
        '(?m)^"AllowsBackgroundLoad"=dword:00000001[ \t]*\r?$' -or $registration.Contains('"AllowsBackgroundLoading"')) {
        throw 'Background package registration must declare AllowsBackgroundLoad (not AllowsBackgroundLoading) in the package section.'
    }
    $autoloadSection = [regex]::Match($registration,
        '(?ms)^\[\$RootKey\$\\AutoLoadPackages\\\{d114938f-591c-46cf-a785-500a82d97410\}\]\r?\n(?<values>.*?)(?=^\[|\z)')
    if (!$autoloadSection.Success -or $autoloadSection.Groups['values'].Value -notmatch
        '(?m)^"\{bde8af5e-30f5-46b9-85a6-047aac74a170\}"=dword:00000002[ \t]*\r?$') {
        throw 'Background autoload must use flag 2 for the Object Explorer UI context.'
    }
    $version = [version]$identity.Version
    $assemblyVersion = '{0}.{1}.{2}.{3}' -f $version.Major, $version.Minor, [Math]::Max(0, $version.Build), [Math]::Max(0, $version.Revision)
    if (!$packageSection.Groups['values'].Value.Contains("Version=$assemblyVersion,")) { throw 'Package registration assembly version does not match the extension version.' }
    if ($setupManifest.id -ne $identity.Id -or $setupManifest.vsixId -ne $identity.Id -or
        $setupManifest.version -ne $identity.Version -or $setupManifest.type -ne 'Vsix') {
        throw 'VSIX v3 setup identity must match the extension manifest.'
    }
    $setupFiles = @($setupManifest.files | ForEach-Object { $_.fileName.TrimStart('/') })
    $expectedSetupFiles = @('extension.vsixmanifest', 'FabricExternalFunctions.pkgdef',
        'FabricExternalFunctions.Ssms.dll', 'FabricExternalFunctions.Core.dll', 'LICENSE', 'THIRD-PARTY-NOTICES.txt')
    if (@(Compare-Object ($expectedSetupFiles | Sort-Object) ($setupFiles | Sort-Object)).Count -ne 0) {
        throw 'VSIX v3 setup payload does not match the allowed extension files.'
    }
    $catalogPackage = @($catalog.packages | Where-Object { $_.id -eq $identity.Id })
    if ($catalogPackage.Count -ne 1 -or $catalogPackage[0].type -ne 'Vsix' -or
        $catalogPackage[0].version -ne $identity.Version) { throw 'VSIX v3 catalog identity mismatch.' }
    $typesReader = [IO.StreamReader]::new($zip.GetEntry('[Content_Types].xml').Open())
    try { [xml]$null = $typesReader.ReadToEnd() } finally { $typesReader.Dispose() }
    Write-Host 'PASS VSIX v3 setup catalog, SSMS-only target, MEF assets, background package/autoload registration, and XML.'
} finally { $zip.Dispose() }

$windowsPowerShell = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
& $windowsPowerShell -NoProfile -NonInteractive -File (Join-Path $PSScriptRoot 'Test-VsixReader.ps1') `
    -Path (Resolve-Path -LiteralPath $Path).Path -VsixEngineDir $VsixEngineDir
if ($LASTEXITCODE -ne 0) { throw 'The installed Microsoft VSIX reader rejected the package. See the reader error above.' }
