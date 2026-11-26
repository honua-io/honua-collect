-- The base seed projects field lengths but does not copy layer annotations.
-- Preserve the disposable fixture's attachment capability in the active snapshot.
SELECT honua.seed_metadata_v2_compat_snapshot();

WITH fixture AS (
    SELECT s.environment, s.revision,
           jsonb_set(s.document, ARRAY['resources', (r.ordinality - 1)::text, 'metadata', 'annotations'],
                       l.metadata -> 'annotations') AS document
    FROM honua.metadata_v2_snapshots s
    JOIN honua.metadata_v2_current c USING (environment, revision)
    CROSS JOIN LATERAL jsonb_array_elements(s.document -> 'resources') WITH ORDINALITY AS r(value, ordinality)
    JOIN honua.layers l ON l.layer_id = 68950
    WHERE r.value #>> '{metadata,id}' = 'res-layer-68950'
), updated AS (
    UPDATE honua.metadata_v2_snapshots s
    SET document = f.document, etag = '"' || md5(f.document::text) || '"'
    FROM fixture f
    WHERE (s.environment, s.revision) = (f.environment, f.revision)
    RETURNING s.environment, s.revision, s.etag
)
UPDATE honua.metadata_v2_current c
SET etag = u.etag
FROM updated u
WHERE (c.environment, c.revision) = (u.environment, u.revision);
