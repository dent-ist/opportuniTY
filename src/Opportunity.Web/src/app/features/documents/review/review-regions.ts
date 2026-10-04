import {
  ChangeDetectionStrategy,
  Component,
  Signal,
  computed,
  input,
  output,
  signal,
} from '@angular/core';
import type { FieldResource, SearchHit } from '../../../core/api/generated/models';
import { Button, EmptyState, Icon, LoadingState } from '../../../ui';
import type { CodingValue, DocumentCoding, TextChunk } from './review-ports';

// The three regions of Review mode (familiarity guide §3.2). Each is a placeholder with the typed inputs its
// ticket builds on: the viewer modes (E16-T04, #130), the coding pane (E16-T05, #131) and Related Items
// (E16-T10). Their regions, landmarks, sizes and keyboard commands belong to the review workspace (E16-T03).

/** What the viewer shows: the displayed document and its first text chunk once loaded. */
export interface ViewerDocument {
  readonly hit: SearchHit;
  readonly state: 'loading' | 'ready' | 'unavailable';
  readonly text: TextChunk | null;
}

/**
 * The document viewer (E16-T04 replaces the body with the viewer modes). Until then it shows the start of the
 * extracted text, delivered through the protected-content gateway like every viewer request.
 */
@Component({
  selector: 'opp-review-viewer',
  imports: [EmptyState, Icon, LoadingState],
  template: `@let doc = document();
    <p class="pane__note">
      <opp-icon name="info" />
      <span
        >Viewer modes (Extracted Text, Image, Native, Production, Metadata) are coming soon. The
        start of the extracted text is shown.</span
      >
    </p>
    <div
      class="viewer__body"
      [attr.data-viewer-document]="doc.hit.documentId"
      [attr.data-state]="doc.state"
      [attr.aria-busy]="doc.state === 'loading' ? true : null"
    >
      @switch (doc.state) {
        @case ('loading') {
          <opp-loading-state label="Loading document…" />
        }
        @case ('unavailable') {
          <opp-empty-state
            title="This document cannot be shown"
            message="Its content is not available. Move to the next document or go back to the list."
          />
        }
        @default {
          @if (doc.text?.text) {
            <pre
              class="viewer__text"
              tabindex="0"
              [attr.aria-label]="'Extracted text of ' + doc.hit.controlNumber"
              >{{ doc.text!.text }}</pre>
            @if (doc.text!.partial) {
              <p class="pane__muted">More text follows in the full viewer.</p>
            }
          } @else {
            <opp-empty-state
              title="No extracted text"
              message="This document has no extracted text."
            />
          }
        }
      }
    </div>`,
  styleUrl: './review-regions.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { class: 'pane__content' },
})
export class ReviewViewer {
  readonly document = input.required<ViewerDocument>();
}

/**
 * What Review mode needs from the coding pane (E16-T05): whether it holds unsaved edits, and saving or
 * discarding them. Save & Next, Save & Previous and the unsaved-changes prompt are built on this.
 */
export interface CodingEditor {
  readonly dirty: Signal<boolean>;
  /** Saves the edits; false when they could not be saved (validation, conflict) and the move must not happen. */
  save(): Promise<boolean>;
  discard(): void;
}

/** The coding pane's load state for the displayed document. */
export type CodingState = DocumentCoding | 'loading' | 'unavailable';

/**
 * The coding pane (E16-T05 renders the coding layout here). Until then it lists the document's current values
 * of the workspace's coding fields, read only, and offers the Save & Previous / Save & Next moves.
 */
@Component({
  selector: 'opp-review-coding',
  imports: [Button, Icon, LoadingState],
  template: `<p class="pane__note">
      <opp-icon name="info" />
      <span>Coding layouts and editable coding fields are coming soon.</span>
    </p>
    @switch (stateKind()) {
      @case ('loading') {
        <opp-loading-state label="Loading coding…" />
      }
      @case ('unavailable') {
        <p class="pane__muted">The coding of this document could not be loaded.</p>
      }
      @default {
        @if (rows().length) {
          <dl class="coding__values">
            @for (row of rows(); track row.queryName) {
              <div class="coding__value">
                <dt>{{ row.label }}</dt>
                <dd [class.pane__muted]="!row.value">{{ row.value || 'Not set' }}</dd>
              </div>
            }
          </dl>
        } @else {
          <p class="pane__muted">This workspace has no coding fields yet.</p>
        }
      }
    }
    @if (canCode()) {
      <div class="coding__actions">
        <button type="button" oppButton="secondary" (click)="move.emit('previous')">
          <opp-icon name="chevron-left" />Save &amp; Previous
        </button>
        <button type="button" oppButton="primary" (click)="move.emit('next')">
          Save &amp; Next<opp-icon name="chevron-right" />
        </button>
      </div>
    }`,
  styleUrl: './review-regions.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { class: 'pane__content' },
})
export class ReviewCoding implements CodingEditor {
  readonly documentId = input.required<string>();
  /** The workspace's fields (GET …/fields); the coding fields among them are shown. */
  readonly fields = input<readonly FieldResource[] | null>(null);
  readonly coding = input.required<CodingState>();
  /** Coding.Write: without it the pane is read-only (familiarity guide §3.3). */
  readonly canCode = input(false);
  /** Save & Next / Save & Previous. */
  readonly move = output<'next' | 'previous'>();

  /** No editable fields yet, so never dirty (E16-T05 tracks its form here). */
  readonly dirty = signal(false).asReadonly();

  protected readonly stateKind = computed(() => {
    const c = this.coding();
    return typeof c === 'string' ? c : 'ready';
  });
  protected readonly rows = computed(() => {
    const c = this.coding();
    const values = typeof c === 'string' ? {} : c.values;
    return (this.fields() ?? [])
      .filter((f) => f.storage === 'coding' && !f.isHidden)
      .map((f) => ({
        queryName: f.queryName,
        label: f.displayName,
        value: formatValue(values[f.queryName] ?? null),
      }));
  });

  save(): Promise<boolean> {
    return Promise.resolve(true);
  }

  discard(): void {
    // Nothing to discard until the coding form exists (E16-T05).
  }
}

function formatValue(value: CodingValue): string {
  if (value === null || value === '') return '';
  if (Array.isArray(value)) return value.join(', ');
  if (typeof value === 'boolean') return value ? 'Yes' : 'No';
  return String(value);
}

/** Related Items (E16-T10 lists Family, Duplicates and Email Thread members here). */
@Component({
  selector: 'opp-review-related',
  imports: [Icon],
  template: `<p class="pane__note">
      <opp-icon name="info" />
      <span
        >Family, Duplicates and Email Thread members of this document are listed here soon.</span
      >
    </p>
    <p class="related__family">{{ family() }}</p>`,
  styleUrl: './review-regions.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { class: 'pane__content' },
})
export class ReviewRelated {
  readonly hit = input.required<SearchHit>();

  protected readonly family = computed(() => {
    const hit = this.hit();
    if (hit.parentDocumentId) return `${hit.controlNumber} is an attachment in a family.`;
    if (hit.familyId && hit.familyId !== hit.documentId)
      return `${hit.controlNumber} belongs to a family.`;
    return `${hit.controlNumber} is a parent or stand-alone document.`;
  });
}
