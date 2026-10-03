import { DialogRef } from '@angular/cdk/dialog';
import { ChangeDetectionStrategy, Component, inject, signal } from '@angular/core';
import { Button, DialogLayout, Select, TextField } from '../../ui';

/** Example form dialog for the showcase. */
@Component({
  selector: 'opp-showcase-dialog',
  imports: [DialogLayout, Button, TextField, Select],
  template: `
    <opp-dialog-layout title="Save search">
      <div class="form">
        <opp-text-field
          label="Name"
          [(value)]="name"
          [required]="true"
          hint="Shown in the saved-search browser."
        />
        <opp-select label="Folder" [(value)]="folder" [options]="folders" />
      </div>
      <ng-container dialogActions>
        <button type="button" oppButton="ghost" (click)="ref.close()">Cancel</button>
        <button type="button" oppButton="primary" (click)="ref.close()">Save</button>
      </ng-container>
    </opp-dialog-layout>
  `,
  styleUrl: './showcase-dialog.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class ShowcaseDialog {
  protected readonly ref = inject(DialogRef);
  protected readonly name = signal('');
  protected readonly folder = signal('private');
  protected readonly folders = [
    { value: 'private', label: 'My searches' },
    { value: 'shared', label: 'Shared searches' },
  ];
}
