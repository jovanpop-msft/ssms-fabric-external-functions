[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Path,
    [string]$VsixEngineDir = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\resources\app\ServiceHub\Services\Microsoft.VisualStudio.Setup.Service\VsixInstaller"
)
$ErrorActionPreference = 'Stop'
$scripts = Join-Path (Split-Path $PSScriptRoot -Parent) 'scripts'
foreach ($mode in @('InvalidV3', 'LegacyWarning', 'BackgroundFlagTypo', 'SyncAutoloadFlag', 'MissingLicense', 'EmptyLicense')) {
    $fixture = Join-Path ([IO.Path]::GetTempPath()) ("FabricExternalFunctions.invalid." + [guid]::NewGuid().ToString('N') + '.vsix')
    try {
        [IO.File]::Copy((Resolve-Path -LiteralPath $Path).Path, $fixture)
        $zip = [IO.Compression.ZipFile]::Open($fixture, [IO.Compression.ZipArchiveMode]::Update)
        try {
            if ($mode -in @('MissingLicense', 'EmptyLicense')) {
                $zip.GetEntry('LICENSE').Delete()
                if ($mode -eq 'EmptyLicense') { [void]$zip.CreateEntry('LICENSE') }
            }
            if ($mode -in @('InvalidV3', 'LegacyWarning')) {
                $zip.GetEntry('manifest.json').Delete()
                $zip.GetEntry('catalog.json').Delete()
            }
            if ($mode -eq 'LegacyWarning') {
                $entry = $zip.GetEntry('extension.vsixmanifest')
                $reader = [IO.StreamReader]::new($entry.Open())
                try { [xml]$manifest = $reader.ReadToEnd() } finally { $reader.Dispose() }
                $prerequisites = $manifest.DocumentElement.ChildNodes | Where-Object LocalName -eq 'Prerequisites'
                $manifest.DocumentElement.RemoveChild($prerequisites) | Out-Null
                $entry.Delete()
                $stream = $zip.CreateEntry('extension.vsixmanifest').Open()
                try { $manifest.Save($stream) } finally { $stream.Dispose() }
            }
            if ($mode -in @('BackgroundFlagTypo', 'SyncAutoloadFlag')) {
                $entry = $zip.GetEntry('FabricExternalFunctions.pkgdef')
                $reader = [IO.StreamReader]::new($entry.Open())
                try { $registration = $reader.ReadToEnd() } finally { $reader.Dispose() }
                $registration = if ($mode -eq 'BackgroundFlagTypo') {
                    $registration.Replace('"AllowsBackgroundLoad"', '"AllowsBackgroundLoading"')
                } else { $registration.Replace('dword:00000002', 'dword:00000000') }
                $entry.Delete()
                $writer = [IO.StreamWriter]::new($zip.CreateEntry('FabricExternalFunctions.pkgdef').Open())
                try { $writer.Write($registration) } finally { $writer.Dispose() }
            }
        } finally { $zip.Dispose() }

        if ($mode -in @('InvalidV3', 'LegacyWarning')) {
            $expected = if ($mode -eq 'InvalidV3') { 'InvalidExtensionPackageException' }
                else { 'Legacy VSIX format triggers the installer compatibility warning' }
            $windowsPowerShell = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
            $output = & $windowsPowerShell -NoProfile -NonInteractive -File (Join-Path $scripts 'Test-VsixReader.ps1') `
                -Path $fixture -VsixEngineDir $VsixEngineDir 2>&1
            if ($LASTEXITCODE -eq 0 -or ($output -join "`n") -notmatch [regex]::Escape($expected)) {
                throw "The VSIX reader did not reproduce $mode correctly: $output"
            }
        }
        $expectedRejection = switch ($mode) {
            'BackgroundFlagTypo' { 'Background package registration must declare AllowsBackgroundLoad*' }
            'SyncAutoloadFlag' { 'Background autoload must use flag 2*' }
            'EmptyLicense' { 'Package license does not match the source LICENSE.' }
            default { 'Unexpected package content.*' }
        }
        $rejected = $false
        try { & (Join-Path $scripts 'Test-Package.ps1') -Path $fixture -VsixEngineDir $VsixEngineDir }
        catch {
            if ($_.Exception.Message -notlike $expectedRejection) { throw }
            $rejected = $true
        }
        if (!$rejected) { throw "The build gate failed to reject $mode." }
        Write-Host "PASS VSIX regression: $mode is rejected by the build gate."
    } finally {
        if ([IO.File]::Exists($fixture)) { [IO.File]::Delete($fixture) }
    }
}
# Expected rejected fixtures must not leak their native exit code to successful builds.
exit 0
