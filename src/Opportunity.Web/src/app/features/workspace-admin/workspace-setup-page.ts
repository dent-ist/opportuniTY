import { ChangeDetectionStrategy, Component, computed, inject, signal } from '@angular/core';
import { RouterLink } from '@angular/router';
import { UiPreferences } from '../../core/preferences/ui-preferences';
import { PERMISSIONS } from '../../core/workspace/sections';
import { ActiveWorkspace, WorkspaceContext } from '../../core/workspace/workspace-context';
import { Badge, Button, Icon, Progress } from '../../ui';
import {
  HttpWorkspaceSetupApi,
  SETUP_STEPS,
  type SetupStepDefinition,
  type SetupStepState,
  WorkspaceSetupApi,
} from './workspace-setup';

interface StepView {
  readonly step: SetupStepDefinition;
  readonly state: SetupStepState;
  readonly status: string;
  /** Router link to where the step is done, or null without the permission that shows that page. */
  readonly link: readonly string[] | null;
}

/**
 * Setup checklist (E04-T07): where a new workspace lands. Import documents → Fields → Coding layouts → Users, each
 * linking to where it is done and ticking itself off when the workspace has that thing. Steps whose page is not in
 * this version say so; steps the user may not check are marked as such instead of guessed.
 */
@Component({
  selector: 'opp-workspace-setup-page',
  imports: [Badge, Button, Icon, Progress, RouterLink],
  templateUrl: './workspace-setup-page.html',
  styleUrl: './workspace-admin.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
  providers: [{ provide: WorkspaceSetupApi, useClass: HttpWorkspaceSetupApi }],
  host: { class: 'wsa-page' },
})
export class WorkspaceSetupPage {
  private readonly api = inject(WorkspaceSetupApi);
  private readonly context = inject(WorkspaceContext);
  private readonly prefs = inject(UiPreferences);
  protected readonly workspace = inject(ActiveWorkspace).current;
  protected readonly workspaceId = this.context.workspaceId;
  protected readonly canOpenDocuments = this.context.can(PERMISSIONS.documentView);

  private readonly counts = signal<ReadonlyMap<string, number | 'error' | 'checking'>>(new Map());
  protected readonly checking = computed(() => [...this.counts().values()].includes('checking'));

  protected readonly steps = computed<StepView[]>(() => {
    const format = new Intl.NumberFormat(this.prefs.locale()).format;
    return SETUP_STEPS.map((step) => {
      const link = this.context.can(step.link.permission)
        ? ['/w', this.workspaceId, ...step.link.path]
        : null;
      if (step.readPermission && !this.context.can(step.readPermission))
        return { step, link, state: 'unknown', status: 'You cannot check this step' };
      const count = this.counts().get(step.key) ?? 'checking';
      if (count === 'checking') return { step, link, state: 'checking', status: 'Checking…' };
      if (count === 'error') return { step, link, state: 'error', status: 'Could not check' };
      return step.isDone(count)
        ? { step, link, state: 'done', status: step.doneText(count, format) }
        : { step, link, state: 'todo', status: step.todoText };
    });
  });

  protected readonly doneCount = computed(
    () => this.steps().filter((s) => s.state === 'done').length,
  );
  protected readonly summary = computed(
    () => `${this.doneCount()} of ${this.steps().length} steps done`,
  );

  constructor() {
    void this.check();
  }

  /** Reads every step the user may check, in parallel; a failing read marks only its own step. */
  protected async check(): Promise<void> {
    const readable = SETUP_STEPS.filter(
      (s) => !s.readPermission || this.context.can(s.readPermission),
    );
    this.counts.set(new Map(readable.map((s) => [s.key, 'checking' as const])));
    await Promise.all(
      readable.map(async (step) => {
        let result: number | 'error';
        try {
          result = await step.read(this.api);
        } catch {
          result = 'error';
        }
        this.counts.update((counts) => new Map(counts).set(step.key, result));
      }),
    );
  }
}
