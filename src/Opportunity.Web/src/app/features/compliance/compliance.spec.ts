import { LiveAnnouncer } from '@angular/cdk/a11y';
import { TestBed } from '@angular/core/testing';
import { Router } from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';
import { provideAppRouting } from '../../app.config';
import { FakeApi, provideFakeApi } from '../../core/api/fake-api.testing';
import { provideOpportunityHttp } from '../../core/api/http';
import { PERMISSIONS } from '../../core/workspace/sections';
import { expectNoAxeViolations } from '../../ui/testing/axe.testing';
import { dayBoundary } from './compliance-api';

const WS = '/api/v1/workspaces/ws-1';
const WALL = 'wall-1';
const DOC = '0199c3a0-0000-7000-8000-00000000d001';

function wallProof(held: boolean) {
  return {
    from: '2026-09-11T00:00:00Z',
    to: '2026-10-11T00:00:00Z',
    generatedAt: '2026-10-11T09:00:00Z',
    held,
    counts: { attempts: held ? 1 : 2, denied: 1, breakGlass: 0, allowed: held ? 0 : 1, failed: 0 },
    walls: [
      {
        wallId: WALL,
        name: 'Northwind conflict',
        createdAt: '2026-09-01T08:00:00Z',
        members: [
          {
            userId: 'u-9',
            group: null,
            displayName: 'Morgan Walled',
            addedAt: '2026-09-01T08:00:00Z',
          },
          {
            userId: null,
            group: 'Northwind team',
            displayName: null,
            addedAt: '2026-09-01T08:00:00Z',
          },
        ],
        documentsCovered: 42,
      },
    ],
    attempts: [
      ...(held
        ? []
        : [
            {
              eventId: 'e-2',
              occurredAt: '2026-10-02T10:00:00Z',
              result: 'Allowed',
              userId: 'u-9',
              userDisplay: 'Morgan Walled',
              documentId: DOC,
              wallIds: [WALL],
              action: 'Document.Viewed',
              outcome: 'Success',
              reasonCode: null,
              accessPath: 'Normal',
              correlationId: 'c-2',
            },
          ]),
      {
        eventId: 'e-1',
        occurredAt: '2026-10-01T10:00:00Z',
        result: 'Denied',
        userId: 'u-9',
        userDisplay: 'Morgan Walled',
        documentId: DOC,
        wallIds: [WALL],
        action: 'Document.Retrieved',
        outcome: 'Denied',
        reasonCode: 'EthicalWall',
        accessPath: 'Normal',
        correlationId: 'c-1',
      },
    ],
    truncated: false,
    wallChanges: [],
  };
}

describe('Admin › Compliance (E14-T06)', () => {
  let api: FakeApi;
  let harness: RouterTestingHarness;

  async function setup(permissions: string[]) {
    api = new FakeApi()
      .on('GET', '/api/v1/me', {
        body: { userId: 'u-1', displayName: 'Alex Auditor', email: 'a@example.test', groups: [] },
      })
      .on('GET', WS, {
        body: { workspaceId: 'ws-1', name: 'Acme v. Widget', displayTimeZone: 'UTC', permissions },
      })
      .on('GET', `${WS}/compliance/wall-proof`, { body: wallProof(false) })
      .on('GET', `${WS}/compliance/access-review`, {
        body: {
          generatedAt: '2026-10-11T09:00:00Z',
          users: 1,
          groups: 1,
          entries: [
            {
              principal: 'User',
              userId: 'u-8',
              group: null,
              displayName: 'Casey Grouped',
              email: 'casey@example.test',
              roles: [
                {
                  role: 'Reviewer',
                  displayName: 'Reviewer',
                  viaGroup: 'Northwind team',
                  assignedAt: '2026-09-01T08:00:00Z',
                },
              ],
              walls: [{ wallId: WALL, name: 'Northwind conflict', viaGroup: 'Northwind team' }],
              lastAccessAt: null,
              lastSignInAt: '2026-10-02T08:00:00Z',
              knownMembers: null,
            },
            {
              principal: 'Group',
              userId: null,
              group: 'Northwind team',
              displayName: null,
              email: null,
              roles: [],
              walls: [{ wallId: WALL, name: 'Northwind conflict', viaGroup: null }],
              lastAccessAt: null,
              lastSignInAt: null,
              knownMembers: '1',
            },
          ],
        },
      });
    TestBed.configureTestingModule({
      providers: [...provideAppRouting(), ...provideOpportunityHttp(), ...provideFakeApi(api)],
    });
    vi.spyOn(TestBed.inject(LiveAnnouncer), 'announce').mockResolvedValue();
    localStorage.clear();
    harness = await RouterTestingHarness.create();
    await TestBed.inject(Router).navigateByUrl('/w/ws-1/admin/compliance');
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
    [...root().querySelectorAll<HTMLButtonElement>('button')].find((b) => text(b).endsWith(label))!;

  it('generates the wall proof and the access review only when asked, and links the downloads', async () => {
    await setup([PERMISSIONS.complianceReport, PERMISSIONS.auditExport]);
    expect(text(root().querySelector('main h1')!)).toBe('Compliance');
    expect(api.urls().filter((u) => u.includes('/compliance/'))).toEqual([]);

    button('Generate proof').click();
    await settle();
    const proofUrl = api.urls().find((u) => u.includes('/compliance/wall-proof'))!;
    expect(proofUrl).toContain(`from=${dayBoundary(localDay(-30))}`);
    expect(proofUrl).toContain(`to=${dayBoundary(localDay(0), true)}`);
    expect(text()).toContain('Walls did not hold');
    const rows = [...root().querySelectorAll('table')[1].querySelectorAll('tbody tr')].map((r) =>
      text(r),
    );
    expect(rows[0]).toContain('Allowed');
    expect(rows[0]).toContain('Northwind conflict');
    expect(rows[1]).toContain('Denied');
    expect(text(root().querySelector('table')!)).toContain('Morgan Walled, Group Northwind team');

    button('Generate review').click();
    await settle();
    expect(text()).toContain('1 person and 1 group');
    expect(text()).toContain('Reviewer (via Northwind team)');
    expect(text()).toContain('Group · 1 known member');

    const links = [...root().querySelectorAll<HTMLAnchorElement>('a[download]')].map((a) =>
      a.getAttribute('href'),
    );
    expect(links[0]).toMatch(new RegExp(`^${WS}/compliance/wall-proof/export\\?from=.+&to=.+$`));
    expect(links[1]).toBe(`${WS}/compliance/access-review/export`);
    expect(links[2]).toBe(`${WS}/compliance/audit-export`);
    await expectNoAxeViolations(root());
  });

  it('hides the audit export without Audit.Export and refuses a reversed period', async () => {
    await setup([PERMISSIONS.complianceReport]);
    expect(text()).not.toContain('Audit export');

    const from = root().querySelector<HTMLInputElement>('input[type="date"]')!;
    from.value = '2999-01-01';
    from.dispatchEvent(new Event('input'));
    await settle();
    expect(text(root().querySelector('[role="alert"]')!)).toBe(
      'The start date must be on or before the end date.',
    );
    expect(button('Generate proof').disabled).toBe(true);
    expect(root().querySelector('a[download]')!.hasAttribute('href')).toBe(false);
  });
});

function localDay(offsetDays: number): string {
  const d = new Date(Date.now() + offsetDays * 86_400_000);
  const pad = (n: number) => String(n).padStart(2, '0');
  return `${d.getFullYear()}-${pad(d.getMonth() + 1)}-${pad(d.getDate())}`;
}
