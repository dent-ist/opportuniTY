import { HttpClient, HttpResponse } from '@angular/common/http';
import { DOCUMENT, Injectable, inject } from '@angular/core';
import { firstValueFrom } from 'rxjs';
import { ApiConfiguration } from '../../../core/api/generated/api-configuration';
import { listCodingLayouts } from '../../../core/api/generated/fn/fields/list-coding-layouts';
import { listFields } from '../../../core/api/generated/fn/fields/list-fields';
import type {
  CodingChangeRequest,
  CodingLayoutFieldResource,
  CodingLayoutResource,
  CursorPageOfDocumentPageResource,
  DocumentCodingResource,
  DocumentPageResource,
  DocumentResource,
  DocumentTextChunkResource,
  DocumentTextHitsResource,
  DocumentViewRecord,
  FieldResource,
  UpdateDocumentCodingRequest,
} from '../../../core/api/generated/models';
import { ApiError } from '../../../core/api/problem-details';
import { WorkspaceContext } from '../../../core/workspace/workspace-context';

// The two backends Review mode talks to, as ports so the viewer (E16-T04) and coding pane (E16-T05) can be built
// against them while the document content API (E11-T01) and the coding API (E10-T01) land. The HTTP adapters
// follow ADR-019 (workspace-scoped routes, ETag/If-Match, problem details); the e2e mock API serves the same
// shapes. Both are provided by the Documents page, so they are workspace-scoped and replaceable in tests.

/**
 * Why content is fetched (ADR-013 §7.3, gateway `purpose`): `display` for the document on screen, `prefetch`
 * for the next one. Prefetch is audited as such and never counts as viewed.
 */
export type ContentPurpose = 'display' | 'prefetch';

/** A gateway delivery: the content and its audit reference (`X-Opportunity-Retrieval-Id`), sent with the view. */
export interface Delivered<T> {
  readonly value: T;
  readonly retrievalId: string | null;
}

/** One fixed-size chunk of a document's extracted text (`GET …/text/chunks/{n}`, 256 KiB of UTF-8 each). */
export interface TextChunk {
  readonly documentId: string;
  readonly index: number;
  readonly count: number;
  readonly text: string;
  readonly isLast: boolean;
  /** The document has no stored text (chunk 0 of a document without text). */
  readonly missing: boolean;
  /** Search indexed only the start of the text (Q-29); the viewer still shows all of it. */
  readonly truncated: boolean;
  readonly encodingWarning: boolean;
  readonly retrievalId: string | null;
}

/** A highlighted unit (E16-T12): a term, phrase, wildcard or W/n proximity of the search or of a Highlight Set term. */
export interface TextHitUnit {
  readonly unit: number;
  readonly source: 'search' | 'highlightSet';
  readonly highlightSetId: string | null;
  readonly label: string;
  readonly kind: 'term' | 'phrase' | 'wildcard' | 'proximity';
  /** Palette colour (`search` for search hits). */
  readonly color: string;
  readonly count: number;
}

/** A hit span: UTF-16 offsets in the text of `chunk`; `end` may run past it into the next chunk. */
export interface TextHit {
  readonly unit: number;
  readonly chunk: number;
  readonly start: number;
  readonly end: number;
}

/** One page of `GET …/text/hits`: the units with this page's counts, and its hits in text order. */
export interface TextHitsPage {
  readonly units: readonly TextHitUnit[];
  readonly hits: readonly TextHit[];
  readonly nextChunk: number | null;
  readonly chunkCount: number;
  readonly missing: boolean;
}

export interface TextHitsRequest {
  /** The current search handle (its hits), or null. */
  readonly searchId: string | null;
  readonly highlightSetIds: readonly string[];
  readonly fromChunk: number;
}

/** Page image or thumbnail of the active page set. */
export type PageImageKind = 'image' | 'thumbnail';

/**
 * The document content API (E11-T01) as the viewer uses it. Every call goes through the protected-content gateway,
 * which authorizes and audits it before the first byte; nothing here is cached by the browser (`no-store`).
 */
@Injectable()
export abstract class DocumentContentApi {
  /** `GET …/documents/{id}`: fields with display values and which artifacts (text, images, native) exist. */
  abstract document(
    documentId: string,
    purpose: ContentPurpose,
  ): Promise<Delivered<DocumentResource>>;
  /** `GET …/documents/{id}/text/chunks/{n}`; chunk 0 of a document without text answers `missing`. */
  abstract textChunk(
    documentId: string,
    index: number,
    purpose: ContentPurpose,
  ): Promise<TextChunk>;
  /**
   * `GET …/documents/{id}/text/hits`: server-computed hit spans (search and Highlight Sets) for a page of chunks;
   * phrases and proximities are whole spans. An expired search handle or a deleted set answers 404.
   */
  abstract textHits(documentId: string, request: TextHitsRequest): Promise<TextHitsPage>;
  /** `GET …/documents/{id}/pages`: every page of the active page set, in order. */
  abstract pages(
    documentId: string,
    purpose: ContentPurpose,
  ): Promise<Delivered<readonly DocumentPageResource[]>>;
  /** `GET …/documents/{id}/pages/{n}/image|thumbnail` as bytes, shown through a short-lived object URL. */
  abstract pageImage(
    documentId: string,
    pageNumber: number,
    kind: PageImageKind,
    purpose: ContentPurpose,
  ): Promise<Delivered<Blob>>;
  /**
   * Starts the browser download of the native (`Document.DownloadNative`; the gateway audits it and sends an
   * attachment). `fileName` is the name the gateway gives it too (`ControlNumber.ext`).
   */
  abstract downloadNative(documentId: string, fileName: string): void;
  /** Records that the viewer displayed the document (`Document.Viewed`); never called for a prefetch alone. */
  abstract recordView(documentId: string, retrievalId: string | null): Promise<void>;
}

/** The content routes of ADR-019 under `…/workspaces/{id}/documents/{documentId}`. */
@Injectable()
export class HttpDocumentContentApi extends DocumentContentApi {
  private readonly http = inject(HttpClient);
  private readonly context = inject(WorkspaceContext);
  private readonly page = inject(DOCUMENT);

  async document(
    documentId: string,
    purpose: ContentPurpose,
  ): Promise<Delivered<DocumentResource>> {
    const response = await firstValueFrom(
      this.http.get<DocumentResource>(this.context.apiUrl('documents', documentId), {
        params: { purpose },
        observe: 'response',
      }),
    );
    return { value: response.body!, retrievalId: retrievalIdOf(response) };
  }

  async textChunk(documentId: string, index: number, purpose: ContentPurpose): Promise<TextChunk> {
    const response = await firstValueFrom(
      this.http.get<DocumentTextChunkResource>(
        this.context.apiUrl('documents', documentId, 'text', 'chunks', String(index)),
        { params: { purpose }, observe: 'response' },
      ),
    );
    return toTextChunk(documentId, response.body!, retrievalIdOf(response));
  }

  async textHits(documentId: string, request: TextHitsRequest): Promise<TextHitsPage> {
    const params: Record<string, string | string[]> = { fromChunk: String(request.fromChunk) };
    if (request.searchId) params['searchId'] = request.searchId;
    if (request.highlightSetIds.length) params['highlightSetId'] = [...request.highlightSetIds];
    const body = await firstValueFrom(
      this.http.get<DocumentTextHitsResource>(
        this.context.apiUrl('documents', documentId, 'text', 'hits'),
        { params },
      ),
    );
    return toTextHitsPage(body);
  }

  async pages(
    documentId: string,
    purpose: ContentPurpose,
  ): Promise<Delivered<readonly DocumentPageResource[]>> {
    const items: DocumentPageResource[] = [];
    let cursor: string | null = null;
    let first: string | null = null;
    do {
      const params: Record<string, string> = cursor ? { purpose, cursor } : { purpose };
      const response: HttpResponse<CursorPageOfDocumentPageResource> = await firstValueFrom(
        this.http.get<CursorPageOfDocumentPageResource>(
          this.context.apiUrl('documents', documentId, 'pages'),
          { params, observe: 'response' },
        ),
      );
      first ??= retrievalIdOf(response);
      items.push(...(response.body?.items ?? []));
      cursor = response.body?.nextCursor ?? null;
    } while (cursor);
    return { value: items, retrievalId: first };
  }

  async pageImage(
    documentId: string,
    pageNumber: number,
    kind: PageImageKind,
    purpose: ContentPurpose,
  ): Promise<Delivered<Blob>> {
    const response = await firstValueFrom(
      this.http.get(
        this.context.apiUrl('documents', documentId, 'pages', String(pageNumber), kind),
        { params: { purpose }, observe: 'response', responseType: 'blob' },
      ),
    );
    return { value: response.body ?? new Blob(), retrievalId: retrievalIdOf(response) };
  }

  downloadNative(documentId: string, fileName: string): void {
    // A navigation, so the browser streams the attachment to disk (natives can be large) with the session cookie.
    // The link exists only for this click: there is no content address on the page to copy.
    const link = this.page.createElement('a');
    link.href = this.context.apiUrl('documents', documentId, 'native');
    // `download` keeps a refused request (a problem response) from replacing the review page.
    link.download = fileName;
    link.rel = 'noopener';
    this.page.body.appendChild(link);
    link.click();
    link.remove();
  }

  async recordView(documentId: string, retrievalId: string | null): Promise<void> {
    const body: DocumentViewRecord = { retrievalId };
    await firstValueFrom(
      this.http.post(this.context.apiUrl('documents', documentId, 'views'), body),
    );
  }
}

export function toTextHitsPage(body: DocumentTextHitsResource): TextHitsPage {
  return {
    units: body.units.map((u) => ({
      unit: Number(u.unit),
      source: u.source,
      highlightSetId: u.highlightSetId,
      label: u.label,
      kind: u.kind,
      color: u.color ?? 'search',
      count: Number(u.count),
    })),
    hits: body.hits.map((h) => ({
      unit: Number(h.unit),
      chunk: Number(h.chunk),
      start: Number(h.start),
      end: Number(h.end),
    })),
    nextChunk: body.nextChunk === null ? null : Number(body.nextChunk),
    chunkCount: Number(body.chunkCount),
    missing: body.missing,
  };
}

function retrievalIdOf(response: HttpResponse<unknown>): string | null {
  return response.headers.get('X-Opportunity-Retrieval-Id');
}

export function toTextChunk(
  documentId: string,
  body: DocumentTextChunkResource,
  retrievalId: string | null,
): TextChunk {
  return {
    documentId,
    index: Number(body.chunkIndex),
    count: Number(body.chunkCount),
    text: body.text ?? '',
    isLast: body.isLast,
    missing: body.missing,
    truncated: body.truncated,
    encodingWarning: body.encodingWarning,
    retrievalId,
  };
}

/** A coding value as the API carries it (ADR-003 types: text, number, yes/no, choice names, user id). */
export type CodingValue = string | number | boolean | readonly string[] | null;

/** What the API says about one field of a document's coding. */
export interface CodingFieldState {
  /** The caller may change it: Coding.Write, plus Coding.WritePrivilege for a security-affecting field. */
  readonly editable: boolean;
  /** Changing it can change who may see the document (Q-11). */
  readonly securityAffecting: boolean;
  /** When it last changed (ISO 8601); null when never coded. */
  readonly changedAt: string | null;
}

/** Who made the latest coding change of a document (E10-T01 `lastEditor`). */
export interface CodingLastEditor {
  readonly displayName: string | null;
  readonly changedAt: string;
  /** Set when the change came from a Mass Edit job. */
  readonly jobId: string | null;
}

/** Search freshness of a document's coding: `pending` until the index has the saved version (E16-T05). */
export type CodingIndexState = 'searchable' | 'pending' | 'failed';

/** The current coding of a document (E10-T01). */
export interface DocumentCoding {
  readonly documentId: string;
  /** `DocumentVersion`, sent back as `If-Match` on save (ADR-019 §2.7). */
  readonly version: string;
  /** Values by field query name; fields without a value are absent. */
  readonly values: Readonly<Record<string, CodingValue>>;
  /** "Saved · indexing" while `pending`; searchable once the index caught up (read-your-own-writes). */
  readonly indexState: CodingIndexState;
  /** Per field (by query name): editability and last change, for every coding field the caller may see. */
  readonly fields: Readonly<Record<string, CodingFieldState>>;
  readonly lastEditor: CodingLastEditor | null;
}

/** A choice of a choice field, in admin order; inactive choices keep their values but cannot be newly chosen. */
export interface CodingChoice {
  readonly name: string;
  readonly active: boolean;
}

/** "Shown when `queryName` has any of `choices`" (choice field) or "… is `value`" (Yes/No field). */
export interface CodingCondition {
  readonly queryName: string;
  readonly choices: readonly string[] | null;
  readonly value: boolean | null;
}

/** A field of a coding layout, with what the editor needs from the field catalogue. */
export interface CodingLayoutField {
  readonly queryName: string;
  readonly label: string;
  readonly type: FieldResource['type'];
  readonly multiValue: boolean;
  readonly securityAffecting: boolean;
  readonly datePrecision: 'date' | 'dateTime' | null;
  readonly choices: readonly CodingChoice[];
  readonly required: boolean;
  readonly readOnly: boolean;
  readonly visibleWhen: CodingCondition | null;
  /** Q-48: the layout offers this field ticked in Apply to Family / Duplicates. */
  readonly applyToFamilyByDefault: boolean;
}

export interface CodingLayoutSection {
  readonly title: string;
  readonly fields: readonly CodingLayoutField[];
}

/** A coding layout the user may use (E04-T03): sections of fields in order. */
export interface CodingLayout {
  /** Stable key for the layout selector and the remembered choice. */
  readonly id: string;
  readonly name: string;
  readonly isDefault: boolean;
  readonly sections: readonly CodingLayoutSection[];
  /** The `layoutId` saves are checked against; null for the all-fields layout of a workspace without one. */
  readonly serverId: string | null;
}

/** One save attempt. A retry of the same attempt reuses its key, so the API applies it at most once. */
export interface CodingSaveOptions {
  readonly layoutId: string | null;
  readonly idempotencyKey: string;
}

/** 412 `version-conflict`: someone changed the document since it was read. Never last-write-wins. */
export class CodingConflictError extends Error {
  constructor(readonly current: DocumentCoding) {
    super('The document was changed by someone else.');
    this.name = 'CodingConflictError';
  }
}

/** The save was refused (validation, permission); `fieldErrors` are messages by query name. */
export class CodingRejectedError extends Error {
  constructor(
    readonly status: number,
    readonly fieldErrors: Readonly<Record<string, string>>,
    message: string,
  ) {
    super(message);
    this.name = 'CodingRejectedError';
  }
}

/** Key of the layout used when the workspace's layouts list no fields (the default layout starts empty). */
export const ALL_FIELDS_LAYOUT = 'all-coding-fields';

@Injectable()
export abstract class CodingApi {
  /** The layouts the user's roles may use, default first; never empty. */
  abstract layouts(): Promise<readonly CodingLayout[]>;
  abstract get(documentId: string): Promise<DocumentCoding>;
  /**
   * Saves changed values. Rejects with `CodingConflictError` when `version` is stale and `CodingRejectedError` when
   * the API refuses the values; never overwrites a newer version silently.
   */
  abstract save(
    documentId: string,
    version: string,
    values: Readonly<Record<string, CodingValue>>,
    options: CodingSaveOptions,
  ): Promise<DocumentCoding>;
}

/** Transient failures a save is retried once for, with the same Idempotency-Key. */
const RETRYABLE = new Set([0, 502, 503, 504]);

/**
 * `GET` / `PUT …/documents/{id}/coding` with `If-Match` and `Idempotency-Key` (E10-T01), and `GET …/coding-layouts`.
 * The API addresses fields by id and choices by choice id (ADR-003: ids survive renames); this adapter translates to
 * and from the query names and choice names the review UI works with, using the workspace's field catalogue
 * (`GET …/fields`, loaded once per adapter).
 */
@Injectable()
export class HttpCodingApi extends CodingApi {
  private readonly http = inject(HttpClient);
  private readonly rootUrl = inject(ApiConfiguration).rootUrl;
  private readonly context = inject(WorkspaceContext);
  private catalog?: Promise<CodingCatalog>;

  async layouts(): Promise<readonly CodingLayout[]> {
    const [catalog, items] = await Promise.all([
      this.fields(),
      firstValueFrom(
        listCodingLayouts(this.http, this.rootUrl, { workspaceId: this.context.workspaceId }),
      ).then(
        (r) => r.body.items,
        () => [] as CodingLayoutResource[],
      ),
    ]);
    return layoutsOf(items, catalog);
  }

  async get(documentId: string): Promise<DocumentCoding> {
    const [response, catalog] = await Promise.all([
      firstValueFrom(
        this.http.get<DocumentCodingResource>(
          this.context.apiUrl('documents', documentId, 'coding'),
          { observe: 'response' },
        ),
      ),
      this.fields(),
    ]);
    return toCoding(response, catalog);
  }

  async save(
    documentId: string,
    version: string,
    values: Readonly<Record<string, CodingValue>>,
    options: CodingSaveOptions,
  ): Promise<DocumentCoding> {
    const catalog = await this.fields();
    const body: UpdateDocumentCodingRequest = {
      changes: Object.entries(values).map(([queryName, value]) =>
        toChange(catalog, queryName, value),
      ),
      layoutId: options.layoutId,
    };
    const put = () =>
      firstValueFrom(
        this.http.put<DocumentCodingResource>(
          this.context.apiUrl('documents', documentId, 'coding'),
          body,
          {
            observe: 'response',
            headers: { 'If-Match': `"${version}"`, 'Idempotency-Key': options.idempotencyKey },
          },
        ),
      );
    try {
      let response: HttpResponse<DocumentCodingResource>;
      try {
        response = await put();
      } catch (e) {
        if (!(e instanceof ApiError) || !RETRYABLE.has(e.status)) throw e;
        response = await put();
      }
      return toCoding(response, catalog);
    } catch (e) {
      throw codingSaveError(e, catalog);
    }
  }

  private fields(): Promise<CodingCatalog> {
    this.catalog ??= firstValueFrom(
      listFields(this.http, this.rootUrl, { workspaceId: this.context.workspaceId }),
    ).then((r) => catalogOf(r.body.items));
    return this.catalog;
  }
}

interface CodingCatalogField {
  readonly fieldId: string;
  readonly queryName: string;
  /** Choice id → name, for choice fields. */
  readonly choices: ReadonlyMap<string, string>;
  readonly multiple: boolean;
  readonly resource: FieldResource;
}

export interface CodingCatalog {
  readonly byId: ReadonlyMap<string, CodingCatalogField>;
  readonly byName: ReadonlyMap<string, CodingCatalogField>;
}

export function catalogOf(items: readonly FieldResource[]): CodingCatalog {
  const fields = items.map((f): CodingCatalogField => ({
    fieldId: String(f.fieldId),
    queryName: f.queryName,
    choices: new Map((f.choices ?? []).map((c) => [String(c.choiceId), c.name])),
    multiple: f.type === 'multiChoice',
    resource: f,
  }));
  return {
    byId: new Map(fields.map((f) => [f.fieldId, f])),
    byName: new Map(fields.map((f) => [f.queryName.toLowerCase(), f])),
  };
}

/**
 * One `set` change; choice names become choice ids, sent as JSON numbers as the API's canonical values require
 * (ADR-003 §3). An unknown name is sent as is, so the API reports it.
 */
export function toChange(
  catalog: CodingCatalog,
  queryName: string,
  value: CodingValue,
): CodingChangeRequest {
  const field = catalog.byName.get(queryName.toLowerCase());
  if (!field) throw new Error(`Unknown coding field: ${queryName}`);
  const choiceId = (name: string): number | string => {
    const id = [...field.choices].find(([, n]) => n === name)?.[0];
    return id === undefined ? name : Number(id);
  };
  let sent: unknown = value;
  if (field.choices.size > 0 && value !== null) {
    sent = Array.isArray(value) ? value.map((v) => choiceId(String(v))) : choiceId(String(value));
    if (field.multiple && !Array.isArray(sent)) sent = [sent];
  }
  return { fieldId: field.fieldId, operation: 'set', value: sent };
}

export function toCoding(
  response: HttpResponse<DocumentCodingResource>,
  catalog: CodingCatalog,
): DocumentCoding {
  const etag = response.headers.get('ETag')?.replace(/^W\//, '').replace(/"/g, '');
  return codingOf(response.body!, catalog, etag);
}

/** A `DocumentCodingResource` (a read, a save, or the `current` of a 412) in query names and choice names. */
export function codingOf(
  body: DocumentCodingResource,
  catalog: CodingCatalog,
  etag?: string,
): DocumentCoding {
  const values: Record<string, CodingValue> = {};
  const fields: Record<string, CodingFieldState> = {};
  for (const entry of body.fields) {
    const field = catalog.byId.get(String(entry.fieldId));
    if (!field) continue;
    fields[field.queryName] = {
      editable: entry.editable ?? false,
      securityAffecting: entry.isSecurityAffecting ?? field.resource.isSecurityAffecting ?? false,
      changedAt: (entry.changedAt as string | null | undefined) ?? null,
    };
    if (entry.value === null || entry.value === undefined) continue;
    const raw = entry.value as unknown;
    const name = (id: unknown) => field.choices.get(String(id)) ?? String(id);
    values[field.queryName] =
      field.choices.size > 0
        ? Array.isArray(raw)
          ? raw.map(name)
          : name(raw)
        : (raw as CodingValue);
  }
  const editor = body.lastEditor;
  return {
    documentId: body.documentId,
    version: etag || String(body.documentVersion),
    values,
    indexState:
      body.indexingState === 'indexed'
        ? 'searchable'
        : body.indexingState === 'failed'
          ? 'failed'
          : 'pending',
    fields,
    lastEditor: editor
      ? {
          displayName: editor.displayName,
          changedAt: String(editor.changedAt),
          jobId: editor.jobId,
        }
      : null,
  };
}

/** The layouts in query names; a layout without fields shows every coding field (the API lists them all then). */
export function layoutsOf(
  items: readonly CodingLayoutResource[],
  catalog: CodingCatalog,
): CodingLayout[] {
  const entryOf = (id: unknown) => catalog.byId.get(String(id));
  const field = (f: CodingLayoutFieldResource): CodingLayoutField[] => {
    const entry = entryOf(f.fieldId);
    if (!entry) return [];
    const condition = f.visibleWhen;
    const controlling = condition ? entryOf(condition.fieldId) : undefined;
    return [
      {
        ...describe(entry.resource),
        required: f.isRequired,
        readOnly: f.isReadOnly,
        applyToFamilyByDefault: f.applyToFamilyByDefault ?? false,
        visibleWhen:
          condition && controlling
            ? {
                queryName: controlling.queryName,
                choices: condition.choiceIds
                  ? condition.choiceIds.map((c) => controlling.choices.get(String(c)) ?? String(c))
                  : null,
                value: condition.booleanValue,
              }
            : null,
      },
    ];
  };
  const layouts = items.map((l): CodingLayout => {
    const sections = l.sections
      .map((s) => ({ title: s.title, fields: s.fields.flatMap(field) }))
      .filter((s) => s.fields.length > 0);
    return sections.length > 0
      ? { id: l.layoutId, name: l.name, isDefault: l.isDefault, sections, serverId: l.layoutId }
      : { ...allFieldsLayout(catalog), id: l.layoutId, name: l.name, isDefault: l.isDefault };
  });
  return layouts.length > 0 ? layouts : [allFieldsLayout(catalog)];
}

/** Every visible coding field of the catalogue in field order, saved without a layout. */
function allFieldsLayout(catalog: CodingCatalog): CodingLayout {
  const fields = [...catalog.byId.values()]
    .map((f) => f.resource)
    .filter((f) => f.storage === 'coding' && !f.isHidden)
    .sort((a, b) => Number(a.fieldId) - Number(b.fieldId))
    .map((f) => ({
      ...describe(f),
      required: false,
      readOnly: false,
      visibleWhen: null,
      applyToFamilyByDefault: false,
    }));
  return {
    id: ALL_FIELDS_LAYOUT,
    name: 'All coding fields',
    isDefault: true,
    sections: fields.length > 0 ? [{ title: 'Coding fields', fields }] : [],
    serverId: null,
  };
}

function describe(
  f: FieldResource,
): Omit<CodingLayoutField, 'required' | 'readOnly' | 'visibleWhen' | 'applyToFamilyByDefault'> {
  return {
    queryName: f.queryName,
    label: f.displayName,
    type: f.type,
    multiValue: f.multiValue || f.type === 'multiChoice',
    securityAffecting: f.isSecurityAffecting,
    datePrecision: f.datePrecision ?? null,
    choices: (f.choices ?? []).map((c) => ({ name: c.name, active: c.isActive })),
  };
}

/** A failed save as the coding pane handles it: conflict, refused values or permission, or the original error. */
export function codingSaveError(e: unknown, catalog: CodingCatalog): unknown {
  if (!(e instanceof ApiError)) return e;
  const problem = e.problem;
  if (e.status === 412 && problem['current']) {
    return new CodingConflictError(codingOf(problem['current'] as DocumentCodingResource, catalog));
  }
  if (e.status === 400 || e.status === 422) {
    const fieldErrors: Record<string, string> = {};
    const listed =
      (problem['fieldErrors'] as { field: string; message: string }[] | undefined) ?? [];
    const byKey = Object.entries(problem.errors ?? {}).map(([field, messages]) => ({
      field,
      message: messages[0] ?? '',
    }));
    for (const { field, message } of [...listed, ...byKey]) {
      const id = /^f(\d+)$/.exec(field)?.[1];
      const name = id ? catalog.byId.get(id)?.queryName : undefined;
      if (name && !fieldErrors[name]) fieldErrors[name] = message;
    }
    return new CodingRejectedError(
      e.status,
      fieldErrors,
      problem.detail ?? 'The coding was not saved. Correct the highlighted fields.',
    );
  }
  if (e.status === 403) {
    return new CodingRejectedError(
      403,
      {},
      'You do not have permission to change one or more of these fields.',
    );
  }
  return e;
}
