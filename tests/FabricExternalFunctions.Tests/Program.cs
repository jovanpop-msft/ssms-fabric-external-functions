using FabricExternalFunctions.Core;

var failures = new List<string>();
var count = 0;
var mutationDate = new DateTime(2026, 10, 15, 10, 20, 30, 3);
void Check(string name, Action body)
{
    count++;
    try { body(); Console.WriteLine("PASS " + name); }
    catch (Exception error) { failures.Add(name + ": " + error.Message); }
}
void Equal<T>(T expected, T actual)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
        throw new Exception($"Expected '{expected}', got '{actual}'.");
}
void Reject(Action body)
{
    try { body(); } catch (ArgumentException) { return; } catch (FormatException) { return; }
    throw new Exception("Invalid input was accepted.");
}

Check("External function query template matches the requested text and placeholders", () =>
    Equal(string.Join("\r\n", new[]
    {
        "-- ======================================================",
        "-- Create Exteral Function Template for Fabric Data Warehouse",
        "-- ======================================================",
        "SET ANSI_NULLS ON",
        "GO",
        "SET QUOTED_IDENTIFIER ON",
        "GO",
        "-- =============================================",
        "-- Author:      <Author, , Name>",
        "-- Create Date: <Create Date, , >",
        "-- Description: <Description, , >",
        "-- =============================================",
        "CREATE FUNCTION <Function_Name, sysname, FunctionName>",
        "RETURNS <Function_Data_Type, ,int>",
        "AS EXTERNAL FUNCTION <Function__Set_Name, sysname, FunctionSetName>.<UDF_Function_Name, sysname, UDSFunctionName>",
        "GO",
        ""
    }), SqlText.ExternalFunctionTemplate));

Check("Identifier escaping", () => Equal("[a]]b].[select]", SqlText.Qualified("a]b", "select")));
Check("Effective return type uses catalog metadata, not declaration inference or overrides", () =>
{
    foreach (string? definition in new[] { null,
        "CREATE FUNCTION dbo.s2 AS EXTERNAL FUNCTION item.remote;",
        "CREATE FUNCTION dbo.s2 RETURNS bigint AS EXTERNAL FUNCTION item.remote;" })
    {
        foreach (string type in new[] { "float", "decimal(20,4)", "varchar(max)", "[types].[custom]" })
        {
            var metadata = new FunctionMetadata(42, "dbo", "s2", DateTime.MinValue,
                new[] { new ParameterMetadata(1, "latitude", "int"), new ParameterMetadata(0, "", type) }, definition);
            Equal(type, metadata.ReturnType);
            Equal(true, metadata.Signature.EndsWith(" -> " + type));
        }
    }
});
Check("Missing return metadata is not guessed from the definition or inputs", () =>
    Equal<string?>(null, new FunctionMetadata(42, "dbo", "s2", DateTime.MinValue,
        new[] { new ParameterMetadata(1, "latitude", "float") },
        "CREATE FUNCTION dbo.s2 RETURNS bigint AS EXTERNAL FUNCTION item.remote;").ReturnType));
Check("Identifier length limit", () => Reject(() => SqlText.Identifier(new string('x', 129))));
Check("Blank identifier rejected", () => Reject(() => SqlText.Identifier(" ")));
Check("Simple call name stays readable", () => Equal("dbo.example", SqlText.CallName("dbo", "example")));
Check("Reserved call name quoted", () => Equal("dbo.[select]", SqlText.CallName("dbo", "select")));
Check("Dots inside names stay components", () => Equal("[a.b].[c.d]", SqlText.CallName("a.b", "c.d")));
Check("Three-part call keeps ordinary identifiers readable", () =>
    Equal("FunctionDW.spatial.s2", SqlText.CallName("FunctionDW", "spatial", "s2")));
Check("Three-part call quotes each component only when necessary", () =>
    Equal("[My DW].[select].[f.name]", SqlText.CallName("My DW", "select", "f.name")));
Check("Three-part call escapes closing brackets in every component", () =>
    Equal("[D]]W].[s]]x].[f]]y]", SqlText.CallName("D]W", "s]x", "f]y")));
Check("Reserved warehouse name is quoted", () =>
    Equal("[order].dbo.f", SqlText.CallName("order", "dbo", "f")));
foreach (string keyword in new[] { "containstable", "FREETEXTTABLE", "Identity_Insert", "identitycol",
    "semanticsimilaritytable", "SEMANTICSIMILARITYDETAILSTABLE", "StopList" })
{
    Check("Reserved names are quoted in every call component: " + keyword, () =>
    {
        string quoted = "[" + keyword + "]";
        Equal(quoted + "." + quoted + "." + quoted, SqlText.CallName(keyword, keyword, keyword));
        Equal("DW.dbo.f(0)", new FunctionMetadata(42, "dbo", "f", DateTime.MinValue,
            new[] { new ParameterMetadata(0, "", "int"), new ParameterMetadata(1, keyword, "int") }, null).CallTemplate("DW"));
    });
}
Check("Punctuation in warehouse name cannot introduce SQL", () =>
    Equal("[DW;--].dbo.f", SqlText.CallName("DW;--", "dbo", "f")));
Check("Warehouse names containing dots remain one component", () =>
    Equal("[DW.prod].dbo.f", SqlText.CallName("DW.prod", "dbo", "f")));
Check("Blank or oversized warehouse names are rejected", () =>
{
    Reject(() => SqlText.CallName(" ", "dbo", "f"));
    Reject(() => SqlText.CallName(new string('x', 129), "dbo", "f"));
    Reject(() => SqlText.CallName("DW\nother", "dbo", "f"));
});
foreach (var n in new[] { 0, 1, 2, 3, 5 })
{
    var inputs = Enumerable.Range(1, n).Select(i => new ParameterMetadata(i, "@p" + i, "int"));
    var metadata = new FunctionMetadata(42, "dbo", "example", new DateTime(2026, 1, 1),
        inputs.Prepend(new ParameterMetadata(0, "", "int")), null);
    Check("Literal call arity " + n, () =>
        Equal("FunctionDW.dbo.example(" + string.Join(", ", Enumerable.Repeat("0", n)) + ")", metadata.CallTemplate("FunctionDW")));
}
Check("Warehouse inputs use literals instead of names", () =>
    Equal("FunctionDW.spatial.s2(0, 0)", new FunctionMetadata(1, "spatial", "s2", DateTime.MinValue,
        new[] { new ParameterMetadata(0, "", "bigint"), new ParameterMetadata(1, "latitude", "float"),
            new ParameterMetadata(2, "longitude", "float") }, null).CallTemplate("FunctionDW")));
Check("Unusual input names do not appear in literal calls", () =>
    Equal("FunctionDW.dbo.f(0, 0, 0)", new FunctionMetadata(1, "dbo", "f", DateTime.MinValue,
        new[] { new ParameterMetadata(0, "", "int"), new ParameterMetadata(1, "select", "int"),
            new ParameterMetadata(2, "a.b", "int"), new ParameterMetadata(3, "a]b", "int") }, null).CallTemplate("FunctionDW")));
Check("Unicode parameter names do not appear in literal calls", () =>
    Equal("FunctionDW.dbo.f(0)", new FunctionMetadata(1, "dbo", "f", DateTime.MinValue,
        new[] { new ParameterMetadata(0, "", "int"), new ParameterMetadata(1, "@\u03b1", "int") }, null).CallTemplate("FunctionDW")));
Check("Missing input names do not prevent type-based literal calls", () =>
    Equal("FunctionDW.dbo.f(0)", new FunctionMetadata(1, "dbo", "f", DateTime.MinValue,
        new[] { new ParameterMetadata(0, "", "int"), new ParameterMetadata(1, "", "int") }, null).CallTemplate("FunctionDW")));
Check("Three-part drop text and argument offset handle quoted names containing parentheses", () =>
{
    var metadata = new FunctionMetadata(1, "spatial data", "f(x)", DateTime.MinValue,
        new[] { new ParameterMetadata(0, "", "float"), new ParameterMetadata(1, "latitude", "float") }, null);
    string text = metadata.CallTemplate("DW(prod)");
    Equal("[DW(prod)].[spatial data].[f(x)](0)", text);
    int firstArgument = SqlText.CallName("DW(prod)", metadata.Schema, metadata.Name).Length + 1;
    Equal("0)", text.Substring(firstArgument));
});
Check("Return row never an input", () =>
{
    var f = new FunctionMetadata(1, "dbo", "f", DateTime.MinValue,
        new[] { new ParameterMetadata(0, "", "int") }, null);
    Equal(0, f.Inputs.Count);
});
foreach (var value in new[]
{
    ("bigint", "0"), ("int", "0"), ("smallint", "0"), ("tinyint", "0"), ("bit", "0"),
    ("decimal(20,4)", "0"), ("numeric(38,0)", "0"), ("float", "0"), ("float(53)", "0"),
    ("real", "0"), ("money", "0"), ("smallmoney", "0"),
    ("char(10)", "''"), ("varchar(100)", "''"), ("varchar(max)", "''"), ("text", "''"),
    ("date", "''"), ("datetime", "''"), ("smalldatetime", "''"), ("datetime2(7)", "''"),
    ("datetimeoffset(3)", "''"), ("time(0)", "''"), ("xml", "''"),
    ("nchar(10)", "N''"), ("nvarchar(max)", "N''"), ("ntext", "N''"), ("sysname", "N''"),
    ("binary(16)", "0x"), ("varbinary(max)", "0x"), ("image", "0x"), ("rowversion", "0x"), ("timestamp", "0x"),
    ("uniqueidentifier", "'00000000-0000-0000-0000-000000000000'"), ("json", "'{}'"), ("sql_variant", "NULL"),
    ("  NVARCHAR ( MAX )  ", "N''"), ("DECIMAL ( 18 , 2 )", "0")
})
{
    Check("Typed literal placeholder: " + value.Item1, () => Equal(value.Item2, SqlTypes.ArgumentLiteral(value.Item1)));
}
foreach (string? type in new[] { null, "", " ", "unavailable", " UNAVAILABLE ", "[types].[custom]",
    "geography", "varchar(max); DROP TABLE dbo.t", "int /* comment */", "decimal(18,2" })
{
    Check("Missing or unsupported parameter types fail explicitly: " + type, () =>
    {
        Reject(() => SqlTypes.ArgumentLiteral(type));
        var metadata = new FunctionMetadata(42, "dbo", "f", mutationDate,
            new[] { new ParameterMetadata(0, "", "varchar(max)"), new ParameterMetadata(1, "p", type ?? "") }, null);
        Reject(() => metadata.CallTemplate("DW"));
        foreach (var kind in new[] { FunctionScriptKind.Select, FunctionScriptKind.SelectFrom,
            FunctionScriptKind.DeclareVariable, FunctionScriptKind.SetVariable })
            Reject(() => metadata.Script(kind, "DW"));
    });
}
Check("Mixed-type literals preserve input order and match all script and drag forms", () =>
{
    var metadata = new FunctionMetadata(42, "ExampleSet", "calculateValue", mutationDate, new[]
    {
        new ParameterMetadata(4, "payload", "varbinary(max)"),
        new ParameterMetadata(2, "count", "int"),
        new ParameterMetadata(0, "", "varchar(max)"),
        new ParameterMetadata(3, "@currency", "nvarchar(10)"),
        new ParameterMetadata(1, "startDate", "date")
    }, null);
    const string call = "FunctionDW.ExampleSet.calculateValue('', 0, N'', 0x)";
    Equal(call, metadata.CallTemplate("FunctionDW"));
    Equal("SELECT " + call + ";", metadata.Script(FunctionScriptKind.Select, "FunctionDW"));
    Equal(true, metadata.Script(FunctionScriptKind.SelectFrom, "FunctionDW").Contains("FROM OPENJSON(" + call + ")"));
    Equal("DECLARE @result varchar(max) = " + call + ";", metadata.Script(FunctionScriptKind.DeclareVariable, "FunctionDW"));
    Equal("DECLARE @result varchar(max);\r\nSET @result = " + call + ";", metadata.Script(FunctionScriptKind.SetVariable, "FunctionDW"));
    Equal("startDate", metadata.Inputs[0].Name);
    Equal("date", metadata.Inputs[0].SqlType);
    Equal(4, metadata.Inputs.Count);
    Equal(true, metadata.Signature.Contains("startDate date"));
});
foreach (int arity in new[] { 0, 1, 2, 3, 5 })
{
    Check("Invocation scripts and drag calls use the same literals with exact arity " + arity, () =>
    {
        var parameters = Enumerable.Range(1, arity).Select(i => new ParameterMetadata(i, "p" + i, "int"));
        var metadata = new FunctionMetadata(42, "dbo", "f", mutationDate,
            parameters.Prepend(new ParameterMetadata(0, "", "decimal(20,4)")), null);
        string call = "FunctionDW.dbo.f(" + string.Join(", ", Enumerable.Repeat("0", arity)) + ")";
        Equal(call, metadata.CallTemplate("FunctionDW"));
        Equal("SELECT " + call + ";", metadata.Script(FunctionScriptKind.Select, "FunctionDW"));
        var jsonMetadata = new FunctionMetadata(42, "dbo", "f", mutationDate,
            metadata.Inputs.Prepend(new ParameterMetadata(0, "", "varchar(max)")), null);
        Equal("-- For DataFrame, Series or JSON results.\r\n" +
            "-- Do not use for scalar results.\r\n" +
            "-- Uncomment WITH and define column names and SQL types.\r\n" +
            "SELECT *\r\nFROM OPENJSON(" + call + ")\r\n--WITH ( <add schema here> )\r\n;",
            jsonMetadata.Script(FunctionScriptKind.SelectFrom, "FunctionDW"));
        Equal("DECLARE @result decimal(20,4) = " + call + ";",
            metadata.Script(FunctionScriptKind.DeclareVariable, "FunctionDW"));
        Equal("DECLARE @result decimal(20,4);\r\nSET @result = " + call + ";",
            metadata.Script(FunctionScriptKind.SetVariable, "FunctionDW"));
    });
}
Check("Invocation scripts quote each call component and replace input names with typed literals", () =>
{
    var metadata = new FunctionMetadata(42, "select", "f]x", mutationDate,
        new[] { new ParameterMetadata(0, "", "bigint"), new ParameterMetadata(2, "@longitude", "float"),
            new ParameterMetadata(1, "latitude value", "float") }, null);
    const string call = "[My DW].[select].[f]]x](0, 0)";
    Equal(call, metadata.CallTemplate("My DW"));
    Equal("SELECT " + call + ";", metadata.Script(FunctionScriptKind.Select, "My DW"));
    var jsonMetadata = new FunctionMetadata(42, "select", "f]x", mutationDate,
        metadata.Inputs.Prepend(new ParameterMetadata(0, "", "varchar(max)")), null);
    Equal("-- For DataFrame, Series or JSON results.\r\n" +
        "-- Do not use for scalar results.\r\n" +
        "-- Uncomment WITH and define column names and SQL types.\r\n" +
        "SELECT *\r\nFROM OPENJSON(" + call + ")\r\n--WITH ( <add schema here> )\r\n;",
        jsonMetadata.Script(FunctionScriptKind.SelectFrom, "My DW"));
    Equal("DECLARE @result bigint = " + call + ";", metadata.Script(FunctionScriptKind.DeclareVariable, "My DW"));
    Equal("DECLARE @result bigint;\r\nSET @result = " + call + ";", metadata.Script(FunctionScriptKind.SetVariable, "My DW"));
});
foreach (string type in new[] { "int", "varchar(max)", "nvarchar(40)", "decimal(20,4)", "datetime2(3)", "[types].[return]]type]" })
{
    Check("Variable scripts preserve the effective catalog return type: " + type, () =>
    {
        var metadata = new FunctionMetadata(42, "dbo", "f", mutationDate,
            new[] { new ParameterMetadata(0, "", type) }, "CREATE FUNCTION dbo.f EXTERNAL FUNCTION s.f;");
        Equal("DECLARE @result " + type + " = DW.dbo.f();", metadata.Script(FunctionScriptKind.DeclareVariable, "DW"));
        Equal("DECLARE @result " + type + ";\r\nSET @result = DW.dbo.f();", metadata.Script(FunctionScriptKind.SetVariable, "DW"));
    });
}
foreach (string? type in new string?[] { null, "", " ", "unavailable", "UNAVAILABLE" })
{
    foreach (var kind in new[] { FunctionScriptKind.DeclareVariable, FunctionScriptKind.SetVariable })
    {
        Check("Variable scripts reject missing return metadata: " + kind + "/" + type, () =>
        {
            var parameters = type == null ? Array.Empty<ParameterMetadata>() : new[] { new ParameterMetadata(0, "", type) };
            var metadata = new FunctionMetadata(42, "dbo", "f", mutationDate, parameters, null);
            try { metadata.Script(kind, "DW"); }
            catch (InvalidOperationException) { return; }
            throw new Exception("Missing return type was scripted as a variable.");
        });
    }
}
Check("Literal invocation scripts and dragging do not require parameter names", () =>
{
    var metadata = new FunctionMetadata(42, "dbo", "f", mutationDate,
        new[] { new ParameterMetadata(0, "", "varchar(max)"), new ParameterMetadata(1, "", "int") }, null);
    Equal("DW.dbo.f(0)", metadata.CallTemplate("DW"));
    Equal("SELECT DW.dbo.f(0);", metadata.Script(FunctionScriptKind.Select, "DW"));
    foreach (var kind in new[] { FunctionScriptKind.SelectFrom, FunctionScriptKind.DeclareVariable, FunctionScriptKind.SetVariable })
        Equal(true, metadata.Script(kind, "DW").Contains("DW.dbo.f(0)"));
});
Check("SELECT scripting rejects an unknown signature like dragging does", () =>
    Reject(() => new FunctionMetadata(42, "dbo", "f", mutationDate,
        Array.Empty<ParameterMetadata>(), null).Script(FunctionScriptKind.Select, "DW")));
foreach (string? type in new string?[] { "varchar(max)", "VARCHAR(MAX)", "varchar(100)", "nvarchar(max)",
    "float", "decimal(20,4)", "[types].[varchar(max)]", "unavailable", "", null })
{
    foreach (string? definition in new[] { null,
        "CREATE FUNCTION dbo.f AS EXTERNAL FUNCTION item.remote;",
        "CREATE FUNCTION dbo.f RETURNS varchar(max) AS EXTERNAL FUNCTION item.remote;",
        "CREATE FUNCTION dbo.f RETURNS float AS EXTERNAL FUNCTION item.remote;" })
    {
        Check("SELECT FROM requires the effective catalog varchar(max) type: " + type + "/" + definition, () =>
        {
            var parameters = type == null ? Array.Empty<ParameterMetadata>() : new[] { new ParameterMetadata(0, "", type) };
            var metadata = new FunctionMetadata(42, "dbo", "f", mutationDate, parameters, definition);
            bool eligible = string.Equals(type, "varchar(max)", StringComparison.OrdinalIgnoreCase);
            Equal(eligible, metadata.CanScriptSelectFrom);
            if (metadata.HasSignature)
                Equal("SELECT DW.dbo.f();", metadata.Script(FunctionScriptKind.Select, "DW"));
            if (eligible)
                Equal(true, metadata.Script(FunctionScriptKind.SelectFrom, "DW").Contains("FROM OPENJSON(DW.dbo.f())"));
            else
            {
                try { metadata.Script(FunctionScriptKind.SelectFrom, "DW"); }
                catch (InvalidOperationException error)
                {
                    Equal("SELECT FROM requires a varchar(max) return type.", error.Message);
                    return;
                }
                throw new Exception("SELECT FROM was generated for an unsupported or missing return type.");
            }
        });
    }
}
Check("Unsupported script modes are rejected", () =>
    Reject(() => new FunctionMetadata(42, "dbo", "f", mutationDate,
        Array.Empty<ParameterMetadata>(), null).Script((FunctionScriptKind)int.MaxValue, "DW")));
Check("Unified scripting preserves the existing definition script modes", () =>
{
    var metadata = new FunctionMetadata(42, "dbo", "f", mutationDate, Array.Empty<ParameterMetadata>(), null,
        "CREATE FUNCTION dbo.f EXTERNAL FUNCTION s.f;");
    foreach (var kind in new[] { FunctionScriptKind.Create, FunctionScriptKind.CreateOrAlter, FunctionScriptKind.Drop })
        Equal(metadata.ScriptDefinition(kind), metadata.Script(kind, "DW"));
});
Check("Unknown signature rejected", () => Reject(() =>
    new FunctionMetadata(1, "dbo", "f", DateTime.MinValue, Array.Empty<ParameterMetadata>(), null).CallTemplate("FunctionDW")));
Check("Input order follows ordinal", () =>
{
    var f = new FunctionMetadata(1, "dbo", "f", DateTime.MinValue,
        new[] { new ParameterMetadata(2, "@b", "int"), new ParameterMetadata(0, "", "int"),
            new ParameterMetadata(1, "@a", "int") }, null);
    Equal("@a", f.Inputs[0].Name);
});
Check("Duplicate parameter ordinals rejected", () => Reject(() =>
    new FunctionMetadata(1, "dbo", "f", DateTime.MinValue,
        new[] { new ParameterMetadata(0, "", "int"), new ParameterMetadata(0, "", "int") }, null)));
foreach (int[] ordinals in new[] { new[] { 0, 2 }, new[] { 0, 1, 3 }, new[] { 1, 2 }, new[] { 0, int.MaxValue } })
{
    Check("Incomplete signatures never guess call arity: " + string.Join(",", ordinals), () =>
    {
        var metadata = new FunctionMetadata(42, "dbo", "f", mutationDate,
            ordinals.Select(i => new ParameterMetadata(i, "p" + i, "varchar(max)")), null);
        Equal(false, metadata.HasSignature);
        Equal(ordinals.Count(i => i > 0), metadata.Inputs.Count);
        Reject(() => metadata.CallTemplate("DW"));
        foreach (var kind in new[] { FunctionScriptKind.Select,
            FunctionScriptKind.DeclareVariable, FunctionScriptKind.SetVariable })
        {
            if (metadata.ReturnType == null && kind != FunctionScriptKind.Select)
            {
                try { metadata.Script(kind, "DW"); }
                catch (InvalidOperationException) { continue; }
                throw new Exception("A missing signature produced a variable script.");
            }
            Reject(() => metadata.Script(kind, "DW"));
        }
        if (metadata.CanScriptSelectFrom)
            Reject(() => metadata.Script(FunctionScriptKind.SelectFrom, "DW"));
    });
}
Check("VARCHAR MAX formatting", () => Equal("varchar(max)", SqlTypes.Format("varchar", -1, 0, 0)));
Check("NVARCHAR byte length formatting", () => Equal("nvarchar(20)", SqlTypes.Format("nvarchar", 40, 0, 0)));
Check("Decimal precision and scale", () => Equal("decimal(18,2)", SqlTypes.Format("decimal", 9, 18, 2)));
Check("Datetime scale", () => Equal("datetime2(3)", SqlTypes.Format("datetime2", 8, 0, 3)));
Check("Valid return type normalization", () => Equal("varchar(100)", SqlTypes.ValidateReturnType(" VARCHAR ( 100 ) ")));
Check("Return type SQL injection rejected", () => Reject(() => SqlTypes.ValidateReturnType("int; DROP TABLE dbo.t")));
Check("Return type invalid precision rejected", () => Reject(() => SqlTypes.ValidateReturnType("decimal(2,3)")));
Check("Return type invalid size rejected", () => Reject(() => SqlTypes.ValidateReturnType("varchar(0)")));
Check("Return type unsupported rejected", () => Reject(() => SqlTypes.ValidateReturnType("made_up")));
foreach (string type in new[] { "varchar([max])", "decimal([18],2)", "time(\"3\")" })
    Check("Delimited return-type arguments are rejected: " + type, () =>
        Reject(() => SqlTypes.ValidateReturnType(type)));
foreach (string type in new[] { "var char(100)", "decimal(1 8,2)", "decimal(1/* gap */8,2)" })
{
    Check("Stored return declarations preserve token boundaries: " + type, () =>
    {
        string stored = "CREATE FUNCTION dbo.f RETURNS " + type + " AS EXTERNAL FUNCTION s.f;";
        Reject(() => ProxyDeclaration.Parse(stored));
        Equal<ProxyDeclaration?>(null, ProxyDeclaration.FromDefinition(stored, "dbo", "f", out _));
    });
}
Check("Comments between complete return-type tokens remain supported", () =>
    Equal("decimal(18,2)", ProxyDeclaration.Parse(
        "CREATE FUNCTION dbo.f RETURNS decimal /* precision */ (18, /* scale */ 2) EXTERNAL FUNCTION s.f;").ReturnOverride));
Check("Parse external definition", () =>
{
    var d = ProxyDeclaration.Parse("CREATE OR ALTER FUNCTION dbo.f AS EXTERNAL FUNCTION item.remote;");
    Equal("item", d.FunctionSet); Equal("remote", d.ExternalName); Equal<string?>(null, d.ReturnOverride);
});
Check("Complete short Warehouse definition without AS is verified", () =>
{
    const string stored = "CREATE FUNCTION api.TimeSeriesStockData\r\nEXTERNAL FUNCTION api.TimeSeriesStockData";
    var declaration = ProxyDeclaration.FromDefinition(stored, "api", "TimeSeriesStockData", out string reason);
    Equal("api", declaration?.FunctionSet);
    Equal("TimeSeriesStockData", declaration?.ExternalName);
    Equal<string?>(null, declaration?.ReturnOverride);
    Equal("Verified warehouse definition", reason);
    Equal(stored, ProxyDeclaration.SelectDefinition("api", "TimeSeriesStockData", null, stored));
});
foreach (string operation in new[] { "CREATE", "ALTER", "CREATE OR ALTER" })
{
    foreach (string optionalAs in new[] { "", "AS " })
    {
        Check("Parse explicit override with optional AS: " + operation + " / " + optionalAs, () =>
        {
            var declaration = ProxyDeclaration.Parse(operation +
                " FUNCTION [api].[TimeSeriesStockData] RETURNS decimal(18, 2)\r\n" +
                optionalAs + "/* binding */ EXTERNAL FUNCTION [set.one].[f]]x];");
            Equal("decimal(18,2)", declaration.ReturnOverride);
            Equal("set.one", declaration.FunctionSet);
            Equal("f]x", declaration.ExternalName);
        });
    }
}
Check("Definition diagnostics explain syntax instead of implying a length limit", () =>
{
    Equal<ProxyDeclaration?>(null, ProxyDeclaration.FromDefinition(
        "CREATE FUNCTION api.f EXTERNAL FUNCTION missing_part", "api", "f", out string reason));
    Equal(true, reason.Contains("Expected ."));
    Equal(false, reason.Contains("characters"));
});
Check("Parse quotes, comments and return override", () =>
{
    var d = ProxyDeclaration.Parse("/* x */ ALTER FUNCTION [a.b].[c]]d] RETURNS decimal(18, 2)\nAS -- note\nEXTERNAL FUNCTION [set.one].\"f.two\";");
    Equal("a.b", d.Schema); Equal("c]d", d.Name); Equal("set.one", d.FunctionSet);
    Equal("f.two", d.ExternalName); Equal("decimal(18,2)", d.ReturnOverride);
});
Check("Nested comments", () => Equal("f", ProxyDeclaration.Parse(
    "/* outer /* inner */ done */ CREATE FUNCTION dbo.f AS EXTERNAL FUNCTION s.f;").Name));
foreach (string newline in new[] { "\r", "\n", "\r\n" })
{
    Check("Line comments terminate at each SQL newline: " + newline.Length + "/" + (int)newline[0], () =>
    {
        string stored = "-- header" + newline +
            "CREATE -- operation" + newline + "FUNCTION dbo.f RETURNS int -- return" + newline +
            "EXTERNAL FUNCTION s.f;";
        Equal("int", ProxyDeclaration.Parse(stored).ReturnOverride);
        Equal(stored.Replace("CREATE --", "CREATE OR ALTER --"), SqlText.CreateOrAlterDefinition(stored));
    });
}
Check("Reject unterminated comment", () => Reject(() => ProxyDeclaration.Parse("/* broken")));
Check("Reject unterminated identifier", () => Reject(() =>
    ProxyDeclaration.Parse("CREATE FUNCTION [broken AS EXTERNAL FUNCTION s.f")));
Check("Reject unrelated TSQL", () => Reject(() =>
    ProxyDeclaration.Parse("CREATE FUNCTION dbo.f() RETURNS int AS BEGIN RETURN 1; END")));
Check("Reject extra statements", () => Reject(() =>
    ProxyDeclaration.Parse("CREATE FUNCTION dbo.f AS EXTERNAL FUNCTION s.f; DROP TABLE x;")));
Check("CREATE does not overwrite", () => Equal(
    "CREATE FUNCTION [dbo].[f]\r\nAS EXTERNAL FUNCTION [set].[remote];",
    new ProxyDeclaration("dbo", "f", "set", "remote", null).Script(false)));
Check("Edit uses ALTER, never recreates dropped object", () =>
    Equal(true, new ProxyDeclaration("dbo", "f", "s", "r", "varchar(100)").Script(true).StartsWith("ALTER FUNCTION")));
Check("Parsed explicit override preserved", () =>
{
    var d = ProxyDeclaration.Parse("CREATE FUNCTION dbo.f RETURNS varchar(100) AS EXTERNAL FUNCTION s.r;");
    Equal(true, d.Script(false).Contains("RETURNS varchar(100)"));
});
Check("Unified CREATE OR ALTER script preserves quoted binding and override", () => Equal(
    "CREATE OR ALTER FUNCTION [spatial].[s2]\r\nRETURNS bigint\r\nAS EXTERNAL FUNCTION [set.one].[s]]2];",
    new ProxyDeclaration("spatial", "s2", "set.one", "s]2", "bigint").ScriptCreateOrAlter()));
Check("New reviewed change remains CREATE only", () =>
{
    var declaration = new ProxyDeclaration("dbo", "f", "set", "remote", null);
    Equal(declaration.Script(false), declaration.ScriptChange(null));
});
Check("Unchanged edit identity remains ALTER only", () =>
{
    var declaration = new ProxyDeclaration("dbo", "f", "set", "remote", "bigint");
    var existing = new FunctionMetadata(42, "dbo", "f", mutationDate, Array.Empty<ParameterMetadata>(), null);
    string script = declaration.ScriptChange(existing);
    Equal(true, script.Contains("ISNULL(OBJECT_ID(N'[dbo].[f]'), -1) <> 42"));
    Equal(true, script.Contains("modify_date = CONVERT(datetime, N'2026-10-15T10:20:30.003', 126)"));
    Equal(true, script.Contains("EXEC sys.sp_executesql N'" + declaration.Script(true) + "';"));
    Equal(false, script.Contains("DROP FUNCTION"));
    Equal(false, script.Contains("CREATE FUNCTION"));
});
Check("Source timestamp guards preserve SQL datetime milliseconds across cultures and mutation modes", () =>
{
    var culture = System.Globalization.CultureInfo.CurrentCulture;
    try
    {
        System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo("ar-SA");
        foreach (int millisecond in new[] { 0, 3, 7, 997 })
        {
            var modified = new DateTime(2026, 10, 15, 10, 20, 30, millisecond);
            var source = new FunctionMetadata(42, "dbo", "f", modified, Array.Empty<ParameterMetadata>(), null);
            string timestamp = modified.ToString("yyyy-MM-ddTHH:mm:ss.fff", System.Globalization.CultureInfo.InvariantCulture);
            Equal(modified, new System.Data.SqlTypes.SqlDateTime(
                DateTime.Parse(timestamp, System.Globalization.CultureInfo.InvariantCulture)).Value);
            foreach (var mode in new[] { (Name: "f", Retain: false), (Name: "copy", Retain: true), (Name: "replacement", Retain: false) })
            {
                string script = new ProxyDeclaration("dbo", mode.Name, "set", "remote", null).ScriptChange(source, mode.Retain);
                int check = script.IndexOf("modify_date = CONVERT(datetime, N'" + timestamp + "', 126)", StringComparison.Ordinal);
                Equal(true, check >= 0 && check < script.IndexOf("EXEC sys.sp_executesql", StringComparison.Ordinal));
                Equal(true, script.Contains("AND type = 'XF'"));
            }
        }
    }
    finally { System.Globalization.CultureInfo.CurrentCulture = culture; }
});
foreach (var target in new[] { ("dbo", "renamed"), ("api", "f"), ("api", "renamed"), ("dbo", "F") })
{
    Check("Retaining original uses CREATE only at the new identity " + target, () =>
    {
        var existing = new FunctionMetadata(42, "dbo", "f", mutationDate, Array.Empty<ParameterMetadata>(), null);
        var declaration = new ProxyDeclaration(target.Item1, target.Item2, "set", "remote", "decimal(20,4)");
        Equal(true, declaration.ScriptChange(existing, true).Contains("EXEC sys.sp_executesql N'" + declaration.Script(false) + "';"));
        Equal(true, declaration.ScriptChange(existing, true).Contains("ISNULL(OBJECT_ID(N'[dbo].[f]'), -1) <> 42"));
        Equal(true, declaration.ScriptChange(existing, true).Contains("modify_date = CONVERT"));
        Equal(false, declaration.ScriptChange(existing, true).Contains("DROP"));
        Equal(false, declaration.ScriptChange(existing, true).Contains("ALTER FUNCTION"));
    });
    Check("Unchecked identity change drops the original before creating its replacement " + target, () =>
    {
        var existing = new FunctionMetadata(42, "dbo", "f", mutationDate, Array.Empty<ParameterMetadata>(), null);
        var declaration = new ProxyDeclaration(target.Item1, target.Item2, "set", "remote", "decimal(20,4)");
        string sql = declaration.ScriptChange(existing);
        int drop = sql.IndexOf("DROP FUNCTION IF EXISTS [dbo].[f];", StringComparison.Ordinal);
        int create = sql.IndexOf("EXEC sys.sp_executesql N'" + declaration.Script(false) + "';", StringComparison.Ordinal);
        int destinationGuard = sql.IndexOf("IF OBJECT_ID(N'" + SqlText.Qualified(target.Item1, target.Item2) + "') IS NOT NULL", StringComparison.Ordinal);
        int sourceGuard = sql.IndexOf("modify_date = CONVERT", StringComparison.Ordinal);
        int tryBlock = sql.IndexOf("BEGIN TRY", StringComparison.Ordinal);
        Equal(true, destinationGuard >= 0 && sourceGuard >= 0 && tryBlock > destinationGuard && tryBlock > sourceGuard);
        Equal(true, drop > tryBlock && create > drop);
        Equal(true, sql.IndexOf("END TRY", StringComparison.Ordinal) > create);
        Equal(true, sql.Contains("BEGIN CATCH\r\n    THROW;\r\nEND CATCH;"));
        Equal(false, sql.Contains("ALTER FUNCTION"));
    });
}
Check("Retaining an original requires a different target identity", () =>
{
    var existing = new FunctionMetadata(42, "dbo", "f", DateTime.MinValue, Array.Empty<ParameterMetadata>(), null);
    Reject(() => new ProxyDeclaration("dbo", "f", "set", "remote", null).ScriptChange(existing, true));
});
Check("Retaining a function is not a New operation", () =>
    Reject(() => new ProxyDeclaration("dbo", "f", "set", "remote", null).ScriptChange(null, true)));
Check("Every Warehouse mutation mode omits explicit transactions and unsupported XACT_ABORT", () =>
{
    var source = new FunctionMetadata(42, "dbo", "f", mutationDate, Array.Empty<ParameterMetadata>(), null);
    var edit = new ProxyDeclaration("dbo", "f", "set", "remote", null);
    var target = new ProxyDeclaration("api", "copy", "set", "remote", null);
    foreach (string sql in new[] { edit.ScriptChange(null), edit.ScriptChange(source),
        target.ScriptChange(source, true), target.ScriptChange(source), ProxyDeclaration.ScriptDrop(source) })
    {
        Equal(false, sql.Contains("SET XACT_ABORT", StringComparison.OrdinalIgnoreCase));
        Equal(false, sql.Contains("BEGIN TRAN", StringComparison.OrdinalIgnoreCase));
        Equal(false, sql.Contains("COMMIT TRAN", StringComparison.OrdinalIgnoreCase));
        Equal(false, sql.Contains("ROLLBACK TRAN", StringComparison.OrdinalIgnoreCase));
    }
});
Check("Retained copy escapes both identifiers and nested Unicode SQL literals", () =>
{
    var existing = new FunctionMetadata(42, "old's", "f]x", mutationDate, Array.Empty<ParameterMetadata>(), null);
    var declaration = new ProxyDeclaration("new's", "g]y", "set'one", "r]';--", null);
    string script = declaration.ScriptChange(existing, true);
    Equal(true, script.Contains("OBJECT_ID(N'[old''s].[f]]x]')"));
    Equal(false, script.Contains("DROP FUNCTION"));
    Equal(true, script.Contains("OBJECT_ID(N'[new''s].[g]]y]')"));
    Equal(true, script.Contains("EXEC sys.sp_executesql N'" + declaration.Script(false).Replace("'", "''") + "';"));
    Equal(false, script.Contains("RETURNS"));
});
Check("Replacement quotes its DROP independently and preserves CREATE binding and override", () =>
{
    var existing = new FunctionMetadata(42, "old's", "f]x", mutationDate, Array.Empty<ParameterMetadata>(), null);
    var declaration = new ProxyDeclaration("new's", "g]y", "set'one", "r]';--", "decimal(20,4)");
    string sql = declaration.ScriptChange(existing);
    Equal(true, sql.Contains("DROP FUNCTION IF EXISTS [old's].[f]]x];"));
    Equal(true, sql.Contains("OBJECT_ID(N'[new''s].[g]]y]')"));
    Equal(true, sql.Contains("EXEC sys.sp_executesql N'" + declaration.Script(false).Replace("'", "''") + "';"));
    Equal(true, sql.Contains("If CREATE fails after DROP, the original is not restored."));
});
Check("Guarded edits, copies and replacements reject ambient transactions without changing isolation", () =>
{
    var source = new FunctionMetadata(42, "dbo", "f", mutationDate, Array.Empty<ParameterMetadata>(), null);
    foreach (var mode in new[] { (Name: "f", Retain: false), (Name: "copy", Retain: true), (Name: "replacement", Retain: false) })
    {
        string sql = new ProxyDeclaration("dbo", mode.Name, "set", "remote", null).ScriptChange(source, mode.Retain);
        Equal(true, sql.StartsWith("IF @@TRANCOUNT > 0\r\n"));
        Equal(true, sql.Contains("THROW 50000, 'Run this script outside an explicit transaction.', 1;"));
        Equal(false, sql.Contains("SET TRANSACTION ISOLATION LEVEL"));
        Equal(mode.Name == "replacement" ? 2 : 1,
            System.Text.RegularExpressions.Regex.Matches(sql, @"(?:CREATE|ALTER|DROP) FUNCTION").Count);
        if (mode.Name != "f")
            Equal(true, sql.Contains("IF OBJECT_ID(N'[dbo].[" + mode.Name + "]') IS NOT NULL"));
    }
});
Check("Truncated warehouse definition is rejected instead of inventing the binding", () => Reject(() =>
    ProxyDeclaration.Parse("\r\n\r\n-- Proxy functions mapped to Fabric function-set \"spatial\"\r\n" +
        "CREATE OR ALTER FUNCTION spatial.s2\r\nAS EXTERNAL FUNCTION spatia")));
Check("Complete module definition replaces even a longer truncated catalog value", () =>
{
    const string complete = "CREATE FUNCTION spatial.s2 AS EXTERNAL FUNCTION spatial.s2;";
    string truncated = new string(' ', 128) + "CREATE FUNCTION spatial.s2 AS EXTERNAL FUNCTION spatia";
    Equal(complete, ProxyDeclaration.SelectDefinition("spatial", "s2", truncated, complete));
});
Check("Complete object definition survives an unsupported module definition", () =>
{
    const string complete = "CREATE FUNCTION spatial.s2 AS EXTERNAL FUNCTION spatial.s2;";
    Equal(complete, ProxyDeclaration.SelectDefinition("spatial", "s2", complete, "unsupported module representation"));
});
Check("Another object's complete definition is not accepted", () =>
{
    const string original = "CREATE FUNCTION spatial.s2 AS EXTERNAL FUNCTION spatia";
    Equal(original, ProxyDeclaration.SelectDefinition("spatial", "s2", original,
        "CREATE FUNCTION dbo.x AS EXTERNAL FUNCTION s.x;"));
    Equal<ProxyDeclaration?>(null, ProxyDeclaration.FromDefinition(
        "CREATE FUNCTION dbo.x AS EXTERNAL FUNCTION s.x;", "spatial", "s2", out string reason));
    Equal(true, reason.Contains("identity"));
});
Check("Missing sources remain unavailable", () =>
    Equal<string?>(null, ProxyDeclaration.SelectDefinition("spatial", "s2", null, null)));
Check("Complete module definition recovers a missing object definition", () =>
{
    const string complete = "CREATE FUNCTION spatial.s2 AS EXTERNAL FUNCTION spatial.s2;";
    Equal(complete, ProxyDeclaration.SelectDefinition("spatial", "s2", null, complete));
});
Check("Module scripting normalizes only CREATE while preserving raw metadata and whitespace", () =>
{
    const string stored = "\r\n-- Original comment\nCREATE FUNCTION dbo.f AS EXTERNAL FUNCTION s.r;\n";
    var metadata = new FunctionMetadata(1, "dbo", "f", DateTime.MinValue, Array.Empty<ParameterMetadata>(),
        "CREATE FUNCTION dbo.f AS EXTERNAL FUNCTION another.binding;", stored);
    Equal(stored.Replace("CREATE FUNCTION", "CREATE OR ALTER FUNCTION"), metadata.ScriptDefinition());
    Equal(stored, metadata.ModuleDefinition);
});
Check("Truncated module scripting is unchanged, not rejected or commented out", () =>
{
    const string fragment = "\r\n\r\n-- Proxy functions mapped to Fabric function-set \"spatial\"\r\n" +
        "CREATE OR ALTER FUNCTION spatial.s2\r\nAS EXTERNAL FUNCTION spatia";
    Equal(128, fragment.Length);
    var metadata = new FunctionMetadata(1, "spatial", "s2", DateTime.MinValue, Array.Empty<ParameterMetadata>(),
        "CREATE FUNCTION spatial.s2 AS EXTERNAL FUNCTION unrelated.fallback;", fragment);
    Equal(fragment, metadata.ScriptDefinition());
});
Check("Long module scripting has no 128 or 4000 character limit", () =>
{
    string stored = "-- " + new string('x', 8000) + "\nCREATE FUNCTION dbo.f AS EXTERNAL FUNCTION s.r;";
    Equal(stored.Replace("CREATE FUNCTION", "CREATE OR ALTER FUNCTION"), new FunctionMetadata(1, "dbo", "f", DateTime.MinValue,
        Array.Empty<ParameterMetadata>(), null, stored).ScriptDefinition());
});
foreach (var example in new[]
{
    (Text: "/* CREATE FUNCTION ignored /* nested */ */\r\ncreate FUNCTION dbo.f EXTERNAL FUNCTION s.f",
     Expected: "/* CREATE FUNCTION ignored /* nested */ */\r\nCREATE OR ALTER FUNCTION dbo.f EXTERNAL FUNCTION s.f"),
    (Text: "-- CREATE ignored\r\nALTER /* preserved */ FUNCTION dbo.f AS EXTERNAL FUNCTION s.f",
     Expected: "-- CREATE ignored\r\nCREATE OR ALTER /* preserved */ FUNCTION dbo.f AS EXTERNAL FUNCTION s.f"),
    (Text: "CREATE OR ALTER FUNCTION dbo.f EXTERNAL FUNCTION s.f", Expected: "CREATE OR ALTER FUNCTION dbo.f EXTERNAL FUNCTION s.f"),
    (Text: "CREATE FUNCTION [unfinished", Expected: "CREATE OR ALTER FUNCTION [unfinished"),
    (Text: "CREATE VIEW dbo.v AS SELECT 1", Expected: "CREATE VIEW dbo.v AS SELECT 1"),
    (Text: "/* unclosed CREATE FUNCTION", Expected: "/* unclosed CREATE FUNCTION"),
    (Text: "[CREATE] FUNCTION dbo.f", Expected: "[CREATE] FUNCTION dbo.f")
})
    Check("Definition normalization is prefix-only: " + example.Text, () =>
        Equal(example.Expected, SqlText.CreateOrAlterDefinition(example.Text)));
Check("Missing module text is reported rather than replaced with another source", () =>
{
    foreach (string? missing in new string?[] { null, "", " \r\n " })
    {
        var metadata = new FunctionMetadata(1, "dbo", "f", DateTime.MinValue,
            Array.Empty<ParameterMetadata>(), "CREATE FUNCTION dbo.f AS EXTERNAL FUNCTION s.r;", missing);
        bool rejected = false;
        try { metadata.ScriptDefinition(); }
        catch (InvalidOperationException) { rejected = true; }
        Equal(true, rejected);
    }
});
foreach (string operation in new[] { "CREATE", "ALTER", "CREATE OR ALTER", "CREATE OR REPLACE" })
{
    Check("Script context selects CREATE or CREATE OR ALTER from " + operation, () =>
    {
        string body = " FUNCTION [api].[f] EXTERNAL FUNCTION [set].[remote];";
        string stored = "-- leading comment\r\n" + operation + body;
        var metadata = new FunctionMetadata(42, "api", "f", mutationDate, Array.Empty<ParameterMetadata>(), null, stored);
        Equal("-- leading comment\r\nCREATE" + body, metadata.ScriptDefinition(FunctionScriptKind.Create));
        Equal("-- leading comment\r\nCREATE OR ALTER" + body, metadata.ScriptDefinition(FunctionScriptKind.CreateOrAlter));
        Equal(stored, metadata.ModuleDefinition);
    });
}
Check("DROP scripting needs no stored definition or external binding", () =>
{
    var metadata = new FunctionMetadata(42, "a]b", "f'name", mutationDate, Array.Empty<ParameterMetadata>(), null);
    Equal("DROP FUNCTION IF EXISTS [a]]b].[f'name];", metadata.ScriptDefinition(FunctionScriptKind.Drop));
    Equal("DROP FUNCTION IF EXISTS [a]]b].[f'name];", ProxyDeclaration.ScriptDrop(metadata));
    Equal(ProxyDeclaration.ScriptDrop(metadata), metadata.Script(FunctionScriptKind.Drop, "DW"));
});
foreach (var identity in new[] { ("dbo", "f"), ("select", "f;--"), ("a.b", "f]x") })
    Check("Deletion executes only one quoted DROP FUNCTION IF EXISTS: " + identity, () =>
    {
        var metadata = new FunctionMetadata(42, identity.Item1, identity.Item2, mutationDate,
            Array.Empty<ParameterMetadata>(), null);
        Equal("DROP FUNCTION IF EXISTS " + SqlText.Qualified(identity.Item1, identity.Item2) + ";",
            ProxyDeclaration.ScriptDrop(metadata));
    });
Check("Script-operation rewriting preserves comments within the leading operation", () =>
    Equal("CREATE /* first */  /* second */  FUNCTION dbo.f EXTERNAL FUNCTION s.f",
        SqlText.FunctionDefinition("CREATE /* first */ OR /* second */ ALTER FUNCTION dbo.f EXTERNAL FUNCTION s.f", false)));
Check("Raw module changes invalidate the mutation snapshot", () =>
{
    FunctionMetadata F(string raw) => new(1, "dbo", "f", DateTime.MinValue,
        Array.Empty<ParameterMetadata>(), "same selected definition", raw);
    Equal(false, F("first module definition").SameVersion(F("second module definition")));
});
Check("Signature snapshot changes detected", () =>
{
    FunctionMetadata F(string type) => new(1, "dbo", "f", DateTime.MinValue,
        new[] { new ParameterMetadata(0, "", type) }, null);
    Equal(false, F("int").SameVersion(F("bigint")));
});

foreach (var suffix in new[] { "datawarehouse.fabric.microsoft.com", "test-datawarehouse.fabric.microsoft.com",
    "preview-datawarehouse.fabric.microsoft.com", "future-ring-datawarehouse.fabric.microsoft.com" })
{
    Check("Warehouse endpoint " + suffix, () => Equal(true, WarehouseEndpoint.IsSupported("warehouse." + suffix)));
    Check("Warehouse TCP endpoint and port " + suffix, () =>
        Equal(true, WarehouseEndpoint.IsSupported("TCP:warehouse." + suffix.ToUpperInvariant() + ",1433")));
    Check("Warehouse suffix cannot be appended to another domain " + suffix, () =>
        Equal(false, WarehouseEndpoint.IsSupported("warehouse." + suffix + ".example.org")));
}
Check("Warehouse suffix must start at a label or deployment-prefix boundary", () =>
    Equal(false, WarehouseEndpoint.IsSupported("warehouse.notdatawarehouse.fabric.microsoft.com")));
Check("Warehouse deployment prefixes are not a fixed allowlist", () =>
    Equal(true, WarehouseEndpoint.IsSupported("warehouse.custom-prefix-datawarehouse.fabric.microsoft.com")));
Check("Bare warehouse suffix is not an endpoint", () =>
    Equal(false, WarehouseEndpoint.IsSupported("datawarehouse.fabric.microsoft.com")));
Check("Azure SQL endpoint is excluded", () =>
    Equal(false, WarehouseEndpoint.IsSupported("warehouse.database.windows.net")));
Check("Fabric SQL database endpoint is excluded", () =>
    Equal(false, WarehouseEndpoint.IsSupported("warehouse.database.fabric.microsoft.com")));
Check("Unrelated server is excluded", () => Equal(false, WarehouseEndpoint.IsSupported("localhost")));
Check("Missing endpoint is excluded", () => Equal(false, WarehouseEndpoint.IsSupported(null)));

const string schemaPath = "Server/Database/Schema";
const string pivotPath = "Server/Database/Folder";
const string schemaObjects = "DatabaseDiagram;Table;View;Synonym;StoredProcedure;UserDefinedFunction;Sequence";
Check("Native schema object provides schema identity", () =>
    Equal("Sales.Team", ExplorerSchema.Resolve(schemaPath, "Schema", "Sales.Team", "Sales.Team", null, null)));
Check("Canonical schema observed in SSMS resolves", () =>
    Equal("api", ExplorerSchema.Resolve(schemaPath, "Schema", "api", "api", null, null)));
Check("Observed Functions folder inheriting Schema context is not a schema node", () =>
    Equal<string?>(null, ExplorerSchema.Resolve(schemaPath + "/UsrDbFunctionsFolder",
        "Schema", "api", "api", null, "UserDefinedFunction;UserDefinedAggregate")));
foreach (var child in new[] { "UsrDbTablesFolder", "UsrDbViewsFolder", "UsrDbStoredProceduresFolder" })
{
    Check("Inherited Schema context does not identify " + child, () =>
        Equal<string?>(null, ExplorerSchema.Resolve(schemaPath + "/" + child,
            "Schema", "api", "api", null, null)));
}
Check("Inherited Schema context cannot disguise an object as a schema", () =>
    Equal<string?>(null, ExplorerSchema.Resolve(schemaPath + "/UserDefinedFunction",
        "Schema", "api", "api", "'api'", schemaObjects)));
Check("Pivoted Fabric schema uses metadata rather than database context", () =>
    Equal("sales", ExplorerSchema.Resolve(pivotPath, "Database", null, "sales", "'sales'", schemaObjects)));
Check("Pivoted schema preserves escaped apostrophes", () =>
    Equal("O'Brien", ExplorerSchema.Resolve(pivotPath, "Database", null, "O'Brien", "'O''Brien'", schemaObjects)));
Check("Pivoted schema preserves spaces and brackets", () =>
    Equal("a ] schema", ExplorerSchema.Resolve(pivotPath, "Database", null, "a ] schema", "'a ] schema'", schemaObjects)));
Check("Database root is not a schema folder", () =>
    Equal<string?>(null, ExplorerSchema.Resolve("Server/Database", "Database", null, "warehouse", null, "Schema")));
Check("Tables folder is not a schema even with inherited schema metadata", () =>
    Equal<string?>(null, ExplorerSchema.Resolve(pivotPath, "Database", null, "Tables", "'Tables'", "Table")));
Check("Functions folder is not a pivoted schema", () =>
    Equal<string?>(null, ExplorerSchema.Resolve(pivotPath, "Database", null, "sales", "'sales'", "UserDefinedFunction")));
Check("Schema-bearing object is not a schema parent", () =>
    Equal<string?>(null, ExplorerSchema.Resolve("Server/Database/Table", "Table", null, "sales", "'sales'", schemaObjects)));
Check("Pivoted schema metadata must match the native node identity", () =>
    Equal<string?>(null, ExplorerSchema.Resolve(pivotPath, "Database", null, "sales", "'other'", schemaObjects)));
Check("Malformed pivoted schema literal is rejected", () =>
    Reject(() => ExplorerSchema.Resolve(pivotPath, "Database", null, "sales", "'sales' extra", schemaObjects)));
Check("Undoubled quote in pivoted schema is rejected", () =>
    Reject(() => ExplorerSchema.Resolve(pivotPath, "Database", null, "O'Brien", "'O'Brien'", schemaObjects)));
Check("Missing native schema identity is rejected", () =>
    Reject(() => ExplorerSchema.Resolve(schemaPath, "Schema", null, "dbo", null, null)));
Check("Same function name in another schema is excluded", () =>
{
    var function = new FunctionMetadata(1, "sales", "f", DateTime.MinValue, Array.Empty<ParameterMetadata>(), null);
    Equal(true, function.IsInSchema("sales"));
    Equal(false, function.IsInSchema("dbo"));
    Equal(true, function.IsInSchema(null));
});
Check("Schema identity is not split at dots or compared as a prefix", () =>
{
    var function = new FunctionMetadata(1, "a.b", "f", DateTime.MinValue, Array.Empty<ParameterMetadata>(), null);
    Equal(true, function.IsInSchema("a.b"));
    Equal(false, function.IsInSchema("a"));
});
Check("Case-distinct schema metadata is not merged", () =>
    Equal(false, new FunctionMetadata(1, "Sales", "f", DateTime.MinValue,
        Array.Empty<ParameterMetadata>(), null).IsInSchema("sales")));

foreach (var failure in failures) Console.Error.WriteLine("FAIL " + failure);
Console.WriteLine($"{count - failures.Count}/{count} assertions passed.");
return failures.Count == 0 ? 0 : 1;
