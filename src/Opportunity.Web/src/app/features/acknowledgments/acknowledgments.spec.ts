import { LiveAnnouncer } from '@angular/cdk/a11y';
import { HttpClient, HttpRequest } from '@angular/common/http';
import { TestBed } from '@angular/core/testing';
import { Router } from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';
import { firstValueFrom } from 'rxjs';
import { provideAppRouting } from '../../app.config';
import { FakeApi, provideFakeApi } from '../../core/api/fake-api.testing';
import { provideOpportunityHttp } from '../../core/api/http';
import { continueUrl } from '../../core/workspace/acknowledgment';
import { PERMISSIONS } from '../../core/workspace/sections';
import { expectNoAxeViolations } from '../../ui/testing/axe.testing';

const WS = '/api/v1/workspaces/ws-1';
const HASH1 = 'a1'.repeat(32);
const HASH2 = 'b2'.repeat(32);
const ALEX = { userId: 'u-1', displayName: 'Alex Reviewer' };

const TEXT = 'I have read the Stipulated Protective Order.\nI agree to be bound by it.';

function acknowledgment(version: number, hash: string, acknowledged = false) {
  return {
    required: true,
    version,
    title: 'Protective order acknowledgment (Exhibit A)',
    text: version === 1 ? TEXT : TEXT + '\nI have no conflict of interest.',
    textSha256: hash,
    publishedAt: '2026-10-09T08:00:00Z',
    acknowledged,
    acknowledgedAt: acknowledged ? '2026-10-09T09:00:00Z' : null,
  };
}

describe('Acknowledgments (E20-T03)', () => {
  let api: FakeApi;
  let harness: RouterTestingHarness;
  let pending: boolean;

  async function setup(
    permissions: string[] = [PERMISSIONS.documentView],
    configure: (api: FakeApi) => void = () => undefined,
  ) {
    api = new FakeApi()
      .on('GET', '/api/v1/me', { body: { ...ALEX, email: 'a@example.test', groups: [] } })
      .on('GET', WS, () => ({
        body: {
          workspaceId: 'ws-1',
          name: 'Acme v. Widget',
          displayTimeZone: 'UTC',
          permissions,
          acknowledgmentPending: pending,
        },
      }))
      .on('GET', `${WS}/acknowledgment`, () => ({ body: acknowledgment(1, HASH1, !pending) }))
      .on('POST', `${WS}/acknowledgment/acceptances`, () => {
        pending = false;
        return {
          status: 201,
          body: { version: 1, textSha256: HASH1, acceptedAt: '2026-10-09T09:00:00Z' },
        };
      });
    configure(api);
    TestBed.configureTestingModule({
      providers: [...provideAppRouting(), ...provideOpportunityHttp(), ...provideFakeApi(api)],
    });
    vi.spyOn(TestBed.inject(LiveAnnouncer), 'announce').mockResolvedValue();
    localStorage.clear();
    harness = await RouterTestingHarness.create();
  }

  async function go(url: string): Promise<void> {
    await TestBed.inject(Router).navigateByUrl(url);
    await settle();
  }

  async function settle(rounds = 8): Promise<void> {
    for (let i = 0; i < rounds; i++) {
      await new Promise((resolve) => setTimeout(resolve, 0));
      await harness.fixture.whenStable();
    }
  }

  const root = () => harness.fixture.nativeElement as HTMLElement;
  const text = (el: Element = root()) => el.textContent?.replace(/\s+/g, ' ').trim() ?? '';
  const button = (label: string) =>
    [...root().querySelectorAll<HTMLButtonElement>('button')].find((b) => text(b) === label)!;

  it('sends a member who has not accepted to the acknowledgment page before any workspace content', async () => {
    pending = true;
    await setup();
    await go('/w/ws-1/jobs');

    const router = TestBed.inject(Router);
    expect(router.url).toBe('/w/ws-1/acknowledgment?returnUrl=%2Fw%2Fws-1%2Fjobs');
    expect(text(root().querySelector('main h1')!)).toBe(
      'Protective order acknowledgment (Exhibit A)',
    );
    expect(
      root().querySelector('[role="region"][aria-label="Acknowledgment text"]')?.textContent,
    ).toBe(TEXT);
    expect(text()).toContain('Acceptance required');
    expect(text()).toContain(`Text fingerprint (SHA-256): ${HASH1.slice(0, 12)}…`);
    // The workspace stays closed: no sections, no job feed, no content requests.
    expect(root().querySelector('nav[aria-label="Workspace sections"]')).toBeNull();
    expect(api.urls().filter((u) => u.startsWith(`${WS}/`))).toEqual([`${WS}/acknowledgment`]);
    await expectNoAxeViolations(root());

    // Accepting needs the confirmation.
    button('Accept and continue').click();
    await settle();
    expect(text()).toContain('Tick the box to confirm you agree.');
    expect(api.urls('POST')).toEqual([]);

    root().querySelector<HTMLInputElement>('input[type="checkbox"]')!.click();
    await settle();
    button('Accept and continue').click();
    await settle();

    const accepted = api.requests.find((r) => r.method === 'POST') as HttpRequest<unknown>;
    expect(accepted.url).toBe(`${WS}/acknowledgment/acceptances`);
    expect(accepted.body).toEqual({ version: 1, textSha256: HASH1 });
    expect(router.url).toBe('/w/ws-1/jobs');
    expect(root().querySelector('nav[aria-label="Workspace sections"]')).not.toBeNull();
  });

  it('opens the acknowledgment page when the API refuses a call mid-session', async () => {
    pending = false;
    await setup(undefined, (a) =>
      a.on('GET', `${WS}/jobs`, () =>
        pending
          ? {
              status: 403,
              body: {
                title: 'Acknowledgment required',
                status: 403,
                code: 'acknowledgment-required',
                acknowledgmentVersion: 2,
              },
            }
          : { body: { items: [], nextCursor: null, total: { value: 0, relation: 'eq' } } },
      ),
    );
    await go('/w/ws-1/jobs');
    expect(TestBed.inject(Router).url).toBe('/w/ws-1/jobs');
    // An administrator publishes a new version: the next call is refused.
    pending = true;
    await expect(
      firstValueFrom(TestBed.inject(HttpClient).get(`${WS}/jobs`)),
    ).rejects.toMatchObject({
      status: 403,
    });
    await settle();
    expect(TestBed.inject(Router).url).toBe('/w/ws-1/acknowledgment?returnUrl=%2Fw%2Fws-1%2Fjobs');
    expect(text(root().querySelector('main h1')!)).toBe(
      'Protective order acknowledgment (Exhibit A)',
    );
  });

  it('shows the new version when the text changed while it was being read', async () => {
    pending = true;
    let version = 1;
    await setup(undefined, (a) =>
      a
        .on('GET', `${WS}/acknowledgment`, () => ({
          body: acknowledgment(version, version === 1 ? HASH1 : HASH2),
        }))
        .on('POST', `${WS}/acknowledgment/acceptances`, () => ({
          status: 409,
          body: { title: 'Conflict', status: 409, code: 'acknowledgment-outdated' },
        })),
    );
    await go('/w/ws-1/acknowledgment');
    version = 2;
    root().querySelector<HTMLInputElement>('input[type="checkbox"]')!.click();
    await settle();
    button('Accept and continue').click();
    await settle();
    expect(text()).toContain('The text was updated while you were reading it.');
    expect(text()).toContain('Version 2');
    expect(root().querySelector<HTMLInputElement>('input[type="checkbox"]')!.checked).toBe(false);
  });

  it('lets an administrator publish a version, read the roster and download it', async () => {
    pending = false;
    let published: HttpRequest<unknown> | null = null;
    await setup([PERMISSIONS.documentView, PERMISSIONS.manageAcknowledgments], (a) =>
      a
        .on('GET', `${WS}/acknowledgment-versions`, {
          body: {
            currentVersion: 1,
            items: [
              {
                version: 1,
                title: 'Protective order acknowledgment (Exhibit A)',
                text: null,
                textSha256: HASH1,
                publishedBy: { userId: 'u-7', displayName: 'Dana Counsel' },
                publishedAt: '2026-10-09T08:00:00Z',
                acceptedCount: 1,
                isCurrent: true,
              },
            ],
          },
        })
        .on('GET', `${WS}/acknowledgment-versions/1`, {
          body: {
            version: 1,
            title: 'Protective order acknowledgment (Exhibit A)',
            text: TEXT,
            textSha256: HASH1,
            publishedBy: { userId: 'u-7', displayName: 'Dana Counsel' },
            publishedAt: '2026-10-09T08:00:00Z',
            acceptedCount: 1,
            isCurrent: true,
          },
        })
        .on('GET', `${WS}/acknowledgment-roster`, {
          body: {
            items: [
              {
                userId: 'u-1',
                displayName: 'Alex Reviewer',
                email: 'a@example.test',
                directMember: true,
                status: 'current',
                acceptances: [
                  { version: 1, textSha256: HASH1, acceptedAt: '2026-10-09T09:00:00Z' },
                ],
              },
              {
                userId: 'u-3',
                displayName: 'Parker Pending',
                email: null,
                directMember: true,
                status: 'pending',
                acceptances: [],
              },
            ],
            nextCursor: null,
            total: { value: 2, relation: 'eq' },
          },
        })
        .on('POST', `${WS}/acknowledgment-versions`, (req) => {
          published = req;
          pending = true;
          return {
            status: 201,
            body: {
              version: 2,
              title: 'Protective order acknowledgment (Exhibit A)',
              text: TEXT + '\nAmended.',
              textSha256: HASH2,
              publishedBy: ALEX,
              publishedAt: '2026-10-09T10:00:00Z',
              acceptedCount: 0,
              isCurrent: true,
            },
          };
        }),
    );
    await go('/w/ws-1/admin/acknowledgments');
    expect(text(root().querySelector('main h1')!)).toBe('Acknowledgments');
    expect(text()).toContain('version 1, published by Dana Counsel');
    expect(text()).toContain('1 accepted version 1');
    expect(text()).toContain('1 have not accepted');
    const rows = [...root().querySelectorAll('table')[0].querySelectorAll('tbody tr')].map((r) =>
      text(r),
    );
    expect(rows[0]).toContain('Alex Reviewer');
    expect(rows[0]).toContain('Accepted');
    expect(rows[1]).toContain('Not accepted');
    expect(root().querySelector('a[download]')?.getAttribute('href')).toBe(
      `${WS}/acknowledgment-roster/export`,
    );
    await expectNoAxeViolations(root());

    button('Publish new version…').click();
    await settle();
    const dialog = document.querySelector<HTMLElement>('[role="dialog"]')!;
    expect(text(dialog)).toContain('Publish version 2');
    const textarea = dialog.querySelector<HTMLTextAreaElement>('textarea')!;
    expect(textarea.value).toBe(TEXT);
    textarea.value = TEXT + '\nAmended.';
    textarea.dispatchEvent(new Event('input'));
    [...dialog.querySelectorAll<HTMLButtonElement>('button')]
      .find((b) => text(b) === 'Publish version 2')!
      .click();
    await settle();

    expect(published!.headers.get('If-Match')).toBe('"1"');
    expect(published!.body).toEqual({
      title: 'Protective order acknowledgment (Exhibit A)',
      text: TEXT + '\nAmended.',
    });
    // The publisher accepts the new version too before going on.
    expect(TestBed.inject(Router).url).toBe(
      '/w/ws-1/acknowledgment?returnUrl=%2Fw%2Fws-1%2Fadmin%2Facknowledgments',
    );
  });
});

describe('continueUrl', () => {
  it('returns to a page of the same workspace only', () => {
    expect(continueUrl('ws-1', '/w/ws-1/jobs?status=running')).toBe('/w/ws-1/jobs?status=running');
    expect(continueUrl('ws-1', '/w/ws-2/documents')).toBe('/w/ws-1/documents');
    expect(continueUrl('ws-1', 'https://example.test/w/ws-1/')).toBe('/w/ws-1/documents');
    expect(continueUrl('ws-1', '/w/ws-1//evil.test')).toBe('/w/ws-1/documents');
    expect(continueUrl('ws-1', '/w/ws-1/acknowledgment')).toBe('/w/ws-1/documents');
    expect(continueUrl('ws-1', null)).toBe('/w/ws-1/documents');
  });
});
