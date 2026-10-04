import { DIALOG_DATA, DialogRef } from '@angular/cdk/dialog';
import { ChangeDetectionStrategy, Component, computed, inject, signal } from '@angular/core';
import { toApiError } from '../../core/api/problem-details';
import { Announcer, Button, DialogLayout, Icon, IconButton, Select } from '../../ui';
import { SavedSearch, SavedSearchApi, SharePrincipal } from './saved-search-api';
import { savedSearchErrorText } from './saved-search-model';

export interface ShareDialogData {
  readonly saved: SavedSearch;
}

const key = (p: Pick<SharePrincipal, 'kind' | 'id'>) => `${p.kind}:${p.id}`;

/**
 * Share a saved search with workspace users and groups, or stop sharing it (`SavedSearch.Share`). Sharing hands
 * over the search, never access: each person sees only the documents they may see (Q-65, ADR-015 D5.8).
 */
@Component({
  selector: 'opp-share-dialog',
  imports: [Button, DialogLayout, Icon, IconButton, Select],
  template: `<form (submit)="save($event)" novalidate>
    <opp-dialog-layout [title]="'Share ' + data.saved.name">
      <div class="ssd">
        <p class="ssd__note">
          <opp-icon name="info" />
          People you share with can run and copy this search. They see only the documents they may
          see: sharing never gives access to documents.
        </p>
        <h3 class="ssd__heading" id="share-current">Shared with</h3>
        @if (shares().length === 0) {
          <p class="ssd__lead">Nobody yet: the search is private.</p>
        } @else {
          <ul class="ssd__list" aria-labelledby="share-current">
            @for (p of shares(); track p.kind + p.id) {
              <li class="ssd__share">
                <span>
                  {{ p.displayName }}
                  <span class="ssd__share-kind">· {{ p.kind === 'group' ? 'Group' : 'User' }}</span>
                </span>
                <button
                  type="button"
                  oppIconButton
                  [label]="'Stop sharing with ' + p.displayName"
                  (click)="remove(p)"
                >
                  <opp-icon name="close" />
                </button>
              </li>
            }
          </ul>
        }
        @if (candidatesError()) {
          <p class="ssd__note">
            <opp-icon name="warning" />
            {{ candidatesError() }}
          </p>
        } @else {
          <div class="ssd__add">
            <opp-select
              label="Add a user or group"
              placeholder="Choose…"
              [options]="choices()"
              [(value)]="pick"
            />
            <button type="button" oppButton="secondary" [disabled]="!pick()" (click)="add()">
              Add
            </button>
          </div>
        }
        @if (error()) {
          <p class="ssd__error" role="alert">
            <opp-icon name="error" />
            {{ error() }}
          </p>
        }
      </div>
      <ng-container dialogActions>
        <button type="button" oppButton="ghost" (click)="ref.close()">Cancel</button>
        <button type="submit" oppButton="primary" [busy]="saving()">Save sharing</button>
      </ng-container>
    </opp-dialog-layout>
  </form>`,
  styleUrl: './saved-search-dialog.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class ShareDialog {
  protected readonly data = inject<ShareDialogData>(DIALOG_DATA);
  protected readonly ref = inject<DialogRef<SavedSearch>>(DialogRef);
  private readonly api = inject(SavedSearchApi);
  private readonly announcer = inject(Announcer);

  protected readonly shares = signal<readonly SharePrincipal[]>(this.data.saved.sharedWith);
  private readonly candidates = signal<readonly SharePrincipal[]>([]);
  protected readonly candidatesError = signal<string | null>(null);
  protected readonly pick = signal('');
  protected readonly saving = signal(false);
  protected readonly error = signal<string | null>(null);
  protected readonly choices = computed(() => {
    const taken = new Set(this.shares().map(key));
    return this.candidates()
      .filter(
        (c) => !taken.has(key(c)) && !(c.kind === 'user' && c.id === this.data.saved.owner.userId),
      )
      .map((c) => ({
        value: key(c),
        label: `${c.displayName} (${c.kind === 'group' ? 'group' : 'user'})`,
      }));
  });

  constructor() {
    this.api.shareCandidates().then(
      (list) => this.candidates.set(list),
      (e) => {
        const error = toApiError(e);
        this.candidatesError.set(
          error.status === 403
            ? 'Your role cannot list the workspace’s users and groups. You can still stop sharing; ask a Workspace Admin to add people.'
            : 'The workspace’s users and groups could not be loaded. You can still stop sharing.',
        );
      },
    );
  }

  protected add(): void {
    const chosen = this.candidates().find((c) => key(c) === this.pick());
    if (!chosen) return;
    this.shares.update((list) => [...list, chosen]);
    this.pick.set('');
    this.announcer.announce(`${chosen.displayName} added.`);
  }

  protected remove(p: SharePrincipal): void {
    this.shares.update((list) => list.filter((s) => key(s) !== key(p)));
    this.announcer.announce(`${p.displayName} removed.`);
  }

  protected async save(event: Event): Promise<void> {
    event.preventDefault();
    if (this.saving()) return;
    this.saving.set(true);
    this.error.set(null);
    try {
      this.ref.close(await this.api.share(this.data.saved.savedSearchId, this.shares()));
    } catch (e) {
      const message = savedSearchErrorText(toApiError(e));
      this.error.set(message);
      this.announcer.announce(message, { politeness: 'assertive' });
    } finally {
      this.saving.set(false);
    }
  }
}
