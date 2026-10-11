// "Open in Documents" for a family or a duplicate group (E13-T02 privilege conflicts, guide §2.4): Documents reads
// these query parameters once, shows the relation as its search (the duplicate marker's pivot, E16-T10) and drops them
// from the URL. Kept apart from the grid so other sections can link without loading it.

export const RELATED_PARAM = 'related';
export const RELATED_ID_PARAM = 'relatedId';
export const RELATED_OF_PARAM = 'relatedOf';

export type RelatedKind = 'family' | 'duplicates';

/** Query parameters of a link that opens a family or duplicate group in Documents. */
export function relatedDocumentsParams(
  kind: RelatedKind,
  id: string,
  controlNumber: string,
): Record<string, string> {
  return { [RELATED_PARAM]: kind, [RELATED_ID_PARAM]: id, [RELATED_OF_PARAM]: controlNumber };
}

/** The relation a Documents URL asks for, or null. */
export function relatedFromParams(params: {
  get(name: string): string | null;
}): { kind: RelatedKind; id: string; controlNumber: string } | null {
  const kind = params.get(RELATED_PARAM);
  const id = params.get(RELATED_ID_PARAM);
  if ((kind !== 'family' && kind !== 'duplicates') || !id) return null;
  return { kind, id, controlNumber: params.get(RELATED_OF_PARAM) ?? '' };
}

// "Open in Documents" for a list another section holds (E12-T08: a production's documents, a QC exception list):
// `?listQuery=<query>&listLabel=<what it is>` becomes the list's search, read once like the relation above.
export const LIST_QUERY_PARAM = 'listQuery';
export const LIST_LABEL_PARAM = 'listLabel';

/** Query parameters of a link that opens a query in Documents, with a label for what the list shows. */
export function queryDocumentsParams(query: string, label: string): Record<string, string> {
  return { [LIST_QUERY_PARAM]: query, [LIST_LABEL_PARAM]: label };
}

/** The query (and its label) a Documents URL asks for, or null. */
export function listQueryFromParams(params: {
  get(name: string): string | null;
}): { query: string; label: string } | null {
  const query = params.get(LIST_QUERY_PARAM)?.trim();
  return query ? { query, label: params.get(LIST_LABEL_PARAM) ?? 'Documents' } : null;
}
