import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { firstValueFrom } from 'rxjs';
import { ApiConfiguration } from '../../core/api/generated/api-configuration';
import { listCodingLayouts } from '../../core/api/generated/fn/fields/list-coding-layouts';
import { listFields } from '../../core/api/generated/fn/fields/list-fields';
import { listImports } from '../../core/api/generated/fn/import/list-imports';
import { listWorkspaceMembers } from '../../core/api/generated/fn/workspaces/list-workspace-members';
import { PERMISSIONS } from '../../core/workspace/sections';
import { WorkspaceContext } from '../../core/workspace/workspace-context';

/**
 * What the setup checklist reads, through the existing APIs: `GET …/imports` (Import.Run), `GET …/fields` (members),
 * `GET …/coding-layouts` (Document.View) and `GET …/members` (Workspace.ManageUsers). Provided by the checklist page,
 * so it is workspace-scoped and replaceable in tests.
 */
@Injectable()
export abstract class WorkspaceSetupApi {
  /** Number of imports (at least the first page's). */
  abstract imports(): Promise<number>;
  /** Fields that are not system fields: created by an import mapping or an admin. */
  abstract customFields(): Promise<number>;
  /** Coding layouts that have at least one field (a new workspace has only an empty Default layout). */
  abstract layoutsWithFields(): Promise<number>;
  /** Role assignments to users and groups (the creator's own is the first). */
  abstract roleAssignments(): Promise<number>;
}

@Injectable()
export class HttpWorkspaceSetupApi extends WorkspaceSetupApi {
  private readonly http = inject(HttpClient);
  private readonly rootUrl = inject(ApiConfiguration).rootUrl;
  private readonly workspaceId = inject(WorkspaceContext).workspaceId;

  async imports(): Promise<number> {
    const r = await firstValueFrom(
      listImports(this.http, this.rootUrl, { workspaceId: this.workspaceId, limit: 1 }),
    );
    return Math.max(Number(r.body.total?.value ?? 0), r.body.items.length);
  }

  async customFields(): Promise<number> {
    const r = await firstValueFrom(
      listFields(this.http, this.rootUrl, { workspaceId: this.workspaceId }),
    );
    return r.body.items.filter((f) => !f.isSystem).length;
  }

  async layoutsWithFields(): Promise<number> {
    const r = await firstValueFrom(
      listCodingLayouts(this.http, this.rootUrl, { workspaceId: this.workspaceId }),
    );
    return r.body.items.filter((l) => l.sections.some((s) => s.fields.length > 0)).length;
  }

  async roleAssignments(): Promise<number> {
    const r = await firstValueFrom(
      listWorkspaceMembers(this.http, this.rootUrl, { workspaceId: this.workspaceId, limit: 2 }),
    );
    return Math.max(Number(r.body.total?.value ?? 0), r.body.items.length);
  }
}

export type SetupStepKey = 'import' | 'fields' | 'layouts' | 'users';

/** `unknown`: the user may not read what the step depends on, so it is not checked. */
export type SetupStepState = 'checking' | 'done' | 'todo' | 'unknown' | 'error';

export interface SetupStepDefinition {
  readonly key: SetupStepKey;
  readonly title: string;
  readonly description: string;
  /** Permission needed to read the step's state; null when every member may read it. */
  readonly readPermission: string | null;
  /** Where the step is done: a route under the workspace and the permission that shows it. */
  readonly link: {
    readonly path: readonly string[];
    readonly label: string;
    readonly permission: string;
  };
  /** The linked page is a placeholder in this version. */
  readonly comingSoon: boolean;
  /** How the step can be done today when its own page is not there yet. */
  readonly today?: string;
  readonly read: (api: WorkspaceSetupApi) => Promise<number>;
  /** Whether a count means the step is done. */
  readonly isDone: (count: number) => boolean;
  readonly doneText: (count: number, format: (n: number) => string) => string;
  readonly todoText: string;
}

const plural = (n: number, format: (n: number) => string, one: string, many: string) =>
  `${format(n)} ${n === 1 ? one : many}`;

/** Import → Fields → Coding layouts → Users (E04-T07). */
export const SETUP_STEPS: readonly SetupStepDefinition[] = [
  {
    key: 'import',
    title: 'Import documents',
    description: 'Load a DAT load file with its natives, extracted text and images.',
    readPermission: PERMISSIONS.importRun,
    link: {
      path: ['imports', 'new'],
      label: 'Start a new import',
      permission: PERMISSIONS.importRun,
    },
    comingSoon: false,
    read: (api) => api.imports(),
    isDone: (n) => n > 0,
    doneText: (n, f) => `${plural(n, f, 'import', 'imports')} so far`,
    todoText: 'No imports yet',
  },
  {
    key: 'fields',
    title: 'Fields',
    description: 'Add the metadata and coding fields this matter needs beside the system fields.',
    readPermission: null,
    link: { path: ['admin', 'fields'], label: 'Open Fields', permission: PERMISSIONS.manageFields },
    comingSoon: true,
    today:
      'Until the Fields page arrives, an import creates fields when you map a load-file column to a new field.',
    read: (api) => api.customFields(),
    isDone: (n) => n > 0,
    doneText: (n, f) => plural(n, f, 'custom field', 'custom fields'),
    todoText: 'Only the system fields so far',
  },
  {
    key: 'layouts',
    title: 'Coding layouts',
    description: 'Arrange the coding fields reviewers fill in, in the order they review.',
    readPermission: PERMISSIONS.documentView,
    link: {
      path: ['admin', 'coding-layouts'],
      label: 'Open Coding Layouts',
      permission: PERMISSIONS.manageFields,
    },
    comingSoon: true,
    today: 'Until the Coding Layouts page arrives, layouts cannot be edited in the app.',
    read: (api) => api.layoutsWithFields(),
    isDone: (n) => n > 0,
    doneText: (n, f) => `${plural(n, f, 'layout', 'layouts')} with fields`,
    todoText: 'The Default layout has no fields yet',
  },
  {
    key: 'users',
    title: 'Users',
    description: 'Give reviewers and the case team a role in this workspace.',
    readPermission: PERMISSIONS.manageUsers,
    link: {
      path: ['admin', 'users-groups'],
      label: 'Open Users & Groups',
      permission: PERMISSIONS.manageUsers,
    },
    comingSoon: false,
    read: (api) => api.roleAssignments(),
    // The creator's own Workspace Admin assignment does not count.
    isDone: (n) => n > 1,
    doneText: (n, f) => plural(n, f, 'role assignment', 'role assignments'),
    todoText: 'Only the workspace creator has a role so far',
  },
];
