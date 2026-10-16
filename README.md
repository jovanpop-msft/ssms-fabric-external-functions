# Fabric External Functions for SSMS

Browse and manage Microsoft Fabric Warehouse external functions directly in SQL
Server Management Studio (SSMS). The extension adds an **External Functions**
folder to Object Explorer, with dialogs for creating and editing functions,
definition and invocation scripts, and drag-and-drop calls into the SQL editor.

It manages **T-SQL proxy functions** that reference published Fabric User Data
Functions. It does not create, publish or edit the underlying Python code.

**Development preview:** targets SSMS 22 x64. Third-party SSMS extensions are not
supported by Microsoft. The VSIX is unsigned; compatibility and live Warehouse
acceptance remain separate from automated build checks.

## How it works

### Browse functions and parameters

Connect Object Explorer to a Fabric Warehouse and expand a user schema's
**External Functions** folder. Each function shows its effective SQL return type;
expand it to inspect input parameters in declaration order and their SQL types.
Classic Object Explorer view is also supported.

![Object Explorer showing external functions, return types and expanded input parameters](images/Object-Explorer.png)

### Create a new external function

Right-click the folder and choose **New External Function...**. Select an existing
warehouse schema and enter the SQL name, published Fabric function set and Fabric
function name. Leave the return type as **(inferred)** to use the Python definition,
or specify an explicit SQL type.

Choose **Create** to execute directly, or **Preview SQL** to review before applying.
Missing or invalid values show errors beneath the relevant fields. Canceling
preview returns to the editor without losing your values.

![New External Function dialog with schema, name, Fabric binding, return type, Create and Preview SQL](images/New-Function.png)

Prefer writing SQL yourself? **New (from template)...** opens an editable CREATE
template in a new query without executing it.

### Edit a binding or create a clone

Right-click a function and choose **Edit** to update its Fabric binding or return
type. Use **Save** to execute directly or **Preview SQL** to review the change first.

**Changing a name or schema replaces the original by default.** The extension
drops the old proxy and creates the new one. Permissions are not preserved and
callers are not redirected. Replacement is not atomic: a failed CREATE can leave
the original deleted. Check **Retain existing function (create a clone)** to keep
the original instead.

![Edit External Function dialog with schema, name, Fabric binding, return type, Save and Preview SQL](images/Edit-Function.png)

### Script, use and manage functions

Choose **Script External Function as** to open CREATE, CREATE OR ALTER, DROP,
SELECT, SELECT FROM or variable-assignment scripts in a new query using the
selected connection. Generating a script never executes it.

![External function context menu with definition and invocation scripting options](images/Context-Menu.png)

You can also drag a function into a SQL editor to insert a warehouse-qualified
call with editable, type-based argument placeholders. Replace those placeholders
with valid values and verify the query's target connection before running it.
Dragging inserts text only; it does not execute SQL or change the query connection.

Use **Properties** to inspect the binding, parameters, return type and stored SQL.
**Drop** asks for confirmation and deletes only the warehouse proxy, never the
referenced Fabric function.

See the [user guide](docs/usage.md) for scripting options, metadata limitations and
safe error recovery.

## Requirements

- Windows x64 with separately installed SSMS 22.x.
- A Fabric Warehouse connection and Microsoft Entra access to its metadata.
- For mutations, the necessary warehouse permissions and access to the published
  Fabric User Data Functions item.

Catalog access and direct mutations use a separate Entra interactive sign-in.
New query windows use SSMS's selected connection context. The extension does not
extract passwords or access tokens from SSMS.

## Install

Get the unsigned development preview from the
[0.1.33 release](https://github.com/jovanpop-msft/ssms-fabric-external-functions/releases/tag/v0.1.33).

1. [Download FabricExternalFunctions.vsix](https://github.com/jovanpop-msft/ssms-fabric-external-functions/releases/download/v0.1.33/FabricExternalFunctions.vsix).
2. Save your work and close SSMS.
3. Double-click `FabricExternalFunctions.vsix` to open the graphical VSIX Installer and select
   **SQL Server Management Studio 22**, not Visual Studio.
4. Reopen SSMS, connect Object Explorer to a Fabric Warehouse, and expand
   **your schema > External Functions**. In classic view, use
   **Programmability > Functions > External Functions**.

**No command is required to install an existing VSIX.** If double-click does not
open the installer or SSMS 22 is not offered, see the
[installation fallback](docs/usage.md#if-double-click-does-not-work). Build commands
are only for producing a package from source.

The manifest permits SSMS 22.x, not SSMS 21 or 23. That installation range is not
a claim that every 22.x build has been tested. Follow your organization's policy
for unsigned extensions; do not bypass installer compatibility warnings.

To build your own package, see [development](docs/development.md) for prerequisites,
offline checks and the manual acceptance checklist.

## Support, privacy and license

Report issues through this repository's Issues tab with the SSMS version and
sanitized reproduction steps. Do not upload credentials, connection strings,
warehouse data or unreviewed activity logs. Logs may contain server, database,
schema and function names; see [diagnostics](docs/usage.md#diagnostics-and-privacy).

Source code is licensed under [MIT](LICENSE). Fabric artwork attribution and its
license are in [THIRD-PARTY-NOTICES.txt](THIRD-PARTY-NOTICES.txt).
