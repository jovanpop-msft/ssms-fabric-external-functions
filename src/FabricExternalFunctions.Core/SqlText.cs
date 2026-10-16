using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace FabricExternalFunctions.Core
{
    public static class SqlText
    {
        public const string ExternalFunctionTemplate =
            "-- ======================================================\r\n" +
            "-- Create Exteral Function Template for Fabric Data Warehouse\r\n" +
            "-- ======================================================\r\n" +
            "SET ANSI_NULLS ON\r\n" +
            "GO\r\n" +
            "SET QUOTED_IDENTIFIER ON\r\n" +
            "GO\r\n" +
            "-- =============================================\r\n" +
            "-- Author:      <Author, , Name>\r\n" +
            "-- Create Date: <Create Date, , >\r\n" +
            "-- Description: <Description, , >\r\n" +
            "-- =============================================\r\n" +
            "CREATE FUNCTION <Function_Name, sysname, FunctionName>\r\n" +
            "RETURNS <Function_Data_Type, ,int>\r\n" +
            "AS EXTERNAL FUNCTION <Function__Set_Name, sysname, FunctionSetName>.<UDF_Function_Name, sysname, UDSFunctionName>\r\n" +
            "GO\r\n";

        // Conservative keyword set: quote names beyond ordinary non-keyword identifiers.
        private static readonly HashSet<string> Keywords = new HashSet<string>(
            ("ADD ALL ALTER AND ANY AS ASC AUTHORIZATION BACKUP BEGIN BETWEEN BREAK BROWSE BULK BY " +
             "CASCADE CASE CHECK CHECKPOINT CLOSE CLUSTERED COALESCE COLLATE COLUMN COMMIT COMPUTE " +
             "CONSTRAINT CONTAINS CONTAINSTABLE CONTINUE CONVERT CREATE CROSS CURRENT CURRENT_DATE CURRENT_TIME " +
             "CURRENT_TIMESTAMP CURRENT_USER CURSOR DATABASE DBCC DEALLOCATE DECLARE DEFAULT DELETE " +
             "DENY DESC DISK DISTINCT DISTRIBUTED DOUBLE DROP DUMP ELSE END ERRLVL ESCAPE EXCEPT " +
             "EXEC EXECUTE EXISTS EXIT EXTERNAL FETCH FILE FILLFACTOR FOR FOREIGN FREETEXT FREETEXTTABLE FROM " +
             "FULL FUNCTION GOTO GRANT GROUP HAVING HOLDLOCK IDENTITY IDENTITYCOL IDENTITY_INSERT IF IN INDEX INNER INSERT " +
             "INTERSECT INTO IS JOIN KEY KILL LEFT LIKE LINENO LOAD MERGE NATIONAL NOCHECK " +
             "NONCLUSTERED NOT NULL NULLIF OF OFF OFFSETS ON OPEN OPENDATASOURCE OPENQUERY " +
             "OPENROWSET OPENXML OPTION OR ORDER OUTER OVER PERCENT PIVOT PLAN PRECISION PRIMARY " +
             "PRINT PROC PROCEDURE PUBLIC RAISERROR READ READTEXT RECONFIGURE REFERENCES " +
             "REPLICATION RESTORE RESTRICT RETURN RETURNS REVERT REVOKE RIGHT ROLLBACK ROWCOUNT " +
             "ROWGUIDCOL RULE SAVE SCHEMA SECURITYAUDIT SELECT SEMANTICKEYPHRASETABLE " +
             "SEMANTICSIMILARITYDETAILSTABLE SEMANTICSIMILARITYTABLE SESSION_USER " +
             "SET SETUSER SHUTDOWN SOME STATISTICS STOPLIST SYSTEM_USER TABLE TABLESAMPLE TEXTSIZE THEN TO " +
             "TOP TRAN TRANSACTION TRIGGER TRUNCATE TRY_CONVERT TSEQUAL UNION UNIQUE UNPIVOT UPDATE " +
             "UPDATETEXT USE USER VALUES VARYING VIEW WAITFOR WHEN WHERE WHILE WITH WRITETEXT")
            .Split(' '), StringComparer.OrdinalIgnoreCase);

        public static string Identifier(string value)
        {
            if (string.IsNullOrWhiteSpace(value) || value.Length > 128 || value.Any(char.IsControl))
                throw new ArgumentException("Use 1-128 characters with no control characters.");
            return "[" + value.Replace("]", "]]") + "]";
        }
        public static string Qualified(string schema, string name) => Identifier(schema) + "." + Identifier(name);
        public static string CallNamePart(string value)
        {
            Identifier(value);
            return Regex.IsMatch(value, @"^[A-Za-z_][A-Za-z0-9_]*$") && !Keywords.Contains(value)
                ? value : Identifier(value);
        }
        public static string CallName(string schema, string name) => CallNamePart(schema) + "." + CallNamePart(name);
        public static string CallName(string database, string schema, string name) =>
            CallNamePart(database) + "." + CallName(schema, name);

        public static string? CreateOrAlterDefinition(string? definition) => FunctionDefinition(definition, true);

        public static string? FunctionDefinition(string? definition, bool createOrAlter)
        {
            if (definition == null) return null;
            List<SqlToken> tokens;
            try
            {
                tokens = SqlTokens.Read(definition, 2);
                if (tokens.Count == 2 && !tokens[0].Delimited && !tokens[1].Delimited &&
                    tokens[0].Value.Equals("CREATE", StringComparison.OrdinalIgnoreCase) &&
                    tokens[1].Value.Equals("OR", StringComparison.OrdinalIgnoreCase))
                    tokens = SqlTokens.Read(definition, 4);
            }
            catch (FormatException)
            {
                // Unrecognized catalog text remains visible; scripting must not require a complete parse.
                return definition;
            }
            if (tokens.Count < 2 || tokens.Any(token => token.Delimited)) return definition;
            int lastOperationToken;
            if ((tokens[0].Value.Equals("CREATE", StringComparison.OrdinalIgnoreCase) ||
                 tokens[0].Value.Equals("ALTER", StringComparison.OrdinalIgnoreCase)) &&
                tokens[1].Value.Equals("FUNCTION", StringComparison.OrdinalIgnoreCase))
                lastOperationToken = 0;
            else if (tokens.Count == 4 && tokens[0].Value.Equals("CREATE", StringComparison.OrdinalIgnoreCase) &&
                tokens[1].Value.Equals("OR", StringComparison.OrdinalIgnoreCase) &&
                (tokens[2].Value.Equals("ALTER", StringComparison.OrdinalIgnoreCase) ||
                 tokens[2].Value.Equals("REPLACE", StringComparison.OrdinalIgnoreCase)) &&
                tokens[3].Value.Equals("FUNCTION", StringComparison.OrdinalIgnoreCase))
                lastOperationToken = 2;
            else return definition;
            string operation = createOrAlter ? "CREATE OR ALTER" : "CREATE";
            int end = tokens[lastOperationToken].Start + tokens[lastOperationToken].Value.Length;
            string originalOperation = definition.Substring(tokens[0].Start, end - tokens[0].Start);
            if (originalOperation.Equals(operation, StringComparison.OrdinalIgnoreCase)) return definition;
            var rewritten = new StringBuilder(definition.Substring(0, tokens[0].Start)).Append(operation);
            int position = tokens[0].Start + tokens[0].Value.Length;
            for (int i = 1; i <= lastOperationToken; i++)
            {
                string trivia = definition.Substring(position, tokens[i].Start - position);
                if (trivia.Contains("/*") || trivia.Contains("--")) rewritten.Append(trivia);
                position = tokens[i].Start + tokens[i].Value.Length;
            }
            return rewritten.Append(definition.Substring(end)).ToString();
        }
    }

    public static class SqlTypes
    {
        public static string ArgumentLiteral(string? sqlType)
        {
            if (sqlType == null || string.IsNullOrWhiteSpace(sqlType) ||
                string.Equals(sqlType.Trim(), "unavailable", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("An input parameter type is unavailable. Refresh metadata.");
            var match = System.Text.RegularExpressions.Regex.Match(sqlType.Trim(),
                @"^(?<name>[A-Za-z][A-Za-z0-9_]*)(?:\s*\(\s*(?:max|\d+)(?:\s*,\s*\d+)?\s*\))?$",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);
            if (match.Success)
            {
                switch (match.Groups["name"].Value.ToLowerInvariant())
                {
                    case "bigint": case "int": case "smallint": case "tinyint": case "bit":
                    case "decimal": case "numeric": case "float": case "real": case "money": case "smallmoney":
                        return "0";
                    case "char": case "varchar": case "text": case "xml":
                    case "date": case "datetime": case "smalldatetime": case "datetime2": case "datetimeoffset": case "time":
                        return "''";
                    case "nchar": case "nvarchar": case "ntext": case "sysname":
                        return "N''";
                    case "binary": case "varbinary": case "image": case "timestamp": case "rowversion":
                        return "0x";
                    case "uniqueidentifier":
                        return "'00000000-0000-0000-0000-000000000000'";
                    case "json":
                        return "'{}'";
                    case "sql_variant":
                        return "NULL";
                }
            }
            throw new FormatException("No literal placeholder is supported for parameter type " + sqlType +
                ". Enter its argument manually in a query.");
        }

        public static string Format(string name, int length, int precision, int scale)
        {
            switch (name.ToLowerInvariant())
            {
                case "varchar": case "char": case "varbinary": case "binary":
                    return name + "(" + (length == -1 ? "max" : length.ToString(System.Globalization.CultureInfo.InvariantCulture)) + ")";
                case "nvarchar": case "nchar":
                    return name + "(" + (length == -1 ? "max" : (length / 2).ToString(System.Globalization.CultureInfo.InvariantCulture)) + ")";
                case "numeric": case "decimal": return name + "(" + precision + "," + scale + ")";
                case "datetime2": case "datetimeoffset": case "time": return name + "(" + scale + ")";
                default: return name;
            }
        }

        public static string ValidateReturnType(string value)
        {
            var tokens = SqlTokens.Read(value);
            if (tokens.Count == 0 || tokens.Any(token => token.Delimited))
                throw new FormatException("Select a supported SQL return type without delimited type names or arguments.");
            string name = tokens[0].Value.ToLowerInvariant();
            var args = new List<string>();
            int i = 1;
            if (i < tokens.Count && tokens[i].Value == "(")
            {
                i++;
                if (i >= tokens.Count) throw new FormatException("Missing type size.");
                args.Add(tokens[i++].Value.ToLowerInvariant());
                if (i < tokens.Count && tokens[i].Value == ",")
                {
                    i++; if (i >= tokens.Count) throw new FormatException("Missing scale.");
                    args.Add(tokens[i++].Value);
                }
                if (i >= tokens.Count || tokens[i++].Value != ")") throw new FormatException("Invalid return type.");
            }
            if (i != tokens.Count) throw new FormatException("Unexpected text in return type.");
            bool Number(string s, int min, int max) => int.TryParse(s, out int n) && n >= min && n <= max;
            switch (name)
            {
                case "varchar": case "varbinary":
                    if (args.Count != 1 || !(args[0] == "max" || Number(args[0], 1, 8000))) throw new FormatException("Size must be 1-8000 or max.");
                    break;
                case "char": case "binary":
                    if (args.Count != 1 || !Number(args[0], 1, 8000)) throw new FormatException("Size must be 1-8000.");
                    break;
                case "decimal": case "numeric":
                    if (args.Count != 2 || !Number(args[0], 1, 38) || !Number(args[1], 0, int.Parse(args[0])))
                        throw new FormatException("Precision must be 1-38 and scale 0-precision.");
                    break;
                case "datetime2": case "time":
                    if (args.Count > 1 || (args.Count == 1 && !Number(args[0], 0, 7))) throw new FormatException("Scale must be 0-7.");
                    break;
                case "float":
                    if (args.Count > 1 || (args.Count == 1 && !Number(args[0], 1, 53))) throw new FormatException("Float precision must be 1-53.");
                    break;
                case "bigint": case "int": case "smallint": case "tinyint": case "bit":
                case "real": case "date": case "uniqueidentifier":
                    if (args.Count != 0) throw new FormatException("This type takes no size.");
                    break;
                default: throw new FormatException("Unsupported Fabric return type.");
            }
            return name + (args.Count == 0 ? "" : "(" + string.Join(",", args) + ")");
        }
    }
}
