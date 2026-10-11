import { ChangeDetectionStrategy, Component, computed, inject, input } from '@angular/core';
import type { DocumentResource, SearchHit } from '../../../../core/api/generated/models';
import { UiPreferences } from '../../../../core/preferences/ui-preferences';
import { Announcer, Button, Icon } from '../../../../ui';
import { DocumentContentApi } from '../review-ports';
import { formatterFor } from '../../../../core/i18n';

/**
 * Native mode (E16-T04): natives are never rendered in the browser in the MVP (Q-36), so this is a file card
 * (name, type, size, hashes) and **Download native** for roles with `Document.DownloadNative` (Q-18, off for
 * Reviewers by default). The download goes through the gateway, which audits it and sends an attachment; there
 * is no lasting link on the page. A native the import's malware screening quarantined (E08-T09) says so and
 * downloads only with `Document.ViewQuarantined` as well (the gateway enforces and audits it).
 */
@Component({
  selector: 'opp-viewer-native',
  imports: [Button, Icon],
  template: `<section
    class="native"
    aria-labelledby="viewer-native-name"
    [attr.data-viewer-content]="metadata().documentId"
  >
    <opp-icon class="native__icon" name="file" />
    <div class="native__body">
      <h3 id="viewer-native-name" class="native__name">{{ name() }}</h3>
      <dl class="native__facts">
        @for (fact of facts(); track fact.label) {
          <div>
            <dt>{{ fact.label }}</dt>
            <dd>{{ fact.value }}</dd>
          </div>
        }
      </dl>
      @if (quarantined()) {
        <p class="native__quarantine" role="note">
          <opp-icon name="warning" /><span
            ><strong>Content quarantined — not viewable.</strong> The import's malware screening
            flagged this file, so it is never displayed or turned into images.</span
          >
        </p>
      } @else {
        <p class="native__note">
          Native files open in their own application; they are not displayed in the browser.
        </p>
      }
      @if (canDownload() && (!quarantined() || canViewQuarantined())) {
        <button type="button" oppButton="primary" (click)="download()">
          <opp-icon name="download" />{{
            quarantined() ? 'Download quarantined native' : 'Download native'
          }}
        </button>
      } @else if (canDownload()) {
        <p class="native__note">
          <opp-icon name="lock" />Downloading a quarantined native needs the View Quarantined
          permission.
        </p>
      } @else {
        <p class="native__note">
          <opp-icon name="lock" />Your role does not allow downloading natives in this workspace.
        </p>
      }
    </div>
  </section>`,
  styleUrls: ['./viewer-shared.scss', './native-view.scss'],
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { class: 'viewer-mode' },
})
export class ViewerNative {
  readonly hit = input.required<SearchHit>();
  readonly metadata = input.required<DocumentResource>();
  /** `Document.DownloadNative` in this workspace. */
  readonly canDownload = input(false);
  /** `Document.ViewQuarantined` in this workspace (needed as well for a quarantined native). */
  readonly canViewQuarantined = input(false);

  private readonly api = inject(DocumentContentApi);
  private readonly announcer = inject(Announcer);
  private readonly prefs = inject(UiPreferences);

  protected readonly quarantined = computed(() => this.metadata().native.quarantined === true);
  protected readonly name = computed(
    () => this.hit().fileName || `${this.metadata().controlNumber} (native)`,
  );
  /** `ControlNumber.ext`, as the gateway names the attachment (short alphanumeric extensions only). */
  private readonly fileName = computed(() => {
    const { controlNumber, native } = this.metadata();
    const extension = (native.fileExtension ?? this.hit().fileExtension ?? '').replace(/^\./, '');
    return /^[A-Za-z0-9]{1,10}$/.test(extension)
      ? `${controlNumber}.${extension.toLowerCase()}`
      : controlNumber;
  });
  protected readonly facts = computed(() => {
    const hit = this.hit();
    const native = this.metadata().native;
    const extension = native.fileExtension ?? hit.fileExtension;
    const size = native.sizeBytes ?? hit.fileSize;
    const facts = [
      { label: 'File type', value: hit.fileType ?? '—' },
      { label: 'Extension', value: extension ? `.${String(extension).replace(/^\./, '')}` : '—' },
      {
        label: 'Size',
        value: size === null ? '—' : formatBytes(Number(size), this.prefs.locale()),
      },
    ];
    for (const field of this.metadata().fields) {
      if (!field.isHidden && /\b(md5|sha-?1|sha-?256|hash)\b/i.test(field.displayName)) {
        facts.push({ label: field.displayName, value: field.displayValue ?? '—' });
      }
    }
    return facts;
  });

  protected download(): void {
    this.api.downloadNative(this.metadata().documentId, this.fileName());
    this.announcer.announce(`Downloading the native of ${this.metadata().controlNumber}.`);
  }
}

export function formatBytes(bytes: number, locale: string): string {
  return formatterFor(locale).bytes(bytes);
}
