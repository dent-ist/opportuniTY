import { ChangeDetectionStrategy, Component, computed, inject, signal } from '@angular/core';
import { RouterLink } from '@angular/router';
import type { FieldResource } from '../../core/api/generated/models';
import { ApiError, toApiError } from '../../core/api/problem-details';
import { AdminField, FieldAdminApi, HttpFieldAdminApi } from '../../core/fields/field-admin-api';
import { WorkspaceContext } from '../../core/workspace/workspace-context';
import { EmptyState, ErrorState, LoadingState, Select, SelectOption } from '../../ui';
import { ChoiceEditor } from './choice-editor';
import { isChoiceType, storageLabel, typeLabel } from './field-admin-model';

/**
 * Admin › Choices (E04-T06): the choices of one Single or Multiple Choice field at a time, with the same editor as
 * Admin › Fields (add, rename, reorder, deactivate; delete only while never used). New choice fields are created in
 * Admin › Fields.
 */
@Component({
  selector: 'opp-choices-page',
  imports: [ChoiceEditor, EmptyState, ErrorState, LoadingState, RouterLink, Select],
  template: `
    <header class="fa__header">
      <div>
        <h1 class="fa__title" tabindex="-1">Choices</h1>
        <p class="fa__lead">
          The values reviewers pick in Single Choice and Multiple Choice fields. A choice that was
          ever used can be deactivated but not deleted, so coded documents keep it.
          <a [routerLink]="fieldsLink()">Create choice fields in Fields.</a>
        </p>
      </div>
    </header>
    @if (loading()) {
      <opp-loading-state label="Loading choice fields…" />
    } @else if (loadError(); as error) {
      <opp-error-state [error]="error" (retry)="load()" />
    } @else if (!options().length) {
      <opp-empty-state
        title="No choice fields yet"
        message="Create a Single Choice or Multiple Choice field in Fields first."
      />
    } @else {
      <div class="fa__toolbar">
        <opp-select
          label="Choice field"
          [options]="options()"
          [value]="selectedId()"
          (valueChange)="select($event)"
        />
      </div>
      @if (opening()) {
        <opp-loading-state label="Loading choices…" />
      } @else if (field(); as f) {
        <section class="fa__card" aria-labelledby="choices-title">
          <h2 id="choices-title" class="fa__card-title">{{ f.displayName }}</h2>
          <p class="fa__muted">{{ typeLabel(f.type) }} · {{ storageLabel(f.storage) }} field</p>
          <opp-choice-editor [field]="f" (changed)="field.set($event)" />
        </section>
      }
    }
  `,
  styleUrl: './field-admin.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
  providers: [{ provide: FieldAdminApi, useClass: HttpFieldAdminApi }],
  host: { class: 'fa-page' },
})
export class ChoicesPage {
  private readonly api = inject(FieldAdminApi);
  private readonly context = inject(WorkspaceContext);

  protected readonly loading = signal(true);
  protected readonly loadError = signal<ApiError | null>(null);
  protected readonly fields = signal<readonly FieldResource[]>([]);
  protected readonly selectedId = signal('');
  protected readonly field = signal<AdminField | null>(null);
  protected readonly opening = signal(false);
  protected readonly typeLabel = typeLabel;
  protected readonly storageLabel = storageLabel;

  protected readonly options = computed<SelectOption[]>(() =>
    this.fields()
      .filter((f) => isChoiceType(f.type))
      .sort((a, b) => a.displayName.localeCompare(b.displayName))
      .map((f) => ({ value: String(f.fieldId), label: f.displayName })),
  );
  protected readonly fieldsLink = computed(() => [
    '/w',
    this.context.workspaceId,
    'admin',
    'fields',
  ]);

  constructor() {
    void this.load();
  }

  async load(): Promise<void> {
    this.loading.set(true);
    this.loadError.set(null);
    try {
      this.fields.set(await this.api.fields());
      const first = this.options()[0]?.value;
      if (first) await this.select(first);
    } catch (e) {
      this.loadError.set(toApiError(e));
    } finally {
      this.loading.set(false);
    }
  }

  protected async select(id: string): Promise<void> {
    this.selectedId.set(id);
    this.opening.set(true);
    try {
      this.field.set(await this.api.field(Number(id)));
    } catch (e) {
      this.loadError.set(toApiError(e));
    } finally {
      this.opening.set(false);
    }
  }
}
