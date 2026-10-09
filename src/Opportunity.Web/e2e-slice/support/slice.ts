import {
  expect,
  type APIRequestContext,
  type APIResponse,
  type Locator,
  type Page,
} from '@playwright/test';
import { randomUUID } from 'node:crypto';
import { existsSync, mkdirSync, readFileSync, writeFileSync } from 'node:fs';
import { resolve } from 'node:path';

// Shared plumbing of the vertical-slice suite (E03-T03): the run's scope, signed-in sessions, and a small API client
// that talks to the real stack the way the app does (BFF cookie, anti-forgery header, Idempotency-Key).

export const repoRoot = resolve(__dirname, '../../../..');
export const baseURL = process.env['E2E_SLICE_BASE_URL'] ?? 'http://localhost:8080';
/** The developer profile's demo workspace (`./opportunity.sh seed`). */
export const workspaceId =
  process.env['E2E_SLICE_WORKSPACE'] ?? '00000000-0000-4000-8000-00000000d3e0';
export const stateDir = resolve(__dirname, '../../test-results/e2e-slice-state');
/** Demo users of the developer IdP (deploy/docker-compose/keycloak); the password is the realm's public dev default. */
export const users = {
  admin: { username: 'admin.dev', displayName: 'Ada Admin' },
  reviewer: { username: 'reviewer.dev', displayName: 'Riley Reviewer' },
} as const;
export type User = keyof typeof users;
export const password = process.env['E2E_SLICE_PASSWORD'] ?? 'opportunity';

/** What the setup project imported, read by the path specs. */
export interface RunInfo {
  /** Control-number prefix of this run's import: every query is scoped to it, so runs never see each other. */
  prefix: string;
  importId: string;
  documents: number;
  /** Planted needle terms and the control numbers (with prefix) that contain each, from the generator's ground truth. */
  needles: Record<string, string[]>;
}

export const storageState = (user: User) => resolve(stateDir, `${user}.json`);

export function saveRun(run: RunInfo): void {
  mkdirSync(stateDir, { recursive: true });
  writeFileSync(resolve(stateDir, 'run.json'), JSON.stringify(run, null, 2));
}

export function loadRun(): RunInfo {
  const file = resolve(stateDir, 'run.json');
  if (!existsSync(file)) throw new Error(`${file} is missing: the setup project did not run.`);
  return JSON.parse(readFileSync(file, 'utf8')) as RunInfo;
}

/** The run's scope as query text. */
export const scope = (run: RunInfo) => `controlnumber:${run.prefix}*`;

/**
 * Signs in through the app's Sign in page, the BFF and the Keycloak login form, as a reviewer would. With
 * `keyboard`, the whole round trip is done with Tab, typing and Enter.
 */
export async function signIn(page: Page, user: User, options: { keyboard?: boolean } = {}) {
  await page.goto('/workspaces');
  await expect(page).toHaveURL(/\/sign-in/);
  const signInButton = page.getByRole('button', { name: 'Sign in', exact: true });
  if (options.keyboard) {
    await tabTo(page, signInButton);
    await page.keyboard.press('Enter');
  } else {
    await signInButton.click();
  }
  // Keycloak's login form (a third-party page: its stable ids are the contract).
  const username = page.locator('#username');
  await expect(username).toBeVisible();
  if (options.keyboard) {
    await expect(username).toBeFocused();
    await page.keyboard.type(users[user].username);
    await page.keyboard.press('Tab');
    await expect(page.locator('#password')).toBeFocused();
    await page.keyboard.type(password);
    await page.keyboard.press('Enter');
  } else {
    await username.fill(users[user].username);
    await page.locator('#password').fill(password);
    await page.locator('#kc-login').click();
  }
  await expect(page).toHaveURL(/\/workspaces$/);
  await expect(page.getByRole('button', { name: /User menu/ })).toContainText(
    users[user].displayName,
  );
}

/**
 * Tabs forward until `target` has focus, asserting a visible focus indicator on every stop of the app (WCAG 2.4.7;
 * the same check as e2e/keyboard.spec.ts).
 */
export async function tabTo(page: Page, target: Locator, maxStops = 60): Promise<void> {
  for (let stop = 0; stop < maxStops; stop++) {
    await page.keyboard.press('Tab');
    const indicator = await page.evaluate(() => {
      const el = document.activeElement;
      if (!el || el === document.body) return { label: 'body', visible: true };
      const ring = (node: Element) => {
        const style = getComputedStyle(node);
        return (
          (style.outlineStyle !== 'none' && parseFloat(style.outlineWidth) > 0) ||
          style.boxShadow !== 'none'
        );
      };
      let visible = ring(el);
      for (
        let up = el.parentElement, depth = 0;
        !visible && up && depth < 3;
        up = up.parentElement, depth++
      ) {
        visible = up.matches(':focus-within') && ring(up);
      }
      return { label: el.outerHTML.slice(0, 120), visible };
    });
    expect(indicator.visible, `No visible focus indicator on ${indicator.label}`).toBe(true);
    if (await target.evaluate((el) => el === document.activeElement)) return;
  }
  throw new Error(`Did not reach ${target} within ${maxStops} Tab stops`);
}

/** JSON API calls with the page's session: anti-forgery header on writes, Idempotency-Key where the API wants one. */
export class Api {
  constructor(
    private readonly request: APIRequestContext,
    private readonly cookies: () => Promise<{ name: string; value: string }[]>,
  ) {}

  static of(page: Page): Api {
    return new Api(page.request, () => page.context().cookies(baseURL));
  }

  path(relative: string): string {
    return `/api/v1/workspaces/${workspaceId}${relative}`;
  }

  async get<T>(relative: string): Promise<T> {
    return (await this.ok(await this.request.get(this.path(relative)))).json() as Promise<T>;
  }

  async getRaw(relative: string): Promise<APIResponse> {
    return this.ok(await this.request.get(this.path(relative)));
  }

  async post<T>(relative: string, data: unknown, idempotent = false): Promise<T> {
    const response = await this.request.post(this.path(relative), {
      data,
      headers: await this.writeHeaders(idempotent),
    });
    return (await this.ok(response)).json() as Promise<T>;
  }

  async postMultipart<T>(
    relative: string,
    multipart: Record<string, string | { name: string; mimeType: string; buffer: Buffer }>,
  ): Promise<T> {
    const response = await this.request.post(this.path(relative), {
      multipart,
      headers: await this.writeHeaders(true),
    });
    return (await this.ok(response)).json() as Promise<T>;
  }

  /** `DELETE` with the anti-forgery header and `If-Match` (the ETag of a `GET` of the same resource). */
  async delete(relative: string): Promise<void> {
    const etag = (await this.getRaw(relative)).headers()['etag'];
    const response = await this.request.delete(this.path(relative), {
      headers: { ...(await this.writeHeaders(false)), ...(etag ? { 'If-Match': etag } : {}) },
    });
    await this.ok(response);
  }

  /** Polls `relative` until `done` holds for its JSON body. */
  async waitFor<T>(
    relative: string,
    done: (body: T) => boolean,
    { timeout = 180_000, message = relative } = {},
  ): Promise<T> {
    let last: T | undefined;
    await expect
      .poll(
        async () => {
          last = await this.get<T>(relative);
          return done(last);
        },
        { timeout, intervals: [500, 1_000, 2_000], message },
      )
      .toBe(true);
    return last!;
  }

  /** Total of a search, as the API counts it. */
  async count(query: string): Promise<number> {
    const page = await this.post<{ total: { value: number } }>('/searches', {
      query,
      pageSize: 1,
      countExact: true,
    });
    return page.total.value;
  }

  private async writeHeaders(idempotent: boolean): Promise<Record<string, string>> {
    const xsrf = (await this.cookies()).find((c) => c.name.endsWith('opp-xsrf'))?.value;
    if (!xsrf) throw new Error('No anti-forgery cookie: is the session signed in?');
    return {
      Origin: baseURL,
      'X-XSRF-TOKEN': xsrf,
      ...(idempotent ? { 'Idempotency-Key': randomUUID() } : {}),
    };
  }

  private async ok(response: APIResponse): Promise<APIResponse> {
    if (!response.ok()) {
      throw new Error(`${response.url()} → ${response.status()}: ${await response.text()}`);
    }
    return response;
  }
}

/** The documents page of the workspace, with the keyword box. */
export const documentsPath = `/w/${workspaceId}/documents`;
