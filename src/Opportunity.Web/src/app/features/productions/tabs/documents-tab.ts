import {
  ChangeDetectionStrategy,
  Component,
  computed,
  effect,
  inject,
  signal,
} from '@angular/core';
import { RouterLink } from '@angular/router';
import type { ProductionDocumentResource } from '../../../core/api/generated/models';
import { ApiError, toApiError } from '../../../core/api/problem-details';
import { WorkspaceContext } from '../../../core/workspace/workspace-context';
import { Button, EmptyState, ErrorState, Icon, LoadingState, ToastService } from '../../../ui';
import { queryDocumentsParams } from '../../documents/relationships/relationship-link';
import { ProductionApi } from '../production-api';
import { controlNumberQuery } from '../production-model';
import { ProductionStore } from '../production-store';
import { downloadCrossReference } from './cross-reference';
import { DisplayFormat } from '../../../core/i18n';

/**
 * Documents tab: the production's documents in production order with ProdBegBates/ProdEndBates and the attachment
 * range, once Bates numbers are assigned. Documents the user may not view are left out (Q-52). The listed documents
 * open in Documents (and from there in Review mode); the Bates cross-reference downloads as CSV.
 */
@Component({
  selector: 'opp-production-documents-tab',
  imports: [Button, EmptyState, ErrorState, Icon, LoadingState, RouterLink],
  templateUrl: './documents-tab.html',
  styleUrl: '../../exports/delivery.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class DocumentsTab {
  protected readonly store = inject(ProductionStore);
  private readonly api = inject(ProductionApi);
  private readonly fmt = inject(DisplayFormat);
  private readonly toasts = inject(ToastService);
  private readonly context = inject(WorkspaceContext);

  protected readonly rows = signal<readonly ProductionDocumentResource[] | null>(null);
  protected readonly cursor = signal<string | null>(null);
  protected readonly error = signal<ApiError | null>(null);
  protected readonly loadingMore = signal(false);
  protected readonly exporting = signal(false);
  private loadedFor: string | null = null;

  protected readonly openParams = computed(() => {
    const p = this.store.production();
    const query = controlNumberQuery((this.rows() ?? []).map((r) => r.controlNumber));
    return p && query ? queryDocumentsParams(query, `Documents of ${p.name}`) : null;
  });

  constructor() {
    // Read once the numbers are assigned, and again when they change (a new allocation).
    effect(() => {
      const p = this.store.production();
      const key = p && p.bates.state === 'allocated' ? `${p.productionId}:${p.bates.first}` : null;
      if (key && key !== this.loadedFor) {
        this.loadedFor = key;
        void this.load();
      }
    });
  }

  protected async load(): Promise<void> {
    const p = this.store.production();
    if (!p) return;
    this.error.set(null);
    try {
      const page = await this.api.documents(p.productionId);
      this.rows.set(page.items);
      this.cursor.set(page.nextCursor);
    } catch (e) {
      this.error.set(toApiError(e));
    }
  }

  protected async more(): Promise<void> {
    const p = this.store.production();
    if (!p || this.loadingMore()) return;
    this.loadingMore.set(true);
    try {
      const page = await this.api.documents(p.productionId, this.cursor());
      this.rows.update((rows) => [...(rows ?? []), ...page.items]);
      this.cursor.set(page.nextCursor);
    } catch (e) {
      this.error.set(toApiError(e));
    } finally {
      this.loadingMore.set(false);
    }
  }

  protected async crossReference(): Promise<void> {
    const p = this.store.production();
    if (!p || this.exporting()) return;
    this.exporting.set(true);
    try {
      const n = await downloadCrossReference(this.api, p, this.context.workspace.name);
      this.toasts.show(`Bates cross-reference of ${this.num(n)} documents downloaded.`, {
        tone: 'success',
      });
    } catch (e) {
      this.toasts.show(toApiError(e).problem.detail ?? 'The cross-reference could not be built.', {
        tone: 'error',
      });
    } finally {
      this.exporting.set(false);
    }
  }

  protected num(n: number | string | null | undefined): string {
    return n === null || n === undefined ? '—' : this.fmt.number(Number(n));
  }
}
