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
