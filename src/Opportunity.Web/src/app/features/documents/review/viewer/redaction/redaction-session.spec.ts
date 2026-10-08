import { signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { SessionService } from '../../../../../core/session/session';
import { WorkspaceContext } from '../../../../../core/workspace/workspace-context';
import { RedactionApi } from './redaction-api';
import { FakeRedactionApi, savedRedaction } from './redaction-fixtures.testing';
import { RedactionSession } from './redaction-session';

describe('Redaction session (E11-T04)', () => {
  let api: FakeRedactionApi;
  let session: RedactionSession;

  function setup(permissions: readonly string[] = ['Redaction.Apply', 'Redaction.Remove']): void {
    TestBed.configureTestingModule({
      providers: [
        RedactionSession,
        { provide: RedactionApi, useClass: FakeRedactionApi },
        { provide: WorkspaceContext, useValue: { can: (p: string) => permissions.includes(p) } },
        { provide: SessionService, useValue: { principal: signal({ userId: 'me' }) } },
      ],
    });
    localStorage.clear();
    api = TestBed.inject(RedactionApi) as FakeRedactionApi;
    session = TestBed.inject(RedactionSession);
  }

  const settle = async () => {
    for (let i = 0; i < 5; i++) await new Promise((r) => setTimeout(r, 0));
  };

  it('loads the sets, active reasons and the document redactions, and saves each change as the next version', async () => {
    setup();
    await session.open('doc-1');
    expect(session.status()).toBe('ready');
    expect(session.setId()).toBe('set-1');
    expect(session.activeReasons().map((r) => r.code)).toEqual([
      'AttorneyClient',
      'PII',
      'PersonalDataGdpr',
    ]);
    session.reasonCode.set('PersonalDataGdpr');
    session.type.set('labelled');

    const id = session.add(1, { x: 0, y: 0, w: 100_000, h: 50_000 })!;
    expect(session.redactions()[0].unsaved).toBe(true);
    await settle();
    expect(api.saves).toEqual([`0:add(${id})`]);
    expect(api.received[0].changes[0]).toMatchObject({
      type: 'labelled',
      reasonCode: 'PersonalDataGdpr',
    });
    expect(session.saved()!.version).toBe(1);
    expect(session.redactions()[0].unsaved).toBe(false);

    session.reshape(id, { x: 10_000, y: 0, w: 100_000, h: 50_000 });
    session.commit(id);
    session.relabel(id, { reasonCode: 'PII' });
    await settle();
    expect(api.saves.slice(1)).toEqual([`1:modify(${id})`, `2:modify(${id})`]);
    expect(session.redactions()[0]).toMatchObject({ rect: { x: 10_000 }, reasonCode: 'PII' });
  });

  it('keeps a change refused by a concurrent edit on screen, sends nothing more and recovers on refresh', async () => {
    setup();
    await session.open('doc-1');
    api.concurrentEdit = savedRedaction('theirs', 1, { x: 0, y: 500_000, w: 200_000, h: 50_000 });

    const mine = session.add(1, { x: 0, y: 0, w: 100_000, h: 50_000 })!;
    await settle();
    expect(api.saves).toEqual(['0:conflict']);
    expect(session.notice()).toMatchObject({ kind: 'conflict' });
    expect(session.notice()!.message).toContain('Your last change was not saved');
    expect(session.redactions().find((r) => r.id === mine)?.unsaved).toBe(true);
    expect(session.canDraw()).toBe(false);
    expect(session.add(1, { x: 0, y: 0, w: 100_000, h: 50_000 })).toBeNull();

    await session.refresh();
    expect(session.notice()).toBeNull();
    expect(session.redactions().map((r) => r.id)).toEqual(['theirs']);
    expect(session.canDraw()).toBe(true);
    expect(api.received).toHaveLength(1);
  });

  it('saves a keyboard move after a pause or when leaving the box, and undoes the last change', async () => {
    vi.useFakeTimers();
    try {
      setup();
      await session.open('doc-1');
      const id = session.add(2, { x: 0, y: 0, w: 100_000, h: 50_000 })!;
      await vi.runAllTimersAsync();
      session.reshape(id, { x: 10_000, y: 0, w: 100_000, h: 50_000 }, true);
      session.reshape(id, { x: 20_000, y: 0, w: 100_000, h: 50_000 }, true);
      expect(api.saves).toHaveLength(1);
      await vi.advanceTimersByTimeAsync(800);
      expect(api.saves).toEqual([`0:add(${id})`, `1:modify(${id})`]);
      expect(api.received[1].changes[0]).toMatchObject({ rect: { x: 20_000 } });

      expect(session.undo()).toBe(true);
      await vi.runAllTimersAsync();
      expect(api.received[2].changes[0]).toMatchObject({ operation: 'modify', rect: { x: 0 } });
    } finally {
      vi.useRealTimers();
    }
  });

  it('lets Redaction.Apply change only its own redactions and never remove', async () => {
    setup(['Redaction.Apply']);
    api.docs.set('doc-1/set-1', {
      version: 4,
      redactions: [savedRedaction('theirs', 1, { x: 0, y: 0, w: 100_000, h: 100_000 })],
    });
    await session.open('doc-1');
    expect(session.redactions()[0].editable).toBe(false);
    session.reshape('theirs', { x: 50_000, y: 0, w: 100_000, h: 100_000 });
    expect(session.redactions()[0].unsaved).toBe(false);
    expect(session.remove('theirs')).toBe(false);
    const mine = session.add(1, { x: 0, y: 500_000, w: 100_000, h: 100_000 })!;
    await settle();
    expect(session.redactions().find((r) => r.id === mine)?.editable).toBe(true);
    expect(session.remove(mine)).toBe(false);
  });

  it('says why redaction is unavailable and draws nothing without rendered images', async () => {
    setup();
    api.redactable = false;
    await session.open('doc-9');
    expect(session.unavailable()).toBe('Redaction requires rendered images');
    expect(session.canDraw()).toBe(false);
    expect(session.add(1, { x: 0, y: 0, w: 100_000, h: 100_000 })).toBeNull();
    expect(api.received).toHaveLength(0);
  });

  it('remembers the chosen Redaction Set', async () => {
    setup();
    await session.open('doc-1');
    await session.chooseSet('set-2');
    expect(session.saved()!.setId).toBe('set-2');
    expect(localStorage.getItem('opp.redaction.set')).toBe('set-2');
  });
});
