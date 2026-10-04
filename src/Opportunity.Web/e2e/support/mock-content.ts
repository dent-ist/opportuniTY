import type { Route } from '@playwright/test';

// The document content API (E11-T01) of the e2e mock: metadata with the viewer's availability flags, extracted text
// in 256 KiB chunks, page lists, page images and thumbnails, and native downloads. Every delivery is "audited" in
// the mock's log with its rendition and purpose, like the protected-content gateway (ADR-013).
//
// The synthetic corpus (document number n):
// - n % 10 === 4: a PDF attachment with page images (imported), text and native;
// - n % 10 === 7: a scanned file without extracted text (native only);
// - n % 10 === 9: text and native, page images still rendering;
// - otherwise: an e-mail with text and native.
// One document can be given a 10 MB text (`largeTextDocument`), truncated for search (Q-29).

export type Rendition = 'metadata' | 'text' | 'pages' | 'image' | 'thumbnail' | 'native';

export interface ContentDelivery {
  documentId: string;
  rendition: Rendition;
  purpose: 'display' | 'prefetch' | 'download';
}

export interface ContentOptions {
  /** Document number whose extracted text is 10 MB (40 chunks). */
  largeTextDocument?: number;
}

export const CHUNK_BYTES = 256 * 1024;
const LARGE_TEXT_BYTES = 10 * 1024 * 1024;

const controlNumber = (n: number) => `ACM${String(n).padStart(7, '0')}`;
const hasImages = (n: number) => n % 10 === 4;
const hasText = (n: number) => n % 10 !== 7;
const imagesPending = (n: number) => n % 10 === 9;
export const pageCountOf = (n: number) => (n % 7) + 1;

/** Extracted text of mock document `n`: a few paragraphs, so the viewer shows something realistic. */
export function documentText(n: number): string {
  const cn = controlNumber(n);
  return [
    `Document ${cn}`,
    '',
    `Subject: Quarterly terms ${n}`,
    '',
    'Please find the revised supply terms attached. The pricing schedule applies from the start of the next ' +
      'quarter, and either party may end the agreement with thirty days of written notice.',
    '',
    'Regards,',
    'A. Sender',
  ].join('\n');
}

const largeChunks = new Map<number, string>();

/** Chunk `index` of the 10 MB text: numbered lines of ASCII, exactly 256 KiB each. */
function largeChunk(n: number, index: number): string {
  let text = largeChunks.get(index);
  if (text === undefined) {
    const lines: string[] = [];
    let size = 0;
    for (let line = 1; size < CHUNK_BYTES; line++) {
      const content =
        `${controlNumber(n)} part ${index + 1} line ${line}: the supply agreement continues on ` +
        'these terms, and either party may give notice under clause 14.\n';
      lines.push(content);
      size += content.length;
    }
    text = lines.join('').slice(0, CHUNK_BYTES);
    largeChunks.set(index, text);
  }
  return text;
}

/** Snippets of a search hit for a keyword query: the first occurrence of the word, highlighted (E07). */
export function snippetsFor(n: number, query: string) {
  const word = query.trim().split(/\s+/)[0] ?? '';
  if (!word || word.includes(':') || !hasText(n)) return [];
  const text = documentText(n);
  const at = text.toLowerCase().indexOf(word.toLowerCase());
  if (at < 0) return [];
  const start = Math.max(0, at - 40);
  const snippet = text.slice(start, at + word.length + 40).replace(/\n/g, ' ');
  return [{ text: snippet, highlights: [{ start: at - start, end: at - start + word.length }] }];
}

function field(
  fieldId: number,
  displayName: string,
  typeLabel: string,
  displayValue: string | null,
  rawValue: string | null = displayValue,
  storage = 'column',
) {
  return {
    fieldId,
    displayName,
    typeLabel,
    displayValue,
    rawValue,
    value: displayValue,
    type: typeLabel === 'Date' ? 'date' : 'text',
    format: typeLabel === 'Date' ? 'dateTime' : 'text',
    storage,
    isHidden: false,
    isSystem: storage === 'column',
    multiValue: false,
    choices: null,
    decimalScale: null,
  };
}

/** `GET …/documents/{id}` (DocumentResource). */
export function documentResource(n: number, options: ContentOptions) {
  const attachment = n % 4 === 0;
  const large = options.largeTextDocument === n;
  const date = new Date(Date.UTC(2024, 0, 1) + n * 3_600_000);
  const iso = date.toISOString();
  const textBytes = large ? LARGE_TEXT_BYTES : documentText(n).length;
  const extension = hasImages(n) || attachment ? 'pdf' : hasText(n) ? 'msg' : 'tif';
  return {
    documentId: `doc-${n}`,
    controlNumber: controlNumber(n),
    documentVersion: 3,
    familyId: `doc-${attachment ? n - 1 : n}`,
    parentDocumentId: attachment ? `doc-${n - 1}` : null,
    displayTimeZone: 'UTC',
    fields: [
      field(1, 'Control Number', 'Identifier', controlNumber(n)),
      field(
        2,
        'Document Date',
        'Date',
        `${iso.slice(0, 10)} ${iso.slice(11, 16)} UTC`,
        `${date.getUTCMonth() + 1}/${date.getUTCDate()}/${date.getUTCFullYear()} ${iso.slice(11, 16)}`,
      ),
      field(
        3,
        'File Name',
        'Text',
        attachment ? `Attachment ${n}.pdf` : `RE: Quarterly terms ${n}.msg`,
      ),
      field(4, 'File Type', 'Text', attachment ? 'PDF' : 'Email'),
      field(5, 'Custodian', 'Text', n % 2 ? 'Smith, Jordan' : 'Lee, Avery'),
      field(6, 'Page Count', 'Whole Number', String(pageCountOf(n))),
      field(7, 'MD5 Hash', 'Text', (n * 2654435761).toString(16).padStart(32, '0').slice(-32)),
      field(
        1000,
        'Responsiveness',
        'Single Choice',
        n % 3 === 0 ? 'Responsive' : null,
        null,
        'coding',
      ),
    ],
    text: {
      available: hasText(n),
      missing: false,
      chunkCount: hasText(n) ? Math.ceil(textBytes / CHUNK_BYTES) : 0,
      chunkSizeBytes: CHUNK_BYTES,
      sizeBytes: hasText(n) ? textBytes : null,
      length: hasText(n) ? textBytes : null,
      truncated: large,
      encodingWarning: false,
    },
    images: {
      available: hasImages(n),
      incomplete: false,
      pageCount: hasImages(n) ? pageCountOf(n) : 0,
      source: hasImages(n) ? 'imported' : null,
      status: hasImages(n) ? 'ready' : imagesPending(n) ? 'pending' : null,
    },
    native: {
      available: true,
      missing: false,
      fileExtension: extension,
      sizeBytes: 1024 * ((n * 37) % 900) + 512,
    },
  };
}

function textChunk(n: number, index: number, options: ContentOptions) {
  const large = options.largeTextDocument === n;
  if (!hasText(n)) {
    return {
      chunkIndex: 0,
      chunkCount: 0,
      chunkSizeBytes: CHUNK_BYTES,
      byteStart: 0,
      byteEnd: 0,
      totalBytes: 0,
      isLast: true,
      text: '',
      truncated: false,
      missing: true,
      encodingWarning: false,
    };
  }
  const count = large ? LARGE_TEXT_BYTES / CHUNK_BYTES : 1;
  if (index >= count) return null;
  const text = large ? largeChunk(n, index) : documentText(n);
  const totalBytes = large ? LARGE_TEXT_BYTES : text.length;
  return {
    chunkIndex: index,
    chunkCount: count,
    chunkSizeBytes: CHUNK_BYTES,
    byteStart: index * CHUNK_BYTES,
    byteEnd: index * CHUNK_BYTES + text.length,
    totalBytes,
    isLast: index === count - 1,
    text,
    truncated: large,
    missing: false,
    encodingWarning: false,
  };
}

function pageList(n: number) {
  const items = Array.from({ length: pageCountOf(n) }, (_, i) => ({
    pageNumber: i + 1,
    widthPt: 612,
    heightPt: 792,
    rotation: 0,
    hasImage: true,
    hasThumbnail: true,
    imageMissing: false,
    imageContentType: 'image/svg+xml',
    imageWidthPx: 1275,
    imageHeightPx: 1650,
    colorMode: 'bitonal',
  }));
  return { items, nextCursor: null, total: { value: items.length, relation: 'eq' } };
}

/** A synthetic page: a letter-size sheet with the control number, the page number and grey text lines. */
function pageSvg(n: number, page: number): string {
  const lines = Array.from(
    { length: 18 },
    (_, i) =>
      `<rect x="72" y="${170 + i * 30}" width="${380 + ((i * 53 + n) % 90)}" height="10" fill="#9aa3ad"/>`,
  ).join('');
  return (
    '<svg xmlns="http://www.w3.org/2000/svg" width="612" height="792" viewBox="0 0 612 792">' +
    '<rect width="612" height="792" fill="#ffffff"/>' +
    `<text x="72" y="96" font-family="sans-serif" font-size="22" fill="#1a1e23">${controlNumber(n)}</text>` +
    `<text x="72" y="128" font-family="sans-serif" font-size="14" fill="#525b66">Supply agreement · page ${page}</text>` +
    lines +
    `<text x="540" y="760" font-family="sans-serif" font-size="12" fill="#525b66">${page}</text>` +
    '</svg>'
  );
}

const CONTENT_PATH =
  /^\/api\/v1\/workspaces\/[^/]+\/documents\/doc-(\d+)(?:\/(text\/chunks\/(\d+)|pages|pages\/(\d+)\/(image|thumbnail)|native))?$/;

/**
 * Answers a content request (and logs the delivery), or returns false when `path` is not a content route this
 * module serves. `GET …/text` (Range) and `POST …/views` stay in mock-api.ts.
 */
export function serveContent(
  route: Route,
  path: string,
  url: URL,
  options: ContentOptions & { contentDelayMs: number },
  log: (delivery: ContentDelivery) => string,
): Promise<void> | false {
  const match = CONTENT_PATH.exec(path);
  if (!match || route.request().method() !== 'GET') return false;
  const n = Number(match[1]);
  const documentId = `doc-${n}`;
  const purpose = url.searchParams.get('purpose') === 'prefetch' ? 'prefetch' : 'display';
  const respond = (rendition: Rendition, send: (retrievalId: string) => Promise<void>) => {
    const retrievalId = log({
      documentId,
      rendition,
      purpose: rendition === 'native' ? 'download' : purpose,
    });
    const run = () => send(retrievalId);
    return options.contentDelayMs > 0
      ? new Promise<void>((r) => setTimeout(r, options.contentDelayMs)).then(run)
      : run();
  };
  const headers = (retrievalId: string) => ({
    'X-Opportunity-Retrieval-Id': retrievalId,
    'Cache-Control': 'no-store',
  });
  const notFound = () =>
    route.fulfill({
      status: 404,
      contentType: 'application/problem+json',
      body: JSON.stringify({ title: 'Not found', status: 404 }),
    });

  if (match[2] === undefined) {
    return respond('metadata', (id) =>
      route.fulfill({ json: documentResource(n, options), headers: headers(id) }),
    );
  }
  if (match[3] !== undefined) {
    const chunk = textChunk(n, Number(match[3]), options);
    if (!chunk) return notFound();
    return respond('text', (id) =>
      route.fulfill({
        json: chunk,
        headers: chunk.missing ? { 'Cache-Control': 'no-store' } : headers(id),
      }),
    );
  }
  if (match[2] === 'pages') {
    if (!hasImages(n))
      return route.fulfill({
        json: { items: [], nextCursor: null, total: { value: 0, relation: 'eq' } },
      });
    return respond('pages', (id) => route.fulfill({ json: pageList(n), headers: headers(id) }));
  }
  if (match[5] !== undefined) {
    const page = Number(match[4]);
    if (!hasImages(n) || page < 1 || page > pageCountOf(n)) return notFound();
    return respond(match[5] as Rendition, (id) =>
      route.fulfill({
        status: 200,
        contentType: 'image/svg+xml',
        headers: headers(id),
        body: pageSvg(n, page),
      }),
    );
  }
  return respond('native', (id) =>
    route.fulfill({
      status: 200,
      contentType: 'application/octet-stream',
      headers: {
        ...headers(id),
        'Content-Disposition': `attachment; filename="${controlNumber(n)}.${documentResource(n, options).native.fileExtension}"`,
      },
      body: `Synthetic native of ${controlNumber(n)}`,
    }),
  );
}
