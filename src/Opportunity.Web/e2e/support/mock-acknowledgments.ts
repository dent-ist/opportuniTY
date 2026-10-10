import { createHash } from 'node:crypto';
import type { Route } from '@playwright/test';

const ME = { userId: 'user-1', displayName: 'Alex Reviewer' };
const DANA = { userId: 'user-7', displayName: 'Dana Counsel' };

const TITLE = 'Protective order acknowledgment (Exhibit A)';
const TEXT =
  'I have read the Stipulated Protective Order entered in this matter and agree to be bound by it.\n' +
  'I will use material designated under it only for this litigation and will not disclose it to anyone not authorised by the order.';

interface MockVersion {
  version: number;
  title: string;
  text: string;
  textSha256: string;
  publishedBy: { userId: string; displayName: string };
  publishedAt: string;
}

function sha256(title: string, text: string): string {
  return createHash('sha256').update(`${title}\n\n${text}`, 'utf8').digest('hex');
}

function version(n: number, title: string, text: string, by = DANA): MockVersion {
  return {
    version: n,
    title,
    text,
    textSha256: sha256(title, text),
    publishedBy: by,
    publishedAt: `2026-10-0${Math.min(n + 7, 9)}T08:00:00Z`,
  };
}

const problem = (status: number, code: string, extra: Record<string, unknown> = {}) => ({
  status,
  contentType: 'application/problem+json',
  body: JSON.stringify({
    type: `urn:opportunity:problem:${code}`,
    title: code,
    status,
    code,
    ...extra,
  }),
});

/**
 * Reviewer attestation and protective-order acknowledgment (E20-T03). ws-1 publishes version 1 (by Dana), which the
 * signed-in user accepted unless `pending` is set; ws-2 and new workspaces require nothing. While the user has not
 * accepted the current version, every other route of that workspace answers 403 `acknowledgment-required`, like the API.
 * Accepting and publishing are recorded in `writes`.
 */
export class AcknowledgmentsMock {
  readonly writes: { path: string; body: Record<string, unknown>; ifMatch: string | null }[] = [];
  private readonly versions = new Map<string, MockVersion[]>([['ws-1', [version(1, TITLE, TEXT)]]]);
  /** Accepted versions of the signed-in user per workspace. */
  private readonly accepted = new Map<string, Map<number, string>>();

  constructor(pending: boolean) {
    if (!pending) this.accepted.set('ws-1', new Map([[1, '2026-10-08T09:30:00Z']]));
  }

  private current(ws: string): MockVersion | null {
    return this.versions.get(ws)?.[0] ?? null;
  }

  /** `WorkspaceResource.acknowledgmentPending` for the signed-in user. */
  pending(ws: string): boolean {
    const current = this.current(ws);
    return !!current && !this.accepted.get(ws)?.has(current.version);
  }

  handle(route: Route, method: string, path: string): Promise<void> | undefined {
    const scoped = /^\/api\/v1\/workspaces\/([^/]+)(\/.*)$/.exec(path);
    if (!scoped) return undefined;
    const [, ws, rest] = scoped;
    const current = this.current(ws);
    const accepted = this.accepted.get(ws) ?? new Map<number, string>();
    if (rest === '/acknowledgment' && method === 'GET') {
      return route.fulfill({
        json: {
          required: !!current,
          version: current?.version ?? null,
          title: current?.title ?? null,
          text: current?.text ?? null,
          textSha256: current?.textSha256 ?? null,
          publishedAt: current?.publishedAt ?? null,
          acknowledged: !this.pending(ws),
          acknowledgedAt: current ? (accepted.get(current.version) ?? null) : null,
        },
      });
    }
    if (rest === '/acknowledgment/acceptances' && method === 'POST') {
      const body = route.request().postDataJSON() as Record<string, unknown>;
      this.writes.push({ path, body, ifMatch: null });
      if (!current) return route.fulfill(problem(409, 'conflict'));
      if (body['version'] !== current.version || body['textSha256'] !== current.textSha256)
        return route.fulfill(problem(409, 'acknowledgment-outdated'));
      const at = '2026-10-10T09:00:00Z';
      const created = !accepted.has(current.version);
      accepted.set(current.version, accepted.get(current.version) ?? at);
      this.accepted.set(ws, accepted);
      return route.fulfill({
        status: created ? 201 : 200,
        json: { version: current.version, textSha256: current.textSha256, acceptedAt: at },
      });
    }
    // The gate: nothing else of the workspace before the current version is accepted.
    if (this.pending(ws))
      return route.fulfill(
        problem(403, 'acknowledgment-required', {
          acknowledgmentVersion: current!.version,
          acknowledgmentUrl: `/api/v1/workspaces/${ws}/acknowledgment`,
        }),
      );
    const list = this.versions.get(ws) ?? [];
    const resource = (v: MockVersion, withText: boolean) => ({
      ...v,
      text: withText ? v.text : null,
      acceptedCount: (v.version === 1 ? 2 : 0) + (accepted.has(v.version) ? 1 : 0),
      isCurrent: v.version === current?.version,
    });
    if (rest === '/acknowledgment-versions' && method === 'GET') {
      return route.fulfill({
        headers: { ETag: `"${current?.version ?? 0}"` },
        json: { items: list.map((v) => resource(v, false)), currentVersion: current?.version ?? 0 },
      });
    }
    if (rest === '/acknowledgment-versions' && method === 'POST') {
      const body = route.request().postDataJSON() as Record<string, unknown>;
      const ifMatch = route.request().headers()['if-match'] ?? null;
      this.writes.push({ path, body, ifMatch });
      if (ifMatch !== `"${current?.version ?? 0}"`)
        return route.fulfill(problem(412, 'version-conflict'));
      const title = String(body['title'] ?? '').trim();
      const text = String(body['text'] ?? '')
        .replace(/\r\n?/g, '\n')
        .trim();
      if (!title || !text)
        return route.fulfill(
          problem(400, 'validation', {
            errors: title ? { text: ['text is required.'] } : { title: ['title is required.'] },
          }),
        );
      const published = version((current?.version ?? 0) + 1, title, text, ME);
      this.versions.set(ws, [published, ...list]);
      return route.fulfill({ status: 201, json: resource(published, true) });
    }
    const one = /^\/acknowledgment-versions\/(\d+)$/.exec(rest);
    if (one && method === 'GET') {
      const v = list.find((x) => x.version === Number(one[1]));
      return v
        ? route.fulfill({ json: resource(v, true) })
        : route.fulfill(problem(404, 'not-found'));
    }
    if (rest === '/acknowledgment-roster' && method === 'GET') {
      const v1 = list.find((v) => v.version === 1);
      const entry = (
        userId: string,
        displayName: string,
        email: string | null,
        versions: [number, string][],
      ) => ({
        userId,
        displayName,
        email,
        directMember: true,
        status: versions.some(([n]) => n === current?.version)
          ? 'current'
          : versions.length
            ? 'outdated'
            : 'pending',
        acceptances: versions.map(([n, at]) => ({
          version: n,
          textSha256: list.find((x) => x.version === n)?.textSha256 ?? v1?.textSha256 ?? '',
          acceptedAt: at,
        })),
      });
      const items = current
        ? [
            entry(
              ME.userId,
              ME.displayName,
              'alex@example.test',
              [...accepted.entries()].sort((a, b) => b[0] - a[0]),
            ),
            entry(DANA.userId, DANA.displayName, 'dana@example.test', [
              [1, '2026-10-08T08:05:00Z'],
            ]),
            entry('user-9', 'Riley Reviewer', 'riley@example.test', [[1, '2026-10-08T12:00:00Z']]),
            entry('user-11', 'Parker Pending', null, []),
          ]
        : [];
      return route.fulfill({
        json: { items, nextCursor: null, total: { value: items.length, relation: 'eq' } },
      });
    }
    return undefined;
  }
}
