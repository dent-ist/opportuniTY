import { Injectable, computed, inject, signal } from '@angular/core';
import type {
  ColumnMapping,
  ImportMode,
  ImportProfileSummary,
  ImportResource,
  ImportTargetResource,
  MappingPreviewResult,
  MappingTarget,
} from '../../core/api/generated/models';
import { ApiError, toApiError } from '../../core/api/problem-details';
import { WorkspaceContext } from '../../core/workspace/workspace-context';
import { PERMISSIONS } from '../../core/workspace/sections';
import {
  ImportApi,
  ImportFiles,
  ImportRequest,
  LoadFileOptions,
  OverlayOptions,
  PathOptions,
  PreflightResult,
} from './import-api';
import {
  DEFAULT_LOAD_FILE,
  DEFAULT_OVERLAY,
  DEFAULT_PATHS,
  DelimiterPreset,
  StepId,
  WizardSettings,
  buildProfile,
  defaultImportName,
  detectPreset,
  mapsControlNumber,
  settingsFromProfile,
  stepsFor,
  volumeRootError,
} from './import-model';

/** Leading bytes of the DAT sent to the mapping preview (the API accepts up to 16 MB). */
export const PREVIEW_SAMPLE_BYTES = 1024 * 1024;
/** Rows the preview parses and shows (ticket E08-T08). */
export const PREVIEW_ROWS = 20;

type Status = 'idle' | 'busy' | 'done' | 'failed';

/**
 * State and actions of one run of the import wizard (E08-T08). Provided by the wizard page, so leaving the page or
 * the workspace discards it. Every setting that changes what the load file means (format, mapping, mode, overlay,
 * paths) makes the preview and the pre-flight stale; the wizard re-runs them before it lets the user go on.
 */
@Injectable()
export class ImportWizardStore {
  private readonly api = inject(ImportApi);
  private readonly context = inject(WorkspaceContext);

  // Step 1: source and mode.
  readonly datFile = signal<File | null>(null);
  readonly optFile = signal<File | null>(null);
  readonly name = signal('');
  readonly mode = signal<ImportMode>('append');
  readonly profileId = signal('');
  readonly profiles = signal<readonly ImportProfileSummary[]>([]);
  readonly profilesError = signal<ApiError | null>(null);
  readonly detectedPreset = signal<Exclude<DelimiterPreset, 'custom'> | null>(null);

  // Steps 2–5: settings.
  readonly loadFile = signal<LoadFileOptions>(DEFAULT_LOAD_FILE);
  readonly overlay = signal<OverlayOptions>(DEFAULT_OVERLAY);
  readonly paths = signal<PathOptions>(DEFAULT_PATHS);
  readonly imageMatchBy = signal<'controlNumber' | 'begBates'>('controlNumber');
  /** Mappings chosen in the wizard or by the profile, by column; the preview auto-maps the others when `autoMap`. */
  readonly columns = signal<Readonly<Record<string, ColumnMapping>>>({});
  readonly autoMap = signal(true);
  readonly targets = signal<readonly ImportTargetResource[]>([]);

  // Preview (steps 2–3).
  readonly preview = signal<MappingPreviewResult | null>(null);
  readonly previewStatus = signal<Status>('idle');
  readonly previewError = signal<ApiError | null>(null);
  private previewKey = '';

  // Pre-flight and start (step 6).
  readonly preflight = signal<PreflightResult | null>(null);
  readonly preflightStatus = signal<Status>('idle');
  readonly preflightError = signal<ApiError | null>(null);
  readonly warningsAcknowledged = signal(false);
  private preflightKey = '';
  readonly startStatus = signal<Status>('idle');
  readonly startError = signal<ApiError | null>(null);
  private idempotency: { key: string; request: string } | null = null;

  readonly canOverlay = this.context.can(PERMISSIONS.importOverlay);
  readonly steps = computed(() => stepsFor(this.mode()));

  readonly settings = computed<WizardSettings>(() => ({
    mode: this.mode(),
    loadFile: this.loadFile(),
    overlay: this.overlay(),
    paths: this.paths(),
    imageMatchBy: this.imageMatchBy(),
    columns: this.columns(),
  }));

  readonly defaultName = computed(() => {
    const dat = this.datFile();
    return dat ? defaultImportName(dat.name, new Date()) : '';
  });

  readonly volumeRootError = computed(() => volumeRootError(this.paths().volumeRoot ?? ''));

  /** The request pre-flight and start send: the preview's applied mapping, frozen, so what was shown is what loads. */
  readonly request = computed<ImportRequest>(() => {
    const preview = this.preview();
    const columns = preview?.effectiveProfile.columns ?? Object.values(this.columns());
    return {
      name: this.name().trim() || null,
      mode: this.mode(),
      autoMap: false,
      profile: buildProfile(this.settings(), columns),
    };
  });

  readonly previewStale = computed(() => this.previewInputKey() !== this.previewKeySignal());
  private readonly previewKeySignal = signal('');
  private readonly previewInputKey = computed(() =>
    JSON.stringify([
      this.datFile()?.name,
      this.datFile()?.size,
      this.datFile()?.lastModified,
      this.autoMap(),
      buildProfile(this.settings()),
    ]),
  );

  private readonly preflightKeySignal = signal('');
  readonly preflightStale = computed(
    () => this.preflightKeySignal() !== this.preflightInputKey() || this.previewStale(),
  );
  private readonly preflightInputKey = computed(() =>
    JSON.stringify([this.optFile()?.name, this.optFile()?.size, this.request()]),
  );

  /** Why the user cannot continue from a step, or null. */
  blocker(step: StepId): string | null {
    switch (step) {
      case 'source':
        if (!this.datFile()) return 'Choose the load file (DAT) to import.';
        return this.volumeRootError();
      case 'format':
      case 'mapping':
      case 'files': {
        const preview = this.preview();
        if (this.previewStatus() === 'busy') return 'Wait for the preview to finish.';
        if (!preview || this.previewStatus() === 'failed')
          return 'The load file could not be previewed with these settings.';
        if (step === 'format') return null;
        if (this.mode() !== 'overlay' && !mapsControlNumber(preview.columns))
          return 'Map a column to Control Number: every new document needs one.';
        if (!preview.canImport) return 'Resolve the mapping errors listed above.';
        return null;
      }
      case 'overlay':
        return null;
      case 'validate': {
        const result = this.preflight();
        if (!result || this.preflightStale()) return 'Validate the import first.';
        if (result.blocking) return 'Fix the errors, then validate again.';
        if (result.warningCount > 0 && !this.warningsAcknowledged())
          return 'Acknowledge the warnings to start the import.';
        return null;
      }
    }
  }

  async loadReferenceData(): Promise<void> {
    try {
      const [profiles, targets] = await Promise.all([this.api.profiles(), this.api.targets()]);
      this.profiles.set(profiles);
      this.targets.set(targets);
      this.profilesError.set(null);
    } catch (e) {
      this.profilesError.set(toApiError(e));
    }
  }

  /** Pre-fills from a previous import ("Re-import corrected error file"): same profile and mode. */
  async prefillFrom(importId: string): Promise<ImportResource | null> {
    try {
      const previous = await this.api.get(importId);
      if (previous.profileId) await this.applyProfile(previous.profileId);
      this.setMode(previous.mode);
      return previous;
    } catch {
      return null;
    }
  }

  async setDat(file: File | null): Promise<void> {
    this.datFile.set(file);
    this.detectedPreset.set(null);
    if (!file) return;
    const head = new Uint8Array(await file.slice(0, 64 * 1024).arrayBuffer());
    const preset = detectPreset(head);
    this.detectedPreset.set(preset);
    // A profile's delimiters win over detection; otherwise the detected preset is the starting point.
    if (!this.profileId()) this.loadFile.update((lf) => ({ ...lf, delimiters: preset }));
  }

  setMode(mode: ImportMode): void {
    if (mode !== 'append' && !this.canOverlay) return;
    this.mode.set(mode);
  }

  async applyProfile(profileId: string): Promise<void> {
    this.profileId.set(profileId);
    if (!profileId) return;
    const profile = await this.api.profile(profileId);
    const next = settingsFromProfile(profile, this.settings());
    this.setMode(next.mode);
    this.loadFile.set(next.loadFile);
    this.overlay.set(next.overlay);
    this.paths.set(next.paths);
    this.imageMatchBy.set(next.imageMatchBy);
    this.columns.set(next.columns);
    this.autoMap.set(true);
  }

  /** The user's mapping for one column: targets, `ignore` (Do not import) or null (back to auto-mapping, if on). */
  mapColumn(column: string, targets: readonly MappingTarget[] | 'ignore' | null): void {
    this.columns.update((cols) => {
      const next = { ...cols };
      if (targets === 'ignore') next[column] = { column, ignore: true, targets: [] };
      else if (targets === null || targets.length === 0) delete next[column];
      else next[column] = { column, ignore: false, targets: [...targets] };
      return next;
    });
  }

  /** Points a structural target (Native Path, Extracted Text Path) at one column, or at none. */
  setStructuralColumn(structural: 'nativePath' | 'textPath', column: string): void {
    const preview = this.preview();
    if (!preview) return;
    this.columns.update((cols) => {
      const next = { ...cols };
      for (const c of preview.columns) {
        const has = c.targets.some((t) => t.target.structural === structural);
        if (has && c.column !== column) {
          const rest = c.targets
            .filter((t) => t.target.structural !== structural)
            .map((t) => t.target);
          next[c.column] = { column: c.column, ignore: rest.length === 0, targets: rest };
        }
      }
      if (column) {
        next[column] = {
          column,
          ignore: false,
          targets: [{ kind: 'structural', structural }],
        };
      }
      return next;
    });
  }

  /** Auto-map: forget the wizard's choices and match every column by name and alias again. */
  autoMapAll(): void {
    this.columns.set({});
    this.autoMap.set(true);
  }

  /** Clear: every column unmapped, nothing auto-mapped. */
  clearMapping(): void {
    this.columns.set({});
    this.autoMap.set(false);
  }

  /** Runs the mapping preview unless it is current for the settings. */
  async runPreview(force = false): Promise<void> {
    const dat = this.datFile();
    if (!dat) return;
    const key = this.previewInputKey();
    if (!force && key === this.previewKey && this.previewStatus() === 'done') return;
    this.previewKey = key;
    this.previewStatus.set('busy');
    this.previewError.set(null);
    try {
      const result = await this.api.preview(
        dat.slice(0, PREVIEW_SAMPLE_BYTES),
        dat.name,
        dat.size > PREVIEW_SAMPLE_BYTES,
        buildProfile(this.settings()),
        this.autoMap(),
        PREVIEW_ROWS,
      );
      if (this.previewKey !== key) return;
      this.preview.set(result);
      this.previewKeySignal.set(key);
      this.previewStatus.set('done');
    } catch (e) {
      if (this.previewKey !== key) return;
      this.previewError.set(toApiError(e));
      this.previewStatus.set('failed');
    }
  }

  async runPreflight(): Promise<void> {
    const files = this.files();
    if (!files) return;
    const key = this.preflightInputKey();
    this.preflightKey = key;
    this.preflightStatus.set('busy');
    this.preflightError.set(null);
    this.warningsAcknowledged.set(false);
    try {
      const result = await this.api.preflight(files, this.request());
      if (this.preflightKey !== key) return;
      this.preflight.set(result);
      this.preflightKeySignal.set(key);
      this.preflightStatus.set('done');
    } catch (e) {
      if (this.preflightKey !== key) return;
      this.preflight.set(null);
      this.preflightError.set(toApiError(e));
      this.preflightStatus.set('failed');
    }
  }

  /** Starts the import job; a retry of the same request reuses its Idempotency-Key, so it never starts twice. */
  async start(): Promise<ImportResource | null> {
    const files = this.files();
    if (!files || this.blocker('validate') || this.startStatus() === 'busy') return null;
    const request = this.request();
    const body = JSON.stringify(request);
    if (this.idempotency?.request !== body) {
      this.idempotency = { key: crypto.randomUUID(), request: body };
    }
    this.startStatus.set('busy');
    this.startError.set(null);
    try {
      const started = await this.api.start(files, request, this.idempotency.key);
      this.startStatus.set('done');
      return started;
    } catch (e) {
      this.startError.set(toApiError(e));
      this.startStatus.set('failed');
      return null;
    }
  }

  async saveProfile(name: string): Promise<boolean> {
    const preview = this.preview();
    const profile = buildProfile(
      this.settings(),
      preview?.effectiveProfile.columns ?? Object.values(this.columns()),
    );
    const saved = await this.api.saveProfile(name.trim(), profile);
    this.profiles.update((list) => [
      ...list,
      {
        profileId: saved.profileId,
        name: saved.name,
        description: null,
        mode: profile.mode,
        columnCount: profile.columns.length,
        updatedAt: new Date().toISOString(),
        version: 1,
      },
    ]);
    this.profileId.set(saved.profileId);
    return true;
  }

  preflightIssuesUrl(): string | null {
    const result = this.preflight();
    return result ? this.api.preflightIssuesUrl(result.preflightId) : null;
  }

  private files(): ImportFiles | null {
    const dat = this.datFile();
    if (!dat) return null;
    const opt = this.optFile();
    return { dat, datName: dat.name, opt, optName: opt?.name ?? null };
  }
}
