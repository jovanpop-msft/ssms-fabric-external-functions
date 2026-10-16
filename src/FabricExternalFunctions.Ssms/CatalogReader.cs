using System;
using System.Collections.Generic;
using System.Data;
using System.Threading;
using System.Threading.Tasks;
using FabricExternalFunctions.Core;
using Microsoft.Data.SqlClient;

namespace FabricExternalFunctions.Ssms
{
    internal sealed class CatalogReader
    {
        internal static bool IsUserSchema(string name) =>
            !string.IsNullOrWhiteSpace(name) &&
            !name.StartsWith("db_", StringComparison.OrdinalIgnoreCase) &&
            !name.Equals("sys", StringComparison.OrdinalIgnoreCase) &&
            !name.Equals("INFORMATION_SCHEMA", StringComparison.OrdinalIgnoreCase) &&
            !name.Equals("guest", StringComparison.OrdinalIgnoreCase) &&
            !name.Equals("queryinsights", StringComparison.OrdinalIgnoreCase);

        private const string Query = @"
SELECT o.object_id, SCHEMA_NAME(o.schema_id), o.name, o.modify_date,
       p.parameter_id, p.name, t.name, p.max_length, p.precision, p.scale,
       SCHEMA_NAME(t.schema_id), t.is_user_defined,
       CONVERT(nvarchar(max), OBJECT_DEFINITION(o.object_id)),
       CONVERT(nvarchar(max), m.definition)
FROM sys.objects AS o
LEFT JOIN sys.sql_modules AS m ON m.object_id = o.object_id
LEFT JOIN sys.parameters AS p ON p.object_id = o.object_id
LEFT JOIN sys.types AS t ON t.user_type_id = p.user_type_id
WHERE o.type = 'XF' AND (@object_id IS NULL OR o.object_id = @object_id)
  AND (@schema_name IS NULL OR SCHEMA_NAME(o.schema_id) = @schema_name)
ORDER BY o.object_id, p.parameter_id;";

        public async Task<IReadOnlyList<string>> ReadSchemasAsync(Warehouse warehouse, CancellationToken token)
        {
            using (var connection = warehouse.NewConnection())
            {
                await connection.OpenAsync(token).ConfigureAwait(false);
                using (var command = new SqlCommand(
                    "SELECT name FROM sys.schemas ORDER BY name;",
                    connection) { CommandTimeout = 30 })
                using (var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false))
                {
                    var schemas = new List<string>();
                    while (await reader.ReadAsync(token).ConfigureAwait(false))
                    {
                        string name = reader.GetString(0);
                        if (IsUserSchema(name)) schemas.Add(name);
                    }
                    if (schemas.Count == 0) throw new InvalidOperationException("No available warehouse schemas were returned.");
                    return schemas;
                }
            }
        }

        public async Task<IReadOnlyList<FunctionMetadata>> ReadAsync(Warehouse warehouse, int? objectId,
            CancellationToken token, string? schemaName = null)
        {
            using (var connection = warehouse.NewConnection())
            {
                await connection.OpenAsync(token).ConfigureAwait(false);
                using (var command = new SqlCommand(Query, connection) { CommandTimeout = 30 })
                {
                    command.Parameters.Add("@object_id", SqlDbType.Int).Value = objectId.HasValue ? (object)objectId.Value : DBNull.Value;
                    command.Parameters.Add("@schema_name", SqlDbType.NVarChar, 128).Value =
                        schemaName == null ? (object)DBNull.Value : schemaName;
                    using (var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false))
                    {
                        var functions = new List<FunctionMetadata>();
                        var parameters = new List<ParameterMetadata>();
                        int id = -1; string schema = "", name = ""; string? definition = null;
                        string? moduleDefinition = null; DateTime modified = default;
                        void Flush()
                        {
                            if (id >= 0) functions.Add(new FunctionMetadata(id, schema, name, modified, parameters, definition, moduleDefinition));
                            parameters.Clear();
                        }
                        while (await reader.ReadAsync(token).ConfigureAwait(false))
                        {
                            if (reader.GetInt32(0) != id)
                            {
                                Flush(); id = reader.GetInt32(0); schema = reader.GetString(1); name = reader.GetString(2);
                                modified = reader.GetDateTime(3); definition = reader.IsDBNull(12) ? null : reader.GetString(12);
                                moduleDefinition = reader.IsDBNull(13) ? null : reader.GetString(13);
                                definition = ProxyDeclaration.SelectDefinition(schema, name, definition, moduleDefinition);
                            }
                            if (!reader.IsDBNull(4))
                            {
                                string type = reader.IsDBNull(6) ? "unavailable" : reader.GetString(6);
                                if (!reader.IsDBNull(11) && reader.GetBoolean(11))
                                    type = SqlText.Qualified(reader.GetString(10), type);
                                else if (!reader.IsDBNull(6))
                                    type = SqlTypes.Format(type, reader.GetInt16(7), reader.GetByte(8), reader.GetByte(9));
                                parameters.Add(new ParameterMetadata(reader.GetInt32(4),
                                    reader.IsDBNull(5) ? "" : reader.GetString(5), type));
                            }
                        }
                        Flush(); return functions;
                    }
                }
            }
        }

        public async Task ExecuteAsync(Warehouse warehouse, ProxyDeclaration declaration, FunctionMetadata? expected,
            bool retainExisting, CancellationToken token)
        {
            if (!IsUserSchema(declaration.Schema))
                throw new ArgumentException("Choose a non-system warehouse schema for the function.", nameof(declaration));
            string sql = declaration.ScriptChange(expected, retainExisting);
            if (expected != null)
                await ValidateSourceAsync(warehouse, expected, token).ConfigureAwait(false);
            await ExecuteCommandAsync(warehouse, sql, token).ConfigureAwait(false);
        }

        public async Task ExecuteDropAsync(Warehouse warehouse, FunctionMetadata expected, CancellationToken token)
        {
            if (!IsUserSchema(expected.Schema))
                throw new ArgumentException("Choose a non-system warehouse schema for the function.", nameof(expected));
            await ValidateSourceAsync(warehouse, expected, token).ConfigureAwait(false);
            await ExecuteCommandAsync(warehouse, ProxyDeclaration.ScriptDrop(expected), token).ConfigureAwait(false);
        }

        private async Task ValidateSourceAsync(Warehouse warehouse, FunctionMetadata expected, CancellationToken token)
        {
            var current = await ReadAsync(warehouse, expected.ObjectId, token, expected.Schema).ConfigureAwait(false);
            if (current.Count != 1 || !expected.SameVersion(current[0]))
                throw new InvalidOperationException("Function metadata changed or the object was dropped. Refresh and review again.");
        }

        private static async Task ExecuteCommandAsync(Warehouse warehouse, string sql, CancellationToken token)
        {
            using (var connection = warehouse.NewConnection())
            {
                await connection.OpenAsync(token).ConfigureAwait(false);
                using (var command = new SqlCommand(sql, connection) { CommandTimeout = 60 })
                    await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            }
        }
    }
}
