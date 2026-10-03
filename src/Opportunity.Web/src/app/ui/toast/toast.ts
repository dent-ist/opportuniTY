import { ChangeDetectionStrategy, Component, Injectable, inject, signal } from '@angular/core';
import { Button, IconButton } from '../button/button';
import { Icon, IconName } from '../icon/icon';
import { Announcer } from './announcer';

export type ToastTone = 'info' | 'success' | 'warning' | 'error';

export interface ToastOptions {
  tone?: ToastTone;
  /** Optional single action, e.g. "View job". */
  action?: { label: string; run: () => void };
  /** Auto-dismiss delay; errors never auto-dismiss. Default 6 s. */
  durationMs?: number;
}

export interface Toast {
  id: number;
  message: string;
  tone: ToastTone;
  action?: { label: string; run: () => void };
  durationMs: number | null;
}

/**
 * Transient, non-modal notifications. Every toast is also announced (errors assertively). Toasts with an
 * action or an error persist until dismissed; others pause their timer while hovered or focused
 * (WCAG 2.2.1) and never take focus.
 */
@Injectable({ providedIn: 'root' })
export class ToastService {
  private readonly announcer = inject(Announcer);
  private readonly _toasts = signal<readonly Toast[]>([]);
  private readonly timers = new Map<number, ReturnType<typeof setTimeout>>();
  private nextId = 1;

  readonly toasts = this._toasts.asReadonly();

  show(message: string, options: ToastOptions = {}): number {
    const tone = options.tone ?? 'info';
    const persistent = tone === 'error' || !!options.action;
    const toast: Toast = {
      id: this.nextId++,
      message,
      tone,
      action: options.action,
      durationMs: persistent ? null : (options.durationMs ?? 6000),
    };
    this._toasts.update((list) => [...list.slice(-4), toast]);
    this.announcer.announce(message, { politeness: tone === 'error' ? 'assertive' : 'polite' });
    this.resume(toast.id);
    return toast.id;
  }

  dismiss(id: number): void {
    this.pause(id);
    this._toasts.update((list) => list.filter((t) => t.id !== id));
  }

  pause(id: number): void {
    clearTimeout(this.timers.get(id));
    this.timers.delete(id);
  }

  resume(id: number): void {
    const toast = this._toasts().find((t) => t.id === id);
    if (!toast?.durationMs || this.timers.has(id)) return;
    this.timers.set(
      id,
      setTimeout(() => this.dismiss(id), toast.durationMs),
    );
  }
}

const ICONS: Record<ToastTone, IconName> = {
  info: 'info',
  success: 'success',
  warning: 'warning',
  error: 'error',
};

/**
 * Renders the toast stack; place once in the app shell. A labelled region (not a live region: the
 * Announcer speaks the message once, so screen readers do not hear it twice).
 */
@Component({
  selector: 'opp-toast-region',
  imports: [Icon, Button, IconButton],
  templateUrl: './toast-region.html',
  styleUrl: './toast.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class ToastRegion {
  protected readonly service = inject(ToastService);
  protected readonly icons = ICONS;

  protected run(toast: Toast): void {
    toast.action?.run();
    this.service.dismiss(toast.id);
  }
}
