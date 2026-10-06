import type { Route } from '@playwright/test';
import { CODING_LAYOUTS } from './mock-coding';

// Field and coding layout administration (E04-T06) as the API serves it: `…/fields` (list, create), `…/fields/{id}`
// (read with hasValues, replace, retire), choices and choice order, `…/field-capacity`, and `…/coding-layouts`
// (list, create, read, replace, delete), with versions checked against If-Match. It owns the workspace's field list
// and layouts, so admin changes show up wherever the app lists them.

type Json = Record<string, unknown>;

interface MockChoice {
  choiceId: number;
  name: string;
  isActive: boolean;
}

interface MockField extends Json {
  fieldId: number;
  displayName: string;
  queryName: string;
  type: string;
  storage: string;
  isSystem: boolean;
  isHidden: boolean;
  isSecurityAffecting: boolean;
  datePrecision: string | null;
  multiValue: boolean;
  choices: MockChoice[] | null;
}

interface MockLayout {
  layoutId: string;
  name: string;
  isDefault: boolean;
  roles: string[];
  sections: { sectionId: string; title: string; fields: Json[] }[];
  version: number;
}

/** Fields that documents already hold values for (their type is locked) and choices already used. */
const WITH_VALUES = new Set([1000, 1001, 1002, 1005]);
const USED_CHOICES = new Set([1, 2, 11, 12, 21, 22, 41, 42]);

function capabilities(type: string, storage: string, overflow = false) {
  const choice = type === 'singleChoice' || type === 'multiChoice' || type === 'user';
  const numeric = type === 'integer' || type === 'decimal' || type === 'date';
  return {
    sortable: !overflow && !choice,
    filterable: true,
    rangeable: !overflow && numeric,
    aggregatable: !overflow && type !== 'text',
    fullText: !overflow && type === 'text',
    wildcard: type === 'text' || type === 'keyword',
    leadingWildcard: storage === 'column' && type === 'text',
    highlightable: type === 'text',
    exists: true,
  };
}

function limitations(c: ReturnType<typeof capabilities>, overflow: boolean): string[] {
  const out: string[] = [];
  if (overflow)
    out.push(
      'This workspace has used all search slots for this type, so the field gets reduced search: exact and prefix matches only.',
    );
  if (!c.sortable) out.push('This field will not be sortable.');
  if (!c.aggregatable) out.push('Value counts (facets) will not be available for this field.');
  return out;
}

const TYPES = [
  'text',
  'keyword',
  'integer',
  'decimal',
  'date',
  'boolean',
  'singleChoice',
  'multiChoice',
  'user',
];

export class FieldAdminMock {
  readonly fields: MockField[];
  readonly layouts: MockLayout[];
  readonly versions = new Map<number, number>();
  /** Every change request, as `METHOD path`. */
  readonly writes: string[] = [];
  private nextField = 2000;
  private nextChoice = 500;
  private nextLayout = 1;

  constructor(fields: readonly Json[]) {
    this.fields = structuredClone(fields) as MockField[];
    this.layouts = CODING_LAYOUTS.map((l) => ({
      ...structuredClone(l),
      roles: l.isDefault ? [] : ['Reviewer'],
      version: 1,
    })) as MockLayout[];
  }

  /** Answers admin routes, and the field and layout lists; undefined for anything else. */
  handle(route: Route, method: string, path: string): Promise<void> | undefined {
    const ws = /^\/api\/v1\/workspaces\/[^/]+/.exec(path)?.[0];
    if (!ws) return undefined;
    const rest = path.slice(ws.length);
    const ifMatch = route.request().headers()['if-match'] ?? null;
    if (rest === '/fields' && method === 'GET') return this.page(route, this.fields);
    if (rest === '/field-capacity' && method === 'GET') {
      const items = ['coding', 'metadata'].flatMap((storage) =>
        TYPES.map((type) => {
          const c = capabilities(type, storage);
          return {
            storage,
            type,
            textAnalysis: type === 'text' ? 'prose' : null,
            budget: 20,
            available: 20,
            capabilities: c,
            limitations: limitations(c, false),
          };
        }),
      );
      return route.fulfill({ json: { items } });
    }
    if (rest === '/fields' && method === 'POST') return this.createField(route, path);
    const field = /^\/fields\/(\d+)(\/choices(?:\/(\d+))?|\/choice-order)?$/.exec(rest);
    if (field)
      return this.fieldRoute(route, method, path, Number(field[1]), field[2], field[3], ifMatch);

    if (rest === '/coding-layouts' && method === 'GET') {
      const items = [...this.layouts]
        .sort((a, b) => Number(b.isDefault) - Number(a.isDefault) || a.name.localeCompare(b.name))
        .map(({ layoutId, name, isDefault, sections }) => ({
          layoutId,
          name,
          isDefault,
          sections,
        }));
      return this.page(route, items);
    }
    if (rest === '/coding-layouts' && method === 'POST') {
      this.writes.push(`POST ${path}`);
      const body = route.request().postDataJSON() as Json;
      const layout = this.toLayout(`layout-new-${this.nextLayout++}`, body, 1);
      if (layout.isDefault) this.layouts.forEach((l) => (l.isDefault = false));
      this.layouts.push(layout);
      return route.fulfill({ status: 201, json: layout, headers: etag(1) });
    }
    const layoutMatch = /^\/coding-layouts\/([^/]+)$/.exec(rest);
    if (layoutMatch) {
      const index = this.layouts.findIndex((l) => l.layoutId === layoutMatch[1]);
      if (index < 0) return route.fulfill(problem(404, 'not-found', 'No such coding layout.'));
      const layout = this.layouts[index];
      if (method === 'GET') return route.fulfill({ json: layout, headers: etag(layout.version) });
      if (!ifMatch) return route.fulfill(problem(428, 'precondition-required', 'Send If-Match.'));
      if (ifMatch !== '*' && ifMatch !== `"${layout.version}"`)
        return route.fulfill(problem(412, 'version-conflict', 'The layout was modified.'));
      this.writes.push(`${method} ${path}`);
      if (method === 'DELETE') {
        if (layout.isDefault)
          return route.fulfill(problem(409, 'conflict', 'The default layout cannot be deleted.'));
        this.layouts.splice(index, 1);
        return route.fulfill({ status: 204 });
      }
      if (method === 'PUT') {
        const updated = this.toLayout(
          layout.layoutId,
          route.request().postDataJSON() as Json,
          layout.version + 1,
        );
        if (updated.isDefault)
          this.layouts.forEach(
            (l) => l.isDefault && l !== layout && ((l.isDefault = false), l.version++),
          );
        this.layouts[index] = updated;
        return route.fulfill({ json: updated, headers: etag(updated.version) });
      }
    }
    return undefined;
  }

  private page(route: Route, items: readonly unknown[]): Promise<void> {
    return route.fulfill({
      json: { items, nextCursor: null, total: { value: items.length, relation: 'eq' } },
    });
  }

  private createField(route: Route, path: string): Promise<void> {
    this.writes.push(`POST ${path}`);
    const body = route.request().postDataJSON() as Json;
    const name = String(body['displayName'] ?? '').trim();
    if (!name)
      return route.fulfill(
        problem(400, 'validation', 'Invalid field.', { displayName: ['Enter a name.'] }),
      );
    if (this.fields.some((f) => f.displayName.toLowerCase() === name.toLowerCase()))
      return route.fulfill(
        problem(400, 'validation', 'Invalid field.', {
          name: [`A field named '${name}' already exists in this workspace.`],
        }),
      );
    const type = String(body['type']);
    const field: MockField = {
      fieldId: this.nextField++,
      displayName: name,
      queryName: name.toLowerCase().replace(/[^a-z0-9]+/g, '_'),
      type,
      storage: String(body['storage'] ?? 'coding'),
      multiValue: type === 'multiChoice' || !!body['multiValue'],
      isSystem: false,
      isHidden: false,
      isSecurityAffecting: !!body['securityClass'],
      datePrecision: type === 'date' ? String(body['datePrecision'] ?? 'dateTime') : null,
      capabilities: capabilities(type, String(body['storage'] ?? 'coding')),
      reducedCapabilities: false,
      choices: type === 'singleChoice' || type === 'multiChoice' ? [] : null,
      description: (body['description'] as string | null) ?? null,
      securityClass: (body['securityClass'] as string | null) ?? null,
    };
    this.fields.push(field);
    return route.fulfill({ status: 201, json: this.resource(field), headers: etag(1) });
  }

  private fieldRoute(
    route: Route,
    method: string,
    path: string,
    id: number,
    sub: string | undefined,
    choiceId: string | undefined,
    ifMatch: string | null,
  ): Promise<void> | undefined {
    const field = this.fields.find((f) => f.fieldId === id);
    if (!field) return route.fulfill(problem(404, 'not-found', 'No such field.'));
    const version = this.versions.get(id) ?? 1;
    if (method === 'GET' && !sub)
      return route.fulfill({ json: this.resource(field), headers: etag(version) });
    if (!ifMatch) return route.fulfill(problem(428, 'precondition-required', 'Send If-Match.'));
    if (ifMatch !== '*' && ifMatch !== `"${version}"`)
      return route.fulfill(problem(412, 'version-conflict', 'The field was modified.'));
    this.writes.push(`${method} ${path}`);
    const body = (route.request().postDataJSON() ?? {}) as Json;
    const choices = field.choices ?? [];
    if (!sub && method === 'DELETE') {
      if (field.isSystem)
        return route.fulfill(
          problem(409, 'conflict', 'System fields cannot be deleted; hide them instead.'),
        );
      this.fields.splice(this.fields.indexOf(field), 1);
      return route.fulfill({ status: 204 });
    }
    if (!sub && method === 'PUT') {
      if (body['type'] !== field.type && (WITH_VALUES.has(id) || field.isSystem))
        return route.fulfill(
          problem(
            409,
            'conflict',
            'The field holds values; its type cannot change. Create a new field instead (ADR-003 R7).',
          ),
        );
      field.displayName = String(body['displayName']);
      field.isHidden = !!body['isHidden'];
      field.type = String(body['type']);
      field['description'] = body['description'] || null;
    } else if (sub === '/choices' && method === 'POST') {
      const name = String(body['name'] ?? '').trim();
      if (choices.some((c) => c.name.toLowerCase() === name.toLowerCase()))
        return route.fulfill(
          problem(400, 'validation', 'Invalid choice.', {
            name: [`A choice named '${name}' already exists.`],
          }),
        );
      choices.push({ choiceId: this.nextChoice++, name, isActive: true });
    } else if (sub?.startsWith('/choices/') && method === 'PUT') {
      const choice = choices.find((c) => c.choiceId === Number(choiceId));
      if (!choice) return route.fulfill(problem(404, 'not-found', 'No such field or choice.'));
      if (typeof body['name'] === 'string') choice.name = body['name'];
      if (typeof body['isActive'] === 'boolean') choice.isActive = body['isActive'];
    } else if (sub?.startsWith('/choices/') && method === 'DELETE') {
      const index = choices.findIndex((c) => c.choiceId === Number(choiceId));
      if (index < 0) return route.fulfill(problem(404, 'not-found', 'No such field or choice.'));
      if (USED_CHOICES.has(Number(choiceId)))
        return route.fulfill(
          problem(409, 'conflict', 'The choice has been used; deactivate it instead.'),
        );
      choices.splice(index, 1);
    } else if (sub === '/choice-order' && method === 'PUT') {
      const ids = (body['choiceIds'] as number[]).map(Number);
      field.choices = ids.map((cid) => choices.find((c) => c.choiceId === cid)!).filter(Boolean);
    } else {
      return undefined;
    }
    this.versions.set(id, version + 1);
    return route.fulfill({ json: this.resource(field), headers: etag(version + 1) });
  }

  private resource(field: MockField): Json {
    const c = capabilities(field.type, field.storage);
    return {
      ...field,
      description: field['description'] ?? null,
      securityClass:
        field['securityClass'] ?? (field.isSecurityAffecting ? 'privilegeStatus' : null),
      decimalPrecision: field.type === 'decimal' ? 18 : null,
      decimalScale: field.type === 'decimal' ? 2 : null,
      textAnalysis: field.type === 'text' ? 'prose' : null,
      isSearchable: true,
      capabilities: field['capabilities'] ?? c,
      limitations: limitations(c, false),
      hasValues: WITH_VALUES.has(field.fieldId),
      choices:
        field.choices?.map((ch) => ({ ...ch, inUse: USED_CHOICES.has(ch.choiceId) })) ?? null,
      version: this.versions.get(field.fieldId) ?? 1,
    };
  }

  private toLayout(layoutId: string, body: Json, version: number): MockLayout {
    let n = 0;
    return {
      layoutId,
      name: String(body['name'] ?? ''),
      isDefault: !!body['isDefault'],
      roles: (body['roles'] as string[] | null) ?? [],
      sections: (body['sections'] as Json[]).map((s) => ({
        sectionId: (s['sectionId'] as string | null) ?? `${layoutId}-s${++n}`,
        title: String(s['title']),
        fields: s['fields'] as Json[],
      })),
      version,
    };
  }
}

function etag(version: number) {
  return { ETag: `"${version}"` };
}

function problem(status: number, code: string, detail: string, errors?: Record<string, string[]>) {
  return {
    status,
    contentType: 'application/problem+json',
    body: JSON.stringify({ status, title: detail, detail, code, ...(errors ? { errors } : {}) }),
  };
}
