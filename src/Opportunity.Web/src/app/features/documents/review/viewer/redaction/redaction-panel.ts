import {
  ChangeDetectionStrategy,
  Component,
  LOCALE_ID,
  computed,
  inject,
  model,
  output,
} from '@angular/core';
import { Button, Checkbox, Icon, Select, type SelectOption } from '../../../../../ui';
import type { RedactionType } from './redaction-api';
import { type EditableRedaction, RedactionSession } from './redaction-session';

const TYPE_OPTIONS: readonly SelectOption[] = [
  { value: 'black', label: 'Black box' },
  { value: 'labelled', label: 'Labelled box' },
];

const CATEGORY: Record<string, string> = {
  privilege: 'Privilege',
  privacy: 'Privacy',
  other: 'Other',
};

/**
 * The Redaction mode panel beside the page (E11-T04, ticket review E11-T04): the Redaction Set, the box style and
 * reason for new boxes (or of the selected box), New box / Redact full page / Remove / Undo, the production preview
 * switch, what blocks redaction ("Redaction requires rendered images", a concurrent edit), and the list of the
 * document's redactions with page, reason, style, author and time — choosing one shows its page and selects it.
 */
@Component({
  selector: 'opp-redaction-panel',
  imports: [Button, Checkbox, Icon, Select],
  templateUrl: './redaction-panel.html',
  styleUrl: './redaction-panel.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class RedactionPanel {
  protected readonly session = inject(RedactionSession);
  private readonly locale = inject(LOCALE_ID);

  /** The reviewer asked for a new box from the keyboard. */
  readonly newBox = output<void>();
  readonly fullPage = output<void>();
  /** Show this redaction: its page, selected. */
  readonly jump = output<EditableRedaction>();
  /** Production preview: boxes as they will be burned. */
  readonly preview = model(false);

  protected readonly typeOptions = TYPE_OPTIONS;
  protected readonly setOptions = computed<readonly SelectOption[]>(() =>
    this.session.sets().map((s) => ({
      value: s.id,
      label: s.retired ? `${s.name} (retired)` : s.name,
    })),
  );
  protected readonly reasonOptions = computed<readonly SelectOption[]>(() => {
    const selected = this.session.selected();
    return this.session
      .reasons()
      .filter((r) => r.active || r.code === selected?.reasonCode)
      .map((r) => ({
        value: r.code,
        label: `${r.name} (${CATEGORY[r.category] ?? r.category})`,
        disabled: !r.active,
      }));
  });

  /** The style and reason shown: the selected box's, else the ones for new boxes. */
  protected readonly shownType = computed(
    () => this.session.selected()?.type ?? this.session.type(),
  );
  protected readonly shownReason = computed(
    () => this.session.selected()?.reasonCode ?? this.session.reasonCode() ?? '',
  );
  protected readonly selectedEditable = computed(() => {
    const s = this.session.selected();
    return !!s && s.editable && this.session.canDraw();
  });

  protected setType(value: string): void {
    const type = value as RedactionType;
    this.session.type.set(type);
    const selected = this.session.selected();
    if (selected && this.selectedEditable()) this.session.relabel(selected.id, { type });
  }

  protected setReason(code: string): void {
    if (!code) return;
    this.session.reasonCode.set(code);
    const selected = this.session.selected();
    if (selected && this.selectedEditable())
      this.session.relabel(selected.id, { reasonCode: code });
  }

  protected removeSelected(): void {
    const selected = this.session.selected();
    if (selected) this.session.remove(selected.id);
  }

  protected typeLabel(r: EditableRedaction): string {
    return r.type === 'labelled' ? 'Labelled box' : 'Black box';
  }

  protected when(iso: string): string {
    const date = new Date(iso);
    if (Number.isNaN(date.getTime())) return '';
    return new Intl.DateTimeFormat(this.locale, { dateStyle: 'medium', timeStyle: 'short' }).format(
      date,
    );
  }
}
