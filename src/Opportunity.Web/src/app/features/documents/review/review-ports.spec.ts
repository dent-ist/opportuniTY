import {
  HttpHeaders,
  HttpResponse,
  provideHttpClient,
  withInterceptors,
} from '@angular/common/http';
import { TestBed } from '@angular/core/testing';
import { FakeApi, provideFakeApi } from '../../../core/api/fake-api.testing';
import { problemDetailsInterceptor } from '../../../core/api/http';
import { ApiError } from '../../../core/api/problem-details';
import type {
  CodingLayoutResource,
  DocumentCodingResource,
  FieldResource,
} from '../../../core/api/generated/models';
import { WorkspaceContext } from '../../../core/workspace/workspace-context';
import { DocumentAccess, DocumentUnavailableError } from './document-access';
import {
  ALL_FIELDS_LAYOUT,
  CodingAccessLossError,
  CodingConflictError,
  CodingRejectedError,
  HttpCodingApi,
  HttpDocumentContentApi,
  catalogOf,
  codingSaveError,
  layoutsOf,
  toChange,
  toCoding,
} from './review-ports';

const field = (f: Partial<FieldResource>) =>
  ({ storage: 'coding', isHidden: false, isSecurityAffecting: false, ...f }) as FieldResource;
const FIELDS = [
  field({
    fieldId: 1000,
    queryName: 'responsiveness',
    displayName: 'Responsiveness',
    type: 'singleChoice',
    choices: [
      { choiceId: 1, name: 'Responsive', isActive: true },
      { choiceId: 2, name: 'Not Responsive', isActive: true },
    ],
  }),
  field({
    fieldId: 1001,
    queryName: 'issues',
    displayName: 'Issues',
    type: 'multiChoice',
    choices: [
      { choiceId: 7, name: 'Pricing', isActive: true },
      { choiceId: 8, name: 'Termination', isActive: false },
    ],
  }),
  field({ fieldId: 1002, queryName: 'notes', displayName: 'Notes', type: 'text', choices: null }),
  field({
    fieldId: 1003,
    queryName: 'privilege',
    displayName: 'Privilege',
    type: 'singleChoice',
    isSecurityAffecting: true,
    choices: [{ choiceId: 9, name: 'Withhold', isActive: true }],
  }),
  field({ fieldId: 5, queryName: 'controlnumber', type: 'keyword', storage: 'column' }),
];
const catalog = catalogOf(FIELDS);

describe('coding API adapter (E10-T01 ↔ Review mode)', () => {
  it('reads field ids and choice ids as query names and choice names, with field states and the last editor', () => {
    const body = {
      documentId: 'd1',
      documentVersion: 5,
      indexingState: 'pending',
      lastEditor: {
        userId: 'u2',
        displayName: 'J. Smith',
        changedAt: '2026-10-04T10:42:00Z',
        documentVersion: 5,
        jobId: null,
      },
      fields: [
        { fieldId: 1000, value: 2, editable: true, isSecurityAffecting: false },
        { fieldId: 1001, value: [7, 8], editable: true, isSecurityAffecting: false },
        { fieldId: 1002, value: null, editable: true, isSecurityAffecting: false },
        { fieldId: 1003, value: null, editable: false, isSecurityAffecting: true },
        { fieldId: 1004, value: 'not in the catalogue', editable: true },
      ],
    } as unknown as DocumentCodingResource;
    const coding = toCoding(
      new HttpResponse({ body, headers: new HttpHeaders({ ETag: 'W/"5"' }) }),
      catalog,
    );
    expect(coding).toEqual({
      documentId: 'd1',
      version: '5',
      values: { responsiveness: 'Not Responsive', issues: ['Pricing', 'Termination'] },
      indexState: 'pending',
      fields: {
        responsiveness: { editable: true, securityAffecting: false, changedAt: null },
        issues: { editable: true, securityAffecting: false, changedAt: null },
        notes: { editable: true, securityAffecting: false, changedAt: null },
        privilege: { editable: false, securityAffecting: true, changedAt: null },
      },
      lastEditor: { displayName: 'J. Smith', changedAt: '2026-10-04T10:42:00Z', jobId: null },
    });
    const failed = { ...body, indexingState: 'failed' } as DocumentCodingResource;
    expect(toCoding(new HttpResponse({ body: failed }), catalog).indexState).toBe('failed');
  });

  it('writes query names and choice names as field ids and numeric choice ids (ADR-003 §3)', () => {
    expect(toChange(catalog, 'Responsiveness', 'Responsive')).toEqual({
      fieldId: '1000',
      operation: 'set',
      value: 1,
    });
    expect(toChange(catalog, 'issues', ['Termination'])).toEqual({
      fieldId: '1001',
      operation: 'set',
      value: [8],
    });
    expect(toChange(catalog, 'issues', 'Pricing').value).toEqual([7]);
    // An unknown choice name goes as is, so the API reports it.
    expect(toChange(catalog, 'responsiveness', 'Nope').value).toBe('Nope');
    expect(toChange(catalog, 'notes', 'text').value).toBe('text');
    expect(toChange(catalog, 'responsiveness', null).value).toBeNull();
    expect(() => toChange(catalog, 'unknown', 'x')).toThrow(/Unknown coding field/);
  });

  it('reads layouts in query names, with conditions by choice name, and falls back to every coding field', () => {
    const layouts: CodingLayoutResource[] = [
      { layoutId: 'l-default', name: 'Default', isDefault: true, sections: [] },
      {
        layoutId: 'l-first',
        name: 'First Pass Review',
        isDefault: false,
        sections: [
          {
            sectionId: 's1',
            title: 'Coding',
            fields: [
              { fieldId: 1000, isRequired: true, isReadOnly: false, visibleWhen: null },
              {
                fieldId: 1001,
                isRequired: false,
                isReadOnly: true,
                visibleWhen: { fieldId: 1000, choiceIds: [1], booleanValue: null },
              },
              { fieldId: 4242, isRequired: false, isReadOnly: false, visibleWhen: null },
            ],
          },
        ],
      },
    ];
    const [all, first] = layoutsOf(layouts, catalog);
    expect(all.id).toBe('l-default');
    expect(all.serverId).toBeNull();
    expect(all.sections[0].fields.map((f) => f.queryName)).toEqual([
      'responsiveness',
      'issues',
      'notes',
      'privilege',
    ]);
    expect(first.serverId).toBe('l-first');
    expect(first.sections[0].fields).toEqual([
      expect.objectContaining({ queryName: 'responsiveness', required: true, visibleWhen: null }),
      expect.objectContaining({
        queryName: 'issues',
        readOnly: true,
        multiValue: true,
        choices: [
          { name: 'Pricing', active: true },
          { name: 'Termination', active: false },
        ],
        visibleWhen: { queryName: 'responsiveness', choices: ['Responsive'], value: null },
      }),
    ]);
    expect(layoutsOf([], catalog).map((l) => l.id)).toEqual([ALL_FIELDS_LAYOUT]);
  });

  it('turns a 412 into a conflict with the current coding, and validation and permission problems into messages', () => {
    const current = {
      documentId: 'd1',
      documentVersion: 6,
      indexingState: 'indexed',
      lastEditor: null,
      fields: [{ fieldId: 1000, value: 1, editable: true, isSecurityAffecting: false }],
    };
    const conflict = codingSaveError(
      new ApiError(412, { code: 'version-conflict', current }),
      catalog,
    ) as CodingConflictError;
    expect(conflict).toBeInstanceOf(CodingConflictError);
    expect(conflict.current.version).toBe('6');
    expect(conflict.current.values).toEqual({ responsiveness: 'Responsive' });

    const invalid = codingSaveError(
      new ApiError(400, {
        code: 'validation',
        detail: 'The coding was not saved.',
        errors: { f1000: ['Responsiveness is required.'] },
        fieldErrors: [{ field: 'f1001', code: 'inactive-choice', message: 'Inactive choice.' }],
      }),
      catalog,
    ) as CodingRejectedError;
    expect(invalid).toBeInstanceOf(CodingRejectedError);
    expect(invalid.fieldErrors).toEqual({
      responsiveness: 'Responsiveness is required.',
      issues: 'Inactive choice.',
    });
    const forbidden = codingSaveError(new ApiError(403, {}), catalog) as CodingRejectedError;
    expect(forbidden.status).toBe(403);
    const other = new ApiError(500, {});
    expect(codingSaveError(other, catalog)).toBe(other);
  });

  it('saves with If-Match, the layout and one Idempotency-Key, retried once with the same key', async () => {
    let attempts = 0;
    const saved = {
      documentId: 'doc-1',
      documentVersion: 8,
      indexingState: 'pending',
      lastEditor: null,
      fields: [{ fieldId: 1000, value: 1, editable: true }],
    };
    const api = new FakeApi()
      .on('GET', '/api/v1/workspaces/ws-1/fields', { body: { items: FIELDS } })
      .on('PUT', '/api/v1/workspaces/ws-1/documents/doc-1/coding', () =>
        ++attempts === 1
          ? { status: 503, body: { title: 'Unavailable', status: 503 } }
          : { body: saved, headers: { ETag: '"8"' } },
      );
    TestBed.configureTestingModule({
      providers: [
        provideHttpClient(withInterceptors([problemDetailsInterceptor])),
        provideFakeApi(api),
        {
          provide: WorkspaceContext,
          useValue: {
            workspaceId: 'ws-1',
            apiUrl: (...s: string[]) => ['/api/v1/workspaces/ws-1', ...s].join('/'),
          },
        },
        HttpCodingApi,
      ],
    });
    const coding = await TestBed.inject(HttpCodingApi).save(
      'doc-1',
      '7',
      { responsiveness: 'Responsive' },
      { layoutId: 'l-first', idempotencyKey: 'key-1' },
    );
    expect(coding).toEqual(expect.objectContaining({ version: '8', indexState: 'pending' }));
    const puts = api.requests.filter((r) => r.method === 'PUT');
    expect(puts).toHaveLength(2);
    for (const put of puts) {
      expect(put.headers.get('If-Match')).toBe('"7"');
      expect(put.headers.get('Idempotency-Key')).toBe('key-1');
      expect(put.body).toEqual({
        changes: [{ fieldId: '1000', operation: 'set', value: 1 }],
        layoutId: 'l-first',
      });
    }
  });

  it('asks for confirmation before a save that ends the reviewer’s access, then sends it confirmed (E16-T08)', async () => {
    const catalog = catalogOf(FIELDS);
    const loss = codingSaveError(
      new ApiError(409, { code: 'confirmation-required', reason: 'removes-own-access' }),
      catalog,
    );
    expect(loss).toBeInstanceOf(CodingAccessLossError);
    // Another 409 (not about access) stays what it was.
    const other = new ApiError(409, { code: 'conflict' });
    expect(codingSaveError(other, catalog)).toBe(other);

    const api = new FakeApi()
      .on('GET', '/api/v1/workspaces/ws-1/fields', { body: { items: FIELDS } })
      .on('PUT', '/api/v1/workspaces/ws-1/documents/doc-1/coding', (req) =>
        (req.body as { confirmAccessLoss?: boolean }).confirmAccessLoss
          ? {
              body: {
                documentId: 'doc-1',
                documentVersion: 8,
                indexingState: 'pending',
                lastEditor: null,
                fields: [],
                accessRetained: false,
              },
              headers: { ETag: '"8"' },
            }
          : {
              status: 409,
              body: { status: 409, code: 'confirmation-required', reason: 'removes-own-access' },
            },
      );
    configure(api);
    const http = TestBed.inject(HttpCodingApi);
    const options = { layoutId: null, idempotencyKey: 'key-1' };
    await expect(
      http.save('doc-1', '7', { privilege: 'Withhold' }, options),
    ).rejects.toBeInstanceOf(CodingAccessLossError);
    const saved = await http.save(
      'doc-1',
      '7',
      { privilege: 'Withhold' },
      { ...options, idempotencyKey: 'key-2', confirmAccessLoss: true },
    );
    expect(saved.accessLost).toBe(true);
    expect(saved.values).toEqual({});
    const bodies = api.requests.filter((r) => r.method === 'PUT').map((r) => r.body);
    expect(bodies[0]).not.toHaveProperty('confirmAccessLoss');
    expect(bodies[1]).toEqual(expect.objectContaining({ confirmAccessLoss: true }));
  });

  it('turns the document 404 of every content and coding route into "not available" (E16-T08)', async () => {
    const api = new FakeApi().on('GET', '/api/v1/workspaces/ws-1/fields', {
      body: { items: FIELDS },
    });
    configure(api, [HttpDocumentContentApi]);
    const access = TestBed.inject(DocumentAccess);
    const content = TestBed.inject(HttpDocumentContentApi);
    const coding = TestBed.inject(HttpCodingApi);
    for (const [id, call] of [
      ['doc-1', () => content.document('doc-1', 'display')],
      ['doc-2', () => content.textChunk('doc-2', 0, 'display')],
      ['doc-3', () => content.pages('doc-3', 'prefetch')],
      ['doc-4', () => content.pageImage('doc-4', 1, 'image', 'display')],
      ['doc-5', () => coding.get('doc-5')],
      [
        'doc-6',
        () => coding.save('doc-6', '1', { notes: 'x' }, { layoutId: null, idempotencyKey: 'k' }),
      ],
    ] as const) {
      const error = await (call as () => Promise<unknown>)().catch((e: unknown) => e);
      expect(error, id).toBeInstanceOf(DocumentUnavailableError);
      expect((error as DocumentUnavailableError).documentId).toBe(id);
      expect(access.isUnavailable(id)).toBe(true);
    }
    // Other failures are not about access.
    api.on('GET', '/api/v1/workspaces/ws-1/documents/doc-7', {
      status: 500,
      body: { title: 'Boom', status: 500 },
    });
    await expect(content.document('doc-7', 'display')).rejects.toBeInstanceOf(ApiError);
    expect(access.isUnavailable('doc-7')).toBe(false);
  });
});

function configure(api: FakeApi, extra: unknown[] = []): void {
  TestBed.configureTestingModule({
    providers: [
      provideHttpClient(withInterceptors([problemDetailsInterceptor])),
      provideFakeApi(api),
      {
        provide: WorkspaceContext,
        useValue: {
          workspaceId: 'ws-1',
          apiUrl: (...s: string[]) => ['/api/v1/workspaces/ws-1', ...s].join('/'),
        },
      },
      DocumentAccess,
      HttpCodingApi,
      ...(extra as never[]),
    ],
  });
}
