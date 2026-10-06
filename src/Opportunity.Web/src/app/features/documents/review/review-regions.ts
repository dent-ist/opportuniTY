import type { Signal } from '@angular/core';

// Regions of Review mode (familiarity guide §3.2): the viewer is viewer/document-viewer.ts (E16-T04), the coding pane
// coding/coding-pane.ts (E16-T05) and Related Items related/related-items.ts (E16-T10). Their regions, landmarks,
// sizes and keyboard commands belong to the review workspace (E16-T03).

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
