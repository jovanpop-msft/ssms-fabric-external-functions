using System;
using System.Collections.Generic;
using System.Linq;

namespace FabricExternalFunctions.Core
{
    public enum FunctionScriptKind { Create, CreateOrAlter, Drop, Select, DeclareVariable, SetVariable, SelectFrom }

    public sealed class ParameterMetadata
    {
        public ParameterMetadata(int ordinal, string name, string sqlType)
        {
            if (ordinal < 0) throw new ArgumentOutOfRangeException(nameof(ordinal));
            Ordinal = ordinal; Name = name; SqlType = sqlType;
        }
        public int Ordinal { get; }
        public string Name { get; }
        public string SqlType { get; }
    }

    public sealed class FunctionMetadata
    {
        public FunctionMetadata(int objectId, string schema, string name, DateTime modified,
            IEnumerable<ParameterMetadata> parameters, string? definition, string? moduleDefinition = null)
        {
            ObjectId = objectId; Schema = schema; Name = name; Modified = modified;
            var ordered = parameters.OrderBy(p => p.Ordinal).ToArray();
            if (ordered.GroupBy(p => p.Ordinal).Any(g => g.Count() != 1))
                throw new ArgumentException("Duplicate parameter ordinals.");
            Parameters = Array.AsReadOnly(ordered);
            Inputs = Array.AsReadOnly(ordered.Where(p => p.Ordinal > 0).ToArray());
            HasSignature = ordered.Length > 0 && ordered.Select((p, index) => p.Ordinal == index).All(matches => matches);
            Definition = definition;
            ModuleDefinition = moduleDefinition;
        }
        public int ObjectId { get; }
        public string Schema { get; }
        public string Name { get; }
        public DateTime Modified { get; }
        public IReadOnlyList<ParameterMetadata> Parameters { get; }
        public IReadOnlyList<ParameterMetadata> Inputs { get; }
        public string? Definition { get; }
        public string? ModuleDefinition { get; }
        public bool IsInSchema(string? schema) => schema == null || string.Equals(Schema, schema, StringComparison.Ordinal);
        public bool HasSignature { get; }
        public string? ReturnType => Parameters.FirstOrDefault(p => p.Ordinal == 0)?.SqlType;
        public bool CanScriptSelectFrom => string.Equals(ReturnType, "varchar(max)", StringComparison.OrdinalIgnoreCase);
        public string DisplayName => SqlText.CallName(Schema, Name);
        public string Signature => DisplayName + "(" +
            string.Join(", ", Inputs.Select(p => p.Name + " " + p.SqlType)) + ") -> " +
            (ReturnType ?? "unavailable");

        public string CallTemplate(string database)
        {
            if (!HasSignature) throw new ArgumentException("Signature unavailable or incomplete; refresh metadata before generating a function call.");
            return SqlText.CallName(database, Schema, Name) + "(" +
                string.Join(", ", Inputs.Select(p => SqlTypes.ArgumentLiteral(p.SqlType))) + ")";
        }

        public string ScriptDefinition(FunctionScriptKind kind = FunctionScriptKind.CreateOrAlter)
        {
            if (kind == FunctionScriptKind.Drop) return "DROP FUNCTION IF EXISTS " + SqlText.Qualified(Schema, Name) + ";";
            if (kind != FunctionScriptKind.Create && kind != FunctionScriptKind.CreateOrAlter)
                throw new ArgumentOutOfRangeException(nameof(kind));
            if (string.IsNullOrWhiteSpace(ModuleDefinition))
                throw new InvalidOperationException("sys.sql_modules did not return a function definition. Refresh the folder and check metadata visibility.");
            return SqlText.FunctionDefinition(ModuleDefinition!, kind == FunctionScriptKind.CreateOrAlter)!;
        }

        public string Script(FunctionScriptKind kind, string database)
        {
            switch (kind)
            {
                case FunctionScriptKind.Create:
                case FunctionScriptKind.CreateOrAlter:
                case FunctionScriptKind.Drop:
                    return ScriptDefinition(kind);
                case FunctionScriptKind.Select:
                    return "SELECT " + CallTemplate(database) + ";";
                case FunctionScriptKind.SelectFrom:
                    if (!CanScriptSelectFrom)
                        throw new InvalidOperationException("SELECT FROM requires a varchar(max) return type.");
                    return "-- For DataFrame, Series or JSON results.\r\n" +
                        "-- Do not use for scalar results.\r\n" +
                        "-- Uncomment WITH and define column names and SQL types.\r\n" +
                        "SELECT *\r\nFROM OPENJSON(" + CallTemplate(database) + ")\r\n" +
                        "--WITH ( <add schema here> )\r\n;";
                case FunctionScriptKind.DeclareVariable:
                case FunctionScriptKind.SetVariable:
                    var returnType = ReturnType;
                    if (string.IsNullOrWhiteSpace(returnType) ||
                        string.Equals(returnType, "unavailable", StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException("The SQL return type is unavailable. Refresh metadata before scripting a variable.");
                    string call = CallTemplate(database);
                    string declaration = "DECLARE @result " + returnType;
                    return kind == FunctionScriptKind.DeclareVariable
                        ? declaration + " = " + call + ";"
                        : declaration + ";\r\nSET @result = " + call + ";";
                default:
                    throw new ArgumentOutOfRangeException(nameof(kind));
            }
        }

        public bool SameVersion(FunctionMetadata other) =>
            ObjectId == other.ObjectId && Schema == other.Schema && Name == other.Name &&
            Modified == other.Modified && Definition == other.Definition && ModuleDefinition == other.ModuleDefinition &&
            Parameters.Select(p => p.Ordinal + ":" + p.Name + ":" + p.SqlType)
                .SequenceEqual(other.Parameters.Select(p => p.Ordinal + ":" + p.Name + ":" + p.SqlType));
    }
}
