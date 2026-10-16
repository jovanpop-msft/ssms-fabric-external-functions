[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$PackageAssembly,
    [string]$SsmsIdeDir = 'C:\Program Files\Microsoft SQL Server Management Studio 22\Release\Common7\IDE'
)
$ErrorActionPreference = 'Stop'
try {
    if ($PSVersionTable.PSEdition -ne 'Desktop') { throw 'Dialog checks require Windows PowerShell (.NET Framework).' }
    Add-Type -AssemblyName System.Windows.Forms, System.Drawing, System.ComponentModel.Composition
    [void][Reflection.Assembly]::LoadFrom((Join-Path $SsmsIdeDir 'PublicAssemblies\Microsoft.VisualStudio.Shell.Framework.dll'))
    [void][Reflection.Assembly]::LoadFrom((Join-Path $SsmsIdeDir 'Microsoft.Data.SqlClient.dll'))
    $editorDir = Join-Path $SsmsIdeDir 'CommonExtensions\Microsoft\Editor'
    foreach ($name in @('Microsoft.VisualStudio.CoreUtility', 'Microsoft.VisualStudio.Text.Data',
        'Microsoft.VisualStudio.Text.UI', 'Microsoft.VisualStudio.Text.UI.Wpf')) {
        [void][Reflection.Assembly]::LoadFrom((Join-Path $editorDir ($name + '.dll')))
    }
    $core = [Reflection.Assembly]::LoadFrom((Join-Path (Split-Path $PackageAssembly -Parent) 'FabricExternalFunctions.Core.dll'))
    $assembly = [Reflection.Assembly]::LoadFrom((Resolve-Path -LiteralPath $PackageAssembly).Path)
    $dialogs = $assembly.GetType('FabricExternalFunctions.Ssms.Dialogs', $true)
    $warehouse = $assembly.GetType('FabricExternalFunctions.Ssms.Warehouse', $true).GetConstructors()[0].Invoke(
        [object[]]@('example.datawarehouse.fabric.microsoft.com', 'FunctionDW'))

    Add-Type -ReferencedAssemblies @([System.Windows.Forms.Form].Assembly.Location,
        [System.Drawing.Font].Assembly.Location, [System.Linq.Enumerable].Assembly.Location,
        [Microsoft.Data.SqlClient.SqlException].Assembly.Location,
        (Join-Path (Split-Path $PackageAssembly -Parent) 'FabricExternalFunctions.Core.dll'),
        'System.Data.dll') -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows.Forms;
using FabricExternalFunctions.Core;
using Microsoft.Data.SqlClient;

public static class DialogLayoutChecks
{
    private static Control Named(Control root, string name) { return root.Controls.Find(name, true).Single(); }
    private static void Click(Control button)
    {
        typeof(Button).GetMethod("OnClick", BindingFlags.Instance | BindingFlags.NonPublic)
            .Invoke(button, new object[] { EventArgs.Empty });
    }
    private static bool LocalVisible(Control control)
    {
        return (bool)typeof(Control).GetMethod("GetState", BindingFlags.Instance | BindingFlags.NonPublic)
            .Invoke(control, new object[] { 2 });
    }
    private static SqlException SqlFailure(params int[] numbers)
    {
        var errors = (SqlErrorCollection)Activator.CreateInstance(typeof(SqlErrorCollection), true);
        var constructor = typeof(SqlError).GetConstructor(BindingFlags.Instance | BindingFlags.NonPublic, null,
            new[] { typeof(int), typeof(byte), typeof(byte), typeof(string), typeof(string), typeof(string),
                typeof(int), typeof(Exception) }, null);
        foreach (int number in numbers)
        {
            string message = number == 31728 ? "Function set 'missing_set' is not found." :
                number == 3621 ? "The statement has been terminated." :
                "Fabric function 'missing_function' is not found in 'missing_set' function set.";
            var error = constructor.Invoke(new object[] { number, (byte)1, (byte)16, "offline.test", message, "", 1, null });
            typeof(SqlErrorCollection).GetMethod("Add", BindingFlags.Instance | BindingFlags.NonPublic)
                .Invoke(errors, new[] { error });
        }
        return (SqlException)typeof(SqlException).GetMethod("CreateException", BindingFlags.Static | BindingFlags.NonPublic,
            null, new[] { typeof(SqlErrorCollection), typeof(string) }, null).Invoke(null, new object[] { errors, "22.0" });
    }
    private static void ConfigureExecution(Form form, Func<ProxyDeclaration, bool, Task> execute,
        List<Tuple<Exception, bool>> reports)
    {
        Named(form, "ApplyFunction");
        form.GetType().GetProperty("ExecuteAsync").SetValue(form, execute, null);
        form.GetType().GetProperty("ReportError").SetValue(form,
            new Action<Exception, bool>((error, showDialog) => reports.Add(Tuple.Create(error, showDialog))), null);
    }
    private static void WaitForSubmission(Form form)
    {
        var task = (Task)form.GetType().GetProperty("Submission").GetValue(form, null);
        var clock = System.Diagnostics.Stopwatch.StartNew();
        while (!task.IsCompleted && clock.ElapsedMilliseconds < 5000) Application.DoEvents();
        if (!task.IsCompleted) throw new Exception("Editor submission did not complete.");
        task.GetAwaiter().GetResult();
    }
    public static void CheckActions(Assembly package, object warehouse, FunctionMetadata known,
        FunctionMetadata unknown, string[] schemas)
    {
        var create = package.GetType("FabricExternalFunctions.Ssms.Dialogs", true).GetMethod("CreateEditor");
        Func<FunctionMetadata, Form> editor = existing => {
            var form = (Form)create.Invoke(null, new object[] { warehouse, existing, "spatial", schemas });
            if (existing == null) Named(form, "FunctionName").Text = "new_function";
            Named(form, "FunctionSet").Text = "spatial functions";
            Named(form, "ExternalName").Text = "s2";
            return form;
        };
        foreach (var existing in new[] { null, known })
        {
            using (var form = editor(existing))
            {
                var reports = new List<Tuple<Exception, bool>>();
                int calls = 0;
                ConfigureExecution(form, (declaration, retain) => {
                    calls++;
                    if (retain || declaration.Name != (existing == null ? "new_function" : "s2"))
                        throw new Exception("Direct submission lost its captured identity.");
                    string sql = declaration.ScriptChange(existing, retain);
                    if ((existing == null && !sql.StartsWith("CREATE FUNCTION ")) ||
                        (existing != null && !sql.Contains("EXEC sys.sp_executesql N'ALTER FUNCTION ")))
                        throw new Exception("Direct Create/Save must preserve CREATE versus guarded ALTER.");
                    return Task.CompletedTask;
                }, reports);
                var apply = Named(form, "ApplyFunction");
                var preview = Named(form, "PreviewSql");
                if (apply.Text != (existing == null ? "Create" : "Save") ||
                    apply.Parent.Controls.GetChildIndex(apply) >= preview.Parent.Controls.GetChildIndex(preview) ||
                    !ReferenceEquals(form.AcceptButton, preview))
                    throw new Exception("Create/Save must be left of Preview SQL; Enter must still default to preview.");
                Click(apply); WaitForSubmission(form);
                if (calls != 1 || reports.Count != 0 || form.DialogResult != DialogResult.OK ||
                    !(bool)form.GetType().GetProperty("Applied").GetValue(form, null))
                    throw new Exception("Direct Create/Save must execute exactly once and close only after success.");
                Click(apply); WaitForSubmission(form);
                if (calls != 1) throw new Exception("A completed direct submission must not be replayed.");
            }
        }
        foreach (var existing in new[] { null, known })
        foreach (var numbers in new[] { new[] { 31727 }, new[] { 31728 }, new[] { 31727, 31728 },
            new[] { 31727, 3621 }, new[] { 50000 }, new[] { 31727, 50000 } })
        {
            using (var form = editor(existing))
            {
                var reports = new List<Tuple<Exception, bool>>();
                int calls = 0;
                var failure = SqlFailure(numbers);
                ConfigureExecution(form, (declaration, retain) => {
                    calls++;
                    return calls == 1 ? Task.FromException(failure) : Task.CompletedTask;
                }, reports);
                Click(Named(form, "ApplyFunction")); WaitForSubmission(form);
                bool inline = !numbers.Contains(50000);
                if (form.DialogResult != DialogResult.None || reports.Count != 1 ||
                    !ReferenceEquals(reports[0].Item1, failure) || reports[0].Item2 == inline ||
                    !Named(form, "EditorPanel").Enabled || form.GetType().GetProperty("Declaration").GetValue(form, null) != null)
                    throw new Exception("Failed direct submissions must stay editable and report through the correct surface.");
                foreach (var pair in new[] { Tuple.Create(31727, "ExternalName"), Tuple.Create(31728, "FunctionSet") })
                {
                    string text = Named(form, pair.Item2 + "Error").Text;
                    if ((text.Length != 0) != (inline && numbers.Contains(pair.Item1)))
                        throw new Exception("Only numbered binding errors may become field errors.");
                    if (text.Length != 0 && !failure.Errors.Cast<SqlError>().Any(error => error.Message == text))
                        throw new Exception("Inline binding errors must preserve the server message.");
                }
                if (numbers.Contains(31728)) Named(form, "FunctionSet").Text = "corrected set";
                else if (numbers.Contains(3621)) Named(form, "ExternalName").Text = "corrected_function";
                else if (numbers.Length != 1 || numbers[0] != 31727) Named(form, "FunctionSet").Text = "corrected set";
                // The single-31727 case explicitly retries unchanged values after a remote fix.
                if (numbers.Length == 1 && numbers[0] == 31727)
                {
                    Click(Named(form, "ApplyFunction")); WaitForSubmission(form);
                    if (calls != 2 || reports.Count != 1 || form.DialogResult != DialogResult.OK)
                        throw new Exception("An explicit unchanged retry must not be blocked by stale server errors.");
                    continue;
                }
                if (Named(form, "FunctionSetError").Text.Length != 0 || Named(form, "ExternalNameError").Text.Length != 0)
                    throw new Exception("Changing the function set must clear dependent server errors.");
                Click(Named(form, "ApplyFunction")); WaitForSubmission(form);
                if (calls != 2 || reports.Count != 1 || form.DialogResult != DialogResult.OK)
                    throw new Exception("An explicit corrected retry must execute once, without a hidden automatic retry.");
            }
        }
        using (var form = editor(null))
        {
            var reports = new List<Tuple<Exception, bool>>();
            var pending = new TaskCompletionSource<bool>();
            int calls = 0;
            ConfigureExecution(form, (declaration, retain) => { calls++; return pending.Task; }, reports);
            Click(Named(form, "ApplyFunction"));
            var submission = form.GetType().GetProperty("Submission").GetValue(form, null);
            Click(Named(form, "ApplyFunction"));
            var closing = new FormClosingEventArgs(CloseReason.UserClosing, false);
            typeof(Form).GetMethod("OnFormClosing", BindingFlags.Instance | BindingFlags.NonPublic)
                .Invoke(form, new object[] { closing });
            if (calls != 1 || Named(form, "EditorPanel").Enabled || Named(form, "ApplyFunction").Enabled ||
                form.ControlBox || !closing.Cancel ||
                !ReferenceEquals(submission, form.GetType().GetProperty("Submission").GetValue(form, null)))
                throw new Exception("Pending DDL must block repeated submission, field changes and closing.");
            pending.SetResult(true); WaitForSubmission(form);
            if (calls != 1 || reports.Count != 0 || form.DialogResult != DialogResult.OK)
                throw new Exception("Pending DDL must complete once without a second confirmation.");
        }
        using (var form = editor(known))
        {
            var warning = Named(form, "FunctionIdentity");
            var retain = (CheckBox)Named(form, "RetainExistingFunction");
            if (LocalVisible(warning) || LocalVisible(retain) || retain.Checked)
                throw new Exception("Unchanged identity must hide the copy warning and checkbox.");
            Named(form, "FunctionName").Text = "copy_s2";
            if (!LocalVisible(warning) || !LocalVisible(retain))
                throw new Exception("Changed identity must show the copy warning and checkbox.");
            retain.Checked = true;
            if (retain.Text != "Retain existing function (create a clone)" ||
                !warning.Text.Contains("keeps the original") || warning.Text.Contains("drops the original"))
                throw new Exception("Checked retention must clearly explain cloning without dropping the original.");
            Named(form, "FunctionName").Text = known.Name;
            if (LocalVisible(warning) || LocalVisible(retain) || retain.Checked)
                throw new Exception("Restoring the name must hide and uncheck retention.");
            ((ComboBox)Named(form, "Schema")).SelectedItem = "api"; retain.Checked = true;
            ((ComboBox)Named(form, "Schema")).SelectedItem = known.Schema;
            if (LocalVisible(warning) || LocalVisible(retain) || retain.Checked)
                throw new Exception("Restoring the schema must hide and uncheck retention.");
            Named(form, "FunctionName").Text = "copy_s2";
            ((ComboBox)Named(form, "Schema")).SelectedItem = "api"; retain.Checked = true;
            Named(form, "FunctionName").Text = known.Name;
            if (!LocalVisible(warning) || !LocalVisible(retain) || !retain.Checked)
                throw new Exception("Restoring only one of two changed identity values must retain copy controls.");
            ((ComboBox)Named(form, "Schema")).SelectedItem = known.Schema;
            if (LocalVisible(warning) || LocalVisible(retain) || retain.Checked)
                throw new Exception("Restoring both identity values must reset retention.");
        }
        foreach (bool retainExisting in new[] { false, true })
        using (var form = editor(known))
        {
            int calls = 0;
            var reports = new List<Tuple<Exception, bool>>();
            Named(form, "FunctionName").Text = "copied_s2";
            ((CheckBox)Named(form, "RetainExistingFunction")).Checked = retainExisting;
            ConfigureExecution(form, (declaration, retain) => {
                calls++;
                string sql = declaration.ScriptChange(known, retain);
                if (retain != retainExisting || !sql.Contains("EXEC sys.sp_executesql N'CREATE FUNCTION ") ||
                    sql.Contains("DROP FUNCTION IF EXISTS ") == retainExisting)
                    throw new Exception("Direct Save must capture retention and choose replacement versus clone.");
                if (!retain && sql.IndexOf("DROP FUNCTION IF EXISTS ", StringComparison.Ordinal) >
                    sql.IndexOf("EXEC sys.sp_executesql", StringComparison.Ordinal))
                    throw new Exception("Unchecked direct Save must drop the old function before creating the new one.");
                return Task.CompletedTask;
            }, reports);
            Click(Named(form, "ApplyFunction")); WaitForSubmission(form);
            if (calls != 1 || reports.Count != 0 || form.DialogResult != DialogResult.OK)
                throw new Exception("Direct replacement or clone Save must execute once.");
        }
        using (var form = editor(known))
        {
            var reports = new List<Tuple<Exception, bool>>();
            Named(form, "FunctionName").Text = "replacement_s2";
            ConfigureExecution(form, (declaration, retain) => Task.FromException(SqlFailure(31727)), reports);
            Click(Named(form, "ApplyFunction")); WaitForSubmission(form);
            if (reports.Count != 1 || reports[0].Item2 || form.DialogResult != DialogResult.None ||
                Named(form, "ExternalNameError").Text.Length == 0 ||
                !Named(form, "FunctionIdentity").Text.Contains("the original isn't restored"))
                throw new Exception("Replacement failures must preserve inline binding errors and the non-atomic replacement warning.");
        }
        using (var form = editor(unknown))
        {
            int calls = 0;
            var reports = new List<Tuple<Exception, bool>>();
            ConfigureExecution(form, (declaration, retain) => { calls++; return Task.CompletedTask; }, reports);
            Click(Named(form, "ApplyFunction")); WaitForSubmission(form);
            if (calls != 0 || Named(form, "AcceptUnverifiedError").Text.Length == 0)
                throw new Exception("Direct Save must still require unverified-binding acknowledgement.");
            ((CheckBox)Named(form, "AcceptUnverified")).Checked = true;
            Click(Named(form, "ApplyFunction")); WaitForSubmission(form);
            if (calls != 1 || reports.Count != 0 || form.DialogResult != DialogResult.OK)
                throw new Exception("Acknowledged direct Save must use the same mutation path.");
        }
        using (var form = editor(null))
        {
            var reports = new List<Tuple<Exception, bool>>();
            var failure = new InvalidOperationException("Offline target-context failure.");
            ConfigureExecution(form, (declaration, retain) => Task.FromException(failure), reports);
            Click(Named(form, "ApplyFunction")); WaitForSubmission(form);
            if (reports.Count != 1 || !ReferenceEquals(reports[0].Item1, failure) || !reports[0].Item2 ||
                form.DialogResult != DialogResult.None || Named(form, "FunctionSetError").Text.Length != 0 ||
                Named(form, "ExternalNameError").Text.Length != 0)
                throw new Exception("Non-SQL execution failures must remain dialog errors, not guessed binding errors.");
        }
        using (var form = editor(null))
        {
            int calls = 0;
            var reports = new List<Tuple<Exception, bool>>();
            ConfigureExecution(form, (declaration, retain) => { calls++; return Task.CompletedTask; }, reports);
            Named(form, "FunctionName").Text = "";
            Click(Named(form, "ApplyFunction")); WaitForSubmission(form);
            if (calls != 0 || Named(form, "FunctionNameError").Text != "Enter a name." ||
                form.DialogResult != DialogResult.None)
                throw new Exception("Direct Create must validate Name before executing.");
        }
        foreach (var existing in new[] { null, known, unknown })
        using (var form = editor(existing))
        {
            int previews = 0, executions = 0;
            var reports = new List<Tuple<Exception, bool>>();
            ConfigureExecution(form, (declaration, retain) => { executions++; return Task.CompletedTask; }, reports);
            Named(form, "FunctionName").Text = "reviewed_function";
            ((ComboBox)Named(form, "Schema")).SelectedItem = "api";
            Named(form, "ReturnOverride").Text = "decimal(20,4)";
            if (existing != null) ((CheckBox)Named(form, "RetainExistingFunction")).Checked = true;
            if (existing == unknown) ((CheckBox)Named(form, "AcceptUnverified")).Checked = true;
            var result = DialogResult.Cancel;
            string expectedName = "reviewed_function";
            form.GetType().GetProperty("PreviewSql").SetValue(form,
                new Func<ProxyDeclaration, bool, DialogResult>((declaration, retain) => {
                    previews++;
                    if (declaration.Name != expectedName || declaration.Schema != "api" ||
                        declaration.ReturnOverride != "decimal(20,4)" || retain != (existing != null))
                        throw new Exception("Preview must receive the current fields and retention choice.");
                    return result;
                }), null);
            Click(Named(form, "PreviewSql"));
            if (form.DialogResult != DialogResult.None || previews != 1 || executions != 0 || reports.Count != 0 ||
                Named(form, "FunctionName").Text != "reviewed_function" ||
                Named(form, "FunctionSet").Text != "spatial functions" || Named(form, "ExternalName").Text != "s2" ||
                Named(form, "ReturnOverride").Text != "decimal(20,4)" ||
                (existing != null && !((CheckBox)Named(form, "RetainExistingFunction")).Checked) ||
                (existing == unknown && !((CheckBox)Named(form, "AcceptUnverified")).Checked))
                throw new Exception("Cancel/back from preview must preserve the same editor and all entered values without executing.");
            expectedName = "revised_function";
            Named(form, "FunctionName").Text = expectedName;
            result = DialogResult.OK;
            Click(Named(form, "PreviewSql"));
            if (form.DialogResult != DialogResult.OK || previews != 2 || executions != 0 ||
                ((ProxyDeclaration)form.GetType().GetProperty("Declaration").GetValue(form, null)).Name != expectedName ||
                (bool)form.GetType().GetProperty("Applied").GetValue(form, null))
                throw new Exception("Preview Apply must finish the parent and request execution exactly once by its caller.");
        }
        foreach (var existing in new[] { null, known })
        using (var form = editor(existing))
        {
            int executions = 0;
            var reports = new List<Tuple<Exception, bool>>();
            ConfigureExecution(form, (declaration, retain) => { executions++; return Task.CompletedTask; }, reports);
            form.GetType().GetProperty("PreviewSql").SetValue(form,
                new Func<ProxyDeclaration, bool, DialogResult>((declaration, retain) => DialogResult.Yes), null);
            Click(Named(form, "PreviewSql"));
            if (form.DialogResult != DialogResult.Yes || executions != 0 || reports.Count != 0 ||
                (bool)form.GetType().GetProperty("Applied").GetValue(form, null))
                throw new Exception("Successful query export must finish the parent without applying the declaration.");
        }
    }
    private static IEnumerable<Control> All(Control root)
    {
        yield return root;
        foreach (Control child in root.Controls)
            foreach (Control descendant in All(child)) yield return descendant;
    }
    private static void Arrange(Control root)
    {
        var handle = root.Handle;
        foreach (Control child in root.Controls) Arrange(child);
        root.PerformLayout();
    }
    public static void ScaleForTest(Form form, float scale)
    {
        // Exercise scaled fonts as well as geometry; bounds-only scaling misses clipped text.
        var explicitFonts = All(form).Where(c => c.Parent == null || !c.Font.Equals(c.Parent.Font))
            .Select(c => new KeyValuePair<Control, Font>(c, c.Font)).ToArray();
        form.AutoScaleMode = AutoScaleMode.None;
        form.Scale(new SizeF(scale, scale));
        var owned = new List<Font>();
        foreach (var item in explicitFonts)
        {
            var font = new Font(item.Value.FontFamily, item.Value.SizeInPoints * scale, item.Value.Style);
            owned.Add(font); item.Key.Font = font;
        }
        form.Disposed += delegate { foreach (var font in owned) font.Dispose(); };
    }
    public static void Check(Form form)
    {
        var handle = form.Handle;
        Arrange(form); Arrange(form);
        if (All(form).Any(c => c.Name == "DialogHeading"))
            throw new Exception("Dialogs must not repeat the window title as a bold heading.");
        if (All(form).OfType<Label>().Any(c => c.Text.IndexOf("Verified warehouse definition", StringComparison.OrdinalIgnoreCase) >= 0))
            throw new Exception("Dialogs must not display a redundant verified-definition label.");
        var description = All(form).Single(c => c.Name == "DialogDescription");
        if (!description.Font.Equals(form.Font))
            throw new Exception("Dialog descriptions must use the normal dialog font.");
        var descriptionBounds = new Rectangle(form.PointToClient(description.PointToScreen(Point.Empty)), description.Size);
        if (!form.ClientRectangle.Contains(descriptionBounds))
            throw new Exception("The dialog description is clipped.");
        foreach (var button in All(form).OfType<Button>())
        {
            var position = form.PointToClient(button.PointToScreen(Point.Empty));
            var bounds = new Rectangle(position, button.Size);
            if (!form.ClientRectangle.Contains(bounds))
                throw new Exception(button.Name + " is outside the dialog: " + bounds + " / " + form.ClientRectangle);
            var preferred = button.GetPreferredSize(Size.Empty);
            if (button.Width < preferred.Width || button.Height < preferred.Height)
                throw new Exception(button.Name + " is too small for its text: " + button.Size + " / " + preferred);
        }
        if (All(form).Any(c => c.Name == "OpenQuery"))
        {
            var footer = All(form).Single(c => c.Name == "DialogFooter");
            var buttons = All(footer).OfType<Button>().ToArray();
            var bounds = buttons.Select(b => new Rectangle(
                form.PointToClient(b.PointToScreen(Point.Empty)), b.Size)).ToArray();
            if (bounds.Select(b => b.Top).Distinct().Count() != 1)
                throw new Exception("Preview actions must stay on one footer row.");
            for (int i = 0; i < bounds.Length; i++)
                for (int j = i + 1; j < bounds.Length; j++)
                    if (bounds[i].IntersectsWith(bounds[j]))
                        throw new Exception("Preview actions overlap: " + buttons[i].Name + " " + bounds[i] +
                            " / " + buttons[j].Name + " " + bounds[j] + "; client=" + form.ClientSize);
            foreach (var button in buttons)
                if (!button.Parent.ClientRectangle.Contains(button.Bounds))
                    throw new Exception(button.Name + " is clipped by the preview footer.");
            var layout = All(form).Single(c => c.Name == "DialogLayout");
            int bottomGap = form.ClientSize.Height - bounds.Max(b => b.Bottom);
            int expectedGap = layout.Padding.Bottom + buttons.Max(b => b.Margin.Bottom);
            if (bottomGap > expectedGap + 2)
                throw new Exception("Preview footer reserves unused space below its buttons: " + bottomGap + " / " + expectedGap);
            if (buttons.Single(b => b.Name == "OpenQuery").Text != "Open in a new query")
                throw new Exception("The query action must clearly create a new query.");
            if (buttons.Where(b => b.Name == "CopySql" || b.Name == "SaveSql")
                .Any(b => b.DialogResult != DialogResult.None))
                throw new Exception("Copy SQL and Save SQL must keep the preview open.");
            var apply = buttons.SingleOrDefault(b => b.Name == "ApplyDdl");
            if (apply != null && apply.Text != "Apply")
                throw new Exception("The Apply action must use the compact label.");
        }
        foreach (var label in All(form).OfType<Label>().Where(c => c.Name.EndsWith("Label")))
        {
            var preferred = label.GetPreferredSize(Size.Empty);
            if (label.Width < preferred.Width || label.Height < preferred.Height)
                throw new Exception(label.Name + " wraps or clips a field caption.");
        }
        foreach (var box in All(form).OfType<TextBox>().Where(c => !c.Multiline))
        {
            if (box.Height < box.PreferredHeight) throw new Exception(box.Name + " clips its text vertically.");
            if (box.Width < box.Font.Height * 12) throw new Exception(box.Name + " is too narrow to read an existing value.");
        }
        foreach (var box in All(form).OfType<ComboBox>())
            if (box.Height < box.PreferredHeight || box.Width < box.Font.Height * 12)
                throw new Exception(box.Name + " clips the dropdown or its selected value.");
        if (form.Icon == null || form.CancelButton == null) throw new Exception("Dialog icon/Cancel keyboard action is missing.");
    }
}
'@

    $parameterType = $core.GetType('FabricExternalFunctions.Core.ParameterMetadata', $true)
    $parameters = [Array]::CreateInstance($parameterType, 3)
    foreach ($value in @(@(0, '', 'bigint'), @(1, 'latitude', 'float'), @(2, 'longitude', 'float'))) {
        $parameter = $parameterType.GetConstructors()[0].Invoke([object[]]$value)
        $parameters.SetValue($parameter.PSObject.BaseObject, [int]$value[0])
    }
    $complete = "CREATE OR ALTER FUNCTION spatial.s2 RETURNS bigint AS EXTERNAL FUNCTION [spatial functions].[s2];"
    $truncated = "`r`n`r`n-- Proxy functions mapped to Fabric function-set `"spatial`"`r`nCREATE OR ALTER FUNCTION spatial.s2`r`nAS EXTERNAL FUNCTION spatia"
    if ($truncated.Length -ne 128) { throw 'The regression must preserve the 128-character truncated-definition boundary.' }
    $metadataType = $core.GetType('FabricExternalFunctions.Core.FunctionMetadata', $true)
    function Metadata([AllowNull()][string]$definition) {
        $metadataType.GetConstructors()[0].Invoke([object[]]@(
            42, 'spatial', 's2', [DateTime]'2026-10-15T10:20:30.003', $parameters, $definition, $definition))
    }
    $known = Metadata $complete
    $paddedDefinition = "`r`n`r`n-- Warehouse definition`r`nCREATE FUNCTION spatial.s2`r`n`r`nAS EXTERNAL FUNCTION [spatial functions].[s2];`r`n`r`n"
    $padded = Metadata $paddedDefinition
    $unknown = Metadata $truncated
    $inferred = Metadata 'CREATE FUNCTION spatial.s2 AS EXTERNAL FUNCTION [spatial functions].[s2];'
    $noAs = Metadata 'CREATE FUNCTION spatial.s2 RETURNS bigint EXTERNAL FUNCTION [spatial functions].[s2];'
    $argsForParse = [object[]]@($unknown.PSObject.BaseObject, '')
    if ($null -ne $dialogs.GetMethod('VerifiedDeclaration').Invoke($null, $argsForParse) -or $argsForParse[1] -notmatch 'Expected \.') {
        throw 'Truncated metadata must remain explicitly unverified, not reconstructed from a comment or proxy name.'
    }

    $provider = $assembly.GetType('FabricExternalFunctions.Ssms.CallDropProvider', $true)
    $attributes = $provider.GetCustomAttributesData()
    $contentTypes = @($attributes | Where-Object { $_.AttributeType.Name -eq 'ContentTypeAttribute' } |
        ForEach-Object { $_.ConstructorArguments[0].Value })
    if ('SQL' -notin $contentTypes -or 'T-SQL' -notin $contentTypes -or
        !($attributes | Where-Object { $_.AttributeType.Name -eq 'NameAttribute' })) {
        throw 'The editor provider is missing its required SQL content-type/name discovery metadata.'
    }
    Write-Host 'PASS drop provider exports SQL/T-SQL content types and a named handler, matching the native SQL provider contract.'

    $openQuery = [Action[string]]{ param($sql) throw 'Dialog layout checks must not open an editor or execute SQL.' }
    foreach ($unverified in @($false, $true)) {
        $exports = New-Object 'System.Collections.Generic.List[string]'
        $captureQuery = [Action[string]]{ param($sql) $exports.Add($sql) }
        $preview = $dialogs.GetMethod('CreatePreview').Invoke($null,
            [object[]]@($complete, $warehouse.PSObject.BaseObject, $true, $unverified, $captureQuery))
        try {
            $button = $preview.Controls.Find('OpenQuery', $true)[0]
            [void][Windows.Forms.Button].GetMethod('OnClick', [Reflection.BindingFlags]'Instance,NonPublic').Invoke(
                $button.PSObject.BaseObject, [object[]]@([EventArgs]::Empty))
            if ($exports.Count -ne 1 -or
                $exports[0] -cne $preview.Controls.Find('StoredDefinition', $true)[0].Text -or
                !$exports[0].StartsWith('-- Target: ') -or
                $exports[0].Contains('-- Unverified binding and return type') -ne $unverified -or
                $preview.DialogResult -ne [Windows.Forms.DialogResult]::Yes) {
                throw 'Open in a new query must export the exact reviewed SQL and finish with an export-only result, never Apply.'
            }
        } finally { $preview.Dispose() }
    }
    Write-Host 'PASS query export preserves the reviewed target, warnings and SQL and closes with an export-only result, without execution.'
    $schemaFilter = $assembly.GetType('FabricExternalFunctions.Ssms.CatalogReader', $true).GetMethod(
        'IsUserSchema', [Reflection.BindingFlags]'Static,NonPublic')
    foreach ($schemaName in @('sys', 'SYS', 'INFORMATION_SCHEMA', 'guest', 'queryinsights', 'QueryInsights',
        'db_owner', 'db_datareader', 'DB_DATAWRITER', 'db_securityadmin', 'db_custom')) {
        if ($schemaFilter.Invoke($null, [object[]]@($schemaName))) { throw "System schema $schemaName must be excluded." }
    }
    foreach ($schemaName in @('dbo', 'api', 'spatial', 'qpi', 'database_tools', 'sys_custom', 'queryinsights_custom')) {
        if (!$schemaFilter.Invoke($null, [object[]]@($schemaName))) { throw "User schema $schemaName must remain available." }
    }
    Write-Host 'PASS shared schema filtering excludes system/queryinsights/db_ names without excluding dbo or unrelated user names.'
    $catalogType = $assembly.GetType('FabricExternalFunctions.Ssms.CatalogReader', $true)
    $catalog = [Activator]::CreateInstance($catalogType, $true)
    $declarationType = $core.GetType('FabricExternalFunctions.Core.ProxyDeclaration', $true)
    foreach ($schemaName in @('sys', 'INFORMATION_SCHEMA', 'guest', 'queryinsights', 'db_owner')) {
        $invalidTarget = $declarationType.GetConstructors()[0].Invoke(
            [object[]]@($schemaName, 'f', 'set', 'remote', $null))
        $rejected = $false
        try {
            $pending = $catalogType.GetMethod('ExecuteAsync').Invoke($catalog,
                [object[]]@($warehouse.PSObject.BaseObject, $invalidTarget.PSObject.BaseObject, $null, $false,
                    [Threading.CancellationToken]::None))
            $pending.GetAwaiter().GetResult()
        } catch {
            $failure = $_.Exception
            while ($null -ne $failure.InnerException) { $failure = $failure.InnerException }
            if ($failure -isnot [ArgumentException] -or $failure.Message -notmatch 'non-system') { throw }
            $rejected = $true
        }
        if (!$rejected) { throw 'System-schema mutations must be rejected before any SQL connection is opened.' }
    }
    Write-Host 'PASS system-schema mutations are blocked before authentication or SQL execution.'
    $schemas = [string[]]@('api', 'dbo', 'spatial', 'queryinsights', 'sys', 'INFORMATION_SCHEMA',
        'guest', 'db_owner', 'DB_DATAREADER')
    function SubmitEditor($editor) {
        $editor.PreviewSql = [Func[FabricExternalFunctions.Core.ProxyDeclaration,bool,Windows.Forms.DialogResult]]{
            param($declaration, $retain) [Windows.Forms.DialogResult]::OK
        }
        if ($editor.Controls.Find('FunctionNameError', $true).Count -ne 1) {
            throw 'Editor validation must use inline error labels, not modal message boxes.'
        }
        [void][Windows.Forms.Button].GetMethod('OnClick', [Reflection.BindingFlags]'Instance,NonPublic').Invoke(
            $editor.Controls.Find('PreviewSql', $true)[0].PSObject.BaseObject, [object[]]@([EventArgs]::Empty))
    }
    function CheckInlineError($editor, [string]$name) {
        $input = $editor.Controls.Find($name, $true)[0]
        $message = $editor.Controls.Find($name + 'Error', $true)[0]
        if ([string]::IsNullOrWhiteSpace($message.Text) -or $message -isnot [Windows.Forms.Label] -or
            $message.AccessibleRole -ne [Windows.Forms.AccessibleRole]::Alert -or
            $message.Parent -ne $input.Parent -or
            $message.Parent.GetRow($message) -ne $input.Parent.GetRow($input) + 1 -or
            $message.Parent.GetColumn($message) -ne $input.Parent.GetColumn($input)) {
            throw "Validation for $name must appear directly beneath its own control with an accessible error label."
        }
        $expectedColor = if ([Windows.Forms.SystemInformation]::HighContrast) {
            [Drawing.SystemColors]::ControlText
        } else { [Drawing.Color]::Firebrick }
        if ($message.ForeColor -ne $expectedColor) {
            throw 'Inline errors must use system text colors in high contrast and a readable error color otherwise.'
        }
        if ($editor.DialogResult -ne [Windows.Forms.DialogResult]::None -or $null -ne $editor.Declaration) {
            throw 'Invalid input must not close the editor or prepare an executable declaration.'
        }
    }
    foreach ($scale in @(1.0, 1.5, 2.0)) {
        $editor = $dialogs.GetMethod('CreateEditor').Invoke($null,
            [object[]]@($warehouse.PSObject.BaseObject, $null, 'dbo', $schemas))
        try {
            $editor.Controls.Find('Schema', $true)[0].SelectedIndex = -1
            $editor.Controls.Find('ReturnOverride', $true)[0].SelectedIndex = -1
            $editor.Controls.Find('ReturnOverride', $true)[0].Text = ''
            SubmitEditor $editor
            foreach ($name in @('Schema', 'FunctionName', 'FunctionSet', 'ExternalName', 'ReturnOverride')) {
                CheckInlineError $editor $name
            }
            [DialogLayoutChecks]::ScaleForTest($editor, [float]$scale)
            [DialogLayoutChecks]::Check($editor)
            $editor.Controls.Find('Schema', $true)[0].SelectedItem = 'dbo'
            $editor.Controls.Find('FunctionName', $true)[0].Text = 'getRates'
            $editor.Controls.Find('FunctionSet', $true)[0].Text = 'rates set'
            $editor.Controls.Find('ExternalName', $true)[0].Text = 'getRates'
            $editor.Controls.Find('ReturnOverride', $true)[0].Text = '  (INFERRED)  '
            foreach ($name in @('Schema', 'FunctionName', 'FunctionSet', 'ExternalName', 'ReturnOverride')) {
                if ($editor.Controls.Find($name + 'Error', $true)[0].Text.Length -ne 0) {
                    throw "Correcting $name must clear its inline error without another submit."
                }
            }
            SubmitEditor $editor
            if ($editor.DialogResult -ne [Windows.Forms.DialogResult]::OK -or
                $editor.Declaration.Name -ne 'getRates' -or $editor.Declaration.FunctionSet -ne 'rates set' -or
                $null -ne $editor.Declaration.ReturnOverride) {
                throw 'Correcting required fields must prepare the reviewed declaration, with a normalized inferred option.'
            }
        } finally { $editor.Dispose() }
    }
    foreach ($case in @(
        @('FunctionName', '   '), @('FunctionSet', '   '), @('ExternalName', '   '),
        @('FunctionName', ('x' * 129)), @('FunctionSet', ('x' * 129)), @('ExternalName', ('x' * 129)),
        @('FunctionName', "f$([char]1)x"), @('FunctionSet', "f$([char]1)x"), @('ExternalName', "f$([char]1)x"),
        @('ReturnOverride', 'varchar(0)'), @('ReturnOverride', 'decimal(2,3)'),
        @('ReturnOverride', 'int; DROP TABLE dbo.t'), @('ReturnOverride', '[int]'))) {
        $editor = $dialogs.GetMethod('CreateEditor').Invoke($null,
            [object[]]@($warehouse.PSObject.BaseObject, $null, 'dbo', $schemas))
        try {
            foreach ($name in @('FunctionName', 'FunctionSet', 'ExternalName')) {
                $editor.Controls.Find($name, $true)[0].Text = 'valid_name'
            }
            $editor.Controls.Find($case[0], $true)[0].Text = $case[1]
            SubmitEditor $editor
            CheckInlineError $editor $case[0]
            $editor.Controls.Find($case[0], $true)[0].Text = if ($case[0] -eq 'ReturnOverride') {
                'decimal(20,4)'
            } else { 'corrected_name' }
            if ($editor.Controls.Find($case[0] + 'Error', $true)[0].Text.Length -ne 0) {
                throw 'Corrected identifier/type format must clear its field error immediately.'
            }
            SubmitEditor $editor
            if ($editor.DialogResult -ne [Windows.Forms.DialogResult]::OK) {
                throw 'Corrected format must allow SQL review without any modal validation popup.'
            }
        } finally { $editor.Dispose() }
    }
    foreach ($changeKind in @('name', 'schema')) {
        $editor = $dialogs.GetMethod('CreateEditor').Invoke($null,
            [object[]]@($warehouse.PSObject.BaseObject, $known.PSObject.BaseObject, 'spatial', $schemas))
        try {
            $name = $editor.Controls.Find('FunctionName', $true)[0]
            $retain = $editor.Controls.Find('RetainExistingFunction', $true)[0]
            if ($changeKind -eq 'name') { $name.Text = 'new_s2' }
            else { $editor.Controls.Find('Schema', $true)[0].SelectedItem = 'api' }
            SubmitEditor $editor
            if ($editor.DialogResult -ne [Windows.Forms.DialogResult]::OK -or $editor.RetainExistingFunction -or
                !$editor.Declaration.ScriptChange($known, $false).Contains('DROP FUNCTION IF EXISTS [spatial].[s2];')) {
                throw 'An unchecked identity change must prepare DROP followed by CREATE, not require retention.'
            }
            $editor.DialogResult = [Windows.Forms.DialogResult]::None
            $retain.Checked = $true
            if ($editor.Controls.Find('RetainExistingFunctionError', $true)[0].Text.Length -ne 0) {
                throw 'Changing a related name or checkbox must clear the resolved identity error.'
            }
            SubmitEditor $editor
            if ($editor.DialogResult -ne [Windows.Forms.DialogResult]::OK -or !$editor.RetainExistingFunction) {
                throw 'A corrected retained copy must pass inline validation.'
            }
        } finally { $editor.Dispose() }
    }
    $editor = $dialogs.GetMethod('CreateEditor').Invoke($null,
        [object[]]@($warehouse.PSObject.BaseObject, $unknown.PSObject.BaseObject, 'spatial', $schemas))
    try {
        $editor.Controls.Find('FunctionSet', $true)[0].Text = 'set'
        $editor.Controls.Find('ExternalName', $true)[0].Text = 'remote'
        SubmitEditor $editor
        CheckInlineError $editor 'AcceptUnverified'
        $editor.Controls.Find('AcceptUnverified', $true)[0].Checked = $true
        if ($editor.Controls.Find('AcceptUnverifiedError', $true)[0].Text.Length -ne 0) {
            throw 'Acknowledging the unverified binding must clear its inline warning.'
        }
        SubmitEditor $editor
        if ($editor.DialogResult -ne [Windows.Forms.DialogResult]::OK) {
            throw 'Acknowledged manual bindings must still support reviewed edits.'
        }
    } finally { $editor.Dispose() }
    Write-Host 'PASS inline required/format errors, per-field placement, correction, inferred types, retained copies and unverified acknowledgement.'
    [DialogLayoutChecks]::CheckActions($assembly, $warehouse.PSObject.BaseObject,
        $known.PSObject.BaseObject, $unknown.PSObject.BaseObject, $schemas)
    Write-Host 'PASS direct Create/Save, inline SQL errors, replacement/clone choice, preview back/apply/export outcomes and single pending mutation.'
    $excluded = $metadataType.GetConstructors()[0].Invoke([object[]]@(
        42, 'sys', 'f', [DateTime]'2026-10-15T10:20:30.003', $parameters, $null, $null))
    foreach ($source in @($excluded)) {
        $rejected = $false
        try {
            $unexpected = $dialogs.GetMethod('CreateEditor').Invoke($null,
                [object[]]@($warehouse.PSObject.BaseObject, $source, 'sys', $schemas))
            $unexpected.Dispose()
        } catch {
            $failure = $_.Exception
            while ($null -ne $failure.InnerException) { $failure = $failure.InnerException }
            if ($failure -isnot [ArgumentException] -or $failure.Message -notmatch 'excluded' -or
                $failure.Message -match 'Refresh') { throw }
            $rejected = $true
        }
        if (!$rejected) { throw 'An intentionally excluded edit schema must report the restriction, not stale metadata.' }
    }
    foreach ($folderSchema in @('sys', 'queryinsights', 'db_custom', 'no_longer_available', $null)) {
        $newForm = $dialogs.GetMethod('CreateEditor').Invoke($null,
            [object[]]@($warehouse.PSObject.BaseObject, $null, $folderSchema, $schemas))
        try {
            $choice = $newForm.Controls.Find('Schema', $true)[0]
            if ($choice.SelectedItem -ne 'dbo' -or $choice.Items.Count -ne 3) {
                throw 'New must choose dbo when its folder is not an available target schema.'
            }
        } finally { $newForm.Dispose() }
    }
    $newForm = $dialogs.GetMethod('CreateEditor').Invoke($null,
        [object[]]@($warehouse.PSObject.BaseObject, $null, 'sys', [string[]]@('api', 'spatial', 'sys')))
    try {
        if ($newForm.Controls.Find('Schema', $true)[0].SelectedItem -ne 'api') {
            throw 'New must choose the first user schema when dbo is unavailable.'
        }
    } finally { $newForm.Dispose() }
    Write-Host 'PASS New falls back to dbo/first user schema for excluded or absent folder targets; excluded edit schemas remain explicit.'
    foreach ($scale in @(1.0, 1.5, 2.0)) {
        foreach ($kind in @('properties', 'properties-trimmed', 'verified-edit', 'no-as-edit', 'default-edit', 'typed-edit', 'rename-edit', 'move-edit',
            'replace-edit', 'retain-name-edit', 'retain-schema-edit', 'retain-copy-edit', 'inferred-edit',
            'unverified-edit', 'unverified-replacement-edit', 'unverified-copy-edit', 'new', 'script', 'ddl',
            'delete-ok', 'delete-cancel', 'delete-unverified')) {
            Write-Verbose "Checking $kind at $scale font/geometry scaling."
            $form = switch ($kind) {
                'properties' { $dialogs.GetMethod('CreateDetails').Invoke($null, [object[]]@($known.PSObject.BaseObject, $warehouse.PSObject.BaseObject)) }
                'properties-trimmed' { $dialogs.GetMethod('CreateDetails').Invoke($null, [object[]]@($padded.PSObject.BaseObject, $warehouse.PSObject.BaseObject)) }
                { $_ -in @('verified-edit', 'default-edit', 'typed-edit', 'rename-edit', 'move-edit', 'replace-edit',
                    'retain-name-edit', 'retain-schema-edit', 'retain-copy-edit') } {
                    $dialogs.GetMethod('CreateEditor').Invoke($null, [object[]]@($warehouse.PSObject.BaseObject, $known.PSObject.BaseObject, 'spatial', $schemas))
                }
                'inferred-edit' { $dialogs.GetMethod('CreateEditor').Invoke($null, [object[]]@($warehouse.PSObject.BaseObject, $inferred.PSObject.BaseObject, 'spatial', $schemas)) }
                'no-as-edit' { $dialogs.GetMethod('CreateEditor').Invoke($null, [object[]]@($warehouse.PSObject.BaseObject, $noAs.PSObject.BaseObject, 'spatial', $schemas)) }
                { $_ -in @('unverified-edit', 'unverified-replacement-edit', 'unverified-copy-edit') } {
                    $dialogs.GetMethod('CreateEditor').Invoke($null, [object[]]@($warehouse.PSObject.BaseObject, $unknown.PSObject.BaseObject, 'spatial', $schemas))
                }
                'new' { $dialogs.GetMethod('CreateEditor').Invoke($null, [object[]]@($warehouse.PSObject.BaseObject, $null, 'spatial', $schemas)) }
                'script' { $dialogs.GetMethod('CreatePreview').Invoke($null, [object[]]@($complete, $warehouse.PSObject.BaseObject, $false, $false, $openQuery)) }
                'ddl' { $dialogs.GetMethod('CreatePreview').Invoke($null, [object[]]@($complete, $warehouse.PSObject.BaseObject, $true, $true, $openQuery)) }
                { $_ -in @('delete-ok', 'delete-cancel') } {
                    $dialogs.GetMethod('CreateDelete').Invoke($null, [object[]]@($known.PSObject.BaseObject, $warehouse.PSObject.BaseObject))
                }
                'delete-unverified' {
                    $dialogs.GetMethod('CreateDelete').Invoke($null, [object[]]@($unknown.PSObject.BaseObject, $warehouse.PSObject.BaseObject))
                }
            }
            try {
                if ($form.IsDisposed -or $form.DialogResult -ne [System.Windows.Forms.DialogResult]::None) {
                    throw 'A newly constructed dialog must not already be closed or have an automatic result.'
                }
                if ($kind -like '*edit' -or $kind -eq 'new') {
                    $returnLabel = $form.Controls.Find('ReturnOverrideLabel', $true)
                    if ($returnLabel.Count -ne 1 -or $returnLabel[0].Text -ne 'Return type') {
                        throw 'New and Edit must label the return-type field Return type.'
                    }
                }
                if ($kind -like '*edit' -or $kind -eq 'new' -or $kind -like 'properties*') {
                    foreach ($caption in @(@('FunctionNameLabel', 'Name'),
                        @('FunctionSetLabel', 'Function set'), @('ExternalNameLabel', 'Fabric function'))) {
                        if ($form.Controls.Find($caption[0], $true)[0].Text -cne $caption[1]) {
                            throw 'Function captions must concisely distinguish the T-SQL identity from the Fabric binding.'
                        }
                    }
                }
                if ($kind -like 'properties*') {
                    foreach ($field in @(@('Schema', 'spatial'), @('FunctionName', 's2'),
                        @('FunctionSet', 'spatial functions'), @('ExternalName', 's2'), @('CurrentReturnType', 'bigint'))) {
                        if ($form.Controls.Find($field[0], $true)[0].Text -ne $field[1]) { throw "Existing $($field[0]) was not prefilled." }
                    }
                    if ($form.Controls.Find('ReturnOverride', $true).Count -ne 0 -or
                        $form.Controls.Find('ReturnOverrideLabel', $true).Count -ne 0) {
                        throw 'Properties must not show the redundant return-declaration field or label.'
                    }
                    if ($form.Controls.Find('PropertyTabs', $true)[0].TabPages.Count -ne 3) {
                        throw 'Properties must retain its three useful tabs.'
                    }
                    $definitionTab = $form.Controls.Find('PropertyTabs', $true)[0].TabPages[2]
                    if ($definitionTab.Controls.Count -ne 1 -or
                        $definitionTab.Controls[0] -isnot [Windows.Forms.TextBox]) {
                        throw 'The Definition tab must contain only SQL, without normalization explanations.'
                    }
                    if ($form.Controls.Find('StoredDefinition', $true)[0].Text -notmatch 'CREATE OR ALTER FUNCTION') {
                        throw 'Displayed function definitions must use CREATE OR ALTER.'
                    }
                    if ($kind -eq 'properties-trimmed') {
                        $expected = $paddedDefinition.Trim().Replace('CREATE FUNCTION', 'CREATE OR ALTER FUNCTION')
                        if ($form.Controls.Find('StoredDefinition', $true)[0].Text -cne $expected -or
                            $padded.Definition -cne $paddedDefinition -or $padded.ModuleDefinition -cne $paddedDefinition) {
                            throw 'Properties must trim only surrounding whitespace, preserving comments, internal blank lines and raw metadata.'
                        }
                    }
                    $grid = $form.Controls.Find('ParameterGrid', $true)[0]
                    if ($grid.Rows.Count -ne 2 -or $grid.Rows[0].Cells[0].Value -ne 1 -or $grid.Rows[1].Cells[0].Value -ne 2) {
                        throw 'Parameters must list only ordered inputs; the return value belongs on General.'
                    }
                }
                if ($kind -notin @('script', 'ddl') -and ($form.Controls.Find('Database', $true).Count -ne 0 -or
                    $form.Controls.Find('Server', $true).Count -ne 0)) { throw 'Connection fields must not appear in properties/editor.' }
                if ($kind -like '*edit') {
                    if ($form.Controls.Find('EditorTabs', $true).Count -ne 0 -or
                        $form.Controls.Find('EditorPanel', $true).Count -ne 1 -or
                        !$form.Controls.Find('EditorPanel', $true)[0].AutoScroll) {
                        throw 'The editor must use a scrollable panel without the single tab.'
                    }
                    foreach ($name in @('CurrentReturnType', 'ParameterGrid', 'StoredDefinition')) {
                        if ($form.Controls.Find($name, $true).Count -ne 0) { throw "The editor must not display non-editable field $name." }
                    }
                    $schemaChoice = $form.Controls.Find('Schema', $true)[0]
                    $nameField = $form.Controls.Find('FunctionName', $true)[0]
                    if ($schemaChoice.Items.Count -ne 3 -or !$schemaChoice.Items.Contains('dbo') -or
                        !$schemaChoice.Items.Contains('api') -or !$schemaChoice.Items.Contains('spatial')) {
                        throw 'Schema choices must contain only non-system warehouse schemas.'
                    }
                    if ($schemaChoice.DropDownStyle -ne [System.Windows.Forms.ComboBoxStyle]::DropDownList -or
                        $schemaChoice.SelectedItem -ne 'spatial' -or $nameField.ReadOnly -or $nameField.Text -ne 's2') {
                        throw 'Editing must prefill an editable name and an existing-schema dropdown.'
                    }
                    if ($form.Controls.Find('FunctionIdentity', $true)[0].Text -notmatch 'drops the original' -or
                        $form.Controls.Find('FunctionIdentity', $true)[0].Text -notmatch "Permissions aren't preserved" -or
                        $form.Controls.Find('FunctionIdentity', $true)[0].Text -notmatch "callers aren't redirected") {
                        throw 'Identity edits must explain replacement, lost permissions and unchanged callers.'
                    }
                    $retainChoice = $form.Controls.Find('RetainExistingFunction', $true)[0]
                    if ($retainChoice.Checked) { throw 'Retain existing function must start unchecked.' }
                    $returnChoice = $form.Controls.Find('ReturnOverride', $true)[0]
                    if ($returnChoice.Items[0] -ne '(inferred)') { throw 'The first return-type option must be (inferred).' }
                }
                if ($kind -eq 'unverified-edit') {
                    if ($form.Controls.Find('BindingStatus', $true)[0].Text -notmatch 'Binding unavailable' -or
                        $returnChoice.Text -ne '(inferred)' -or $form.Controls.Find('FunctionSet', $true)[0].Text -ne '') {
                        throw 'Unknown bindings must remain unverified rather than guessed.'
                    }
                }
                if ($kind -eq 'inferred-edit' -and $returnChoice.Text -ne '(inferred)') {
                    throw 'An inferred catalog return type must not be prefilled as an explicit override.'
                }
                if ($kind -eq 'no-as-edit' -and ($returnChoice.Text -ne 'bigint' -or
                    $form.Controls.Find('FunctionSet', $true)[0].Text -ne 'spatial functions' -or
                    $form.Controls.Find('BindingStatus', $true).Count -ne 0)) {
                    throw 'A complete definition without AS must prefill a verified binding and explicit return override.'
                }
                if (($kind -like '*edit' -and $kind -notlike 'unverified-*') -or $kind -eq 'new') {
                    if ($form.Controls.Find('BindingStatus', $true).Count -ne 0) {
                        throw 'Verified editors and New must not add redundant binding-status messages.'
                    }
                }
                if ($kind -eq 'default-edit') { $returnChoice.SelectedIndex = 0 }
                if ($kind -eq 'typed-edit') { $returnChoice.SelectedIndex = -1; $returnChoice.Text = 'decimal(20,4)' }
                if ($kind -in @('rename-edit', 'replace-edit', 'unverified-replacement-edit',
                    'retain-name-edit', 'retain-copy-edit', 'unverified-copy-edit')) { $nameField.Text = 'renamed_s2' }
                if ($kind -in @('move-edit', 'replace-edit', 'unverified-replacement-edit',
                    'retain-schema-edit', 'retain-copy-edit', 'unverified-copy-edit')) { $schemaChoice.SelectedItem = 'api' }
                if ($kind -in @('unverified-replacement-edit', 'unverified-copy-edit')) {
                    $form.Controls.Find('FunctionSet', $true)[0].Text = 'spatial functions'
                    $form.Controls.Find('ExternalName', $true)[0].Text = 's2'
                    $form.Controls.Find('AcceptUnverified', $true)[0].Checked = $true
                }
                $retainExpected = $kind -in @('retain-name-edit', 'retain-schema-edit', 'retain-copy-edit', 'unverified-copy-edit')
                if ($retainExpected) { $retainChoice.Checked = $true }
                if ($kind -eq 'new') {
                    $returnChoice = $form.Controls.Find('ReturnOverride', $true)[0]
                    if ($returnChoice.Items[0] -ne '(inferred)' -or $returnChoice.Text -ne '(inferred)' -or
                        $form.Controls.Find('DialogDescription', $true)[0].Text -ne
                            "Enter the schema/name of the external function and the function set/name of the referenced Fabric UDF." -or
                        $form.Controls.Find('ReturnTypeHelp', $true)[0].Text -ne
                            'The return type can be inferred from Python definitions or explicitly defined.') {
                        throw 'New must explain the T-SQL/Fabric names and inferred versus explicit Python return type.'
                    }
                    if ($form.Controls.Find('RetainExistingFunction', $true).Count -ne 0 -or
                        $form.Controls.Find('EditorTabs', $true).Count -ne 0) {
                        throw 'New must use the simple panel and must not offer retaining a nonexistent original.'
                    }
                    $schemaChoice = $form.Controls.Find('Schema', $true)[0]
                    if ($schemaChoice.DropDownStyle -ne [System.Windows.Forms.ComboBoxStyle]::DropDownList -or
                        $schemaChoice.SelectedItem -ne 'spatial' -or $schemaChoice.Items.Count -ne 3) {
                        throw 'New functions must select only user schemas, initially the folder schema.'
                    }
                    $schemaChoice.SelectedItem = 'api'
                    $form.Controls.Find('FunctionName', $true)[0].Text = 'new_function'
                    $form.Controls.Find('FunctionSet', $true)[0].Text = 'spatial functions'
                    $form.Controls.Find('ExternalName', $true)[0].Text = 's2'
                }
                if ($kind -eq 'script' -and $form.Controls.Find('ApplyDdl', $true).Count -ne 0) { throw 'A read-only script must not offer DDL execution.' }
                if ($kind -like 'properties*' -or $kind -like '*edit') {
                    if ($form.Controls.Find('FunctionSection', $true).Count -ne 0) {
                        throw 'Properties/Edit must omit the redundant blue Function heading.'
                    }
                }
                if ($kind -like 'delete-*') {
                    $grid = $form.Controls.Find('DeleteObjectGrid', $true)[0]
                    if ($form.Text -ne 'Delete Object' -or $grid.Rows.Count -ne 1 -or
                        $grid.Rows[0].Cells['ObjectName'].Value -ne 'spatial.s2' -or
                        $grid.Rows[0].Cells['ObjectType'].Value -ne 'External Function' -or
                        !$grid.ReadOnly -or $grid.AllowUserToAddRows -or $grid.AllowUserToDeleteRows) {
                        throw 'Deletion must identify exactly the selected external function in a read-only object list.'
                    }
                    foreach ($name in @('StoredDefinition', 'ApplyDdl', 'OpenQuery')) {
                        if ($form.Controls.Find($name, $true).Count -ne 0) {
                            throw 'Deletion must use OK/Cancel, not the SQL preview or another confirmation.'
                        }
                    }
                    if ($form.AcceptButton.Text -ne 'OK' -or $form.CancelButton.Text -ne 'Cancel' -or
                        $form.AcceptButton.DialogResult -ne [Windows.Forms.DialogResult]::OK -or
                        $form.CancelButton.DialogResult -ne [Windows.Forms.DialogResult]::Cancel) {
                        throw 'Deletion must have explicit OK/Cancel actions and keyboard defaults.'
                    }
                    if ($form.Controls.Find('DeleteTarget', $true)[0].Text -ne 'Database: FunctionDW') {
                        throw 'Delete Object must show only the database, without the server name.'
                    }
                    $actualIcon = $form.Icon.ToBitmap()
                    try {
                        if ($actualIcon.Width -ne 32 -or $actualIcon.Height -ne 32) {
                            throw 'Delete Object must use a 32px delete icon.'
                        }
                        foreach ($point in @(@(0, 0), @(16, 2), @(2, 16))) {
                            if ($actualIcon.GetPixel($point[0], $point[1]).A -ne 0) {
                                throw 'The delete icon must be a plain X on a transparent background, not a circle.'
                            }
                        }
                        foreach ($point in @(@(16, 16), @(8, 8), @(24, 8))) {
                            $pixel = $actualIcon.GetPixel($point[0], $point[1])
                            if ($pixel.A -eq 0 -or $pixel.R -le $pixel.G * 2 -or $pixel.R -le $pixel.B * 2) {
                                throw 'The delete icon must contain two red diagonals.'
                            }
                        }
                    } finally { $actualIcon.Dispose() }
                }
                [DialogLayoutChecks]::ScaleForTest($form, [float]$scale)
                [DialogLayoutChecks]::Check($form)
                $form.Size = $form.MinimumSize
                [DialogLayoutChecks]::Check($form)
                $form.Size = New-Object System.Drawing.Size -ArgumentList ($form.Width + 240), ($form.Height + 160)
                [DialogLayoutChecks]::Check($form)
                if ($kind -eq 'ddl') {
                    [void][Windows.Forms.Button].GetMethod('OnClick', [Reflection.BindingFlags]'Instance,NonPublic').Invoke(
                        $form.Controls.Find('ApplyDdl', $true)[0].PSObject.BaseObject, [object[]]@([EventArgs]::Empty))
                    if ($form.DialogResult -ne [Windows.Forms.DialogResult]::OK) {
                        throw 'Preview Apply must return immediately without another confirmation.'
                    }
                }
                if ($kind -like 'delete-*') {
                    $button = if ($kind -eq 'delete-cancel') { $form.CancelButton } else { $form.AcceptButton }
                    [void][Windows.Forms.Button].GetMethod('OnClick', [Reflection.BindingFlags]'Instance,NonPublic').Invoke(
                        $button.PSObject.BaseObject, [object[]]@([EventArgs]::Empty))
                    $expectedResult = if ($kind -eq 'delete-cancel') {
                        [Windows.Forms.DialogResult]::Cancel
                    } else { [Windows.Forms.DialogResult]::OK }
                    if ($form.DialogResult -ne $expectedResult) {
                        throw 'Delete confirmation must return only the selected OK/Cancel result without executing SQL.'
                    }
                }
                if ($kind -in @('verified-edit', 'no-as-edit', 'default-edit', 'typed-edit', 'rename-edit', 'move-edit',
                    'replace-edit', 'retain-name-edit', 'retain-schema-edit', 'retain-copy-edit',
                    'inferred-edit', 'unverified-replacement-edit', 'unverified-copy-edit', 'new')) {
                    $form.PreviewSql = [Func[FabricExternalFunctions.Core.ProxyDeclaration,bool,Windows.Forms.DialogResult]]{
                        param($declaration, $retain) [Windows.Forms.DialogResult]::OK
                    }
                    $button = $form.Controls.Find('PreviewSql', $true)[0]
                    [void][System.Windows.Forms.Button].GetMethod('OnClick', [Reflection.BindingFlags]'Instance,NonPublic').Invoke(
                        $button.PSObject.BaseObject, [object[]]@([EventArgs]::Empty))
                    if ($form.DialogResult -ne [System.Windows.Forms.DialogResult]::OK -or
                        $form.Declaration.FunctionSet -ne 'spatial functions' -or $form.Declaration.ExternalName -ne 's2') {
                        throw 'Prefilled values did not produce the reviewed declaration.'
                    }
                    if ($kind -in @('default-edit', 'inferred-edit', 'new') -and
                        $form.Declaration.Script($false) -match 'RETURNS') { throw '(inferred) must omit the RETURNS clause.' }
                    if ($kind -eq 'typed-edit' -and $form.Declaration.ReturnOverride -ne 'decimal(20,4)') {
                        throw "A custom precision/scale selected in the dropdown was not preserved: selected='$($returnChoice.Text)', result='$($form.Declaration.ReturnOverride)'."
                    }
                    if ($kind -like '*edit') {
                        $expectedSchema = if ($kind -in @('move-edit', 'replace-edit', 'unverified-replacement-edit',
                            'retain-schema-edit', 'retain-copy-edit', 'unverified-copy-edit')) { 'api' } else { 'spatial' }
                        $expectedName = if ($kind -in @('rename-edit', 'replace-edit', 'unverified-replacement-edit',
                            'retain-name-edit', 'retain-copy-edit', 'unverified-copy-edit')) { 'renamed_s2' } else { 's2' }
                        if ($form.Declaration.Schema -ne $expectedSchema -or $form.Declaration.Name -ne $expectedName) {
                            throw 'Editing ignored the chosen schema or function name.'
                        }
                        if ($form.RetainExistingFunction -ne $retainExpected) { throw 'The retain-existing choice was not captured.' }
                        $changeSql = $form.Declaration.ScriptChange($known, $form.RetainExistingFunction)
                        if ($retainExpected -and (!$changeSql.Contains("EXEC sys.sp_executesql N'" + $form.Declaration.Script($false) + "';") -or
                            $changeSql -match 'DROP FUNCTION')) {
                            throw 'Retaining the existing function must produce CREATE only, never DROP/ALTER.'
                        }
                        $replacementExpected = !$retainExpected -and ($expectedSchema -ne 'spatial' -or $expectedName -ne 's2')
                        if ($changeSql.Contains('DROP FUNCTION IF EXISTS [spatial].[s2];') -ne $replacementExpected) {
                            throw 'Only unchecked identity changes may drop the original.'
                        }
                        if ($changeSql -match 'BEGIN TRAN|COMMIT TRAN|ROLLBACK TRAN') {
                            throw 'Edits, copies and replacements must not wrap function DDL in transactions.'
                        }
                    }
                    if ($kind -eq 'new' -and $form.Declaration.Schema -ne 'api') { throw 'The new function ignored the selected existing schema.' }
                }
            } finally { $form.Dispose() }
        }
        Write-Host "PASS dialog fields, grid, icon, footer and resize layout at $([int]($scale * 100))% font/geometry scaling."
    }
    Write-Host 'No SSMS launch, SQL connection, clipboard changes or DDL execution.'
    exit 0
} catch {
    [Console]::Error.WriteLine($_.Exception.ToString())
    exit 1
}
