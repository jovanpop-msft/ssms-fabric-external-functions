# Development

## Prerequisites

- Windows x64 and PowerShell 7 (`pwsh`).
- .NET 10 SDK and .NET Framework 4.8 reference assemblies.
- Separately installed SSMS 22 x64, providing reference-only host assemblies.
- For packaging, the installed Microsoft VSIX reader used by the validation
  scripts. Its default location is under the Visual Studio Installer directory;
  override it with `-VsixEngineDir` if necessary.

The host targets .NET Framework 4.8. The shared library also targets .NET 10 for
standalone domain tests. The VSSDK packaging tool is a private build dependency;
it is not part of the runtime payload. SSMS assemblies are not redistributed.
Packages include the source MIT license and the separate artwork notices.

## Build and test

From the repository root, compile and run offline checks without creating or
changing a VSIX:

```powershell
pwsh -File .\scripts\Build.ps1 -SkipPackage
```

After reviewing the changes and passing the checks, create an unsigned package:

```powershell
pwsh -File .\scripts\Build.ps1
```

The full command repeats the source checks, packages the extension, validates it
with Microsoft's installed VSIX reader and runs negative package fixtures.
Output is `artifacts\FabricExternalFunctions.vsix`; the command prints its SHA256.
It does not sign or install the package.

For a nondefault SSMS or VSIX reader installation:

```powershell
pwsh -File .\scripts\Build.ps1 -SkipPackage `
    -SsmsIdeDir 'D:\SSMS22\Release\Common7\IDE'

pwsh -File .\scripts\Build.ps1 `
    -SsmsIdeDir 'D:\SSMS22\Release\Common7\IDE' `
    -VsixEngineDir 'D:\VsixInstaller'
```

For domain-only checks:

```powershell
dotnet run --project .\tests\FabricExternalFunctions.Tests -c Release `
    -p:TargetFrameworks=net10.0 --verbosity quiet
```

Builds and automated tests must not install, launch, close or patch SSMS, connect
to a warehouse, execute DDL or extract credentials. Do not run concurrent domain
and host builds against the same shared-library intermediate directory.

## Code layout

| Path | Purpose |
|---|---|
| `src\FabricExternalFunctions.Core` | Catalog models, definition parser, quoting, call templates and mutation scripts |
| `src\FabricExternalFunctions.Ssms` | Package, Object Explorer integration, catalog reader, dialogs and editor drop handler |
| `tests\FabricExternalFunctions.Tests` | Standalone executable domain assertions |
| `tests\Test-*.ps1` | Native contracts, dialogs, editor destinations, offline SQL syntax and package regressions |
| `scripts` | Build, package validation and a manually executed read-only metadata probe |

Keep SSMS reflection isolated and capability-checked. Object Explorer and
connection integration depend on private host surfaces; an installable VSIX does
not establish compatibility with every SSMS build.

Endpoint eligibility uses the shared `datawarehouse.fabric.microsoft.com` suffix
at a dot or hyphen boundary. Other domains are excluded; this check does not grant
authentication or warehouse permissions.
Keep tests fictional; do not introduce real connection details or metadata dumps.

## Automated checks and manual acceptance

Offline checks cover parser/quoting behavior, mutation ordering and guards,
native-node/menu contracts, editor destinations, constructed dialog actions,
100/150/200% layout scaling, and generated SQL syntax using the installed parser.
They do not prove live authentication, SQL-engine concurrency, actual drag/Undo
or modal focus in SSMS.

Manual acceptance is **pending for the current preview**. Use a separately
installed SSMS 22 and a disposable Fabric Warehouse, never a Visual Studio
experimental instance. Record exact versions, date and results before claiming
compatibility; do not treat this checklist as completed evidence.

- [ ] Install, load, update and uninstall; confirm SSMS 21 and Visual Studio are
  not targeted and no compatibility warning is bypassed.
- [ ] Test schema-based and classic views, empty schemas, repeated refresh,
  schema isolation and reconnect/view changes during loading.
- [ ] Verify parameter order, return types, properties and complete, missing or
  truncated definitions against catalog metadata.
- [ ] Create and edit with direct actions and preview Apply; verify target
  connection, permissions, inline errors 31727/31728 and generic failures.
- [ ] Test renamed/schema-changed replacement and retained clones, destination
  conflicts, failed DROP, and failed CREATE after a successful DROP.
- [ ] Exercise concurrent source changes and uncertain/time-out results; inspect
  warehouse state before any retry. Do not claim atomicity from offline guards.
- [ ] Test preview Cancel/Escape/close, Apply, export, copy/save and failed export;
  verify preserved fields, exactly-once execution and restored modal focus.
- [ ] Test every scripting option with unrelated active queries and multiple
  warehouse connections; scripts must not execute on export.
- [ ] Drag zero- and multi-input functions with unusual identifiers into writable
  SQL editors; verify insertion position, placeholders, caret and Undo.
- [ ] Reject stale drags, read-only/non-SQL destinations and missing signatures.
  Check error reporting after insertion or after completed DDL/failed refresh.
- [ ] Test all dialogs at actual display scaling, minimum size and across monitors.
- [ ] Confirm Drop removes only the proxy and browsing never invokes a function.

`scripts\ProbeMetadata.sql` is a read-only query for manual diagnosis. It returns
object identities, definitions and signatures, which may be private. Do not commit
or publish its output.

## Publication hygiene

Keep generated output, activity logs, local editor configuration, environment
files and signing keys out of Git. The ignore rules are safeguards, not a secret
scanner. Review staged files and every branch/tag intended for publication.
Deleting a file in the current tree does not remove earlier committed content,
commit messages or author metadata. Review history separately or publish a
reviewed source-only snapshot to a new repository.

Do not upload local folders wholesale. Publish source through Git and release
packages separately; never bundle SSMS reference assemblies or private logs.
Preserve [LICENSE](../LICENSE) and [third-party notices](../THIRD-PARTY-NOTICES.txt)
when distributing source or binaries.

Use the repository's Issues tab for sanitized reports. See the
[privacy guidance](usage.md#diagnostics-and-privacy) before sharing diagnostics.
