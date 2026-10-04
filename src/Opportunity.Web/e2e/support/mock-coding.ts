import type { Route } from '@playwright/test';

// The mock API's coding "server" (E10-T01 coding API, E04-T03 coding layouts) for the coding pane (E16-T05): one
// coding field of every type, the familiar default template's fields (familiarity guide §3.4), three layouts, and a
// per-document coding store with DocumentVersion, If-Match / 412 conflicts, Idempotency-Key replays and an indexing
// delay, so "Saved · indexing" turns searchable on its own.

const CAPABILITIES = {
  sortable: false,
  filterable: true,
  rangeable: false,
  aggregatable: true,
  fullText: false,
  wildcard: false,
  leadingWildcard: false,
  highlightable: false,
  exists: true,
};

type FieldType =
  | 'text'
  | 'keyword'
  | 'integer'
  | 'decimal'
  | 'date'
  | 'boolean'
  | 'singleChoice'
  | 'multiChoice'
  | 'user';

function field(
  fieldId: number,
  displayName: string,
  type: FieldType,
  options: {
    choices?: readonly (readonly [number, string])[];
    security?: boolean;
    multi?: boolean;
    dateTime?: boolean;
  } = {},
) {
  return {
    fieldId,
    displayName,
    queryName: displayName
      .toLowerCase()
      .replace(/[^a-z0-9]+/g, '_')
      .replace(/_$/, ''),
    type,
    storage: 'coding',
    multiValue: options.multi ?? type === 'multiChoice',
    isSystem: false,
    isHidden: false,
    isSecurityAffecting: options.security ?? false,
    datePrecision: type === 'date' ? (options.dateTime ? 'dateTime' : 'date') : null,
    capabilities: CAPABILITIES,
    reducedCapabilities: false,
    choices: options.choices
      ? options.choices.map(([choiceId, name]) => ({ choiceId, name, isActive: true }))
      : null,
  };
}

const TOPICS = [
  'Intellectual Property',
  'Exclusivity',
  'Payment Terms',
  'Delivery Delays',
  'Quality Complaints',
  'Warranty',
  'Indemnity',
  'Insurance',
  'Non-Compete',
  'Confidentiality Breach',
  'Data Protection',
  'Export Controls',
  'Regulatory Filing',
  'Board Approval',
  'Audit Rights',
  'Force Majeure',
  'Pricing Committee',
  'Supply Chain',
] as const;

/**
 * Coding fields of the mock workspace: the default template (guide §3.4) plus one field of every other type. Ids
 * 1000–1002 and their choice ids are shared with the Mass Edit tests (E16-T06).
 */
export const CODING_FIELDS = [
  field(1000, 'Responsiveness', 'singleChoice', {
    choices: [
      [1, 'Responsive'],
      [2, 'Not Responsive'],
      [3, 'Needs Further Review'],
    ],
  }),
  field(1001, 'Issues', 'multiChoice', {
    choices: [
      [11, 'Pricing'],
      [12, 'Termination'],
      [13, 'Supply'],
    ],
  }),
  field(1002, 'Privilege', 'singleChoice', {
    security: true,
    choices: [
      [21, 'Not Privileged'],
      [22, 'Withhold'],
      [23, 'Redact'],
      [24, 'Needs 2L Review'],
    ],
  }),
  field(1003, 'Privilege Basis', 'multiChoice', {
    choices: [
      [31, 'Attorney-Client'],
      [32, 'Work Product'],
      [33, 'Common Interest'],
      [34, 'Other'],
    ],
  }),
  field(1004, 'Privilege Description', 'text'),
  field(1005, 'Confidentiality Designation', 'singleChoice', {
    security: true,
    choices: [
      [41, 'None'],
      [42, 'CONFIDENTIAL'],
      [43, 'HIGHLY CONFIDENTIAL – AEO'],
    ],
  }),
  field(1006, 'Key Document', 'boolean'),
  field(1007, 'Reviewer Comments', 'text'),
  field(1008, 'Review Date', 'date'),
  field(1009, 'Follow-up At', 'date', { dateTime: true }),
  field(1010, 'Pages Reviewed', 'integer'),
  field(1011, 'Hours Spent', 'decimal'),
  field(1012, 'Second-Level Reviewer', 'user'),
  field(1013, 'Key Terms', 'keyword', { multi: true }),
  // More than 15 choices: a filterable list in the coding pane.
  field(1014, 'Topics', 'multiChoice', {
    choices: TOPICS.map((name, i) => [51 + i, name] as const),
  }),
];

const layoutField = (
  fieldId: number,
  rules: { required?: boolean; readOnly?: boolean; when?: [number, number[]] } = {},
) => ({
  fieldId,
  isRequired: rules.required ?? false,
  isReadOnly: rules.readOnly ?? false,
  visibleWhen: rules.when
    ? { fieldId: rules.when[0], choiceIds: rules.when[1], booleanValue: null }
    : null,
});

/** `GET …/coding-layouts` items (default first). */
export const CODING_LAYOUTS = [
  {
    layoutId: 'layout-first-pass',
    name: 'First Pass Review',
    isDefault: true,
    sections: [
      {
        sectionId: 's-resp',
        title: 'Responsiveness',
        fields: [layoutField(1000, { required: true })],
      },
      {
        sectionId: 's-priv',
        title: 'Privilege',
        fields: [
          layoutField(1002),
          layoutField(1003, { when: [1002, [22, 23]] }),
          layoutField(1005),
        ],
      },
      { sectionId: 's-issues', title: 'Issues', fields: [layoutField(1001), layoutField(1006)] },
      { sectionId: 's-notes', title: 'Notes', fields: [layoutField(1007)] },
    ],
  },
  {
    layoutId: 'layout-privilege',
    name: 'Privilege Review',
    isDefault: false,
    sections: [
      {
        sectionId: 's-priv',
        title: 'Privilege',
        fields: [
          layoutField(1002, { required: true }),
          layoutField(1003, { required: true, when: [1002, [22, 23]] }),
          layoutField(1004, { when: [1002, [22, 23]] }),
          layoutField(1005),
        ],
      },
      { sectionId: 's-resp', title: 'Context', fields: [layoutField(1000, { readOnly: true })] },
    ],
  },
  {
    layoutId: 'layout-details',
    name: 'Review Details',
    isDefault: false,
    sections: [
      {
        sectionId: 's-details',
        title: 'Details',
        fields: [1008, 1009, 1010, 1011, 1012, 1013, 1014].map((id) => layoutField(id)),
      },
    ],
  },
];

type Value = unknown;

interface StoredCoding {
  version: number;
  values: Map<number, Value>;
  changedAt: Map<number, string>;
  editor: { userId: string; displayName: string; changedAt: string } | null;
  indexedAt: number;
}

/** One `PUT …/coding` the mock received. */
export interface CodingSave {
  documentId: string;
  ifMatch: string | null;
  idempotencyKey: string | null;
  changes: { fieldId: string | number; operation: string; value: unknown }[];
  layoutId: string | null;
}

export interface CodingMockOptions {
  /** Coding.WritePrivilege is among the user's permissions (security-affecting fields editable). */
  writePrivilege: boolean;
  /** Coding.Write is among the user's permissions. */
  write: boolean;
  /** How long after a save the document's coding becomes searchable. */
  indexDelayMs: number;
  /** Delay of `PUT …/coding`, to measure the acknowledgement. */
  saveDelayMs: number;
}

/** The coding store of the mock and the scenario switches tests use. */
export class CodingMock {
  readonly saves: CodingSave[] = [];
  private readonly store = new Map<number, StoredCoding>();
  private readonly replays = new Map<string, unknown>();

  constructor(private readonly options: CodingMockOptions) {}

  /** Someone else codes document `n` (a conflict for a reviewer who read it before). */
  codeAsOtherUser(n: number, fieldId: number, value: Value, displayName = 'J. Smith'): void {
    const doc = this.doc(n);
    const at = new Date().toISOString();
    doc.version++;
    doc.values.set(fieldId, value);
    doc.changedAt.set(fieldId, at);
    doc.editor = { userId: 'user-2', displayName, changedAt: at };
    doc.indexedAt = Date.now() + this.options.indexDelayMs;
  }

  /** Answers coding and layout routes; undefined for anything else. */
  handle(route: Route, method: string, path: string): Promise<void> | undefined {
    if (method === 'GET' && /^\/api\/v1\/workspaces\/[^/]+\/coding-layouts$/.test(path)) {
      return route.fulfill({
        json: {
          items: CODING_LAYOUTS,
          nextCursor: null,
          total: { value: CODING_LAYOUTS.length, relation: 'eq' },
        },
      });
    }
    const match = /^\/api\/v1\/workspaces\/[^/]+\/documents\/doc-(\d+)\/coding$/.exec(path);
    if (!match) return undefined;
    const n = Number(match[1]);
    if (method === 'GET') return this.respond(route, 200, n);
    if (method === 'PUT') return this.save(route, n);
    return undefined;
  }

  private async save(route: Route, n: number): Promise<void> {
    const request = route.request();
    const ifMatch = (await request.headerValue('if-match')) ?? null;
    const key = (await request.headerValue('idempotency-key')) ?? null;
    const body = request.postDataJSON() as Pick<CodingSave, 'changes' | 'layoutId'>;
    this.saves.push({
      documentId: `doc-${n}`,
      ifMatch,
      idempotencyKey: key,
      changes: body.changes,
      layoutId: body.layoutId ?? null,
    });
    if (this.options.saveDelayMs > 0) {
      await new Promise((r) => setTimeout(r, this.options.saveDelayMs));
    }
    if (!this.options.write) return problem(route, 403, 'forbidden', 'Forbidden');
    if (!ifMatch) return problem(route, 428, 'precondition-required', 'If-Match required');
    if (key && this.replays.has(key)) {
      return route.fulfill({
        json: this.replays.get(key),
        headers: { 'Idempotent-Replayed': 'true' },
      });
    }
    const doc = this.doc(n);
    if (ifMatch !== '*' && ifMatch.replace(/"/g, '') !== String(doc.version)) {
      return route.fulfill({
        status: 412,
        contentType: 'application/problem+json',
        headers: { ETag: `"${doc.version}"` },
        body: JSON.stringify({
          type: 'urn:opportunity:problem:version-conflict',
          title: 'Precondition Failed',
          status: 412,
          code: 'version-conflict',
          detail: "The document's coding changed since you read it.",
          currentVersion: doc.version,
          lastEditor: this.resource(n).lastEditor,
          current: this.resource(n),
        }),
      });
    }
    const at = new Date().toISOString();
    doc.version++;
    for (const change of body.changes) {
      const id = Number(change.fieldId);
      const def = CODING_FIELDS.find((f) => f.fieldId === id);
      // The adapter sends choice ids as strings; the store keeps numbers like the API.
      const value = def?.choices
        ? Array.isArray(change.value)
          ? change.value.map(Number)
          : change.value === null
            ? null
            : Number(change.value)
        : change.value;
      doc.values.set(id, value);
      doc.changedAt.set(id, at);
    }
    doc.editor = { userId: 'user-1', displayName: 'Alex Reviewer', changedAt: at };
    doc.indexedAt = Date.now() + this.options.indexDelayMs;
    const resource = this.resource(n);
    if (key) this.replays.set(key, resource);
    return route.fulfill({ json: resource, headers: { ETag: `"${doc.version}"` } });
  }

  private respond(route: Route, status: number, n: number): Promise<void> {
    return route.fulfill({
      status,
      json: this.resource(n),
      headers: { ETag: `"${this.doc(n).version}"` },
    });
  }

  private doc(n: number): StoredCoding {
    let doc = this.store.get(n);
    if (!doc) {
      // Responsiveness = Responsive on every third document, coded at version 3.
      const values = new Map<number, Value>(n % 3 === 0 ? [[1000, 1]] : []);
      doc = { version: 3, values, changedAt: new Map(), editor: null, indexedAt: 0 };
      this.store.set(n, doc);
    }
    return doc;
  }

  private resource(n: number) {
    const doc = this.doc(n);
    const indexed = Date.now() >= doc.indexedAt;
    return {
      documentId: `doc-${n}`,
      documentVersion: doc.version,
      projectedVersion: indexed ? doc.version : doc.version - 1,
      indexingState: indexed ? 'indexed' : 'pending',
      layoutId: null,
      lastEditor: doc.editor
        ? {
            userId: doc.editor.userId,
            displayName: doc.editor.displayName,
            changedAt: doc.editor.changedAt,
            documentVersion: doc.version,
            jobId: null,
          }
        : null,
      fields: CODING_FIELDS.map((f) => ({
        fieldId: f.fieldId,
        value: doc.values.get(f.fieldId) ?? null,
        editable: this.options.write && (!f.isSecurityAffecting || this.options.writePrivilege),
        isSecurityAffecting: f.isSecurityAffecting,
        changedAtVersion: null,
        changedBy: null,
        changedAt: doc.changedAt.get(f.fieldId) ?? null,
      })),
    };
  }
}

function problem(route: Route, status: number, code: string, title: string): Promise<void> {
  return route.fulfill({
    status,
    contentType: 'application/problem+json',
    body: JSON.stringify({ type: `urn:opportunity:problem:${code}`, title, status, code }),
  });
}
