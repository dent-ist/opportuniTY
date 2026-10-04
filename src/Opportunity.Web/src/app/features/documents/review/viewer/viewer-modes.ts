import { Injectable, computed, inject } from '@angular/core';
import type { DocumentResource } from '../../../../core/api/generated/models';
import { PreferenceStorage } from '../../../../core/preferences/preference-storage';

/** The viewer modes, in their fixed order (familiarity guide §3.2, ticket review E16-T04). */
export type ViewerMode = 'text' | 'image' | 'native' | 'production' | 'metadata';

export interface ViewerModeDefinition {
  readonly mode: ViewerMode;
  readonly label: string;
  /** Keyboard command that switches to the mode (Alt+Shift+1 … 5 by default). */
  readonly command: string;
}

export const VIEWER_MODES: readonly ViewerModeDefinition[] = [
  { mode: 'text', label: 'Extracted Text', command: 'viewer.mode.text' },
  { mode: 'image', label: 'Image', command: 'viewer.mode.image' },
  { mode: 'native', label: 'Native', command: 'viewer.mode.native' },
  { mode: 'production', label: 'Production', command: 'viewer.mode.production' },
  { mode: 'metadata', label: 'Metadata', command: 'viewer.mode.metadata' },
];

const MODE_IDS: readonly ViewerMode[] = VIEWER_MODES.map((m) => m.mode);

/** Without a usable last mode: Image, then Extracted Text, then Metadata (always there). */
const FALLBACK_ORDER: readonly ViewerMode[] = ['image', 'text', 'metadata'];

export interface ModeAvailability {
  readonly available: boolean;
  /** Why the mode is disabled, in the reviewer's words; null when it is available. */
  readonly reason: string | null;
}

export type ModeAvailabilityMap = Readonly<Record<ViewerMode, ModeAvailability>>;

const ok: ModeAvailability = { available: true, reason: null };
const off = (reason: string): ModeAvailability => ({ available: false, reason });

/** Which modes have an artifact for the document, from the availability flags of `GET …/documents/{id}`. */
export function modeAvailability(document: DocumentResource): ModeAvailabilityMap {
  const { text, images, native } = document;
  return {
    text: text.available
      ? ok
      : off(
          text.missing
            ? 'The extracted text file is missing'
            : 'No extracted text for this document',
        ),
    image: images.available
      ? ok
      : off(
          images.status === 'pending'
            ? 'Image rendering in progress'
            : images.status === 'failed'
              ? 'Image rendering failed'
              : 'No images',
        ),
    native: native.available
      ? ok
      : off(native.missing ? 'The native file is missing' : 'No native'),
    // Produced images arrive with productions (E12, M3).
    production: off('Not produced'),
    metadata: ok,
  };
}

/** The mode a document opens in: the reviewer's last mode when the document has it, else the fallback order. */
export function initialMode(
  availability: ModeAvailabilityMap,
  last: ViewerMode | null,
): ViewerMode {
  if (last && availability[last].available) return last;
  return FALLBACK_ORDER.find((m) => availability[m].available) ?? 'metadata';
}

export function modeLabel(mode: ViewerMode): string {
  return VIEWER_MODES.find((m) => m.mode === mode)!.label;
}

/** The preference key of the last viewer mode (a user-profile key: it follows the reviewer, E15-T03). */
export const VIEWER_MODE_PREFERENCE = 'viewer.mode';

/** The reviewer's last chosen viewer mode, kept in the user profile. A fallback never overwrites it. */
@Injectable({ providedIn: 'root' })
export class ViewerModePreference {
  private readonly storage = inject(PreferenceStorage);

  readonly last = computed<ViewerMode | null>(() => {
    const value = this.storage.read<{ mode?: string }>(VIEWER_MODE_PREFERENCE)?.mode;
    return MODE_IDS.includes(value as ViewerMode) ? (value as ViewerMode) : null;
  });

  set(mode: ViewerMode): void {
    // An object, like every profile value: the preferences API stores JSON documents.
    this.storage.write(VIEWER_MODE_PREFERENCE, { mode });
  }
}
