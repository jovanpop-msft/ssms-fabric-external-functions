using System;
using System.Linq;
using System.Text;

namespace FabricExternalFunctions.Core
{
    public static class ExplorerSchema
    {
        public static string? Resolve(string nodeUrnPath, string contextType, string? contextSchema, string nodeName,
            string? schemaFilter, string? queryHint)
        {
            // Standard child folders inherit the Schema context but are not schema nodes.
            if (contextType == "Schema")
                return nodeUrnPath == "Server/Database/Schema" ? Validate(contextSchema) : null;
            if (contextType != "Database" || schemaFilter == null || queryHint == null) return null;
            var objects = queryHint.Split(';').Select(value => value.Trim()).ToArray();
            if (!objects.Contains("Table") || !objects.Contains("View") ||
                !objects.Contains("UserDefinedFunction")) return null;

            // SSMS's BySchema pivot keeps a Database context and a quoted Schema filter.
            if (schemaFilter.Length < 2 || schemaFilter[0] != '\'' ||
                schemaFilter[schemaFilter.Length - 1] != '\'')
                throw new FormatException("The native schema filter is not a quoted schema name.");
            var schema = new StringBuilder();
            for (int i = 1; i < schemaFilter.Length - 1; i++)
            {
                char c = schemaFilter[i];
                if (c == '\'')
                {
                    if (i + 1 >= schemaFilter.Length - 1 || schemaFilter[i + 1] != '\'')
                        throw new FormatException("The native schema filter contains an unescaped quote.");
                    i++;
                }
                schema.Append(c);
            }
            string name = Validate(schema.ToString());
            return string.Equals(name, nodeName, StringComparison.Ordinal) ? name : null;
        }

        private static string Validate(string? schema)
        {
            if (schema == null) throw new ArgumentException("Schema identity is missing from the native schema node.");
            SqlText.Identifier(schema);
            return schema;
        }
    }
}
