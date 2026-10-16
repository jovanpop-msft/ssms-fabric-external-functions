[CmdletBinding()]
param(
    [string]$SsmsIdeDir = 'C:\Program Files\Microsoft SQL Server Management Studio 22\Release\Common7\IDE',
    [string]$VsixEngineDir = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\resources\app\ServiceHub\Services\Microsoft.VisualStudio.Setup.Service\VsixInstaller",
    [switch]$SkipPackage
)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
if (!(Test-Path -LiteralPath (Join-Path $SsmsIdeDir 'SqlWorkbench.Interfaces.dll')) -or
    !(Test-Path -LiteralPath (Join-Path $SsmsIdeDir 'SSMS.exe'))) {
    throw 'SsmsIdeDir must point to separately installed SSMS 22 Common7\IDE.'
}
if ((Get-Item -LiteralPath (Join-Path $SsmsIdeDir 'SSMS.exe')).VersionInfo.FileMajorPart -ne 22) {
    throw 'Only separately installed SSMS 22 is a supported build reference host.'
}

Push-Location $root
try {
    & dotnet run --project '.\tests\FabricExternalFunctions.Tests' -c Release -p:TargetFrameworks=net10.0 --verbosity quiet
    if ($LASTEXITCODE -ne 0) { throw 'Domain tests failed.' }
    & dotnet build '.\src\FabricExternalFunctions.Ssms' -c Release "-p:SsmsIdeDir=$SsmsIdeDir" --verbosity quiet
    if ($LASTEXITCODE -ne 0) { throw 'SSMS package build failed.' }

    $source = Join-Path $root 'src\FabricExternalFunctions.Ssms'
    $output = Join-Path $source 'bin\Release\net48'
    $windowsPowerShell = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
    & $windowsPowerShell -NoProfile -NonInteractive -File (Join-Path $root 'tests\Test-NativeHierarchy.ps1') `
        -PackageAssembly (Join-Path $output 'FabricExternalFunctions.Ssms.dll') -SsmsIdeDir $SsmsIdeDir
    if ($LASTEXITCODE -ne 0) { throw 'Native hierarchy regression failed.' }
    & $windowsPowerShell -NoProfile -NonInteractive -File (Join-Path $root 'tests\Test-Dialogs.ps1') `
        -PackageAssembly (Join-Path $output 'FabricExternalFunctions.Ssms.dll') -SsmsIdeDir $SsmsIdeDir
    if ($LASTEXITCODE -ne 0) { throw 'Dialog/drop-provider regression failed.' }
    & $windowsPowerShell -NoProfile -NonInteractive -File (Join-Path $root 'tests\Test-DropDestination.ps1') `
        -PackageAssembly (Join-Path $output 'FabricExternalFunctions.Ssms.dll') -SsmsIdeDir $SsmsIdeDir
    if ($LASTEXITCODE -ne 0) { throw 'Drop destination regression failed.' }
    & $windowsPowerShell -NoProfile -NonInteractive -File (Join-Path $root 'tests\Test-SqlSyntax.ps1') `
        -CoreAssembly (Join-Path $output 'FabricExternalFunctions.Core.dll') -SsmsIdeDir $SsmsIdeDir
    if ($LASTEXITCODE -ne 0) { throw 'Offline SQL syntax regression failed.' }
    if ($SkipPackage) {
        Write-Host 'Source compilation and local tests passed. No VSIX created or changed.'
        return
    }
    $files = [ordered]@{
        'LICENSE' = Join-Path $root 'LICENSE'
        'THIRD-PARTY-NOTICES.txt' = Join-Path $root 'THIRD-PARTY-NOTICES.txt'
        'FabricExternalFunctions.pkgdef' = Join-Path $source 'FabricExternalFunctions.pkgdef'
        'FabricExternalFunctions.Ssms.dll' = Join-Path $output 'FabricExternalFunctions.Ssms.dll'
        'FabricExternalFunctions.Core.dll' = Join-Path $output 'FabricExternalFunctions.Core.dll'
    }
    $artifactDir = Join-Path $root 'artifacts'
    New-Item -ItemType Directory -Path $artifactDir -Force | Out-Null
    $artifact = Join-Path $artifactDir 'FabricExternalFunctions.vsix'
    $toolRoot = & dotnet msbuild '.\src\FabricExternalFunctions.Ssms' -nologo -getProperty:PkgMicrosoft_VSSDK_BuildTools
    if ($LASTEXITCODE -ne 0) { throw 'Could not resolve the official VSSDK packaging tool.' }
    $vsixUtil = Join-Path ($toolRoot | Out-String).Trim() 'tools\VSSDK\bin\VsixUtil.exe'
    if (!(Test-Path -LiteralPath $vsixUtil)) { throw "VSSDK packaging tool missing: $vsixUtil" }
    $fileList = @{
        files = @(
            foreach ($item in $files.GetEnumerator()) {
                if (!(Test-Path -LiteralPath $item.Value)) { throw "Missing package input: $($item.Value)" }
                @{ path = $item.Value; targetPath = $item.Key }
            }
        )
    }
    $fileListPath = Join-Path $source 'obj\Release\net48\vsix-files.json'
    $fileList | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $fileListPath -Encoding utf8
    & $vsixUtil package -sourceManifest (Join-Path $source 'source.extension.vsixmanifest') `
        -outputPath $artifact -files $fileListPath -compressionLevel Normal -is64BitBuild `
        -vsixSchemaPath (Join-Path ($toolRoot | Out-String).Trim() 'tools\VSSDK\schemas')
    if ($LASTEXITCODE -ne 0) { throw 'Official VSSDK VSIX v3 packaging failed.' }
    & (Join-Path $PSScriptRoot 'Test-Package.ps1') -Path $artifact -VsixEngineDir $VsixEngineDir
    & (Join-Path $root 'tests\Test-PackageRegression.ps1') -Path $artifact -VsixEngineDir $VsixEngineDir
    if ($LASTEXITCODE -ne 0) { throw 'Package regressions failed.' }
    Get-FileHash -LiteralPath $artifact -Algorithm SHA256 | Format-List
    Write-Host "Built unsigned development VSIX: $artifact"
    Write-Host 'Not installed or launched. Interactive acceptance MUST use your separately installed SSMS 22.'
} finally { Pop-Location }
