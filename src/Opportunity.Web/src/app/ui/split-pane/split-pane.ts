import { _IdGenerator } from '@angular/cdk/a11y';
import { Directionality } from '@angular/cdk/bidi';
import {
  ChangeDetectionStrategy,
  Component,
  ElementRef,
  booleanAttribute,
  computed,
  inject,
  input,
  model,
  numberAttribute,
  signal,
} from '@angular/core';
import { PreferenceStorage } from '../../core/preferences/preference-storage';

interface StoredLayout {
  size: number;
  collapsed: boolean;
}

/**
 * Two resizable panes with a WAI-ARIA window splitter between them. Nest split panes to build the
 * review layout (ADR-018 §9): browser | (list / viewer) | coding pane. Sizes are percentages of the
 * container and persist per `storageKey` through `PreferenceStorage` (saved to the user profile, E15-T03).
 *
 * Keyboard on the splitter: Arrow keys move it by 2 % (Shift: 10 %), Home/End jump to min/max, Enter
 * collapses/restores the sized pane when `collapsible`. Dragging is optional (WCAG 2.5.7); double-click
 * restores the default size.
 */
@Component({
  selector: 'opp-split-pane',
  templateUrl: './split-pane.html',
  styleUrl: './split-pane.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: {
    '[class]': `'split split--' + direction() + ' split--sized-' + sizedPane()`,
    '[class.split--dragging]': 'dragging()',
    '[style.--opp-split-size]': 'effectiveSize() + "%"',
  },
})
export class SplitPane {
  /** `row`: panes side by side (vertical splitter bar); `column`: stacked (horizontal bar). */
  readonly direction = input<'row' | 'column'>('row');
  /** Which pane has the explicit size; the other takes the remaining space. */
  readonly sizedPane = input<'start' | 'end'>('start');
  /** Size of the sized pane in percent. */
  readonly size = model(30);
  readonly min = input(10, { transform: numberAttribute });
  readonly max = input(80, { transform: numberAttribute });
  readonly collapsible = input(false, { transform: booleanAttribute });
  /** Accessible name of the splitter, e.g. "Resize coding pane". */
  readonly label = input.required<string>();
  /** Persist size and collapsed state under this key; omit for no persistence. */
  readonly storageKey = input<string>();

  protected readonly collapsed = signal(false);
  protected readonly dragging = signal(false);
  protected readonly sizedId = inject(_IdGenerator).getId('opp-split-pane-');
  protected readonly effectiveSize = computed(() => (this.collapsed() ? 0 : this.size()));
  protected readonly ariaOrientation = computed(() =>
    this.direction() === 'row' ? 'vertical' : 'horizontal',
  );

  private readonly host = inject<ElementRef<HTMLElement>>(ElementRef);
  private readonly dir = inject(Directionality, { optional: true });
  private readonly storage = inject(PreferenceStorage);
  private defaultSize?: number;

  ngOnInit(): void {
    this.defaultSize = this.size();
    const key = this.storageKey();
    const stored = key ? this.storage.read<StoredLayout>(`pane.${key}`) : undefined;
    if (stored && Number.isFinite(stored.size)) {
      this.size.set(this.clamp(stored.size));
      this.collapsed.set(this.collapsible() && stored.collapsed === true);
    }
  }

  protected onKeydown(event: KeyboardEvent): void {
    const step = event.shiftKey ? 10 : 2;
    const row = this.direction() === 'row';
    const rtl = row && this.dir?.value === 'rtl';
    let towardEnd: number | null = null;
    switch (event.key) {
      case 'ArrowLeft':
        towardEnd = row ? (rtl ? step : -step) : null;
        break;
      case 'ArrowRight':
        towardEnd = row ? (rtl ? -step : step) : null;
        break;
      case 'ArrowUp':
        towardEnd = row ? null : -step;
        break;
      case 'ArrowDown':
        towardEnd = row ? null : step;
        break;
      case 'Home':
        this.setSize(this.min());
        break;
      case 'End':
        this.setSize(this.max());
        break;
      case 'Enter':
        if (this.collapsible()) {
          this.collapsed.update((c) => !c);
          this.persist();
        }
        break;
      default:
        return;
    }
    event.preventDefault();
    if (towardEnd !== null) {
      const current = this.effectiveSize();
      this.setSize(current + (this.sizedPane() === 'start' ? towardEnd : -towardEnd));
    }
  }

  protected onPointerDown(event: PointerEvent): void {
    if (event.button !== 0) return;
    (event.target as HTMLElement).setPointerCapture?.(event.pointerId);
    this.dragging.set(true);
    event.preventDefault();
  }

  protected onPointerMove(event: PointerEvent): void {
    if (!this.dragging()) return;
    const rect = this.host.nativeElement.getBoundingClientRect();
    const row = this.direction() === 'row';
    const extent = row ? rect.width : rect.height;
    if (extent <= 0) return;
    let fromStart = row ? event.clientX - rect.left : event.clientY - rect.top;
    if (row && this.dir?.value === 'rtl') fromStart = rect.width - fromStart;
    const percent = (fromStart / extent) * 100;
    this.setSize(this.sizedPane() === 'start' ? percent : 100 - percent, false);
  }

  protected onPointerUp(event: PointerEvent): void {
    if (!this.dragging()) return;
    (event.target as HTMLElement).releasePointerCapture?.(event.pointerId);
    this.dragging.set(false);
    this.persist();
  }

  protected resetSize(): void {
    if (this.defaultSize !== undefined) this.setSize(this.defaultSize);
  }

  private setSize(size: number, persist = true): void {
    this.collapsed.set(false);
    this.size.set(Math.round(this.clamp(size) * 10) / 10);
    if (persist) this.persist();
  }

  private clamp(size: number): number {
    return Math.min(this.max(), Math.max(this.min(), size));
  }

  private persist(): void {
    const key = this.storageKey();
    if (key) this.storage.write(`pane.${key}`, { size: this.size(), collapsed: this.collapsed() });
  }
}
