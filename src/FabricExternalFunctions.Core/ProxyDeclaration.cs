using System;
using System.Collections.Generic;
using System.Text;

namespace FabricExternalFunctions.Core
{
    internal sealed class SqlToken
    {
        public SqlToken(string value, bool delimited = false, int start = 0)
        { Value = value; Delimited = delimited; Start = start; }
        public string Value { get; }
        public bool Delimited { get; }
        public int Start { get; }
    }

    internal static class SqlTokens
    {
        public static List<SqlToken> Read(string sql, int maxTokens = int.MaxValue)
        {
            var result = new List<SqlToken>();
            int i = 0;
            while (i < sql.Length && result.Count < maxTokens)
            {
                if (char.IsWhiteSpace(sql[i])) { i++; continue; }
                if (i + 1 < sql.Length && sql[i] == '-' && sql[i + 1] == '-')
                { while (i < sql.Length && sql[i] != '\r' && sql[i] != '\n') i++; continue; }
                if (i + 1 < sql.Length && sql[i] == '/' && sql[i + 1] == '*')
                {
                    int depth = 1; i += 2;
                    while (i < sql.Length && depth > 0)
                    {
                        if (i + 1 < sql.Length && sql[i] == '/' && sql[i + 1] == '*') { depth++; i += 2; }
                        else if (i + 1 < sql.Length && sql[i] == '*' && sql[i + 1] == '/') { depth--; i += 2; }
                        else i++;
                    }
                    if (depth != 0) throw new FormatException("Unterminated SQL comment.");
                    continue;
                }
                if (sql[i] == '[' || sql[i] == '"')
                {
                    int tokenStart = i;
                    char close = sql[i++] == '[' ? ']' : '"';
                    var value = new StringBuilder(); bool closed = false;
                    while (i < sql.Length)
                    {
                        char c = sql[i++];
                        if (c != close) { value.Append(c); continue; }
                        if (i < sql.Length && sql[i] == close) { value.Append(close); i++; }
                        else { closed = true; break; }
                    }
                    if (!closed) throw new FormatException("Unterminated identifier.");
                    result.Add(new SqlToken(value.ToString(), true, tokenStart)); continue;
                }
                if (".(),;".IndexOf(sql[i]) >= 0) { result.Add(new SqlToken(sql[i].ToString(), false, i)); i++; continue; }
                if (!(char.IsLetterOrDigit(sql[i]) || sql[i] == '_' || sql[i] == '@' || sql[i] == '#'))
                    throw new FormatException("Unsupported character in proxy declaration.");
                int start = i++;
                while (i < sql.Length && (char.IsLetterOrDigit(sql[i]) || "_@$#".IndexOf(sql[i]) >= 0)) i++;
                result.Add(new SqlToken(sql.Substring(start, i - start), false, start));
            }
            return result;
        }
    }

    public sealed class ProxyDeclaration
    {
        public ProxyDeclaration(string schema, string name, string functionSet, string externalName, string? returnOverride)
        {
            SqlText.Identifier(schema); SqlText.Identifier(name);
            SqlText.Identifier(functionSet); SqlText.Identifier(externalName);
            Schema = schema; Name = name; FunctionSet = functionSet; ExternalName = externalName;
            ReturnOverride = string.IsNullOrWhiteSpace(returnOverride) ? null : SqlTypes.ValidateReturnType(returnOverride!);
        }
        public string Schema { get; }
        public string Name { get; }
        public string FunctionSet { get; }
        public string ExternalName { get; }
        public string? ReturnOverride { get; }

        public string Script(bool edit) => Script(edit ? "ALTER" : "CREATE");
        public string ScriptCreateOrAlter() => Script("CREATE OR ALTER");

        public string ScriptChange(FunctionMetadata? existing, bool retainExisting = false)
        {
            if (retainExisting)
            {
                if (existing == null)
                    throw new ArgumentException("Retaining a function requires an existing source function.");
                if (Schema == existing.Schema && Name == existing.Name)
                    throw new ArgumentException("Choose a different function name or schema to retain the existing function.");
            }
            if (existing == null) return Script(false);
            bool sameIdentity = Schema == existing.Schema && Name == existing.Name;
            return GuardedMutation(existing, Script(sameIdentity), sameIdentity ? null : SqlText.Qualified(Schema, Name),
                !sameIdentity && !retainExisting);
        }

        public static string ScriptDrop(FunctionMetadata existing) =>
            existing.ScriptDefinition(FunctionScriptKind.Drop);

        private static string GuardedMutation(FunctionMetadata existing, string statement, string? target, bool replaceExisting)
        {
            string original = SqlText.Qualified(existing.Schema, existing.Name);
            // Function DDL must start its own batch; metadata guards run in autocommit mode.
            string change = statement.Replace("'", "''");
            string execute = "EXEC sys.sp_executesql N'" + change + "';";
            return "IF @@TRANCOUNT > 0\r\n" +
                "    THROW 50000, 'Run this script outside an explicit transaction.', 1;\r\n" +
                "IF ISNULL(OBJECT_ID(N'" + original.Replace("'", "''") + "'), -1) <> " +
                    existing.ObjectId.ToString(System.Globalization.CultureInfo.InvariantCulture) + "\r\n" +
                "    THROW 50000, 'The original function changed or was dropped. Refresh and review again.', 1;\r\n" +
                "IF NOT EXISTS (SELECT 1 FROM sys.objects WHERE object_id = " +
                    existing.ObjectId.ToString(System.Globalization.CultureInfo.InvariantCulture) +
                    " AND type = 'XF' AND modify_date = CONVERT(datetime, N'" +
                    existing.Modified.ToString("yyyy-MM-ddTHH:mm:ss.fff", System.Globalization.CultureInfo.InvariantCulture) + "', 126))\r\n" +
                "    THROW 50000, 'The original function changed. Refresh and review again.', 1;\r\n" +
                (target != null ? "IF OBJECT_ID(N'" + target.Replace("'", "''") + "') IS NOT NULL\r\n" +
                "    THROW 50000, 'The destination object already exists. No function was created.', 1;\r\n" : "") +
                (replaceExisting
                    ? "-- If CREATE fails after DROP, the original is not restored.\r\n" +
                      "BEGIN TRY\r\n    " + ScriptDrop(existing) + "\r\n    " + execute +
                      "\r\nEND TRY\r\nBEGIN CATCH\r\n    THROW;\r\nEND CATCH;"
                    : execute);
        }

        private string Script(string operation) =>
            operation + " FUNCTION " + SqlText.Qualified(Schema, Name) + "\r\n" +
            (ReturnOverride == null ? "" : "RETURNS " + ReturnOverride + "\r\n") +
            "AS EXTERNAL FUNCTION " + SqlText.Qualified(FunctionSet, ExternalName) + ";";

        public static ProxyDeclaration? FromDefinition(string? definition, string schema, string name, out string reason)
        {
            if (definition == null)
            { reason = "External binding unavailable: the warehouse returned no definition (visibility or engine support)."; return null; }
            try
            {
                var declaration = Parse(definition);
                if (declaration.Schema != schema || declaration.Name != name)
                {
                    reason = "Stored declaration identity does not match the current object.";
                    return null;
                }
                reason = "Verified warehouse definition"; return declaration;
            }
            catch (FormatException error)
            {
                reason = "The external binding could not be verified: " + error.Message +
                    " The stored definition may be incomplete or use unsupported syntax.";
                return null;
            }
            catch (ArgumentException error) { reason = "Invalid definition metadata: " + error.Message; return null; }
        }

        public static string? SelectDefinition(string schema, string name, string? objectDefinition, string? moduleDefinition)
        {
            if (FromDefinition(moduleDefinition, schema, name, out _) != null) return moduleDefinition;
            if (FromDefinition(objectDefinition, schema, name, out _) != null) return objectDefinition;
            // Keep the fuller diagnostic text when neither source can establish a complete binding.
            return moduleDefinition != null && (objectDefinition == null || moduleDefinition.Length > objectDefinition.Length)
                ? moduleDefinition : objectDefinition;
        }

        public static ProxyDeclaration Parse(string definition)
        {
            var t = SqlTokens.Read(definition); int i = 0;
            bool Take(string value)
            {
                if (i < t.Count && !t[i].Delimited && string.Equals(t[i].Value, value, StringComparison.OrdinalIgnoreCase))
                { i++; return true; }
                return false;
            }
            void Need(string value) { if (!Take(value)) throw new FormatException("Expected " + value + " in external proxy definition."); }
            string Identifier()
            {
                if (i >= t.Count || (!t[i].Delimited && ".(),;".Contains(t[i].Value)))
                    throw new FormatException("Expected an identifier.");
                var value = t[i++].Value; SqlText.Identifier(value); return value;
            }
            if (Take("CREATE")) { if (Take("OR")) Need("ALTER"); }
            else Need("ALTER");
            Need("FUNCTION"); string schema = Identifier(); Need("."); string name = Identifier();
            string? returns = null;
            if (Take("RETURNS"))
            {
                int start = i < t.Count ? t[i].Start : definition.Length;
                while (i < t.Count && !(!t[i].Delimited &&
                    (t[i].Value.Equals("AS", StringComparison.OrdinalIgnoreCase) ||
                     t[i].Value.Equals("EXTERNAL", StringComparison.OrdinalIgnoreCase))))
                    i++;
                int end = i < t.Count ? t[i].Start : definition.Length;
                returns = SqlTypes.ValidateReturnType(definition.Substring(start, end - start));
            }
            Take("AS"); Need("EXTERNAL"); Need("FUNCTION");
            string set = Identifier(); Need("."); string remote = Identifier();
            Take(";"); if (i != t.Count) throw new FormatException("Unexpected trailing SQL.");
            return new ProxyDeclaration(schema, name, set, remote, returns);
        }
    }
}
