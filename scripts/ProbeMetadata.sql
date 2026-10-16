-- Run manually in a disposable Fabric Warehouse through separately installed SSMS.
-- Read-only: no function creation, alteration or invocation.
SELECT SCHEMA_NAME(o.schema_id) AS schema_name, o.name, o.object_id, o.type,
       o.type_desc, o.modify_date,
       CONVERT(nvarchar(max), OBJECT_DEFINITION(o.object_id)) AS candidate_definition,
       CONVERT(nvarchar(max), m.definition) AS module_definition,
       DATALENGTH(OBJECT_DEFINITION(o.object_id)) AS candidate_bytes,
       DATALENGTH(m.definition) AS module_bytes
FROM sys.objects AS o
LEFT JOIN sys.sql_modules AS m ON m.object_id = o.object_id
WHERE o.type = 'XF'
ORDER BY schema_name, o.name;

SELECT o.object_id, SCHEMA_NAME(o.schema_id) AS schema_name, o.name AS function_name,
       p.parameter_id, p.name AS parameter_name, t.name AS type_name,
       p.max_length, p.precision, p.scale
FROM sys.objects AS o
LEFT JOIN sys.parameters AS p ON p.object_id = o.object_id
LEFT JOIN sys.types AS t ON t.user_type_id = p.user_type_id
WHERE o.type = 'XF'
ORDER BY o.object_id, p.parameter_id;

-- A null definition does not prove that the external binding does not exist.
-- Check metadata visibility with the warehouse administrator; do not guess names.
-- Compare both sources when a definition appears incomplete or truncated.
-- Neither source is guaranteed to expose a complete XF definition on every engine.
