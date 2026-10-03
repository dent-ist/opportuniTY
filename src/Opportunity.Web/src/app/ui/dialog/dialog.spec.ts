import { Dialog, DialogRef } from '@angular/cdk/dialog';
import { ApplicationRef, Component, inject } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { Button } from '../button/button';
import { TextField } from '../field/text-field';
import { expectNoAxeViolations } from '../testing/axe.testing';
import { DialogService } from './dialog';
import { DialogLayout } from './dialog-layout';

@Component({
  imports: [DialogLayout, Button, TextField],
  template: `<opp-dialog-layout title="Save search">
    <opp-text-field label="Name" />
    <button dialogActions type="button" oppButton="primary" (click)="ref.close('saved')">
      Save
    </button>
  </opp-dialog-layout>`,
})
class SaveDialog {
  readonly ref = inject(DialogRef);
}

@Component({
  imports: [Button],
  template: `<button type="button" oppButton id="trigger">Open</button>`,
})
class Page {}

async function settle(): Promise<void> {
  await new Promise((r) => setTimeout(r));
  await TestBed.inject(ApplicationRef).whenStable();
}

const container = () => document.querySelector<HTMLElement>('.cdk-dialog-container')!;
const submitButton = () => container().querySelector<HTMLButtonElement>('button[type=submit]')!;

describe('DialogService', () => {
  let dialogs: DialogService;
  let trigger: HTMLButtonElement;

  beforeEach(async () => {
    const fixture = TestBed.createComponent(Page);
    await fixture.whenStable();
    trigger = fixture.nativeElement.querySelector('#trigger');
    trigger.focus();
    dialogs = TestBed.inject(DialogService);
  });

  afterEach(() => TestBed.inject(Dialog).closeAll());

  it('opens a labelled modal dialog, moves focus in and passes axe', async () => {
    dialogs.open(SaveDialog);
    await settle();
    const dialog = container();
    expect(dialog.getAttribute('role')).toBe('dialog');
    expect(dialog.getAttribute('aria-modal')).toBe('true');
    expect(document.getElementById(dialog.getAttribute('aria-labelledby')!)?.textContent).toBe(
      'Save search',
    );
    expect(dialog.contains(document.activeElement)).toBe(true);
    expect(document.querySelectorAll('.cdk-focus-trap-anchor').length).toBeGreaterThanOrEqual(2);
    await expectNoAxeViolations(dialog);
  });

  it('closes on Escape and restores focus to the trigger', async () => {
    const ref = dialogs.open(SaveDialog);
    await settle();
    const closed = vi.fn();
    ref.closed.subscribe(closed);
    document.activeElement!.dispatchEvent(
      new KeyboardEvent('keydown', { key: 'Escape', keyCode: 27, bubbles: true }),
    );
    await settle();
    expect(closed).toHaveBeenCalledWith(undefined);
    expect(document.activeElement).toBe(trigger);
  });

  it('confirm resolves false on Cancel (focused first) and true on confirm', async () => {
    const cancelled = dialogs.confirm({
      title: 'Remove designation?',
      message: 'Other groups will see this document.',
      confirmLabel: 'Remove',
      tone: 'danger',
    });
    await settle();
    expect(container().getAttribute('role')).toBe('alertdialog');
    expect(document.activeElement?.textContent?.trim()).toBe('Cancel');
    (document.activeElement as HTMLButtonElement).click();
    expect(await cancelled).toBe(false);

    const confirmed = dialogs.confirm({
      title: 'Remove?',
      message: 'Sure?',
      confirmLabel: 'Remove',
    });
    await settle();
    submitButton().click();
    expect(await confirmed).toBe(true);
  });

  it('requires the typed confirmation text before confirming (Q-34)', async () => {
    const result = dialogs.confirm({
      title: 'Apply coding to 12,400 documents?',
      message: 'This cannot be undone.',
      confirmLabel: 'Apply',
      typedConfirmation: '12400',
    });
    await settle();
    const input = container().querySelector<HTMLInputElement>('input')!;
    expect(document.activeElement).toBe(input);
    expect(submitButton().disabled).toBe(true);
    input.value = '12400';
    input.dispatchEvent(new Event('input'));
    await settle();
    expect(submitButton().disabled).toBe(false);
    submitButton().click();
    expect(await result).toBe(true);
  });
});
