import {
  ChangeDetectionStrategy,
  Component,
  DOCUMENT,
  ElementRef,
  Injector,
  afterNextRender,
  computed,
  inject,
  input,
  linkedSignal,
  signal,
} from '@angular/core';
import type { SearchHit } from '../../../../core/api/generated/models';
import { CommandRegistry } from '../../../../core/commands';
import { PERMISSIONS } from '../../../../core/workspace/sections';
import { WorkspaceContext } from '../../../../core/workspace/workspace-context';
import { Announcer, Button, EmptyState, Icon, LoadingState, Tooltip } from '../../../../ui';
import type { LoadedDocument } from '../document-loader';
import { ViewerImage } from './image-view';
import { ViewerMetadata } from './metadata-view';
import { ViewerNative } from './native-view';
import { ViewerText } from './text-view';
import {
  VIEWER_MODES,
  ViewerMode,
  ViewerModePreference,
  initialMode,
  modeLabel,
} from './viewer-modes';

/**
 * What the viewer shows: the displayed document and, once loaded, its metadata and first content. `unavailable`:
 * it could not be loaded (try again later); `noAccess`: it answers like a missing document (hidden from the
 * reviewer, or gone; E16-T08), so nothing of it is requested or shown.
 */
export interface ViewerDocument {
  readonly hit: SearchHit;
  readonly state: 'loading' | 'ready' | 'unavailable' | 'noAccess';
  readonly content: LoadedDocument | null;
}

/** The standard no-access state (familiarity guide §3.5): the same words whether hidden, walled or deleted. */
export const NO_ACCESS = {
  title: 'Document not available',
  message:
    'You don’t have access to this document, or it no longer exists. Nothing from it is shown.',
} as const;

/**
 * The document viewer of Review mode (E16-T04, familiarity guide §3.2, AI UI guidelines §13): the mode switcher
 * — Extracted Text · Image · Native · Production · Metadata, always in this order — and the chosen mode.
 *
 * - Only modes with an artifact are enabled (availability flags of `GET …/documents/{id}`); a disabled mode stays
 *   focusable and gives its reason in a tooltip ("No extracted text for this document", "Image rendering in
 *   progress", "No native", "Not produced").
 * - The document opens in the reviewer's last mode when it has it, else Image → Extracted Text → Metadata. The
 *   last mode is a user preference (`viewer.mode`); falling back never changes it, and the fallback is named.
 * - Alt+Shift+1 … 5 switch modes from anywhere in Review mode (command registry). The modes are a tab list
 *   (WAI-ARIA tabs, manual activation): the selected mode is the tab stop, arrows / Home / End move between the
 *   tabs and Enter / Space selects, because every mode fetches audited content and browsing the tabs must not.
 *   The design system's tab styles are reused; the pattern is local because the selection also changes from
 *   commands and the document, and the tab stop must follow it.
 * - Every request goes through the protected-content gateway (`purpose=display`); the first content usually
 *   comes from the document loader, prefetched with `purpose=prefetch` (E16-T03).
 */
@Component({
  selector: 'opp-document-viewer',
  imports: [
    Button,
    EmptyState,
    Icon,
    LoadingState,
    Tooltip,
    ViewerImage,
    ViewerMetadata,
    ViewerNative,
    ViewerText,
  ],
  templateUrl: './document-viewer.html',
  styleUrl: './document-viewer.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class DocumentViewer {
  readonly document = input.required<ViewerDocument>();
  /** The handle of the search the document was opened from: its hits are highlighted in Extracted Text. */
  readonly searchId = input<string | null>(null);

  private readonly preference = inject(ViewerModePreference);
  private readonly announcer = inject(Announcer);
  private readonly host = inject<ElementRef<HTMLElement>>(ElementRef);
  private readonly page = inject(DOCUMENT);
  private readonly injector = inject(Injector);
  protected readonly canDownload =
    inject(WorkspaceContext, { optional: true })?.can(PERMISSIONS.downloadNative) ?? false;

  protected readonly modes = VIEWER_MODES;
  protected readonly noAccess = NO_ACCESS;
  protected readonly content = computed(() => this.document().content);
  protected readonly ready = computed(() => this.document().state === 'ready' && !!this.content());

  /** The mode shown: chosen on load (last mode if available, else the fallback order), then by the reviewer. */
  protected readonly mode = linkedSignal<ViewerMode>(() => {
    const content = this.content();
    const last = this.preference.last();
    return content ? initialMode(content.availability, last) : (last ?? 'text');
  });

  /** When the reviewer's usual mode is not there for this document: why, and what is shown instead. */
  protected readonly fallback = computed(() => {
    const content = this.content();
    if (!content) return null;
    const { availability } = content;
    const last = this.preference.last();
    const mode = this.mode();
    const missing: ViewerMode | null =
      last && last !== mode && !availability[last].available
        ? last
        : !last && mode === 'metadata' && !availability.text.available
          ? 'text'
          : null;
    if (!missing) return null;
    return {
      reason: availability[missing].reason!,
      shown: modeLabel(mode),
      others: VIEWER_MODES.filter((m) => m.mode !== mode && availability[m.mode].available),
    };
  });

  /** Redaction mode of the Image mode (E11-T04); stays on from document to document until switched off. */
  protected readonly redactionMode = signal(false);
  /** Why Redaction mode cannot start for this document (no rendered images); cleared with the document. */
  protected readonly redactionBlocked = linkedSignal<string | null>(() => {
    this.content();
    return null;
  });

  /** Modes the document has, other than the one shown (offered when a mode turns out empty). */
  protected readonly otherModes = computed(() => {
    const content = this.content();
    const mode = this.mode();
    return content
      ? VIEWER_MODES.filter((m) => m.mode !== mode && content.availability[m.mode].available)
      : [];
  });

  constructor() {
    const registry = inject(CommandRegistry);
    for (const { mode, command } of VIEWER_MODES) {
      registry.handle(command, () => this.choose(mode), { enabled: () => this.ready() });
    }
    registry.handle('redaction.toggle', () => this.toggleRedaction(), {
      enabled: () => this.ready(),
    });
  }

  /**
   * Redaction mode on or off. Redactions are drawn on page images, so a document without rendered images says
   * so ("Redaction requires rendered images") instead of switching.
   */
  protected toggleRedaction(): void {
    const content = this.content();
    if (!content) return;
    const image = content.availability.image;
    if (!image.available) {
      const message = `Redaction requires rendered images (${image.reason}).`;
      this.redactionBlocked.set(message);
      this.announcer.announce(message);
      return;
    }
    const on = this.mode() !== 'image' || !this.redactionMode();
    if (this.mode() !== 'image') this.choose('image');
    this.redactionMode.set(on);
    this.announcer.announce(on ? 'Redaction mode on' : 'Redaction mode off');
  }

  protected available(mode: ViewerMode): boolean {
    return this.content()?.availability[mode].available ?? false;
  }

  protected reason(mode: ViewerMode): string | null {
    return this.content()?.availability[mode].reason ?? null;
  }

  /** Switches to `mode` and remembers it; a mode the document does not have says why instead. */
  protected choose(mode: ViewerMode | string | undefined): void {
    const content = this.content();
    if (!content || !mode) return;
    const wanted = mode as ViewerMode;
    const availability = content.availability[wanted];
    if (!availability) return;
    if (!availability.available) {
      this.announcer.announce(`${modeLabel(wanted)} is not available: ${availability.reason}.`);
      return;
    }
    if (wanted !== this.mode()) {
      // Focus inside the old mode would be lost with it: it moves to the viewer pane (the region the reviewer was
      // in, so the viewer's keys keep working).
      const host = this.host.nativeElement;
      const hadFocus = host.contains(this.page.activeElement);
      this.mode.set(wanted);
      if (hadFocus) {
        afterNextRender(
          () => {
            if (host.contains(this.page.activeElement)) return;
            (host.closest<HTMLElement>('[data-command-region]') ?? host).focus();
          },
          { injector: this.injector },
        );
      }
    }
    this.preference.set(wanted);
  }

  /** Arrows, Home and End move focus between the mode tabs; Enter and Space select the focused one. */
  protected onTabKey(event: KeyboardEvent): void {
    const tabs = [
      ...(event.currentTarget as HTMLElement).querySelectorAll<HTMLElement>('[role="tab"]'),
    ];
    const index = tabs.indexOf(event.target as HTMLElement);
    if (index < 0) return;
    const last = tabs.length - 1;
    const target: Record<string, number> = {
      ArrowRight: index === last ? 0 : index + 1,
      ArrowLeft: index === 0 ? last : index - 1,
      Home: 0,
      End: last,
    };
    if (event.key in target) {
      event.preventDefault();
      tabs[target[event.key]].focus();
    } else if (event.key === 'Enter' || event.key === ' ') {
      event.preventDefault();
      this.choose(tabs[index].dataset['mode']);
    }
  }
}
