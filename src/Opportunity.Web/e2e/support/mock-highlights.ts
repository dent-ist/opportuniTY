import type { Route } from '@playwright/test';
import { documentText } from './mock-content';

interface Term {
  termId: string;
  expression: string;
  color: string | null;
}

interface HighlightSet {
  highlightSetId: string;
  name: string;
  description: string | null;
  color: string;
  terms: Term[];
  modifiedBy: { userId: string; displayName: string };
  modifiedAt: string;
  version: number;
}

const COLORS = ['amber', 'green', 'blue', 'violet', 'rose', 'teal'];

/**
 * Highlight Sets and term hits (E16-T12) as the API serves them: `…/highlight-sets` CRUD with If-Match, the
 * reviewer's `…/highlight-set-selection` and `…/documents/{id}/text/hits`. Hits are computed over the mock text like
 * the server does: each quoted phrase or bare word of the last search, and each set term, as whole-word,
 * case-insensitive spans (a phrase is one span).
 */
export class HighlightsMock {
  readonly sets: HighlightSet[] = [
    {
      highlightSetId: 'hs-1',
      name: 'Key terms',
      description: null,
      color: 'amber',
      terms: [
        { termId: 't-1', expression: 'agreement', color: null },
        { termId: 't-2', expression: 'notice', color: 'violet' },
      ],
      modifiedBy: { userId: 'user-1', displayName: 'Alex Admin' },
      modifiedAt: '2026-10-01T09:00:00Z',
      version: 1,
    },
  ];
  selection = { disabledSetIds: [] as string[], searchHits: true };
  /** Every `…/text/hits` request, as `documentId fromChunk searchId sets`. */
  readonly requests: string[] = [];

  constructor(private readonly lastQuery: () => string) {}

  handle(route: Route, method: string, path: string, url: URL): Promise<void> | undefined {
    const hits = /^\/api\/v1\/workspaces\/[^/]+\/documents\/doc-(\d+)\/text\/hits$/.exec(path);
    if (hits && method === 'GET') return this.hits(route, Number(hits[1]), url);
    if (/\/highlight-set-selection$/.test(path)) {
      if (method === 'PUT') this.selection = route.request().postDataJSON();
      return route.fulfill({ json: this.selection });
    }
    const set = /^\/api\/v1\/workspaces\/[^/]+\/highlight-sets(?:\/([^/]+))?$/.exec(path);
    if (!set) return undefined;
    const id = set[1];
    if (!id && method === 'GET') {
      return route.fulfill({
        json: {
          items: [...this.sets].sort((a, b) => a.name.localeCompare(b.name)),
          colors: COLORS,
        },
      });
    }
    if (!id && method === 'POST') {
      const body = route.request().postDataJSON();
      const created = this.save(`hs-${this.sets.length + 1}`, body, 1);
      this.sets.push(created);
      return route.fulfill({ status: 201, json: created });
    }
    const index = this.sets.findIndex((s) => s.highlightSetId === id);
    if (index < 0) return route.fulfill(notFound());
    if (method === 'GET') return route.fulfill({ json: this.sets[index] });
    if (method === 'PUT') {
      const updated = this.save(id!, route.request().postDataJSON(), this.sets[index].version + 1);
      this.sets[index] = updated;
      return route.fulfill({ json: updated });
    }
    if (method === 'DELETE') {
      this.sets.splice(index, 1);
      return route.fulfill({ status: 204 });
    }
    return undefined;
  }

  private save(
    id: string,
    body: { name: string; color: string; terms: { expression: string; color: string | null }[] },
    version: number,
  ): HighlightSet {
    return {
      highlightSetId: id,
      name: body.name,
      description: null,
      color: body.color,
      terms: body.terms.map((t, i) => ({
        termId: `${id}-t${i}`,
        expression: t.expression,
        color: t.color,
      })),
      modifiedBy: { userId: 'user-1', displayName: 'Alex Admin' },
      modifiedAt: '2026-10-05T09:00:00Z',
      version,
    };
  }

  private hits(route: Route, n: number, url: URL): Promise<void> {
    const searchId = url.searchParams.get('searchId');
    const setIds = url.searchParams.getAll('highlightSetId');
    this.requests.push(
      `doc-${n} ${url.searchParams.get('fromChunk')} ${searchId ?? '-'} ${setIds.join(',')}`,
    );
    const units: {
      label: string;
      source: string;
      highlightSetId: string | null;
      termId: string | null;
      color: string | null;
    }[] = [];
    if (searchId) {
      for (const term of searchTerms(this.lastQuery())) {
        units.push({
          label: term,
          source: 'search',
          highlightSetId: null,
          termId: null,
          color: null,
        });
      }
    }
    for (const setId of setIds) {
      const set = this.sets.find((s) => s.highlightSetId === setId);
      if (!set) return route.fulfill(notFound());
      for (const term of set.terms) {
        units.push({
          label: term.expression.replaceAll('"', ''),
          source: 'highlightSet',
          highlightSetId: set.highlightSetId,
          termId: term.termId,
          color: term.color ?? set.color,
        });
      }
    }
    const text = documentText(n);
    const found = units.flatMap((u, unit) =>
      [...text.matchAll(new RegExp(`\\b${escape(u.label)}\\b`, 'gi'))].map((m) => ({
        unit,
        chunk: 0,
        start: m.index!,
        end: m.index! + m[0].length,
      })),
    );
    found.sort((a, b) => a.start - b.start || a.unit - b.unit);
    return route.fulfill({
      json: {
        documentId: `doc-${n}`,
        chunkCount: 1,
        fromChunk: 0,
        toChunk: 1,
        nextChunk: null,
        truncated: false,
        missing: false,
        units: units.map((u, unit) => ({
          unit,
          ...u,
          kind: u.label.includes(' ') ? 'phrase' : 'term',
          count: found.filter((h) => h.unit === unit).length,
        })),
        hits: found,
      },
    });
  }
}

/** The quoted phrases and bare words of a query, without operators and fielded clauses. */
function searchTerms(query: string): string[] {
  const terms: string[] = [];
  for (const m of query.matchAll(/"([^"]+)"|(\S+)/g)) {
    const term = m[1] ?? m[2];
    if (!term || /^(AND|OR|NOT)$/.test(term) || term.includes(':') || /^w\/\d+$/i.test(term))
      continue;
    terms.push(term.replace(/[()]/g, ''));
  }
  return [...new Set(terms.filter(Boolean))];
}

function escape(text: string): string {
  return text.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
}

function notFound() {
  return {
    status: 404,
    contentType: 'application/problem+json',
    body: JSON.stringify({ type: 'about:blank', title: 'Not found', status: 404 }),
  };
}
