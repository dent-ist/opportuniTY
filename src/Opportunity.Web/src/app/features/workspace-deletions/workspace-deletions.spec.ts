import { HttpRequest } from '@angular/common/http';
import { TestBed } from '@angular/core/testing';
import { Router } from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';
import { provideAppRouting } from '../../app.config';
import { FakeApi, FakeResponse, provideFakeApi } from '../../core/api/fake-api.testing';
import { provideOpportunityHttp } from '../../core/api/http';
import { SessionService } from '../../core/session/session';
import { INSTALLATION_PERMISSIONS } from '../../core/workspace/sections';
import { expectNoAxeViolations } from '../../ui/testing/axe.testing';

const URL = '/api/v1/workspace-deletions';

function deletion(extra: Record<string, unknown> = {}): Record<string, unknown> {
  return {
    deletionId: 'del-1',
    workspaceId: 'ws-1',
    workspaceName: 'Acme v. Widget',
    matterNumber: 'M-1',
    retentionProfile: 'retainRecords',
    reason: 'Matter closed; protective order ¶ 14',
    externalReference: 'PO ¶ 14',
    status: 'requested',
    currentStep: null,
    requestedBy: { userId: 'user-2', displayName: 'Avery Admin' },
    requestedAt: '2026-10-09T09:00:00Z',
    expiresAt: '2026-11-08T09:00:00Z',
    approvedBy: null,
    approvedAt: null,
    approvalNote: null,
    runNotBefore: null,
    cancelledBy: null,
    cancelledAt: null,
    startedAt: null,
    finishedAt: null,
    haltedAt: null,
    error: null,
    steps: [],
    certificateAvailable: false,
    canApprove: true,
    canCancel: true,
    version: 1,
    ...extra,
  };
}

const step = (name: string, totals: Record<string, number>, finished = true) => ({
  step: name,
  attempt: 1,
  startedAt: '2026-10-16T10:00:00Z',
  finishedAt: finished ? '2026-10-16T10:01:00Z' : null,
  outcome: finished ? 'success' : null,
  totals,
});

describe('Workspace deletions (E20-T02)', () => {
  let api: FakeApi;
  let harness: RouterTestingHarness;

  async function setup(
    options: {
      approver?: boolean;
      list?: Record<string, unknown>[];
      current?: Record<string, unknown>;
      approve?: (req: HttpRequest<unknown>) => FakeResponse;
    } = {},
  ): Promise<void> {
    let current = options.current ?? deletion();
    api = new FakeApi()
      .on('GET', '/api/v1/me', {
        body: {
          userId: 'user-1',
          displayName: 'Riley Approver',
          email: null,
          groups: [],
          mfa: true,
          sessionExpiresAt: null,
          installationPermissions:
            (options.approver ?? true) ? [INSTALLATION_PERMISSIONS.approveDeletion] : [],
        },
      })
      .on('GET', URL, () => ({ body: { items: options.list ?? [current] } }))
      .on('GET', `${URL}/del-1`, () => ({ body: current }))
      .on(
        'POST',
        `${URL}/del-1/approve`,
        options.approve ??
          ((req) => {
            current = {
              ...current,
              status: 'approved',
              approvedBy: { userId: 'user-1', displayName: 'Riley Approver' },
              approvedAt: '2026-10-09T10:00:00Z',
              approvalNote: (req.body as { note: string | null }).note,
              runNotBefore: '2026-10-16T10:00:00Z',
              canApprove: false,
              version: 2,
            };
            return { body: current };
          }),
      )
      .on('POST', `${URL}/del-1/cancel`, () => {
        current = { ...current, status: 'cancelled', canApprove: false, canCancel: false };
        return { body: current };
      });
    TestBed.configureTestingModule({
      providers: [...provideAppRouting(), ...provideOpportunityHttp(), ...provideFakeApi(api)],
    });
    harness = await RouterTestingHarness.create();
  }

  async function go(url: string): Promise<void> {
    await TestBed.inject(Router).navigateByUrl(url);
    await settle();
  }

  async function settle(): Promise<void> {
    for (let i = 0; i < 6; i++) {
      await new Promise((resolve) => setTimeout(resolve, 0));
      await harness.fixture.whenStable();
    }
  }

  afterEach(() => document.querySelectorAll('.cdk-overlay-container').forEach((e) => e.remove()));

  const root = () => harness.routeNativeElement as HTMLElement;
  const squash = (el: Element | null | undefined) =>
    el?.textContent?.replace(/\s+/g, ' ').trim() ?? '';
  const dialog = () => document.querySelector<HTMLElement>('[role="alertdialog"]');
  const buttonIn = (scope: ParentNode, name: string) =>
    [...scope.querySelectorAll<HTMLButtonElement>('button')].find((b) => squash(b) === name);

  it('lists deletions with their status, what is kept and what comes next', async () => {
    await setup({
      list: [
        deletion(),
        deletion({
          deletionId: 'del-0',
          workspaceName: 'Old Matter',
          status: 'completed',
          retentionProfile: 'purgeAll',
          finishedAt: '2026-09-01T00:00:00Z',
          canApprove: false,
          canCancel: false,
        }),
      ],
    });
    await go('/workspace-deletions');
    const table = root().querySelector('table')!;
    expect(squash(table)).toContain('Acme v. Widget');
    expect(squash(table)).toContain('Waiting for approval');
    expect(squash(table)).toContain('Keep productions');
    expect(squash(table)).toContain('Remove everything');
    expect(squash(table)).toContain('Deleted');
    expect(squash(root())).toContain('1 waiting for your approval');
    expect(root().querySelector('a[href="/workspace-deletions/del-1"]')).not.toBeNull();
    await expectNoAxeViolations(root());

    root().querySelector<HTMLInputElement>('input[type="checkbox"]')!.click();
    await settle();
    expect(squash(root().querySelector('table'))).not.toContain('Old Matter');
  });

  it('tells requesters where their requests come from when they have none', async () => {
    await setup({ approver: false, list: [] });
    await go('/workspace-deletions');
    expect(squash(root())).toContain('Your requests to delete workspaces');
    expect(squash(root())).toContain('Request a deletion from Admin › Workspace Settings');
  });

  it('approves after a confirmation with a note for the certificate', async () => {
    await setup();
    await go('/workspace-deletions/del-1');
    expect(squash(root().querySelector('h1'))).toBe('Delete Acme v. Widget');
    expect(squash(root())).toContain('Matter closed; protective order ¶ 14');
    expect(squash(root())).toContain('Avery Admin');
    await expectNoAxeViolations(root());

    const note = root().querySelector<HTMLTextAreaElement>('#approval-note')!;
    note.value = 'Order of 2026-10-01';
    note.dispatchEvent(new Event('input'));
    buttonIn(root(), 'Approve deletion…')!.click();
    await settle();
    expect(squash(dialog())).toContain('Approve deleting Acme v. Widget?');
    buttonIn(dialog()!, 'Approve deletion')!.click();
    await settle();

    const post = api.requests.find((r) => r.method === 'POST')!;
    expect(post.url).toBe(`${URL}/del-1/approve`);
    expect(post.headers.get('If-Match')).toBe('"1"');
    expect(post.body).toEqual({ note: 'Order of 2026-10-01' });
    expect(squash(root())).toContain('Approved');
    expect(squash(root())).toContain('Order of 2026-10-01');
    expect(buttonIn(root(), 'Approve deletion…')).toBeUndefined();
  });

  it('asks to verify the identity when approving needs MFA', async () => {
    await setup({
      approve: () => ({
        status: 403,
        body: { status: 403, code: 'step-up-required', stepUpUrl: '/bff/login?stepUp=true' },
      }),
    });
    const stepUp = vi.spyOn(TestBed.inject(SessionService), 'stepUp').mockImplementation(() => {});
    await go('/workspace-deletions/del-1');
    buttonIn(root(), 'Approve deletion…')!.click();
    await settle();
    buttonIn(dialog()!, 'Approve deletion')!.click();
    await settle();
    const alert = root().querySelector('[role="alert"]')!;
    expect(squash(alert)).toContain('Verify your identity');
    buttonIn(alert, 'Verify your identity')!.click();
    expect(stepUp).toHaveBeenCalledWith(undefined, '/bff/login?stepUp=true');
  });

  it('cancels a request that has not started', async () => {
    await setup({ current: deletion({ canApprove: false }) });
    await go('/workspace-deletions/del-1');
    expect(squash(root())).not.toContain('Approval note');
    buttonIn(root(), 'Cancel request…')!.click();
    await settle();
    buttonIn(dialog()!, 'Cancel request')!.click();
    await settle();
    expect(api.urls('POST')).toEqual([`${URL}/del-1/cancel`]);
    expect(squash(root())).toContain('Cancelled');
  });

  it('shows the progress of a run and offers the certificate when it is done', async () => {
    await setup({
      current: deletion({
        status: 'completed',
        canApprove: false,
        canCancel: false,
        startedAt: '2026-10-16T10:00:00Z',
        finishedAt: '2026-10-16T10:10:00Z',
        certificateAvailable: true,
        steps: [
          step('fence', {}),
          step('drain', { jobsCancelled: 2 }),
          step('inventory', { rows: 1200, documents: 120, objects: 40 }),
          step('searchPurge', { documentsDeleted: 120 }),
          step('databasePurge', { rows: 1150 }),
          step('storagePurge', { objects: 38 }),
          step('keyDestruction', { destroyed: 0, retained: 1 }),
          step('verification', { residualRows: 0, documents: 0, objects: 0 }),
          step('certification', {}),
        ],
      }),
    });
    await go('/workspace-deletions/del-1');
    const progress = root().querySelector('[aria-labelledby="deletion-progress"]')!;
    expect(progress.querySelectorAll('li.is-done')).toHaveLength(9);
    expect(squash(progress)).toContain('2 job(s) cancelled');
    expect(squash(progress)).toContain('1,150 records removed');
    expect(squash(progress)).toContain('Encryption keys kept for the retained productions');
    expect(squash(progress)).toContain('Nothing of the workspace was found in any store');
    const download = [...root().querySelectorAll('a')].find(
      (a) => squash(a) === 'Download destruction certificate',
    )!;
    expect(download.getAttribute('href')).toBe(`${URL}/del-1/certificate`);
    await expectNoAxeViolations(root());
  });

  it('marks the step a legal hold paused', async () => {
    await setup({
      current: deletion({
        status: 'halted',
        canApprove: false,
        canCancel: false,
        currentStep: 'databasePurge',
        startedAt: '2026-10-16T10:00:00Z',
        steps: [
          step('fence', {}),
          step('drain', {}),
          step('inventory', {}),
          step('searchPurge', {}),
        ],
      }),
    });
    await go('/workspace-deletions/del-1');
    expect(squash(root())).toContain('Paused by a legal hold');
    expect(root().querySelector('li.is-halted')).not.toBeNull();
    expect(root().querySelectorAll('li.is-waiting')).toHaveLength(4);
  });
});
