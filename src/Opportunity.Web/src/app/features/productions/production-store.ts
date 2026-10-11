import { DestroyRef, Injectable, computed, inject, signal } from '@angular/core';
import type { ProductionQcReportResource } from '../../core/api/generated/models';
import { ApiError, toApiError } from '../../core/api/problem-details';
import { FrozenSet, FrozenSetApi } from '../exports/frozen-set/frozen-set-api';
import { LoadedProduction, ProductionApi } from './production-api';
import { stageOf } from './production-model';

const POLL_MS = 2000;

/**
 * State of one production page: the production with its ETag, its frozen set and its latest QC run, shared by the
 * tabs. While Bates numbers are being assigned the production is re-read until the job ends. Provided by the page.
 */
@Injectable()
export class ProductionStore {
  private readonly api = inject(ProductionApi);
  private readonly sets = inject(FrozenSetApi);
  private timer: ReturnType<typeof setTimeout> | null = null;
  private productionId = '';

  readonly loaded = signal<LoadedProduction | null>(null);
  readonly production = computed(() => this.loaded()?.production ?? null);
  readonly frozen = signal<FrozenSet | null>(null);
  readonly frozenError = signal<ApiError | null>(null);
  readonly qc = signal<ProductionQcReportResource | null>(null);
  readonly error = signal<ApiError | null>(null);
  readonly stage = computed(() => {
    const p = this.production();
    return p ? stageOf(p, this.qc()) : null;
  });
  readonly isDraft = computed(() => this.production()?.status === 'draft');
  readonly allocated = computed(() => this.production()?.bates.state === 'allocated');

  constructor() {
    inject(DestroyRef).onDestroy(() => this.stop());
  }

  async load(productionId: string): Promise<void> {
    this.productionId = productionId;
    this.error.set(null);
    try {
      const loaded = await this.api.get(productionId);
      this.set(loaded);
      if (this.frozen()?.snapshotId !== loaded.production.snapshotId) {
        void this.loadFrozen(loaded.production.snapshotId);
      }
      await this.loadQc();
    } catch (e) {
      this.error.set(toApiError(e));
    }
  }

  /** A production the API answered with (after a change): kept, and followed while Bates are assigned. */
  set(loaded: LoadedProduction): void {
    this.loaded.set(loaded);
    this.stop();
    if (loaded.production.bates.state === 'allocating') {
      this.timer = setTimeout(() => void this.refresh(), POLL_MS);
    }
  }

  async refresh(): Promise<void> {
    if (!this.productionId) return;
    try {
      const before = this.production()?.bates.state;
      this.set(await this.api.get(this.productionId));
      if (before === 'allocating' && this.production()?.bates.state !== 'allocating') {
        await this.loadQc();
      }
    } catch (e) {
      this.error.set(toApiError(e));
    }
  }

  async loadQc(): Promise<void> {
    try {
      this.qc.set(this.productionId ? await this.api.qc(this.productionId) : null);
    } catch {
      this.qc.set(null);
    }
  }

  private async loadFrozen(snapshotId: string): Promise<void> {
    this.frozenError.set(null);
    try {
      this.frozen.set(await this.sets.frozenSet(snapshotId));
    } catch (e) {
      this.frozen.set(null);
      this.frozenError.set(toApiError(e));
    }
  }

  private stop(): void {
    if (this.timer !== null) clearTimeout(this.timer);
    this.timer = null;
  }
}
