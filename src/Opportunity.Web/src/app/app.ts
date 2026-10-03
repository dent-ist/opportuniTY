import { ChangeDetectionStrategy, Component, inject } from '@angular/core';
import { RouterOutlet } from '@angular/router';
import { UiPreferences } from './core/preferences/ui-preferences';
import { ToastRegion } from './ui';

@Component({
  imports: [RouterOutlet, ToastRegion],
  selector: 'app-root',
  templateUrl: './app.html',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class App {
  // Applies the persisted theme, density and locale to <html> before the first route renders.
  protected readonly prefs = inject(UiPreferences);
}
