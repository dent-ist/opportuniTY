import type { ProductionResource } from '../../../core/api/generated/models';
import { csvFileName, saveCsv, toCsv } from '../../exports/csv';
import type { ProductionApi } from '../production-api';

/**
 * The Bates cross-reference (Control Number ↔ ProdBegBates/ProdEndBates/attachment range) as a CSV, read from the API
 * page by page when the user asks for it, so the list is authorized for them at that moment; documents they may not
 * view are left out by the API (Q-52). Returns the number of rows written.
 */
export async function downloadCrossReference(
  api: ProductionApi,
  production: ProductionResource,
  workspaceName: string,
): Promise<number> {
  const rows: (string | number | null)[][] = [
    [
      'Sequence',
      'Control Number',
      'ProdBegBates',
      'ProdEndBates',
      'ProdBegAttach',
      'ProdEndAttach',
      'Numbers',
      'Output',
      'Designation',
    ],
  ];
  let cursor: string | null = null;
  do {
    const page = await api.documents(production.productionId, cursor, 500);
    for (const d of page.items) {
      rows.push([
        Number(d.sequence),
        d.controlNumber,
        d.prodBegBates,
        d.prodEndBates,
        d.prodBegAttach,
        d.prodEndAttach,
        Number(d.numbers),
        d.output,
        d.designation ?? null,
      ]);
    }
    cursor = page.nextCursor;
  } while (cursor);
  saveCsv(
    csvFileName([
      workspaceName,
      production.name,
      `v${production.version}`,
      'Bates-cross-reference',
    ]),
    toCsv(rows),
  );
  return rows.length - 1;
}
