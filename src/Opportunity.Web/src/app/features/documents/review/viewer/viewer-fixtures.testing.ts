import type {
  DocumentFieldValueResource,
  DocumentPageResource,
  DocumentResource,
  DocumentTextChunkResource,
} from '../../../../core/api/generated/models';

/** `GET …/documents/{id}` of document `n`: text and native by default; override the artifact flags per test. */
export function documentResource(
  n: number,
  overrides: {
    text?: Partial<DocumentResource['text']>;
    images?: Partial<DocumentResource['images']>;
    native?: Partial<DocumentResource['native']>;
    fields?: DocumentFieldValueResource[];
  } = {},
): DocumentResource {
  return {
    documentId: `doc-${n}`,
    controlNumber: `ACM${String(n).padStart(7, '0')}`,
    documentVersion: 7,
    familyId: `doc-${n}`,
    parentDocumentId: null,
    displayTimeZone: 'UTC',
    fields: overrides.fields ?? [
      metadataField(1, 'Control Number', `ACM${String(n).padStart(7, '0')}`),
      metadataField(2, 'Document Date', '2024-03-01 14:30 UTC', '3/1/2024 2:30 PM'),
      metadataField(3, 'Custodian', 'Smith, Jordan'),
    ],
    text: {
      available: true,
      missing: false,
      chunkCount: 1,
      chunkSizeBytes: 262144,
      sizeBytes: 100,
      length: 100,
      truncated: false,
      encodingWarning: false,
      ...overrides.text,
    },
    images: {
      available: false,
      incomplete: false,
      pageCount: 0,
      source: null,
      status: null,
      ...overrides.images,
    },
    native: {
      available: true,
      missing: false,
      fileExtension: 'msg',
      sizeBytes: 2048,
      ...overrides.native,
    },
  };
}

export function metadataField(
  fieldId: number,
  displayName: string,
  displayValue: string | null,
  rawValue: string | null = displayValue,
): DocumentFieldValueResource {
  return {
    fieldId,
    displayName,
    displayValue,
    rawValue,
    value: displayValue,
    typeLabel: 'Text',
    type: 'text',
    format: 'text',
    storage: 'column',
    isHidden: false,
    isSystem: true,
    multiValue: false,
    choices: null,
    decimalScale: null,
  };
}

/** One text chunk (`GET …/text/chunks/{n}`). */
export function textChunkResource(
  text: string,
  overrides: Partial<DocumentTextChunkResource> = {},
): DocumentTextChunkResource {
  return {
    chunkIndex: 0,
    chunkCount: 1,
    chunkSizeBytes: 262144,
    byteStart: 0,
    byteEnd: text.length,
    totalBytes: text.length,
    isLast: true,
    text,
    truncated: false,
    missing: false,
    encodingWarning: false,
    ...overrides,
  };
}

export function pageResource(pageNumber: number): DocumentPageResource {
  return {
    pageNumber,
    widthPt: 612,
    heightPt: 792,
    rotation: 0,
    hasImage: true,
    hasThumbnail: true,
    imageMissing: false,
    imageContentType: 'image/png',
    imageWidthPx: 1275,
    imageHeightPx: 1650,
    colorMode: 'bitonal',
  };
}
