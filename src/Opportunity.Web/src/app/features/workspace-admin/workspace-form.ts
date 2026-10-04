import { ApiError } from '../../core/api/problem-details';
import type { Workspace, WorkspaceWrite } from '../../core/workspace/workspace-api';
import type { SelectOption } from '../../ui';

/** Limits of `WorkspaceWrite` (WorkspaceRules on the API). */
export const NAME_MAX = 200;
export const MATTER_NUMBER_MAX = 100;

/** The editable workspace settings as the form holds them (`PUT /api/v1/workspaces/{id}` supports exactly these). */
export interface WorkspaceDraft {
  readonly name: string;
  readonly matterNumber: string;
  readonly displayTimeZone: string;
}

export type DraftErrors = Partial<Record<keyof WorkspaceDraft, string>>;

export function draftOf(workspace: Workspace): WorkspaceDraft {
  return {
    name: workspace.name,
    matterNumber: workspace.matterNumber ?? '',
    displayTimeZone: workspace.displayTimeZone,
  };
}

export function sameDraft(a: WorkspaceDraft, b: WorkspaceDraft): boolean {
  return (
    a.name.trim() === b.name.trim() &&
    a.matterNumber.trim() === b.matterNumber.trim() &&
    a.displayTimeZone === b.displayTimeZone
  );
}

/** The same rules the API applies, so most mistakes are caught before a round trip. */
export function validateDraft(draft: WorkspaceDraft): DraftErrors {
  const errors: DraftErrors = {};
  const name = draft.name.trim();
  if (!name) errors.name = 'Enter a workspace name.';
  else if (name.length > NAME_MAX) errors.name = `Use at most ${NAME_MAX} characters.`;
  if (draft.matterNumber.trim().length > MATTER_NUMBER_MAX)
    errors.matterNumber = `Use at most ${MATTER_NUMBER_MAX} characters.`;
  if (!draft.displayTimeZone) errors.displayTimeZone = 'Choose a time zone.';
  return errors;
}

export function hasErrors(errors: DraftErrors): boolean {
  return Object.values(errors).some((e) => !!e);
}

/** The request body; the storage profile is kept as it is (the server default on create). */
export function toWrite(draft: WorkspaceDraft, storageProfile?: string | null): WorkspaceWrite {
  const matter = draft.matterNumber.trim();
  return {
    name: draft.name.trim(),
    matterNumber: matter ? matter : null,
    displayTimeZone: draft.displayTimeZone,
    ...(storageProfile ? { storageProfile } : {}),
  };
}

/** Field messages of a 400 `validation` problem, by form field; other keys are ignored. */
export function serverErrors(error: ApiError): DraftErrors {
  const errors: DraftErrors = {};
  const reported = error.problem.errors ?? {};
  for (const key of ['name', 'matterNumber', 'displayTimeZone'] as const) {
    const messages = reported[key];
    if (messages?.length) errors[key] = messages.join(' ');
  }
  return errors;
}

/** The browser's IANA time zone, the default for a new workspace; UTC when it cannot be read. */
export function browserTimeZone(): string {
  try {
    return Intl.DateTimeFormat().resolvedOptions().timeZone || 'UTC';
  } catch {
    return 'UTC';
  }
}

/** IANA time zone IDs the browser knows, UTC first, always including `current` (a stored value stays selectable). */
export function timeZoneOptions(current?: string): SelectOption[] {
  let zones: readonly string[] = [];
  try {
    zones = Intl.supportedValuesOf('timeZone');
  } catch {
    zones = [];
  }
  const ids = new Set<string>(zones);
  ids.delete('UTC');
  if (current && current !== 'UTC') ids.add(current);
  return ['UTC', ...[...ids].sort((a, b) => a.localeCompare(b))].map((id) => ({
    value: id,
    label: id,
  }));
}
