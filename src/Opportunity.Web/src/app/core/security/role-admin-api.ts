import { HttpClient, HttpParams } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { firstValueFrom } from 'rxjs';
import type {
  RestrictionClassList,
  RestrictionClassResource,
  RoleAssignmentCandidateList,
  RoleAssignmentCandidateResource,
  RoleAssignmentChangeResource,
  RoleAssignmentListResource,
  RoleAssignmentPrincipalResource,
  RoleAssignmentRequest,
  RoleCatalogResource,
} from '../api/generated/models';
import { WorkspaceContext } from '../workspace/workspace-context';

/** Who a role assignment names: a user by id or an IdP group by name. */
export type RolePrincipalRef =
  | { readonly kind: 'user'; readonly userId: string }
  | { readonly kind: 'group'; readonly groupName: string };

/** The role-assignment matrix (`GET …/role-assignments`) with numbers normalised. */
export interface RoleAssignments {
  readonly items: readonly RoleAssignmentPrincipalResource[];
  /** Version of the whole set: every change sends it as `If-Match`. */
  readonly version: number;
  readonly administratorPaths: number;
  readonly canAssignBreakGlass: boolean;
}

export interface RoleChange {
  readonly principal: RoleAssignmentPrincipalResource;
  readonly version: number;
  readonly administratorPaths: number;
}

/** A restriction class with its version normalised (`GET …/security/restriction-classes`). */
export interface AdminRestrictionClass extends Omit<RestrictionClassResource, 'version'> {
  readonly version: number;
}

export type RoleCatalog = RoleCatalogResource;
export type RoleCandidate = RoleAssignmentCandidateResource;

export function principalRef(p: {
  readonly kind: string;
  readonly userId?: string | null;
  readonly groupName?: string | null;
}): RolePrincipalRef {
  return p.kind === 'group'
    ? { kind: 'group', groupName: p.groupName ?? '' }
    : { kind: 'user', userId: p.userId ?? '' };
}

export function samePrincipal(a: RolePrincipalRef, b: RolePrincipalRef): boolean {
  return a.kind === 'user'
    ? b.kind === 'user' && a.userId === b.userId
    : b.kind === 'group' && a.groupName === b.groupName;
}

/**
 * Roles, permissions and user/group assignment (E05-T08): `…/roles` (built-in roles and their permissions, read-only),
 * `…/role-assignments` (every user and group with roles; one version for the whole set), replacing one user's or
 * group's roles with `If-Match`, `…/role-assignments/candidates`, and the role grants of restriction classes
 * (`…/security/restriction-classes`, E05-T06). A 412 means someone else changed the assignments first.
 */
@Injectable()
export abstract class RoleAdminApi {
  abstract roles(): Promise<RoleCatalog>;
  abstract assignments(): Promise<RoleAssignments>;
  /** Replaces every role of `principal`; `confirmSelfRemoval` acknowledges giving up one's own role. */
  abstract replace(
    principal: RolePrincipalRef,
    roles: readonly string[],
    version: number,
    confirmSelfRemoval?: boolean,
  ): Promise<RoleChange>;
  abstract candidates(text: string): Promise<readonly RoleCandidate[]>;
  abstract restrictionClasses(): Promise<readonly AdminRestrictionClass[]>;
  /** Replaces the roles that may see documents of the class (its name and coding rules stay). */
  abstract setClassRoles(
    restrictionClass: AdminRestrictionClass,
    roles: readonly string[],
  ): Promise<AdminRestrictionClass>;
}

@Injectable()
export class HttpRoleAdminApi extends RoleAdminApi {
  private readonly http = inject(HttpClient);
  private readonly context = inject(WorkspaceContext);

  roles(): Promise<RoleCatalog> {
    return firstValueFrom(this.http.get<RoleCatalogResource>(this.context.apiUrl('roles')));
  }

  async assignments(): Promise<RoleAssignments> {
    const body = await firstValueFrom(
      this.http.get<RoleAssignmentListResource>(this.context.apiUrl('role-assignments')),
    );
    return {
      items: body.items,
      version: Number(body.version),
      administratorPaths: Number(body.administratorPaths),
      canAssignBreakGlass: body.canAssignBreakGlass,
    };
  }

  async replace(
    principal: RolePrincipalRef,
    roles: readonly string[],
    version: number,
    confirmSelfRemoval = false,
  ): Promise<RoleChange> {
    const request: RoleAssignmentRequest = { roles: [...roles], confirmSelfRemoval };
    const headers = { 'If-Match': `"${version}"` };
    const url =
      principal.kind === 'user'
        ? this.context.apiUrl('role-assignments', 'users', principal.userId)
        : this.context.apiUrl('role-assignments', 'groups');
    const params =
      principal.kind === 'group' ? new HttpParams().set('name', principal.groupName) : undefined;
    const body = await firstValueFrom(
      this.http.put<RoleAssignmentChangeResource>(url, request, { headers, params }),
    );
    return {
      principal: body.principal,
      version: Number(body.version),
      administratorPaths: Number(body.administratorPaths),
    };
  }

  async candidates(text: string): Promise<readonly RoleCandidate[]> {
    const params = new HttpParams().set('q', text).set('limit', 20);
    const body = await firstValueFrom(
      this.http.get<RoleAssignmentCandidateList>(
        this.context.apiUrl('role-assignments', 'candidates'),
        { params },
      ),
    );
    return body.items;
  }

  async restrictionClasses(): Promise<readonly AdminRestrictionClass[]> {
    const body = await firstValueFrom(
      this.http.get<RestrictionClassList>(this.context.apiUrl('security', 'restriction-classes')),
    );
    return body.items.map(toClass);
  }

  async setClassRoles(
    restrictionClass: AdminRestrictionClass,
    roles: readonly string[],
  ): Promise<AdminRestrictionClass> {
    return toClass(
      await firstValueFrom(
        this.http.put<RestrictionClassResource>(
          this.context.apiUrl('security', 'restriction-classes', restrictionClass.classKey),
          {
            displayName: restrictionClass.displayName,
            roles: [...roles],
            rules: restrictionClass.rules,
          },
          { headers: { 'If-Match': `"${restrictionClass.version}"` } },
        ),
      ),
    );
  }
}

function toClass(r: RestrictionClassResource): AdminRestrictionClass {
  return { ...r, version: Number(r.version) };
}
