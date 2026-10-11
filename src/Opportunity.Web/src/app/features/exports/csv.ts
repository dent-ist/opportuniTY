// CSV lists the export and production screens build in the browser (validation errors, the Bates cross-reference).
// RFC 4180 quoting, CRLF line ends, and formula-leading cells neutralised like every platform-produced CSV (E08-T09).

const FORMULA_LEAD = /^[=+\-@\t\r]/;

export function csvCell(value: string | number | null | undefined): string {
  let text = value === null || value === undefined ? '' : String(value);
  if (FORMULA_LEAD.test(text)) text = `'${text}`;
  return /[",\r\n]/.test(text) ? `"${text.replaceAll('"', '""')}"` : text;
}

export function toCsv(rows: readonly (readonly (string | number | null | undefined)[])[]): string {
  return rows.map((r) => r.map(csvCell).join(',')).join('\r\n') + '\r\n';
}

/** `ACME_Production-Volume-1_Bates-cross-reference_2026-10-11T0930.csv` (familiarity guide §6). */
export function csvFileName(parts: readonly string[], at: Date = new Date()): string {
  const stamp = at.toISOString().slice(0, 16).replace(/:/g, '');
  const clean = parts.map((p) =>
    p
      .trim()
      .replace(/[^\w.-]+/g, '-')
      .replace(/^-+|-+$/g, ''),
  );
  return [...clean.filter(Boolean), stamp].join('_') + '.csv';
}

/** Hands a CSV built in the browser to the user as a file (UTF-8 with BOM, so spreadsheet tools read it right). */
export function saveCsv(fileName: string, csv: string): void {
  const blob = new Blob([String.fromCharCode(0xfeff), csv], { type: 'text/csv;charset=utf-8' });
  const url = URL.createObjectURL(blob);
  const a = document.createElement('a');
  a.href = url;
  a.download = fileName;
  a.hidden = true;
  document.body.appendChild(a);
  a.click();
  a.remove();
  setTimeout(() => URL.revokeObjectURL(url), 0);
}

/** Validation errors of a request (`errors` of a 400 problem) as rows: setting, message. */
export function validationRows(errors: Record<string, string[]> | undefined): string[][] {
  return Object.entries(errors ?? {}).flatMap(([key, messages]) => messages.map((m) => [key, m]));
}
