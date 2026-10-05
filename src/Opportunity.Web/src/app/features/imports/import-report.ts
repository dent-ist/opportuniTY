import type { ImportReportResource } from '../../core/api/generated/models';

// The finished import's report summary (E08-T06, Q-71) in plain language, grouped Rows · Files · Images · Families ·
// Fields. Pure rules, unit-tested; the import page renders the groups as description lists.

export interface ReportFact {
  readonly label: string;
  readonly value: number | null;
  /** A non-zero value someone should look at (errors, missing files, orphans). */
  readonly attention: boolean;
}

export interface ReportGroup {
  readonly id: 'rows' | 'files' | 'images' | 'families' | 'fields';
  readonly title: string;
  readonly facts: readonly ReportFact[];
  /** Shown instead of the facts when the group has nothing to report (e.g. no images in this import). */
  readonly empty: string | null;
}

const num = (v: number | string | null | undefined): number | null =>
  v === null || v === undefined ? null : Number(v);

function fact(
  label: string,
  value: number | string | null | undefined,
  attention = false,
): ReportFact {
  const n = num(value);
  return { label, value: n, attention: attention && (n ?? 0) > 0 };
}

const allZero = (facts: readonly ReportFact[]) => facts.every((f) => !f.value);

export function reportGroups(s: ImportReportResource): readonly ReportGroup[] {
  const rows = [
    fact('Rows read from the load file', s.rows.read),
    fact('New documents loaded', s.rows.imported),
    fact('Existing documents updated', s.rows.overlaid),
    fact('Rows left unchanged', s.rows.skipped),
    fact('Rows not loaded (errors)', s.rows.errored, true),
    fact('Rows loaded with warnings', s.rows.withWarnings, true),
  ];
  const files = [
    fact('Natives stored', s.natives.linked),
    fact('Natives missing', s.natives.missing, true),
    fact('Extracted text stored', s.text.linked),
    fact('Extracted text missing', s.text.missing, true),
    fact('Text over the search size limit', s.text.truncated, true),
  ];
  const images = [
    fact('Documents with page images', s.images.documentsLinked),
    fact('Documents without page images', s.images.documentsWithoutImages, true),
    fact('Pages stored', s.images.pagesLinked),
    fact('Pages missing', s.images.pagesMissing, true),
  ];
  const families = [
    fact('Families built', s.families.built),
    fact('Attachments without their parent', s.families.orphans, true),
  ];
  const fields = [
    fact('Fields created', s.fieldsCreated),
    fact('Choices created', s.choicesCreated),
  ];
  return [
    { id: 'rows', title: 'Rows', facts: rows, empty: null },
    {
      id: 'files',
      title: 'Natives and text',
      facts: files,
      empty: allZero(files) ? 'No natives or extracted text were linked by this import.' : null,
    },
    {
      id: 'images',
      title: 'Images',
      facts: images,
      empty: allZero(images) ? 'No page images were loaded by this import.' : null,
    },
    {
      id: 'families',
      title: 'Families',
      facts: families,
      empty: allZero(families) ? 'No document families were built by this import.' : null,
    },
    {
      id: 'fields',
      title: 'Fields',
      facts: fields,
      empty: allZero(fields) ? 'No new fields or choices were needed.' : null,
    },
  ];
}

/** "Took 45 seconds", "Took 3 minutes 5 seconds", "Took 2 hours 10 minutes". */
export function elapsedText(seconds: number | string | null | undefined): string | null {
  const total = num(seconds);
  if (total === null || !Number.isFinite(total) || total < 0) return null;
  const s = Math.round(total);
  const plural = (n: number, unit: string) => `${n} ${unit}${n === 1 ? '' : 's'}`;
  if (s < 60) return `Took ${plural(s, 'second')}`;
  const minutes = Math.floor(s / 60);
  if (minutes < 60) {
    const rest = s % 60;
    return `Took ${plural(minutes, 'minute')}${rest ? ' ' + plural(rest, 'second') : ''}`;
  }
  const hours = Math.floor(minutes / 60);
  const rest = minutes % 60;
  return `Took ${plural(hours, 'hour')}${rest ? ' ' + plural(rest, 'minute') : ''}`;
}

/** Issue counts by code, errors first then by count, for the "Issues by type" table. */
export function issueRows(
  s: ImportReportResource,
): readonly { code: string; severity: 'Error' | 'Warning'; count: number }[] {
  return [...(s.issueCounts ?? [])]
    .map((c) => ({
      code: c.code,
      severity: c.severity === 'error' ? ('Error' as const) : ('Warning' as const),
      count: Number(c.count),
    }))
    .sort(
      (a, b) =>
        (a.severity === b.severity ? 0 : a.severity === 'Error' ? -1 : 1) ||
        b.count - a.count ||
        a.code.localeCompare(b.code),
    );
}
