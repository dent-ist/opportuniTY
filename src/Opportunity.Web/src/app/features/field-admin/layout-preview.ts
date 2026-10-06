import { ChangeDetectionStrategy, Component, Injectable, input } from '@angular/core';
import { ReviewCoding } from '../documents/review/coding/coding-pane';
import { CodingApi, CodingLayout, DocumentCoding } from '../documents/review/review-ports';

/** Feeds the real coding pane an empty, never-saved document for the layout preview. */
@Injectable()
export class PreviewCodingApi extends CodingApi {
  layouts(): Promise<readonly CodingLayout[]> {
    return Promise.resolve([]);
  }

  get(documentId: string): Promise<DocumentCoding> {
    return Promise.resolve({
      documentId,
      version: '0',
      values: {},
      indexState: 'searchable',
      fields: {},
      lastEditor: null,
    });
  }

  save(): Promise<DocumentCoding> {
    return Promise.reject(new Error('The layout preview never saves.'));
  }
}

/**
 * Live preview of a coding layout draft (E04-T06 "Preview renders exactly what the coding panel renders"): the coding
 * pane of Review mode itself, given the draft, on an empty document. Values can be tried out, so conditional fields
 * appear as reviewers will see them; nothing is saved.
 */
@Component({
  selector: 'opp-layout-preview',
  imports: [ReviewCoding],
  template: `<opp-review-coding
    documentId="layout-preview"
    [canCode]="true"
    [previewLayout]="layout()"
  />`,
  changeDetection: ChangeDetectionStrategy.OnPush,
  providers: [{ provide: CodingApi, useClass: PreviewCodingApi }],
  host: { class: 'fa__preview-frame' },
})
export class LayoutPreview {
  readonly layout = input.required<CodingLayout>();
}
