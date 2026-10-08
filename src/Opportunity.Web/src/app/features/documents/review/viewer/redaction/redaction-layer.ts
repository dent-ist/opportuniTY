import {
  ChangeDetectionStrategy,
  Component,
  ElementRef,
  Injector,
  afterNextRender,
  computed,
  inject,
  input,
  signal,
} from '@angular/core';
import { Announcer } from '../../../../../ui';
import type { NormalizedRect } from './redaction-api';
import {
  type FrameRect,
  type ResizeEdges,
  frameFromPoints,
  fromFrame,
  moveFrame,
  normalizeRotation,
  resizeFrame,
  toFrame,
} from './redaction-geometry';
import { type EditableRedaction, RedactionSession } from './redaction-session';

/** Keyboard step as a fraction of the displayed page; Alt gives fine steps. */
const STEP = 0.01;
const FINE_STEP = 0.002;
/** A drag shorter than this (CSS pixels) is a click, not a new box. */
const MIN_DRAG_PX = 4;

type Gesture =
  | { kind: 'draw'; startX: number; startY: number; pointerId: number }
  | {
      kind: 'move';
      id: string;
      origin: FrameRect;
      startX: number;
      startY: number;
      pointerId: number;
    }
  | {
      kind: 'resize';
      id: string;
      origin: FrameRect;
      edges: ResizeEdges;
      startX: number;
      startY: number;
      pointerId: number;
    };

const HANDLES: readonly { name: string; edges: ResizeEdges; label: string }[] = [
  { name: 'nw', edges: { left: true, top: true }, label: 'top left' },
  { name: 'ne', edges: { right: true, top: true }, label: 'top right' },
  { name: 'sw', edges: { left: true, bottom: true }, label: 'bottom left' },
  { name: 'se', edges: { right: true, bottom: true }, label: 'bottom right' },
];

/**
 * The redaction overlay of one displayed page (E11-T04, ADR-012 §6): the page's redactions as outlined,
 * semi-transparent boxes over the review image (opaque, as produced, in production preview). The review image is
 * never modified. Drawing, moving and resizing happen on the displayed (possibly rotated) page and are stored in
 * normalized rotation-0 coordinates, so they apply to every rendering of the page.
 *
 * - Mouse: drag on the page draws a box; drag a box to move it, a corner handle to resize it.
 * - Keyboard (WCAG 2.5.7): the boxes are one tab stop (the selected box); arrow keys move it (Alt: finer),
 *   Shift+arrow keys resize it, Delete removes it, Ctrl/⌘+Z undoes the last change, Esc ends the selection.
 *   "New box" (Alt+Shift+D, draw) adds a box from the keyboard.
 */
@Component({
  selector: 'opp-redaction-layer',
  templateUrl: './redaction-layer.html',
  styleUrl: './redaction-layer.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: {
    class: 'redaction-layer',
    role: 'group',
    tabindex: '-1',
    '[attr.aria-label]': '"Redactions on page " + pageNumber()',
    '[class.redaction-layer--draw]': 'session.canDraw()',
    '[class.redaction-layer--preview]': 'preview()',
    '(pointerdown)': 'onPointerDown($event)',
    '(pointermove)': 'onPointerMove($event)',
    '(pointerup)': 'onPointerUp($event)',
    '(pointercancel)': 'cancelGesture()',
  },
})
export class RedactionLayer {
  readonly pageNumber = input.required<number>();
  /** The page's display rotation in degrees (clockwise). */
  readonly rotation = input(0);
  /** The smallest box in normalized units (2 × 2 pixels at 300 DPI). */
  readonly minSize = input(1);
  /** Production preview: opaque boxes as they will be burned. */
  readonly preview = input(false);

  protected readonly session = inject(RedactionSession);
  private readonly host = inject<ElementRef<HTMLElement>>(ElementRef);
  private readonly injector = inject(Injector);
  private readonly announcer = inject(Announcer);

  protected readonly handles = HANDLES;
  private readonly turn = computed(() => normalizeRotation(this.rotation()));
  private gesture: Gesture | null = null;
  protected readonly draft = signal<FrameRect | null>(null);

  protected readonly boxes = computed(() =>
    this.session
      .redactions()
      .filter((r) => r.pageNumber === this.pageNumber())
      .map((r) => ({ redaction: r, frame: toFrame(r.rect, this.turn()) })),
  );

  /** The box that takes the tab stop: the selected one on this page, else the first. */
  protected readonly tabStop = computed(() => {
    const boxes = this.boxes();
    const selected = this.session.selectedId();
    return boxes.find((b) => b.redaction.id === selected)?.redaction.id ?? boxes[0]?.redaction.id;
  });

  protected boxLabel(r: EditableRedaction, index: number): string {
    const type = r.type === 'labelled' ? 'labelled box' : 'black box';
    const state = r.unsaved ? ', not saved' : '';
    const pageSet = r.onActivePageSet ? '' : ', drawn on earlier page images: review it';
    return `Redaction ${index + 1}: ${r.reasonName}, ${type}, by ${r.createdBy.displayName}${state}${pageSet}`;
  }

  protected boxText(r: EditableRedaction): string {
    return this.session.reasons().find((x) => x.code === r.reasonCode)?.boxLabel ?? r.reasonName;
  }

  /** Adds a box in the middle of the page and focuses it (the keyboard way to draw). */
  addFromKeyboard(frame: FrameRect): void {
    const id = this.session.add(this.pageNumber(), fromFrame(frame, this.turn(), this.minSize()));
    if (id) {
      this.focusBox(id);
      this.announcer.announce(
        'Redaction added. Arrow keys move it, Shift+arrow keys resize it, Delete removes it.',
      );
    }
  }

  // ── Pointer ────────────────────────────────────────────────────────────────────────────────────────────

  protected onPointerDown(event: PointerEvent): void {
    if (event.button !== 0) return;
    const target = event.target as HTMLElement;
    const handle = target.closest<HTMLElement>('[data-handle]');
    const box = target.closest<HTMLElement>('[data-redaction-id]');
    const [x, y] = this.point(event);
    if (box) {
      const id = box.dataset['redactionId']!;
      this.session.select(id);
      const entry = this.boxes().find((b) => b.redaction.id === id);
      if (!entry?.redaction.editable || !this.session.canDraw()) return;
      event.preventDefault();
      const edges = handle ? HANDLES.find((h) => h.name === handle.dataset['handle'])?.edges : null;
      this.gesture = edges
        ? {
            kind: 'resize',
            id,
            origin: entry.frame,
            edges,
            startX: x,
            startY: y,
            pointerId: event.pointerId,
          }
        : {
            kind: 'move',
            id,
            origin: entry.frame,
            startX: x,
            startY: y,
            pointerId: event.pointerId,
          };
    } else {
      this.session.select(null);
      if (!this.session.canDraw()) return;
      event.preventDefault();
      this.gesture = { kind: 'draw', startX: x, startY: y, pointerId: event.pointerId };
    }
    this.host.nativeElement.setPointerCapture?.(event.pointerId);
  }

  protected onPointerMove(event: PointerEvent): void {
    const gesture = this.gesture;
    if (!gesture || gesture.pointerId !== event.pointerId) return;
    const [x, y] = this.point(event);
    if (gesture.kind === 'draw') {
      this.draft.set(frameFromPoints(gesture.startX, gesture.startY, x, y));
      return;
    }
    const frame =
      gesture.kind === 'move'
        ? moveFrame(gesture.origin, x - gesture.startX, y - gesture.startY)
        : resizeFrame(
            gesture.origin,
            gesture.edges,
            x - gesture.startX,
            y - gesture.startY,
            this.minFraction(),
          );
    this.session.reshape(gesture.id, this.toRect(frame));
  }

  protected onPointerUp(event: PointerEvent): void {
    const gesture = this.gesture;
    if (!gesture || gesture.pointerId !== event.pointerId) return;
    this.gesture = null;
    this.host.nativeElement.releasePointerCapture?.(event.pointerId);
    if (gesture.kind === 'draw') {
      const draft = this.draft();
      this.draft.set(null);
      const { width, height } = this.size();
      if (!draft || draft.width * width < MIN_DRAG_PX || draft.height * height < MIN_DRAG_PX)
        return;
      const id = this.session.add(this.pageNumber(), this.toRect(draft));
      if (id) this.focusBox(id);
      return;
    }
    this.session.commit(gesture.id);
  }

  protected cancelGesture(): void {
    this.gesture = null;
    this.draft.set(null);
  }

  // ── Keyboard ───────────────────────────────────────────────────────────────────────────────────────────

  protected onBoxKey(event: KeyboardEvent, id: string): void {
    // The current state, not the rendered one: key repeats can come faster than the view updates.
    const entry = this.boxes().find((b) => b.redaction.id === id);
    if (!entry) return;
    const r = entry.redaction;
    if (
      (event.ctrlKey || event.metaKey) &&
      !event.altKey &&
      event.code === 'KeyZ' &&
      !event.shiftKey
    ) {
      event.preventDefault();
      if (this.session.undo()) this.announcer.announce('Last redaction change undone.');
      return;
    }
    if (event.ctrlKey || event.metaKey) return;
    if (event.key === 'Escape') {
      event.preventDefault();
      this.session.select(null);
      this.session.flushCommit();
      this.host.nativeElement.focus();
      return;
    }
    if (event.key === ' ' || event.key === 'Enter') {
      event.preventDefault();
      this.session.select(r.id);
      return;
    }
    if (event.key === 'Delete' || event.key === 'Backspace') {
      event.preventDefault();
      if (!r.editable || !this.session.canRemove) {
        this.announcer.announce('You cannot remove this redaction.');
        return;
      }
      const next = this.boxes().find((b) => b.redaction.id !== r.id)?.redaction.id;
      if (this.session.remove(r.id)) {
        this.announcer.announce('Redaction removed.');
        if (next) this.focusBox(next);
        else this.host.nativeElement.focus();
      }
      return;
    }
    const arrows: Record<string, [number, number]> = {
      ArrowLeft: [-1, 0],
      ArrowRight: [1, 0],
      ArrowUp: [0, -1],
      ArrowDown: [0, 1],
    };
    const direction = arrows[event.key];
    if (!direction) return;
    event.preventDefault();
    this.session.select(r.id);
    if (!r.editable || !this.session.canDraw()) {
      this.announcer.announce('This redaction cannot be changed here.');
      return;
    }
    const step = event.altKey ? FINE_STEP : STEP;
    const [dx, dy] = [direction[0] * step, direction[1] * step];
    const frame = event.shiftKey
      ? resizeFrame(entry.frame, { right: true, bottom: true }, dx, dy, this.minFraction())
      : moveFrame(entry.frame, dx, dy);
    this.session.reshape(r.id, this.toRect(frame), true);
  }

  protected onBoxBlur(id: string): void {
    // Leaving a box saves its pending keyboard move at once.
    if (this.session.selectedId() === id) this.session.flushCommit();
  }

  focusBox(id: string): void {
    afterNextRender(
      () =>
        this.host.nativeElement
          .querySelector<HTMLElement>(`[data-redaction-id="${CSS.escape(id)}"]`)
          ?.focus(),
      { injector: this.injector },
    );
  }

  // ── Geometry ───────────────────────────────────────────────────────────────────────────────────────────

  private toRect(frame: FrameRect): NormalizedRect {
    return fromFrame(frame, this.turn(), this.minSize());
  }

  private minFraction(): number {
    return this.minSize() / 1_000_000;
  }

  private size(): { width: number; height: number } {
    const rect = this.host.nativeElement.getBoundingClientRect();
    return { width: rect.width || 1, height: rect.height || 1 };
  }

  private point(event: PointerEvent): [number, number] {
    const rect = this.host.nativeElement.getBoundingClientRect();
    return [
      (event.clientX - rect.left) / (rect.width || 1),
      (event.clientY - rect.top) / (rect.height || 1),
    ];
  }
}
