import type {
  BatesLevelResource,
  EndorsementPositionResource,
  ProductionImageFormatResource,
  ProductionQcCheckResultResource,
  ProductionQcReportResource,
  ProductionQcStatusResource,
  ProductionResource,
  ProductionSpecification,
  ProductionStatusResource,
} from '../../core/api/generated/models';
import type { BadgeTone, IconName } from '../../ui';

/** Tabs of a production page (ticket review E12-T08), in order; `?tab=` in the URL. */
export type ProductionTab = 'settings' | 'documents' | 'qc' | 'output' | 'privilegeLog';

export const PRODUCTION_TABS: readonly { readonly id: ProductionTab; readonly label: string }[] = [
  { id: 'settings', label: 'Settings' },
  { id: 'documents', label: 'Documents' },
  { id: 'qc', label: 'QC' },
  { id: 'output', label: 'Output' },
  { id: 'privilegeLog', label: 'Privilege Log' },
];

/** The six endorsement slots (ticket review E12-T04), in reading order. */
export const ENDORSEMENT_SLOTS: readonly {
  readonly position: EndorsementPositionResource;
  readonly label: string;
}[] = [
  { position: 'topLeft', label: 'Header left' },
  { position: 'topCenter', label: 'Header centre' },
  { position: 'topRight', label: 'Header right' },
  { position: 'bottomLeft', label: 'Footer left' },
  { position: 'bottomCenter', label: 'Footer centre' },
  { position: 'bottomRight', label: 'Footer right' },
];

export const MAX_PADDING = 12;

/** `ACME0000001` for prefix ACME, number 1, padding 7. */
export function formatBates(prefix: string, n: number, padding: number, suffix = ''): string {
  return `${prefix}${String(n).padStart(padding, '0')}${suffix}`;
}

export interface BatesPreview {
  readonly first: string;
  /** Null until the number of documents is known. */
  readonly last: string | null;
  readonly count: number | null;
  /**
   * Page-level numbering: every page takes a number, so the documents give only the smallest range (one page each);
   * the exact range is known once the numbers are assigned.
   */
  readonly atLeast: boolean;
}

/** First and last number and the range size before allocation (familiarity guide §5.6 Numbering). */
export function batesPreview(form: ProductionForm, documents: number | null): BatesPreview | null {
  const prefix = form.prefix.trim();
  const start = toInt(form.startNumber);
  const padding = toInt(form.padding);
  if (!prefix || start === null || start < 1 || padding === null || padding < 1) return null;
  const suffix = form.suffix.trim();
  return {
    first: formatBates(prefix, start, padding, suffix),
    last:
      documents === null || documents < 1
        ? null
        : formatBates(prefix, start + documents - 1, padding, suffix),
    count: documents,
    atLeast: form.level === 'page',
  };
}

/**
 * The next free number for a prefix (ticket review E12-T03): one after the highest number any production of the
 * workspace holds with that prefix. Discarded drafts released theirs; voided productions keep theirs forever.
 */
export function nextAvailableStart(
  prefix: string,
  productions: readonly ProductionResource[],
  except?: string,
): number {
  const p = prefix.trim().toLowerCase();
  let last = 0;
  for (const production of productions) {
    if (production.productionId === except || production.status === 'discarded') continue;
    if (production.specification.bates.prefix.trim().toLowerCase() !== p) continue;
    const n = production.bates.lastNumber;
    if (n !== null && n !== undefined) last = Math.max(last, Number(n));
  }
  return last + 1;
}

/** Everything the Settings tab edits; the rest of the specification is kept as it is. */
export interface ProductionForm {
  name: string;
  prefix: string;
  startNumber: string;
  padding: string;
  suffix: string;
  level: BatesLevelResource;
  withheld: 'placeholder' | 'block';
  imageFormat: Exclude<ProductionImageFormatResource, null>;
  dpi: string;
  includeText: boolean;
  redactionSetId: string;
  endorsements: Record<EndorsementPositionResource, string>;
  fontSize: string;
  expandCanvas: boolean;
  withheldText: string;
  technicalIssueText: string;
  nativeSlipSheetText: string;
}

export function emptyEndorsements(): Record<EndorsementPositionResource, string> {
  return {
    topLeft: '',
    topCenter: '',
    topRight: '',
    bottomLeft: '{confidentiality}',
    bottomCenter: '',
    bottomRight: '{bates}',
  };
}

export function defaultProductionForm(): ProductionForm {
  return {
    name: '',
    prefix: '',
    startNumber: '1',
    padding: '7',
    suffix: '',
    level: 'page',
    withheld: 'placeholder',
    imageFormat: 'tiffG4',
    dpi: '300',
    includeText: true,
    redactionSetId: '',
    endorsements: emptyEndorsements(),
    fontSize: '',
    expandCanvas: false,
    withheldText: 'Withheld – Privileged',
    technicalIssueText: 'Technical Issue',
    nativeSlipSheetText: 'Document Produced in Native Format',
  };
}

export function formFromSpecification(name: string, s: ProductionSpecification): ProductionForm {
  const endorsements = emptyEndorsements();
  if (s.endorsements?.items) {
    for (const key of Object.keys(endorsements) as EndorsementPositionResource[]) {
      endorsements[key] = '';
    }
    for (const item of s.endorsements.items) endorsements[item.position] = item.template;
  }
  const defaults = defaultProductionForm();
  return {
    name,
    prefix: s.bates.prefix,
    startNumber: String(s.bates.startNumber ?? 1),
    padding: String(s.bates.padding ?? 7),
    suffix: s.bates.suffix ?? '',
    level: s.bates.level ?? 'page',
    withheld: s.withheldDocuments === 'block' ? 'block' : 'placeholder',
    imageFormat: s.images?.format ?? 'tiffG4',
    dpi: String(s.images?.dpi ?? 300),
    includeText: s.includeText ?? true,
    redactionSetId: s.redactionSetId ?? '',
    endorsements,
    fontSize: s.endorsements?.fontSize ? String(s.endorsements.fontSize) : '',
    expandCanvas: s.endorsements?.expandCanvas === true,
    withheldText: s.placeholders?.withheld ?? defaults.withheldText,
    technicalIssueText: s.placeholders?.technicalIssue ?? defaults.technicalIssueText,
    nativeSlipSheetText: s.placeholders?.nativeSlipSheet ?? defaults.nativeSlipSheetText,
  };
}

/**
 * The specification to send: the form's values over `base` (the stored specification), so settings this screen does
 * not edit (file-type rules, designation levels, the load-file field list) are kept. The API fills every default.
 */
export function toSpecification(
  form: ProductionForm,
  base: ProductionSpecification | null = null,
): ProductionSpecification {
  const fontSize = toInt(form.fontSize);
  return {
    ...(base ?? {}),
    bates: {
      prefix: form.prefix.trim(),
      startNumber: toInt(form.startNumber) ?? 1,
      padding: toInt(form.padding) ?? 7,
      suffix: form.suffix.trim() || null,
      level: form.level,
    },
    withheldDocuments: form.withheld,
    images: { ...(base?.images ?? {}), format: form.imageFormat, dpi: toInt(form.dpi) ?? 300 },
    includeText: form.includeText,
    redactionSetId: form.redactionSetId || null,
    endorsements: {
      ...(base?.endorsements ?? {}),
      items: ENDORSEMENT_SLOTS.filter((s) => form.endorsements[s.position].trim()).map((s) => ({
        position: s.position,
        template: form.endorsements[s.position].trim(),
      })),
      fontSize: fontSize ?? base?.endorsements?.fontSize ?? null,
      expandCanvas: form.expandCanvas,
    },
    placeholders: {
      withheld: form.withheldText.trim() || null,
      technicalIssue: form.technicalIssueText.trim() || null,
      nativeSlipSheet: form.nativeSlipSheetText.trim() || null,
    },
  };
}

/** Client-side checks; the API validates everything again. */
export function formProblems(form: ProductionForm): string[] {
  const problems: string[] = [];
  if (!form.prefix.trim()) problems.push('Bates prefix: give a prefix, e.g. ACME.');
  const start = toInt(form.startNumber);
  if (start === null || start < 1) problems.push('Bates start: a whole number from 1.');
  const padding = toInt(form.padding);
  if (padding === null || padding < 1 || padding > MAX_PADDING) {
    problems.push(`Bates padding: a whole number from 1 to ${MAX_PADDING}.`);
  } else if (start !== null && String(start).length > padding) {
    problems.push('Bates start: more digits than the padding allows.');
  }
  const dpi = toInt(form.dpi);
  if (dpi === null || dpi < 72 || dpi > 600) problems.push('Image resolution: 72 to 600 DPI.');
  if (form.fontSize.trim()) {
    const size = toInt(form.fontSize);
    if (size === null || size < 6 || size > 24) problems.push('Font size: 6 to 24 points.');
  }
  return problems;
}

export function toInt(value: string): number | null {
  const n = Number(value.trim());
  return value.trim() && Number.isInteger(n) ? n : null;
}

/**
 * The user-visible stage (ticket review E12-T02): Draft → Bates assigned → QC passed → Finalized; voided and discarded
 * productions say so. The QC stage needs the latest QC run, which only the production page reads.
 */
export function stageOf(
  p: ProductionResource,
  qc: ProductionQcReportResource | null = null,
): { label: string; tone: BadgeTone; icon: IconName } {
  const byStatus: Record<
    Exclude<ProductionStatusResource, 'draft'>,
    { label: string; tone: BadgeTone; icon: IconName }
  > = {
    finalized: { label: 'Finalized', tone: 'success', icon: 'lock' },
    voided: { label: 'Voided', tone: 'danger', icon: 'error' },
    discarded: { label: 'Discarded', tone: 'neutral', icon: 'trash' },
  };
  if (p.status !== 'draft') return byStatus[p.status];
  if (p.bates.state === 'allocating')
    return { label: 'Assigning Bates', tone: 'info', icon: 'refresh' };
  if (p.bates.state === 'failed') return { label: 'Bates failed', tone: 'danger', icon: 'error' };
  if (p.bates.state === 'allocated') {
    return qc?.outcome === 'passed'
      ? { label: 'QC passed', tone: 'success', icon: 'success' }
      : { label: 'Bates assigned', tone: 'info', icon: 'production' };
  }
  return { label: 'Draft', tone: 'neutral', icon: 'pencil' };
}

export function batesRange(p: ProductionResource): string {
  return p.bates.first && p.bates.last ? `${p.bates.first} – ${p.bates.last}` : '';
}

export const QC_STATUS: Record<
  ProductionQcStatusResource,
  { label: string; tone: BadgeTone; icon: IconName }
> = {
  passed: { label: 'Passed', tone: 'success', icon: 'success' },
  failed: { label: 'Failed', tone: 'danger', icon: 'error' },
  overridden: { label: 'Overridden', tone: 'warning', icon: 'warning' },
  warning: { label: 'Warning', tone: 'warning', icon: 'warning' },
  acknowledged: { label: 'Acknowledged', tone: 'info', icon: 'check' },
};

/** Failed blocking checks that may be overridden with a reason (E12-T07). */
export function overridable(checks: readonly ProductionQcCheckResultResource[]) {
  return checks.filter((c) => c.status === 'failed' && c.overridable);
}

/** Failed blocking checks that cannot be overridden: they must be fixed. */
export function hardFailures(checks: readonly ProductionQcCheckResultResource[]) {
  return checks.filter((c) => c.status === 'failed' && !c.overridable);
}

export function warnings(checks: readonly ProductionQcCheckResultResource[]) {
  return checks.filter((c) => c.status === 'warning');
}

/** `controlnumber:("A" OR "B")`: the documents of a list as a Documents query (guide §2.4 "Open in Documents"). */
export function controlNumberQuery(controlNumbers: readonly (string | null)[]): string | null {
  const numbers = [...new Set(controlNumbers.filter((c): c is string => !!c))];
  if (numbers.length === 0) return null;
  const quoted = numbers.map((n) => `"${n.replace(/["\\]/g, (c) => `\\${c}`)}"`);
  return `controlnumber:(${quoted.join(' OR ')})`;
}
