import { Injectable, inject } from '@angular/core';
import { CommandRegistry } from '../../core/commands';
import type { CommandScope } from '../../core/commands/command-catalog';
import { DialogService } from '../../ui';

/**
 * Opens the cheat sheet and the rebinding dialog. Both are loaded on first use, so the initial bundle carries
 * only the registry.
 */
@Injectable({ providedIn: 'root' })
export class ShortcutDialogs {
  private readonly dialogs = inject(DialogService);
  private readonly registry = inject(CommandRegistry);
  private open = false;

  /** The cheat sheet for the region that has focus now (familiarity guide §4: `?` / Alt+Shift+/). */
  async help(scopes: readonly CommandScope[] = this.registry.activeScopes()): Promise<void> {
    if (this.open) return;
    this.open = true;
    try {
      const { ShortcutHelp } = await import('./shortcut-help');
      const ref = this.dialogs.open(ShortcutHelp, {
        data: { scopes, customise: () => void this.settings() },
        width: '40rem',
        autoFocus: '[data-autofocus]',
      });
      ref.closed.subscribe(() => (this.open = false));
    } catch (e) {
      this.open = false;
      throw e;
    }
  }

  async settings(): Promise<void> {
    if (this.open) return;
    this.open = true;
    try {
      const { ShortcutSettings } = await import('./shortcut-settings');
      const ref = this.dialogs.open(ShortcutSettings, { width: '48rem' });
      ref.closed.subscribe(() => (this.open = false));
    } catch (e) {
      this.open = false;
      throw e;
    }
  }
}
