import {
  ChangeDetectionStrategy,
  Component,
  computed,
  effect,
  inject,
  input,
  output,
  signal,
  untracked,
} from '@angular/core';
import { Badge, Button, Icon, LoadingState } from '../../ui';
import {
  HttpWhereProducedApi,
  ProducedItem,
  ProducedRelation,
  WhereProduced,
  WhereProducedApi,
} from './where-produced-api';
import { DisplayFormat } from '../../core/i18n';

/** A row as shown. */
interface ProducedRow {
  readonly item: ProducedItem;
  readonly key: string;
  readonly production: string;
  readonly status: string;
  readonly volumes: string;
  readonly relation: string;
  readonly bates: string;
  readonly attachment: string;
  readonly isSelf: boolean;
}

const RELATION_LABELS: Record<ProducedRelation, string> = {
  self: 'This document',
  family: 'Family member',
  duplicate: 'Duplicate',
};

const OUTPUT_LABELS: Record<ProducedItem['output'], string> = {
  image: 'Images',
  native: 'Native',
  placeholder: 'Placeholder',
};

/**
 * The "Produced in" tab of Related Items (E13-T04, familiarity guide §3.2): every finalized or voided production,
 * volume and Bates range in which the displayed document, its family members and its duplicates were produced, for
 * clawback. Read-only; the rows download as CSV through the protected-content gateway (audited). Documents the
 * reviewer may not see are omitted entirely (Q-52).
 */
@Component({
  selector: 'opp-produced-in',
  imports: [Badge, Button, Icon, LoadingState],
  templateUrl: './produced-in.html',
  styleUrl: './produced-in.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
  providers: [{ provide: WhereProducedApi, useClass: HttpWhereProducedApi }],
  host: { class: 'produced' },
})
export class ProducedIn {
  readonly documentId = input.required<string>();
  /** The Control Number of the displayed document (table caption). */
  readonly controlNumber = input('');
  /** Show a related document in the viewer (the review cursor stays). */
  readonly openDocument = output<{ documentId: string; controlNumber: string }>();

  private readonly api = inject(WhereProducedApi);
  private readonly fmt = inject(DisplayFormat);

  protected readonly state = signal<WhereProduced | 'loading' | 'unavailable'>('loading');
  private seq = 0;

  private readonly format = this.fmt.formatter;

  protected readonly exportUrl = computed(() => this.api.exportUrl(this.documentId()));

  protected readonly rows = computed<ProducedRow[]>(() => {
    const state = this.state();
    if (typeof state === 'string') return [];
    const f = this.format();
    return state.items.map((item) => ({
      item,
      key: `${item.productionId}/${item.documentId}`,
      production: `${item.productionName} (v${item.productionVersion})`,
      status: item.voided
        ? `Voided ${item.voidedAt ? f.dateTime(item.voidedAt) : ''}`.trim()
        : `Produced ${f.dateTime(item.producedAt)}`,
      volumes: item.volumes.length > 0 ? item.volumes.join(', ') : 'No volume written yet',
      relation: item.relations.map((r) => RELATION_LABELS[r]).join(', '),
      bates:
        item.prodBegBates === item.prodEndBates
          ? item.prodBegBates
          : `${item.prodBegBates} – ${item.prodEndBates}`,
      attachment:
        item.prodBegAttach === item.prodEndAttach
          ? item.prodBegAttach
          : `${item.prodBegAttach} – ${item.prodEndAttach}`,
      isSelf: item.relations.includes('self'),
    }));
  });

  protected readonly summary = computed(() => {
    const state = this.state();
    if (typeof state === 'string') return '';
    const productions = new Set(state.items.map((i) => i.productionId)).size;
    const n = this.fmt.formatter();
    return productions === 1
      ? `Produced in 1 production (${n.number(state.items.length)} Bates ranges).`
      : `Produced in ${n.number(productions)} productions (${n.number(state.items.length)} Bates ranges).`;
  });

  protected readonly truncated = computed(() => {
    const state = this.state();
    return typeof state !== 'string' && state.truncated;
  });

  protected readonly outputLabels = OUTPUT_LABELS;

  constructor() {
    effect(() => {
      const id = this.documentId();
      untracked(() => this.load(id));
    });
  }

  protected open(row: ProducedRow): void {
    this.openDocument.emit({
      documentId: row.item.documentId,
      controlNumber: row.item.controlNumber,
    });
  }

  private load(documentId: string): void {
    const seq = ++this.seq;
    this.state.set('loading');
    this.api.lookup(documentId).then(
      (result) => {
        if (seq === this.seq) this.state.set(result);
      },
      () => {
        if (seq === this.seq) this.state.set('unavailable');
      },
    );
  }
}
