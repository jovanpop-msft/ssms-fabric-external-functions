using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows.Forms;
using FabricExternalFunctions.Core;
using Microsoft.Data.SqlClient;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;

namespace FabricExternalFunctions.Ssms
{
    internal static class Dialogs
    {
        public const string DefaultReturnType = "(inferred)";
        internal sealed class EditorWindow : Form
        {
            public ProxyDeclaration? Declaration { get; set; }
            public bool RetainExistingFunction { get; set; }
            public bool Applied { get; set; }
            public Func<ProxyDeclaration, bool, Task>? ExecuteAsync { get; set; }
            public Func<ProxyDeclaration, bool, DialogResult>? PreviewSql { get; set; }
            public Action<Exception, bool>? ReportError { get; set; }
            public Task Submission { get; set; } = Task.CompletedTask;
        }

        private sealed class WindowOwner : IWin32Window
        {
            public WindowOwner(IntPtr handle) { Handle = handle; }
            public IntPtr Handle { get; }
        }

        public static ProxyDeclaration? VerifiedDeclaration(FunctionMetadata metadata, out string reason) =>
            ProxyDeclaration.FromDefinition(metadata.Definition, metadata.Schema, metadata.Name, out reason);

        public static void Details(FunctionMetadata metadata, Warehouse warehouse)
        {
            using (var form = CreateDetails(metadata, warehouse)) Show(form);
        }

        public static Form CreateDetails(FunctionMetadata metadata, Warehouse warehouse)
        {
            var declaration = VerifiedDeclaration(metadata, out string reason);
            var form = Configure(new Form(), "External Function Properties", new Size(940, 680));
            var body = Layout(form, "Function metadata and stored definition.", out var left, out var right);
            var tabs = new TabControl { Name = "PropertyTabs", Dock = DockStyle.Fill };
            body.Controls.Add(tabs);
            var general = FieldsPage(tabs, "General");
            Field(general, "Schema", "Schema", metadata.Schema, true);
            Field(general, "Name", "FunctionName", metadata.Name, true);
            Field(general, "Object ID", "ObjectId", metadata.ObjectId.ToString(), true);
            Field(general, "Last modified", "Modified", metadata.Modified.ToString("G"), true);
            Field(general, "Return type", "CurrentReturnType", metadata.ReturnType ?? "Unavailable", true);
            Section(general, "Fabric binding");
            Field(general, "Function set", "FunctionSet", declaration?.FunctionSet ?? "Unavailable", true);
            Field(general, "Fabric function", "ExternalName", declaration?.ExternalName ?? "Unavailable", true);
            if (declaration == null) Spanning(general, Note(reason, true));
            ParametersPage(tabs, metadata);
            string? definition = SqlText.CreateOrAlterDefinition(metadata.Definition)?.Trim();
            DefinitionPage(form, tabs, definition);
            CopyDefinition(form, left, definition);
            var close = Button("Close", "Close");
            close.DialogResult = DialogResult.Cancel;
            right.Controls.Add(close); form.AcceptButton = close; form.CancelButton = close;
            return form;
        }

        public static ProxyDeclaration? Edit(Warehouse warehouse, FunctionMetadata? existing, string? folderSchema,
            IReadOnlyList<string>? schemas, Func<ProxyDeclaration, bool, Task> execute,
            Action<Exception, bool> reportError, Action<string> openQuery, out bool retainExisting, out bool applied)
        {
            retainExisting = false;
            applied = false;
            using (var form = CreateEditor(warehouse, existing, folderSchema, schemas))
            {
                form.ExecuteAsync = execute;
                form.ReportError = reportError;
                form.PreviewSql = (declaration, retain) =>
                    Preview(form, declaration.ScriptChange(existing, retain), warehouse, true,
                        existing != null && VerifiedDeclaration(existing, out _) == null, openQuery);
                if (Show(form) != DialogResult.OK) return null;
                retainExisting = form.RetainExistingFunction;
                applied = form.Applied;
                return form.Declaration ?? throw new InvalidOperationException("No function declaration was prepared.");
            }
        }

        public static EditorWindow CreateEditor(Warehouse warehouse, FunctionMetadata? existing, string? folderSchema,
            IReadOnlyList<string>? schemas)
        {
            if (schemas == null || schemas.Count == 0)
                throw new ArgumentException("Load schemas before editing a function.", nameof(schemas));
            var userSchemas = schemas.Where(CatalogReader.IsUserSchema).ToArray();
            if (userSchemas.Length == 0)
                throw new ArgumentException("No user schemas are available.", nameof(schemas));
            string selectedSchema = existing?.Schema ??
                (folderSchema != null && userSchemas.Contains(folderSchema) ? folderSchema :
                    userSchemas.Contains("dbo") ? "dbo" : userSchemas[0]);
            if (!userSchemas.Contains(selectedSchema))
            {
                if (!CatalogReader.IsUserSchema(selectedSchema))
                    throw new ArgumentException("This schema is excluded from editing.", nameof(folderSchema));
                throw new ArgumentException("Schema unavailable. Refresh Object Explorer.", nameof(folderSchema));
            }
            var known = existing == null ? null : VerifiedDeclaration(existing, out _);
            bool unverified = existing != null && known == null;
            var form = Configure(new EditorWindow(), existing == null ? "New External Function" : "Edit External Function",
                new Size(940, 680));
            var body = Layout(form, existing == null
                ? "Enter the schema/name of the external function and the function set/name of the referenced Fabric UDF."
                : "Update the function or preview its SQL.", out _, out var right);
            form.ReportError = (error, showDialog) =>
            {
                ActivityLog.LogError("Fabric External Functions", error.ToString());
                if (showDialog && !form.IsDisposed)
                    MessageBox.Show(form, error.Message +
                        "\r\n\r\nNo automatic DDL retry is performed. If execution timed out, check warehouse state before retrying.",
                        "Fabric External Functions", MessageBoxButtons.OK, MessageBoxIcon.Error);
            };
            var panel = new Panel { Name = "EditorPanel", Dock = DockStyle.Fill, AutoScroll = true, Padding = new Padding(12) };
            body.Controls.Add(panel);
            var fields = Fields(panel);
            var validations = new List<Action>();
            Control? invalidInput = null;
            Control? invalidMessage = null;
            string? returnType = null;
            string? setError = null, remoteError = null;
            bool submitting = false;
            void Validate(Control input, Action rule, params Control[] related)
            {
                var check = InputValidation(fields, input, rule, out var message, related);
                validations.Add(() =>
                {
                    if (!check() && invalidInput == null)
                    {
                        invalidInput = input;
                        invalidMessage = message;
                    }
                });
            }
            if (existing == null) Section(fields, "Function");
            var schema = Choice(fields, "Schema", "Schema", userSchemas, selectedSchema, false);
            Validate(schema, () =>
            {
                if (!(schema.SelectedItem is string choice) || !userSchemas.Contains(choice))
                    throw new ArgumentException("Select a schema.");
            });
            var name = Field(fields, "Name", "FunctionName", existing?.Name ?? "", false);
            Validate(name, () => RequiredIdentifier(name.Text, "Enter a name."));
            CheckBox? retain = null;
            if (existing != null)
            {
                const string replacementWarning = "Changing the schema or name drops the original and creates a new function. " +
                    "Permissions aren't preserved and callers aren't redirected. If creation fails, the original isn't restored. " +
                    "Check below to keep it.";
                var identity = Note(replacementWarning, true);
                identity.Name = "FunctionIdentity"; Spanning(fields, identity);
                retain = new CheckBox
                {
                    Name = "RetainExistingFunction", Text = "Retain existing function (create a clone)",
                    AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0, 8, 0, 8)
                };
                Spanning(fields, retain);
                var retainChoice = retain;
                void UpdateIdentity()
                {
                    bool changed = (schema.SelectedItem as string) != existing.Schema || name.Text != existing.Name;
                    if (!changed) retainChoice.Checked = false;
                    identity.Text = retainChoice.Checked
                        ? "Creates a clone and keeps the original. Permissions aren't copied and callers aren't redirected."
                        : replacementWarning;
                    identity.Visible = changed;
                    retainChoice.Visible = changed;
                }
                name.TextChanged += (_, __) => UpdateIdentity();
                schema.SelectedIndexChanged += (_, __) => UpdateIdentity();
                retainChoice.CheckedChanged += (_, __) => UpdateIdentity();
                UpdateIdentity();
                Validate(retainChoice, () =>
                {
                    bool sameIdentity = (schema.SelectedItem as string) == existing.Schema && name.Text == existing.Name;
                    if (retainChoice.Checked && sameIdentity)
                        throw new ArgumentException("Choose a different name or schema to retain the original.");
                }, name, schema);
            }
            Section(fields, "Fabric binding");
            var set = Field(fields, "Function set", "FunctionSet", known?.FunctionSet ?? "", false);
            set.TextChanged += (_, __) => { setError = null; remoteError = null; };
            Validate(set, () =>
            {
                RequiredIdentifier(set.Text, "Enter a function set.");
                if (setError != null) throw new ArgumentException(setError);
            });
            var remote = Field(fields, "Fabric function", "ExternalName", known?.ExternalName ?? "", false);
            remote.TextChanged += (_, __) => remoteError = null;
            Validate(remote, () =>
            {
                RequiredIdentifier(remote.Text, "Enter a Fabric function name.");
                if (remoteError != null) throw new ArgumentException(remoteError);
            }, set);
            var returnTypes = new List<string> { DefaultReturnType, "bigint", "binary(50)", "bit", "char(50)", "date",
                "datetime2", "decimal(18,2)", "float", "int", "numeric(18,2)", "real", "smallint",
                "time", "tinyint", "uniqueidentifier", "varbinary(max)", "varchar(100)", "varchar(max)" };
            if (known?.ReturnOverride != null && !returnTypes.Contains(known.ReturnOverride)) returnTypes.Add(known.ReturnOverride);
            var returns = Choice(fields, "Return type", "ReturnOverride", returnTypes, known?.ReturnOverride ?? DefaultReturnType, true);
            Validate(returns, () =>
            {
                string value = returns.Text.Trim();
                if (value.Length == 0) throw new ArgumentException("Choose (inferred) or enter a SQL type.");
                returnType = string.Equals(value, DefaultReturnType, StringComparison.OrdinalIgnoreCase)
                    ? null : SqlTypes.ValidateReturnType(value);
            });
            var returnHelp = Note("The return type can be inferred from Python definitions or explicitly defined.", false);
            returnHelp.Name = "ReturnTypeHelp";
            Spanning(fields, returnHelp);
            if (unverified)
            {
                var status = Note("Binding unavailable. Enter and confirm a replacement.", true);
                status.Name = "BindingStatus";
                Spanning(fields, status);
            }
            var acceptUnknown = new CheckBox
            {
                Name = "AcceptUnverified", Text = "I accept this unverified binding and return type.",
                AutoSize = true, Dock = DockStyle.Fill, Visible = unverified, Margin = new Padding(0, 8, 0, 8)
            };
            Spanning(fields, acceptUnknown);
            if (unverified)
                Validate(acceptUnknown, () =>
                {
                    if (!acceptUnknown.Checked) throw new ArgumentException("Confirm the binding and return type.");
                });
            bool ValidateInputs()
            {
                invalidInput = null;
                invalidMessage = null;
                foreach (var validate in validations) validate();
                if (invalidInput != null)
                {
                    panel.ScrollControlIntoView(invalidInput);
                    invalidInput.Focus();
                    panel.ScrollControlIntoView(invalidMessage ?? invalidInput);
                    return false;
                }
                return true;
            }
            bool Prepare()
            {
                form.Declaration = null;
                setError = null; remoteError = null;
                if (!ValidateInputs()) return false;
                var declaration = new ProxyDeclaration(
                    (schema.SelectedItem as string) ?? throw new InvalidOperationException("The validated schema is unavailable."),
                    name.Text, set.Text, remote.Text, returnType);
                bool retainExisting = retain?.Checked ?? false;
                _ = declaration.ScriptChange(existing, retainExisting);
                form.Declaration = declaration;
                form.RetainExistingFunction = retainExisting;
                return true;
            }
            async Task ApplyAsync()
            {
                if (submitting || !Prepare()) return;
                var declaration = form.Declaration ?? throw new InvalidOperationException("No function declaration was prepared.");
                bool retainExisting = form.RetainExistingFunction;
                bool bindingFailure = false;
                submitting = true;
                panel.Enabled = false; right.Enabled = false; form.ControlBox = false; form.UseWaitCursor = true;
                try
                {
                    var execute = form.ExecuteAsync ?? throw new InvalidOperationException("Function execution is unavailable.");
                    await execute(declaration, retainExisting);
                    form.Applied = true;
                }
                catch (Exception error)
                {
                    form.Declaration = null;
                    var errors = BindingErrors(error);
                    if (!form.IsDisposed)
                    {
                        errors.TryGetValue("FunctionSet", out setError);
                        errors.TryGetValue("ExternalName", out remoteError);
                        bindingFailure = errors.Count > 0;
                    }
                    form.ReportError?.Invoke(error, errors.Count == 0 && !form.IsDisposed);
                }
                finally
                {
                    submitting = false;
                    if (!form.IsDisposed)
                    {
                        panel.Enabled = true; right.Enabled = true; form.ControlBox = true; form.UseWaitCursor = false;
                    }
                }
                if (form.Applied && !form.IsDisposed) form.DialogResult = DialogResult.OK;
                else if (bindingFailure && !form.IsDisposed) ValidateInputs();
            }
            var apply = Button(existing == null ? "Create" : "Save", "ApplyFunction");
            var next = Button("Preview SQL", "PreviewSql");
            var cancel = Button("Cancel", "Cancel"); cancel.DialogResult = DialogResult.Cancel;
            apply.Click += (_, __) => { if (!submitting && !form.Applied) form.Submission = ApplyAsync(); };
            next.Click += (_, __) => Guard(form, () =>
            {
                if (submitting || form.Applied || !Prepare()) return;
                var preview = form.PreviewSql ?? throw new InvalidOperationException("SQL preview is unavailable.");
                var declaration = form.Declaration ?? throw new InvalidOperationException("No function declaration was prepared.");
                var result = preview(declaration, form.RetainExistingFunction);
                if (result == DialogResult.OK || result == DialogResult.Yes) form.DialogResult = result;
            });
            form.FormClosing += (_, args) => { if (submitting) args.Cancel = true; };
            right.Controls.Add(apply); right.Controls.Add(next); right.Controls.Add(cancel);
            form.AcceptButton = next; form.CancelButton = cancel;
            return form;
        }

        private static IReadOnlyDictionary<string, string> BindingErrors(Exception error)
        {
            var result = new Dictionary<string, string>();
            if (!(error is SqlException sql)) return result;
            foreach (SqlError detail in sql.Errors)
            {
                if (detail.Class <= 10 || detail.Number == 3621) continue;
                string field;
                if (detail.Number == 31727) field = "ExternalName";
                else if (detail.Number == 31728) field = "FunctionSet";
                else return new Dictionary<string, string>();
                if (!result.ContainsKey(field)) result.Add(field, detail.Message);
            }
            return result;
        }

        public static DialogResult Preview(Form owner, string sql, Warehouse warehouse, bool allowApply, bool unverified, Action<string> openQuery)
        {
            using (var form = CreatePreview(sql, warehouse, allowApply, unverified, openQuery))
                return Show(form, owner);
        }

        public static Form CreatePreview(string sql, Warehouse warehouse, bool allowApply, bool unverified, Action<string> openQuery)
        {
            string header = "-- Target: " + warehouse.DisplayName.Replace("\r", " ").Replace("\n", " ") + "\r\n" +
                (unverified ? "-- Unverified binding and return type\r\n" : "");
            var form = Configure(new Form(), "Review External Function SQL", new Size(1000, 700));
            var body = Layout(form, allowApply ? "Review the SQL and target database."
                : "Copy, save or open the script. No SQL is run.", out var left, out var right);
            left.WrapContents = false;
            var script = SqlBox(form, header + sql);
            body.Controls.Add(script);
            var copy = Button("Copy SQL", "CopySql");
            copy.Click += (_, __) => Guard(form, () => Clipboard.SetText(header + sql));
            var save = Button("Save SQL", "SaveSql");
            save.Click += (_, __) => Guard(form, () =>
            {
                using (var picker = new SaveFileDialog { Filter = "SQL script|*.sql", FileName = "external-function.sql" })
                    if (picker.ShowDialog(form) == DialogResult.OK) System.IO.File.WriteAllText(picker.FileName, header + sql);
            });
            var open = Button("Open in a new query", "OpenQuery");
            open.Click += (_, __) => Guard(form, () =>
            {
                openQuery(header + sql);
                form.DialogResult = DialogResult.Yes;
            });
            left.Controls.Add(copy); left.Controls.Add(save); left.Controls.Add(open);
            if (allowApply)
            {
                var apply = Button("Apply", "ApplyDdl"); apply.DialogResult = DialogResult.OK;
                right.Controls.Add(apply);
            }
            var close = Button(allowApply ? "Cancel" : "Close", "Close");
            close.DialogResult = DialogResult.Cancel;
            right.Controls.Add(close); form.CancelButton = close;
            foreach (Control button in left.Controls.Cast<Control>().Concat(right.Controls.Cast<Control>()))
                button.MinimumSize = new Size(80, 34);
            form.Layout += (_, __) =>
            {
                int width = left.Controls.Cast<Control>().Concat(right.Controls.Cast<Control>()).Sum(button =>
                    Math.Max(button.MinimumSize.Width, button.GetPreferredSize(Size.Empty).Width) + button.Margin.Horizontal) +
                    body.Parent!.Padding.Horizontal + form.Width - form.ClientSize.Width;
                if (form.MinimumSize.Width < width)
                    form.MinimumSize = new Size(width, form.MinimumSize.Height);
            };
            return form;
        }

        public static bool Delete(FunctionMetadata metadata, Warehouse warehouse)
        {
            using (var form = CreateDelete(metadata, warehouse))
                return Show(form) == DialogResult.OK;
        }

        public static Form CreateDelete(FunctionMetadata metadata, Warehouse warehouse)
        {
            var form = Configure(new Form(), "Delete Object", new Size(1000, 640), ObjectExplorerImages.Delete(new Size(32, 32)));
            var body = Layout(form, "Deletes the warehouse proxy, not the Fabric function.", out _, out var right);
            var content = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2 };
            content.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            content.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            content.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            var target = Note("Database: " + warehouse.Database, false);
            target.Name = "DeleteTarget"; target.MaximumSize = new Size(840, 0);
            content.SizeChanged += (_, __) => target.MaximumSize = new Size(Math.Max(1, content.ClientSize.Width), 0);
            content.Controls.Add(target, 0, 0);
            var grid = new DataGridView
            {
                Name = "DeleteObjectGrid", AccessibleName = "Object to be deleted", Dock = DockStyle.Fill,
                ReadOnly = true, AllowUserToAddRows = false, AllowUserToDeleteRows = false, RowHeadersVisible = false,
                AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
                AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.AllCells,
                ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.AutoSize,
                SelectionMode = DataGridViewSelectionMode.FullRowSelect, MultiSelect = false,
                BackgroundColor = SystemColors.Window, BorderStyle = BorderStyle.FixedSingle
            };
            grid.DefaultCellStyle.WrapMode = DataGridViewTriState.True;
            foreach (var column in new[]
            {
                new DataGridViewTextBoxColumn { Name = "ObjectName", HeaderText = "Object Name", FillWeight = 35, MinimumWidth = 180 },
                new DataGridViewTextBoxColumn { Name = "ObjectType", HeaderText = "Object Type", FillWeight = 25, MinimumWidth = 160 },
                new DataGridViewTextBoxColumn { Name = "Status", HeaderText = "Status", FillWeight = 15, MinimumWidth = 80 },
                new DataGridViewTextBoxColumn { Name = "Message", HeaderText = "Message", FillWeight = 25, MinimumWidth = 140 }
            })
            {
                column.SortMode = DataGridViewColumnSortMode.NotSortable;
                grid.Columns.Add(column);
            }
            grid.Rows.Add(metadata.DisplayName, "External Function", "Ready", "");
            content.Controls.Add(grid, 0, 1); body.Controls.Add(content);
            var ok = Button("OK", "ConfirmDelete"); ok.DialogResult = DialogResult.OK;
            var cancel = Button("Cancel", "CancelDelete"); cancel.DialogResult = DialogResult.Cancel;
            right.Controls.Add(ok); right.Controls.Add(cancel);
            form.AcceptButton = ok; form.CancelButton = cancel;
            return form;
        }

        private static T Configure<T>(T form, string title, Size clientSize, Icon? windowIcon = null) where T : Form
        {
            form.Name = "FabricFunctionDialog"; form.Text = title;
            form.Font = SystemFonts.MessageBoxFont;
            form.AutoScaleDimensions = new SizeF(96, 96); form.AutoScaleMode = AutoScaleMode.Dpi;
            form.ClientSize = clientSize; form.MinimumSize = new Size(800, 560);
            form.StartPosition = FormStartPosition.CenterParent; form.ShowInTaskbar = false;
            var icon = (Icon)(windowIcon ?? ObjectExplorerImages.Function(new Size(32, 32))).Clone();
            form.Icon = icon; form.Disposed += (_, __) => icon.Dispose();
            form.Load += (_, __) =>
            {
                var area = Screen.FromControl(form).WorkingArea; area.Inflate(-12, -12);
                form.MinimumSize = new Size(Math.Min(form.MinimumSize.Width, area.Width), Math.Min(form.MinimumSize.Height, area.Height));
                form.Size = new Size(Math.Min(form.Width, area.Width), Math.Min(form.Height, area.Height));
            };
            return form;
        }

        private static DialogResult Show(Form form, Form? parent = null)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            string id = Guid.NewGuid().ToString("N").Substring(0, 8);
            void Log(string message) => ActivityLog.LogInformation("Fabric External Functions",
                "Dialog " + id + " (" + form.Text + "): " + message);
            form.Shown += (_, __) => Log("shown");
            form.FormClosing += (_, args) => Log("closing; reason=" + args.CloseReason +
                "; result=" + form.DialogResult + "; cancelled=" + args.Cancel);
            form.FormClosed += (_, args) => Log("closed; reason=" + args.CloseReason + "; result=" + form.DialogResult);
            if (parent != null)
            {
                Log("opening with parent dialog owner");
                return form.ShowDialog(parent);
            }
            var shell = Package.GetGlobalService(typeof(SVsUIShell)) as IVsUIShell ??
                throw new NotSupportedException("SSMS dialog ownership services are unavailable.");
            Marshal.ThrowExceptionForHR(shell.GetDialogOwnerHwnd(out IntPtr owner));
            if (owner == IntPtr.Zero) throw new NotSupportedException("SSMS did not provide a dialog-owner window.");
            Log("opening with SSMS dialog owner");
            Marshal.ThrowExceptionForHR(shell.EnableModeless(0));
            try
            {
                return form.ShowDialog(new WindowOwner(owner));
            }
            finally { Marshal.ThrowExceptionForHR(shell.EnableModeless(1)); }
        }

        private static Panel Layout(Form form, string text,
            out FlowLayoutPanel left, out FlowLayoutPanel right)
        {
            var root = new TableLayoutPanel
            {
                Name = "DialogLayout", Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Padding = new Padding(16)
            };
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            var header = new Label
            {
                Name = "DialogDescription", Text = text, AutoSize = true, Dock = DockStyle.Fill,
                Margin = new Padding(0, 0, 0, 12), UseMnemonic = false,
                MaximumSize = new Size(form.ClientSize.Width - root.Padding.Horizontal, 0)
            };
            root.SizeChanged += (_, __) => header.MaximumSize =
                new Size(Math.Max(1, root.ClientSize.Width - root.Padding.Horizontal), 0);
            var body = new Panel { Dock = DockStyle.Fill, Margin = Padding.Empty };
            var footer = new TableLayoutPanel
            {
                Name = "DialogFooter", Dock = DockStyle.Fill, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
                ColumnCount = 2, RowCount = 1, Margin = new Padding(0, 12, 0, 0)
            };
            footer.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            left = new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Dock = DockStyle.Fill, WrapContents = true, Margin = Padding.Empty };
            right = new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Dock = DockStyle.Fill, WrapContents = false, Margin = Padding.Empty };
            footer.Controls.Add(left, 0, 0); footer.Controls.Add(right, 1, 0);
            root.Controls.Add(header, 0, 0); root.Controls.Add(body, 0, 1); root.Controls.Add(footer, 0, 2);
            form.Controls.Add(root);
            return body;
        }

        private static Button Button(string text, string name) => new Button
        {
            Name = name, Text = text, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
            MinimumSize = new Size(104, 34), Padding = new Padding(12, 5, 12, 5),
            Margin = new Padding(4, 2, 4, 2), UseVisualStyleBackColor = true
        };

        private static TableLayoutPanel FieldsPage(TabControl tabs, string text)
        {
            var page = new TabPage(text) { Padding = new Padding(12), AutoScroll = true, UseVisualStyleBackColor = true };
            tabs.TabPages.Add(page);
            return Fields(page);
        }

        private static TableLayoutPanel Fields(Control parent)
        {
            var fields = new TableLayoutPanel { AutoSize = true, Dock = DockStyle.Top, ColumnCount = 2, RowCount = 0 };
            fields.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            fields.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            parent.Controls.Add(fields);
            return fields;
        }

        private static TextBox Field(TableLayoutPanel fields, string label, string name, string value, bool readOnly)
        {
            var box = new TextBox
            {
                Name = name, AccessibleName = label, Text = value, ReadOnly = readOnly,
                BackColor = SystemColors.Window, Dock = DockStyle.Top, Margin = new Padding(0, 5, 0, 5)
            };
            AddField(fields, label, name, box); return box;
        }

        private static ComboBox Choice(TableLayoutPanel fields, string label, string name, IReadOnlyList<string> values,
            string selected, bool allowTyping)
        {
            var box = new ComboBox
            {
                Name = name, AccessibleName = label, Dock = DockStyle.Top, Margin = new Padding(0, 5, 0, 5),
                DropDownStyle = allowTyping ? ComboBoxStyle.DropDown : ComboBoxStyle.DropDownList,
                AutoCompleteSource = AutoCompleteSource.ListItems, AutoCompleteMode = AutoCompleteMode.SuggestAppend,
                MaxDropDownItems = 12
            };
            box.Items.AddRange(values.Cast<object>().ToArray());
            box.SelectedItem = selected;
            AddField(fields, label, name, box); return box;
        }

        private static void AddField(TableLayoutPanel fields, string label, string name, Control control)
        {
            int row = fields.RowCount++;
            fields.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            fields.Controls.Add(new Label
            {
                Name = name + "Label", Text = label, AutoSize = true, Anchor = AnchorStyles.Left,
                Margin = new Padding(0, 8, 16, 8), UseMnemonic = false
            }, 0, row);
            fields.Controls.Add(control, 1, row);
        }

        private static void RequiredIdentifier(string value, string requiredMessage)
        {
            if (string.IsNullOrWhiteSpace(value)) throw new ArgumentException(requiredMessage);
            SqlText.Identifier(value);
        }

        private static Func<bool> InputValidation(TableLayoutPanel fields, Control input, Action rule,
            out Label errorLabel, params Control[] related)
        {
            var message = new Label
            {
                Name = input.Name + "Error", AccessibleName = (input.AccessibleName ?? input.Text) + " error",
                AccessibleRole = AccessibleRole.Alert, AutoSize = true, Dock = DockStyle.Top,
                ForeColor = SystemInformation.HighContrast ? SystemColors.ControlText : Color.Firebrick,
                UseMnemonic = false, Visible = false, Margin = new Padding(0, 0, 0, 6)
            };
            errorLabel = message;
            if (fields.GetColumnSpan(input) > 1) Spanning(fields, message);
            else
            {
                int row = fields.RowCount++;
                fields.RowStyles.Add(new RowStyle(SizeType.AutoSize));
                fields.Controls.Add(message, 1, row);
                message.MaximumSize = new Size(Math.Max(1, input.Width), 0);
                input.SizeChanged += (_, __) => message.MaximumSize = new Size(Math.Max(1, input.Width), 0);
            }
            bool submitted = false;
            bool Check()
            {
                submitted = true;
                try
                {
                    rule();
                    message.Text = "";
                    message.Visible = false;
                    return true;
                }
                catch (Exception error) when (error is ArgumentException || error is FormatException)
                {
                    message.Text = error.Message;
                    message.Visible = true;
                    return false;
                }
            }
            EventHandler changed = (_, __) => { if (submitted) Check(); };
            foreach (var control in new[] { input }.Concat(related).Distinct())
                if (control is CheckBox checkbox) checkbox.CheckedChanged += changed;
                else control.TextChanged += changed;
            return Check;
        }

        private static void Section(TableLayoutPanel fields, string text)
        {
            Spanning(fields, new Label
            {
                Name = text.Replace(" ", "") + "Section", Text = text, AutoSize = true,
                Margin = new Padding(0, 12, 0, 6), ForeColor = SystemColors.HotTrack
            });
        }

        private static Label Note(string text, bool warning) => new Label
        {
            Text = text, AutoSize = true, Dock = DockStyle.Fill, UseMnemonic = false,
            BackColor = warning ? SystemColors.Info : SystemColors.Control,
            ForeColor = warning ? SystemColors.InfoText : SystemColors.ControlText,
            Padding = new Padding(8), Margin = new Padding(0, 8, 0, 4)
        };

        private static void Spanning(TableLayoutPanel fields, Control control)
        {
            int row = fields.RowCount++;
            fields.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            control.MaximumSize = new Size(720, 0);
            fields.Controls.Add(control, 0, row); fields.SetColumnSpan(control, 2);
            fields.SizeChanged += (_, __) => control.MaximumSize = new Size(
                Math.Max(1, fields.ClientSize.Width - control.Margin.Horizontal), 0);
        }

        private static void ParametersPage(TabControl tabs, FunctionMetadata metadata)
        {
            var page = new TabPage("Parameters") { Padding = new Padding(12), UseVisualStyleBackColor = true };
            var grid = new DataGridView
            {
                Name = "ParameterGrid", Dock = DockStyle.Fill, ReadOnly = true, AllowUserToAddRows = false,
                AllowUserToDeleteRows = false, RowHeadersVisible = false, AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
                AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.AllCells, ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.AutoSize,
                SelectionMode = DataGridViewSelectionMode.FullRowSelect, MultiSelect = false, BackgroundColor = SystemColors.Window,
                BorderStyle = BorderStyle.None
            };
            grid.DefaultCellStyle.WrapMode = DataGridViewTriState.True;
            grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Ordinal", FillWeight = 12, MinimumWidth = 64 });
            grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Parameter", FillWeight = 36, MinimumWidth = 140 });
            grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "SQL type", FillWeight = 36, MinimumWidth = 140 });
            foreach (var parameter in metadata.Inputs)
                grid.Rows.Add(parameter.Ordinal, parameter.Name, parameter.SqlType);
            page.Controls.Add(grid); tabs.TabPages.Add(page);
        }

        private static TextBox SqlBox(Form form, string text)
        {
            var font = new Font(FontFamily.GenericMonospace, 10);
            form.Disposed += (_, __) => font.Dispose();
            return new TextBox
            {
                Name = "StoredDefinition", Text = text, ReadOnly = true, Multiline = true, MaxLength = int.MaxValue,
                ScrollBars = ScrollBars.Both, WordWrap = false, Dock = DockStyle.Fill, Font = font,
                BackColor = SystemColors.Window
            };
        }

        private static void DefinitionPage(Form form, TabControl tabs, string? definition)
        {
            var page = new TabPage("Definition") { Padding = new Padding(12), UseVisualStyleBackColor = true };
            page.Controls.Add(SqlBox(form, definition ?? "The warehouse did not return a definition."));
            tabs.TabPages.Add(page);
        }

        private static void CopyDefinition(Form form, FlowLayoutPanel buttons, string? definition)
        {
            var copy = Button("Copy definition", "CopyDefinition"); copy.Enabled = !string.IsNullOrEmpty(definition);
            copy.Click += (_, __) => Guard(form, () => Clipboard.SetText(definition ??
                throw new InvalidOperationException("The warehouse did not return a definition.")));
            buttons.Controls.Add(copy);
        }

        private static void Guard(Form owner, Action action)
        {
            try { action(); }
            catch (Exception error)
            {
                ActivityLog.LogError("Fabric External Functions", error.GetType().Name + ": " + error.Message);
                MessageBox.Show(owner, error.Message, "Script export failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
    }
}
