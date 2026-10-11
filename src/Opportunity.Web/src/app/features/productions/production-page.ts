import { ChangeDetectionStrategy, Component, computed, inject, signal } from '@angular/core';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { toApiError } from '../../core/api/problem-details';
import { UiPreferences } from '../../core/preferences/ui-preferences';
import { PERMISSIONS } from '../../core/workspace/sections';
import { WorkspaceContext } from '../../core/workspace/workspace-context';
import {
  Badge,
  Button,
  DialogService,
  ErrorState,
  Icon,
  LoadingState,
  TABS,
  ToastService,
} from '../../ui';
import { FrozenSetApi, HttpFrozenSetApi } from '../exports/frozen-set/frozen-set-api';
import { HttpProductionApi, ProductionApi } from './production-api';
import { PRODUCTION_TABS, ProductionTab, batesRange } from './production-model';
import { ProductionStore } from './production-store';
import { DocumentsTab } from './tabs/documents-tab';
import { OutputTab } from './tabs/output-tab';
import { QcTab } from './tabs/qc-tab';
import { SettingsTab } from './tabs/settings-tab';

const TAB_PARAM = 'tab';

/**
 * One production (ticket review E12-T08): a page with tabs — Settings · Documents · QC · Output · Privilege Log — not a
 * one-shot wizard, because practitioners iterate on a production across days. The header always shows the stage
 * (Draft → Bates assigned → QC passed → Finalized), the frozen set ID with its document count, and the Bates range;
 * "Copy settings" starts a new draft from this specification and a finalized production can get a new version.
 * The open tab is kept in the URL (`?tab=`).
 */
@Component({
  selector: 'opp-production-page',
  imports: [
    Badge,
    Button,
    DocumentsTab,
    ErrorState,
    Icon,
    LoadingState,
    OutputTab,
    QcTab,
    RouterLink,
    SettingsTab,
    ...TABS,
  ],
  templateUrl: './production-page.html',
  styleUrl: '../exports/delivery.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { class: 'dl-page' },
  providers: [
    { provide: ProductionApi, useClass: HttpProductionApi },
    { provide: FrozenSetApi, useClass: HttpFrozenSetApi },
    ProductionStore,
  ],
})
export class ProductionPage {
  protected readonly store = inject(ProductionStore);
  private readonly api = inject(ProductionApi);
  private readonly route = inject(ActivatedRoute);
  private readonly router = inject(Router);
  private readonly prefs = inject(UiPreferences);
  private readonly dialogs = inject(DialogService);
  private readonly toasts = inject(ToastService);
  private readonly context = inject(WorkspaceContext);

  protected readonly productionId = this.route.snapshot.paramMap.get('productionId') ?? '';
  protected readonly tabs = PRODUCTION_TABS;
  protected readonly canLog = this.context.can(PERMISSIONS.privilegeLogGenerate);
  protected readonly tab = signal<ProductionTab>(initialTab(this.route.snapshot.queryParamMap));
  protected readonly creatingVersion = signal(false);
  protected readonly range = computed(() => {
    const p = this.store.production();
    return p ? batesRange(p) : '';
  });

  constructor() {
    void this.store.load(this.productionId);
  }

  protected selectTab(tab: ProductionTab): void {
    this.tab.set(tab);
    void this.router.navigate([], {
      relativeTo: this.route,
      queryParams: { [TAB_PARAM]: tab === 'settings' ? null : tab },
      queryParamsHandling: 'merge',
      replaceUrl: true,
    });
  }

  /** A finalized or voided production changes only as a new version, with new Bates numbers (E12-T02). */
  protected async newVersion(): Promise<void> {
    const p = this.store.production();
    if (!p || this.creatingVersion()) return;
    const confirmed = await this.dialogs.confirm({
      title: 'Create a new version',
      message:
        `Version ${Number(p.version) + 1} of ${p.name} starts as a draft with the same frozen set and ` +
        'specification; its numbering continues after this version. This version stays as it is.',
      confirmLabel: 'Create new version',
    });
    if (!confirmed) return;
    this.creatingVersion.set(true);
    try {
      const created = await this.api.create({ previousVersionId: p.productionId });
      await this.router.navigate(['..', created.production.productionId], {
        relativeTo: this.route,
      });
      this.toasts.show(`Version ${created.production.version} created as a draft.`, {
        tone: 'success',
      });
    } catch (e) {
      this.toasts.show(toApiError(e).problem.detail ?? 'The new version could not be created.', {
        tone: 'error',
      });
    } finally {
      this.creatingVersion.set(false);
    }
  }

  protected num(n: number | string | null | undefined): string {
    return n === null || n === undefined ? '—' : Number(n).toLocaleString(this.prefs.locale());
  }

  protected version(): number {
    return Number(this.store.production()?.version ?? 1);
  }
}

function initialTab(params: { get(name: string): string | null }): ProductionTab {
  const tab = params.get(TAB_PARAM);
  return PRODUCTION_TABS.some((t) => t.id === tab) ? (tab as ProductionTab) : 'settings';
}
