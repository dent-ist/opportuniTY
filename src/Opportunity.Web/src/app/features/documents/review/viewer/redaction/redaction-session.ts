import { Injectable, computed, inject, signal } from '@angular/core';
import { toApiError } from '../../../../../core/api/problem-details';
import { SessionService } from '../../../../../core/session/session';
import { PERMISSIONS } from '../../../../../core/workspace/sections';
import { WorkspaceContext } from '../../../../../core/workspace/workspace-context';
import {
  type DocumentRedactions,
  type NormalizedRect,
  type Redaction,
  RedactionApi,
  type RedactionChange,
  RedactionConflictError,
  type RedactionReason,
  type RedactionSetOption,
  type RedactionType,
} from './redaction-api';
import { sameRect } from './redaction-geometry';

/** A redaction as the editor shows it: saved, or changed here and not saved yet. */
export interface EditableRedaction extends Redaction {
  /** Changed locally (moved, resized, added) and not yet saved, or refused by a conflict. */
  readonly unsaved: boolean;
  /** The reviewer may move, resize or relabel it (own, or Redaction.Remove for anyone's). */
  readonly editable: boolean;
}

/** Why a save did not go through, as the panel shows it. */
export interface RedactionNotice {
  readonly kind: 'conflict' | 'error';
  readonly message: string;
}

/** Remembered Redaction Set choice (per browser; the set itself is workspace data). */
const SET_KEY = 'opp.redaction.set';
const KEYBOARD_COMMIT_MS = 700;

/**
 * Redaction mode's state for one document (E11-T04): the Redaction Sets and reasons, the document's redactions in
 * the chosen set with their version, the selection, and saves. Every change is saved as one new version with
 * If-Match, one at a time; keyboard moves are saved after a short pause. When another user saved first the API
 * answers 412: the refused change stays on screen marked "not saved", nothing else is sent, and the reviewer is
 * asked to refresh — redactions are never overwritten or silently dropped. Undo sends the inverse change.
 */
@Injectable()
export class RedactionSession {
  private readonly api = inject(RedactionApi);
  private readonly workspace = inject(WorkspaceContext, { optional: true });
  private readonly session = inject(SessionService, { optional: true });

  readonly canApply = this.workspace?.can(PERMISSIONS.redactionApply) ?? false;
  readonly canRemove = this.workspace?.can(PERMISSIONS.redactionRemove) ?? false;
  private readonly userId = computed(() => this.session?.principal()?.userId ?? null);

  readonly status = signal<'idle' | 'loading' | 'ready' | 'error'>('idle');
  readonly loadError = signal<string | null>(null);
  readonly sets = signal<readonly RedactionSetOption[]>([]);
  readonly reasons = signal<readonly RedactionReason[]>([]);
  readonly setId = signal<string | null>(null);
  readonly saved = signal<DocumentRedactions | null>(null);
  /** Local geometry and attributes not yet saved, by redaction id (added ones included). */
  private readonly local = signal<ReadonlyMap<string, Redaction>>(new Map());
  readonly selectedId = signal<string | null>(null);
  readonly saving = signal(false);
  readonly notice = signal<RedactionNotice | null>(null);
  /** The reviewer's choices for new redactions. */
  readonly type = signal<RedactionType>('black');
  readonly reasonCode = signal<string | null>(null);

  private documentId: string | null = null;
  private seq = 0;
  private queue: Promise<void> = Promise.resolve();
  private readonly undoStack: RedactionChange[][] = [];
  readonly canUndo = signal(false);
  private commitTimer: ReturnType<typeof setTimeout> | null = null;
  private pendingCommit: string | null = null;

  readonly activeReasons = computed(() => this.reasons().filter((r) => r.active));

  /** The redactions to show: saved ones with local changes applied, plus local additions. */
  readonly redactions = computed<readonly EditableRedaction[]>(() => {
    const saved = this.saved()?.redactions ?? [];
    const local = this.local();
    const result: EditableRedaction[] = saved.map((r) => {
      const changed = local.get(r.id);
      return {
        ...(changed ?? r),
        unsaved: !!changed,
        editable: this.mayEdit(r),
      };
    });
    for (const [id, r] of local) {
      if (!saved.some((s) => s.id === id)) result.push({ ...r, unsaved: true, editable: true });
    }
    return result.sort(
      (a, b) => a.pageNumber - b.pageNumber || a.rect.y - b.rect.y || a.rect.x - b.rect.x,
    );
  });

  readonly selected = computed(
    () => this.redactions().find((r) => r.id === this.selectedId()) ?? null,
  );

  /** Drawing is possible: the reviewer may redact, the set is active, images are rendered, no conflict pending. */
  readonly canDraw = computed(() => {
    const saved = this.saved();
    return (
      this.canApply &&
      !!saved &&
      saved.redactable &&
      !saved.setRetired &&
      this.notice()?.kind !== 'conflict'
    );
  });

  /** Why redactions cannot be drawn now, in the reviewer's words; null when they can. */
  readonly unavailable = computed(() => {
    const saved = this.saved();
    if (!saved) return null;
    if (!saved.redactable) return saved.unavailableReason ?? 'Redaction requires rendered images';
    if (saved.setRetired) return 'This Redaction Set is retired and read-only.';
    if (!this.canApply) return 'You can view redactions but not change them.';
    return null;
  });

  /** Loads the sets and reasons (once) and the document's redactions in the chosen set. */
  async open(documentId: string): Promise<void> {
    this.flushCommit();
    this.documentId = documentId;
    const seq = ++this.seq;
    this.reset();
    this.status.set('loading');
    try {
      if (!this.sets().length) {
        const [sets, reasons] = await Promise.all([this.api.sets(), this.api.reasons()]);
        if (seq !== this.seq) return;
        this.sets.set(sets);
        this.reasons.set(reasons);
        const remembered = readStored(SET_KEY);
        const usable = sets.filter((s) => !s.retired);
        this.setId.set((sets.find((s) => s.id === remembered) ?? usable[0] ?? sets[0])?.id ?? null);
        this.reasonCode.set(reasons.find((r) => r.active)?.code ?? null);
      }
      const setId = this.setId();
      if (!setId) {
        this.loadError.set('This workspace has no Redaction Set.');
        this.status.set('error');
        return;
      }
      const data = await this.api.get(documentId, setId);
      if (seq !== this.seq) return;
      this.saved.set(data);
      this.status.set('ready');
    } catch (e) {
      if (seq !== this.seq) return;
      this.loadError.set(toApiError(e).problem.detail ?? 'The redactions could not be loaded.');
      this.status.set('error');
    }
  }

  /** Switches the Redaction Set (remembered for next time) and loads its redactions. */
  async chooseSet(setId: string): Promise<void> {
    if (setId === this.setId() || !this.documentId) return;
    this.setId.set(setId);
    writeStored(SET_KEY, setId);
    await this.open(this.documentId);
  }

  /** Discards local state and reloads what is saved (after a conflict, or on request). */
  async refresh(): Promise<void> {
    if (this.documentId) await this.open(this.documentId);
  }

  close(): void {
    this.flushCommit();
    this.seq++;
    this.documentId = null;
    this.reset();
    this.status.set('idle');
  }

  select(id: string | null): void {
    if (this.pendingCommit && this.pendingCommit !== id) this.flushCommit();
    this.selectedId.set(id);
  }

  /** Adds a redaction with the current type and reason; saved at once. Returns its id. */
  add(pageNumber: number, rect: NormalizedRect): string | null {
    const reasonCode = this.reasonCode();
    const reason = this.reasons().find((r) => r.code === reasonCode);
    if (!this.canDraw() || !reason) return null;
    const id = crypto.randomUUID();
    const now = new Date().toISOString();
    const me = { userId: this.userId() ?? '', displayName: 'You' };
    const redaction: Redaction = {
      id,
      pageNumber,
      onActivePageSet: true,
      rect,
      type: this.type(),
      reasonCode: reason.code,
      reasonName: reason.name,
      reasonCategory: reason.category,
      note: null,
      createdBy: me,
      createdAt: now,
      modifiedBy: me,
      modifiedAt: now,
    };
    this.setLocal(id, redaction);
    this.selectedId.set(id);
    this.enqueue(
      [
        {
          operation: 'add',
          redactionId: id,
          pageNumber,
          rect,
          type: redaction.type,
          reasonCode: reason.code,
        },
      ],
      [{ operation: 'remove', redactionId: id }],
      [id],
    );
    return id;
  }

  /** Moves or resizes locally (pointer drag or keyboard); `commit` or the keyboard pause saves it. */
  reshape(id: string, rect: NormalizedRect, viaKeyboard = false): void {
    const current = this.redactions().find((r) => r.id === id);
    if (!current?.editable || !this.canDraw()) return;
    this.setLocal(id, { ...current, rect });
    if (viaKeyboard) {
      if (this.commitTimer) clearTimeout(this.commitTimer);
      this.pendingCommit = id;
      this.commitTimer = setTimeout(() => this.flushCommit(), KEYBOARD_COMMIT_MS);
    }
  }

  /** Saves a local move or resize of `id` (when it changed). */
  commit(id: string): void {
    if (this.pendingCommit === id) {
      if (this.commitTimer) clearTimeout(this.commitTimer);
      this.commitTimer = null;
      this.pendingCommit = null;
    }
    const local = this.local().get(id);
    const saved = this.saved()?.redactions.find((r) => r.id === id);
    if (!local || this.notice()?.kind === 'conflict') return;
    if (!saved) {
      // Still being added: save the move once the add is through.
      void this.queue.then(() => this.commit(id));
      return;
    }
    if (sameRect(local.rect, saved.rect)) {
      this.dropLocal(id);
      return;
    }
    this.enqueue(
      [{ operation: 'modify', redactionId: id, rect: local.rect }],
      [{ operation: 'modify', redactionId: id, rect: saved.rect }],
      [id],
    );
  }

  /** Changes the type and/or reason of a saved redaction. */
  relabel(id: string, change: { type?: RedactionType; reasonCode?: string }): void {
    const current = this.redactions().find((r) => r.id === id);
    if (!current?.editable || !this.canDraw()) return;
    const type = change.type ?? current.type;
    const reasonCode = change.reasonCode ?? current.reasonCode;
    if (type === current.type && reasonCode === current.reasonCode) return;
    const reason = this.reasons().find((r) => r.code === reasonCode);
    this.setLocal(id, {
      ...current,
      type,
      reasonCode,
      reasonName: reason?.name ?? reasonCode,
      reasonCategory: reason?.category ?? current.reasonCategory,
    });
    this.enqueue(
      [{ operation: 'modify', redactionId: id, type, reasonCode }],
      [
        {
          operation: 'modify',
          redactionId: id,
          type: current.type,
          reasonCode: current.reasonCode,
        },
      ],
      [id],
    );
  }

  /** Removes a redaction (Redaction.Remove); it stays in the history. */
  remove(id: string): boolean {
    const current = this.saved()?.redactions.find((r) => r.id === id);
    if (!current || !this.canRemove || !this.canDraw()) return false;
    if (this.selectedId() === id) this.selectedId.set(null);
    this.enqueue(
      [{ operation: 'remove', redactionId: id }],
      [
        {
          operation: 'add',
          redactionId: crypto.randomUUID(),
          pageNumber: current.pageNumber,
          rect: current.rect,
          type: current.type,
          reasonCode: current.reasonCode,
        },
      ],
      [],
    );
    return true;
  }

  /** Reverts the reviewer's last saved change of this session (as a new version). */
  undo(): boolean {
    this.flushCommit();
    const inverse = this.undoStack.pop();
    this.canUndo.set(this.undoStack.length > 0);
    if (!inverse || !this.canDraw()) return false;
    // Removing as an undo of an add still needs Redaction.Remove on the server; the API refuses it otherwise.
    this.enqueue(inverse, null, []);
    return true;
  }

  /** Saves a pending keyboard move now. */
  flushCommit(): void {
    if (this.commitTimer) clearTimeout(this.commitTimer);
    this.commitTimer = null;
    const id = this.pendingCommit;
    this.pendingCommit = null;
    if (id) this.commit(id);
  }

  private mayEdit(r: Redaction): boolean {
    if (!this.canApply) return false;
    return this.canRemove || r.createdBy.userId === this.userId();
  }

  /** Sends one save after the previous ones; `inverse` goes on the undo stack when it succeeds. */
  private enqueue(
    changes: readonly RedactionChange[],
    inverse: readonly RedactionChange[] | null,
    localIds: readonly string[],
  ): void {
    const seq = this.seq;
    const documentId = this.documentId;
    const setId = this.setId();
    if (!documentId || !setId) return;
    this.saving.set(true);
    this.queue = this.queue.then(async () => {
      const saved = this.saved();
      if (seq !== this.seq || !saved || this.notice()?.kind === 'conflict') return;
      try {
        const next = await this.api.save(documentId, setId, saved.version, changes);
        if (seq !== this.seq) return;
        this.saved.set(next);
        for (const id of localIds) this.settle(id, next);
        if (inverse) {
          this.undoStack.push([...inverse]);
          this.canUndo.set(true);
        }
        if (this.notice()?.kind === 'error') this.notice.set(null);
      } catch (e) {
        if (seq !== this.seq) return;
        if (e instanceof RedactionConflictError) {
          // Keep the refused change on screen as "not saved"; never retry it over the other user's version.
          this.notice.set({
            kind: 'conflict',
            message:
              `Another user changed the redactions of this document${e.current.lastChange ? ` (${e.current.lastChange.displayName})` : ''}. ` +
              'Your last change was not saved. Refresh to see the current redactions, then make your change again.',
          });
        } else {
          for (const id of localIds) this.dropLocal(id);
          const error = toApiError(e);
          this.notice.set({
            kind: 'error',
            message: error.problem.detail ?? 'The change could not be saved.',
          });
        }
      } finally {
        if (seq === this.seq) this.saving.set(false);
      }
    });
  }

  /** Drops the local copy once the server has it as shown (a move made while saving stays local). */
  private settle(id: string, next: DocumentRedactions): void {
    const local = this.local().get(id);
    const server = next.redactions.find((r) => r.id === id);
    if (
      !local ||
      !server ||
      (sameRect(local.rect, server.rect) &&
        local.type === server.type &&
        local.reasonCode === server.reasonCode)
    ) {
      this.dropLocal(id);
    }
  }

  private setLocal(id: string, redaction: Redaction): void {
    this.local.update((m) => new Map(m).set(id, redaction));
  }

  private dropLocal(id: string): void {
    if (!this.local().has(id)) return;
    this.local.update((m) => {
      const next = new Map(m);
      next.delete(id);
      return next;
    });
  }

  private reset(): void {
    this.saved.set(null);
    this.local.set(new Map());
    this.selectedId.set(null);
    this.notice.set(null);
    this.loadError.set(null);
    this.saving.set(false);
    this.undoStack.length = 0;
    this.canUndo.set(false);
    this.queue = Promise.resolve();
  }
}

function readStored(key: string): string | null {
  try {
    return globalThis.localStorage?.getItem(key) ?? null;
  } catch {
    return null;
  }
}

function writeStored(key: string, value: string): void {
  try {
    globalThis.localStorage?.setItem(key, value);
  } catch {
    // A per-browser convenience only.
  }
}
