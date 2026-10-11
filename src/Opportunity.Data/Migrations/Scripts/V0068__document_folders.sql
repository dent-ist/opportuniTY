-- V0068: read-only document folders derived from imported folder paths (#179, Q-55, familiarity guide P-8).
-- An import that maps the Folder Path structural target files each document under one folder; the folders it names
-- (and every folder above them) are created on the way, in the chunk's transaction. Folders are never created, renamed
-- or moved by users in M3: the tree is a read-only view of the loaded paths. Matching is case-insensitive
-- (path_key = the upper-case display path, Opportunity.Core.Documents.FolderPath.Key); the first spelling loaded is kept.
-- ancestor_ids lists the folder's ancestors outermost first and the folder itself last, so the search projection can
-- carry every folder a document is "in" (with subfolders) without walking the tree.
-- Existing documents keep their loaded path in metadata_raw ("s:FolderPath") and get a folder when an overlay maps it.

CREATE TABLE opportunity.document_folder (
    workspace_id        uuid        NOT NULL,
    folder_id           uuid        NOT NULL,
    parent_folder_id    uuid        NULL,
    name                text        NOT NULL,
    path                text        NOT NULL,
    path_key            text        NOT NULL,
    depth               integer     NOT NULL,
    ancestor_ids        uuid[]      NOT NULL,
    created_at          timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT document_folder_pk PRIMARY KEY (workspace_id, folder_id),
    CONSTRAINT document_folder_workspace_fk FOREIGN KEY (workspace_id) REFERENCES opportunity.workspace (workspace_id),
    CONSTRAINT document_folder_parent_fk FOREIGN KEY (workspace_id, parent_folder_id)
        REFERENCES opportunity.document_folder (workspace_id, folder_id),
    CONSTRAINT document_folder_parent_ck CHECK (parent_folder_id IS DISTINCT FROM folder_id),
    CONSTRAINT document_folder_name_ck CHECK (length(name) BETWEEN 1 AND 255 AND name = btrim(name) AND name !~ '[[:cntrl:]\\/]'),
    CONSTRAINT document_folder_path_ck CHECK (length(path) BETWEEN 1 AND 4000 AND length(path_key) BETWEEN 1 AND 8000),
    CONSTRAINT document_folder_depth_ck CHECK (
        depth BETWEEN 1 AND 64
        AND (parent_folder_id IS NULL) = (depth = 1)
        AND cardinality(ancestor_ids) = depth
        AND ancestor_ids[depth] = folder_id)
) WITH (autovacuum_vacuum_scale_factor = 0.02, autovacuum_analyze_scale_factor = 0.01);

SELECT opportunity.enable_workspace_rls('opportunity.document_folder');

CREATE UNIQUE INDEX document_folder_path_uq ON opportunity.document_folder (workspace_id, path_key);

-- Lazy tree loading: one level at a time, by name (top-level folders share the all-zero parent key).
CREATE INDEX document_folder_children_ix ON opportunity.document_folder
    (workspace_id, coalesce(parent_folder_id, '00000000-0000-0000-0000-000000000000'::uuid), path_key, folder_id);

-- The document's folder. A projection input (re-indexed with the document); NOT VALID so the large table is not scanned.
ALTER TABLE opportunity.document
    ADD COLUMN folder_id uuid NULL,
    ADD CONSTRAINT document_folder_fk FOREIGN KEY (workspace_id, folder_id)
        REFERENCES opportunity.document_folder (workspace_id, folder_id) NOT VALID;
