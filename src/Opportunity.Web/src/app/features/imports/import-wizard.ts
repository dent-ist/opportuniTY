import {
  ChangeDetectionStrategy,
  Component,
  ElementRef,
  Injector,
  afterNextRender,
  computed,
  inject,
  signal,
  viewChild,
} from '@angular/core';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { Button, ErrorState, Icon } from '../../ui';
import { HttpImportApi, ImportApi } from './import-api';
import { StepId } from './import-model';
import { ImportWizardStore } from './import-wizard-store';
import { FilesStep } from './steps/files-step';
import { FormatStep } from './steps/format-step';
import { MappingStep } from './steps/mapping-step';
import { OverlayStep } from './steps/overlay-step';
import { SourceStep } from './steps/source-step';
import { ValidateStep } from './steps/validate-step';

/**
 * Imports → New Import (E08-T08, familiarity guide §5.1): a wizard with a step rail. Source & mode → File format →
 * Field mapping → Overlay settings (Overlay and Append/Overlay only) → Natives, text & images → Validate & run. Every
 * step can go back without losing input; Continue re-runs the preview when a setting changed it, and the import starts
 * only after a current pre-flight without errors and with its warnings acknowledged. Starting goes to the import's
 * page, which follows the job.
 *
 * Keyboard: the rail is a list of buttons (completed steps only); after a step change focus moves to the step heading.
 */
@Component({
  selector: 'opp-import-wizard',
  imports: [
    Button,
    ErrorState,
    FilesStep,
    FormatStep,
    Icon,
    MappingStep,
    OverlayStep,
    RouterLink,
    SourceStep,
    ValidateStep,
  ],
  templateUrl: './import-wizard.html',
  styleUrl: './import-wizard.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
  providers: [{ provide: ImportApi, useClass: HttpImportApi }, ImportWizardStore],
})
export class ImportWizard {
  protected readonly store = inject(ImportWizardStore);
  private readonly router = inject(Router);
  private readonly route = inject(ActivatedRoute);
  private readonly injector = inject(Injector);
  private readonly heading = viewChild<ElementRef<HTMLElement>>('stepHeading');

  protected readonly current = signal<StepId>('source');
  /** Furthest step reached; the rail lets the user go back to any step up to it. */
  private readonly reached = signal(0);
  protected readonly blocked = signal<string | null>(null);
  protected readonly busy = signal(false);
  protected readonly reimportOf = signal<string | null>(null);

  protected readonly steps = this.store.steps;
  protected readonly index = computed(() =>
    Math.max(
      0,
      this.steps().findIndex((s) => s.id === this.current()),
    ),
  );
  protected readonly step = computed(() => this.steps()[this.index()]);
  protected readonly isLast = computed(() => this.index() === this.steps().length - 1);

  constructor() {
    void this.store.loadReferenceData();
    // `?from=<importId>`: re-import a corrected error file with the same profile and mode.
    const from = this.route.snapshot.queryParamMap.get('from');
    if (from) {
      void this.store.prefillFrom(from).then((previous) => {
        if (previous) this.reimportOf.set(previous.name);
      });
    }
  }

  protected reachable(i: number): boolean {
    return i <= this.reached();
  }

  protected async goTo(id: StepId): Promise<void> {
    this.blocked.set(null);
    this.current.set(id);
    this.reached.update((r) => Math.max(r, this.index()));
    this.focusHeading();
    if (id === 'format' || id === 'mapping' || id === 'files') void this.store.runPreview();
    if (id === 'validate' && this.store.preflightStale()) {
      await this.store.runPreview();
      void this.store.runPreflight();
    }
  }

  protected back(): void {
    const i = this.index();
    if (i > 0) void this.goTo(this.steps()[i - 1].id);
  }

  protected async next(): Promise<void> {
    if (this.busy()) return;
    const id = this.current();
    this.busy.set(true);
    try {
      if (id !== 'validate' && this.store.datFile() && !this.store.volumeRootError()) {
        await this.store.runPreview();
      }
      const reason = this.store.blocker(id);
      this.blocked.set(reason);
      if (reason) return;
      if (id === 'validate') {
        const started = await this.store.start();
        if (started) {
          await this.router.navigate(['..', started.importId], { relativeTo: this.route });
        }
        return;
      }
      await this.goTo(this.steps()[this.index() + 1].id);
    } finally {
      this.busy.set(false);
    }
  }

  private focusHeading(): void {
    afterNextRender(() => this.heading()?.nativeElement.focus(), { injector: this.injector });
  }
}
