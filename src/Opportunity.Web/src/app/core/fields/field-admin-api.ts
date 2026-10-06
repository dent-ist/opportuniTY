import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { firstValueFrom } from 'rxjs';
import type {
  CodingLayoutDefinitionResource,
  CodingLayoutRequest,
  CodingLayoutResource,
  CreateFieldRequest,
  CursorPageOfCodingLayoutResource,
  CursorPageOfFieldResource,
  FieldCapacityEntry,
  FieldCapacityResource,
  FieldDefinitionResource,
  FieldResource,
  UpdateFieldRequest,
} from '../api/generated/models';
import { WorkspaceContext } from '../workspace/workspace-context';

/** One field as Admin › Fields edits it (`GET …/fields/{id}`), with numbers normalised. */
export interface AdminField extends Omit<
  FieldDefinitionResource,
  'fieldId' | 'version' | 'decimalPrecision' | 'decimalScale' | 'choices'
> {
  readonly fieldId: number;
  readonly version: number;
  readonly decimalPrecision: number | null;
  readonly decimalScale: number | null;
  readonly choices: readonly AdminChoice[] | null;
}

export interface AdminChoice {
  readonly choiceId: number;
  readonly name: string;
  readonly isActive: boolean;
  /** Assigned at least once: can be deactivated, never deleted. */
  readonly inUse: boolean;
}

/** A coding layout as the editor holds it (`GET …/coding-layouts/{id}`). */
export interface AdminLayout extends Omit<CodingLayoutDefinitionResource, 'version'> {
  readonly version: number;
}

export type CapacityEntry = FieldCapacityEntry;

/**
 * Field and coding layout administration (E04-T06): `…/fields`, `…/fields/{id}`, its choices and choice order,
 * `…/field-capacity` and `…/coding-layouts`. Every change sends `If-Match` with the version the screen read; a 412
 * means someone else changed it first.
 */
@Injectable()
export abstract class FieldAdminApi {
  /** `GET …/fields`: every live field with query names, capabilities and choices. */
  abstract fields(): Promise<readonly FieldResource[]>;
  abstract capacity(): Promise<readonly CapacityEntry[]>;
  abstract field(fieldId: number): Promise<AdminField>;
  abstract createField(request: CreateFieldRequest): Promise<AdminField>;
  abstract updateField(field: AdminField, request: UpdateFieldRequest): Promise<AdminField>;
  abstract retireField(field: AdminField): Promise<void>;
  abstract addChoice(field: AdminField, name: string): Promise<AdminField>;
  abstract updateChoice(
    field: AdminField,
    choiceId: number,
    patch: { name?: string; isActive?: boolean },
  ): Promise<AdminField>;
  abstract deleteChoice(field: AdminField, choiceId: number): Promise<AdminField>;
  abstract reorderChoices(field: AdminField, choiceIds: readonly number[]): Promise<AdminField>;
  /** `GET …/coding-layouts`: Workspace Admins see every layout, default first. */
  abstract layouts(): Promise<readonly CodingLayoutResource[]>;
  abstract layout(layoutId: string): Promise<AdminLayout>;
  abstract createLayout(request: CodingLayoutRequest): Promise<AdminLayout>;
  abstract updateLayout(layout: AdminLayout, request: CodingLayoutRequest): Promise<AdminLayout>;
  abstract deleteLayout(layout: AdminLayout): Promise<void>;
}

@Injectable()
export class HttpFieldAdminApi extends FieldAdminApi {
  private readonly http = inject(HttpClient);
  private readonly context = inject(WorkspaceContext);

  async fields(): Promise<readonly FieldResource[]> {
    const page = await firstValueFrom(
      this.http.get<CursorPageOfFieldResource>(this.context.apiUrl('fields')),
    );
    return page.items;
  }

  async capacity(): Promise<readonly CapacityEntry[]> {
    const body = await firstValueFrom(
      this.http.get<FieldCapacityResource>(this.context.apiUrl('field-capacity')),
    );
    return body.items;
  }

  async field(fieldId: number): Promise<AdminField> {
    return toField(
      await firstValueFrom(
        this.http.get<FieldDefinitionResource>(this.context.apiUrl('fields', String(fieldId))),
      ),
    );
  }

  async createField(request: CreateFieldRequest): Promise<AdminField> {
    return toField(
      await firstValueFrom(
        this.http.post<FieldDefinitionResource>(this.context.apiUrl('fields'), request),
      ),
    );
  }

  async updateField(field: AdminField, request: UpdateFieldRequest): Promise<AdminField> {
    return toField(
      await firstValueFrom(
        this.http.put<FieldDefinitionResource>(this.fieldUrl(field), request, ifMatch(field)),
      ),
    );
  }

  async retireField(field: AdminField): Promise<void> {
    await firstValueFrom(this.http.delete(this.fieldUrl(field), ifMatch(field)));
  }

  async addChoice(field: AdminField, name: string): Promise<AdminField> {
    return toField(
      await firstValueFrom(
        this.http.post<FieldDefinitionResource>(
          this.fieldUrl(field, 'choices'),
          { name },
          ifMatch(field),
        ),
      ),
    );
  }

  async updateChoice(
    field: AdminField,
    choiceId: number,
    patch: { name?: string; isActive?: boolean },
  ): Promise<AdminField> {
    return toField(
      await firstValueFrom(
        this.http.put<FieldDefinitionResource>(
          this.fieldUrl(field, 'choices', String(choiceId)),
          { name: patch.name ?? null, isActive: patch.isActive ?? null },
          ifMatch(field),
        ),
      ),
    );
  }

  async deleteChoice(field: AdminField, choiceId: number): Promise<AdminField> {
    return toField(
      await firstValueFrom(
        this.http.delete<FieldDefinitionResource>(
          this.fieldUrl(field, 'choices', String(choiceId)),
          ifMatch(field),
        ),
      ),
    );
  }

  async reorderChoices(field: AdminField, choiceIds: readonly number[]): Promise<AdminField> {
    return toField(
      await firstValueFrom(
        this.http.put<FieldDefinitionResource>(
          this.fieldUrl(field, 'choice-order'),
          { choiceIds: [...choiceIds] },
          ifMatch(field),
        ),
      ),
    );
  }

  async layouts(): Promise<readonly CodingLayoutResource[]> {
    const page = await firstValueFrom(
      this.http.get<CursorPageOfCodingLayoutResource>(this.context.apiUrl('coding-layouts')),
    );
    return page.items;
  }

  async layout(layoutId: string): Promise<AdminLayout> {
    return toLayout(
      await firstValueFrom(
        this.http.get<CodingLayoutDefinitionResource>(
          this.context.apiUrl('coding-layouts', layoutId),
        ),
      ),
    );
  }

  async createLayout(request: CodingLayoutRequest): Promise<AdminLayout> {
    return toLayout(
      await firstValueFrom(
        this.http.post<CodingLayoutDefinitionResource>(
          this.context.apiUrl('coding-layouts'),
          request,
        ),
      ),
    );
  }

  async updateLayout(layout: AdminLayout, request: CodingLayoutRequest): Promise<AdminLayout> {
    return toLayout(
      await firstValueFrom(
        this.http.put<CodingLayoutDefinitionResource>(
          this.context.apiUrl('coding-layouts', layout.layoutId),
          request,
          ifMatch(layout),
        ),
      ),
    );
  }

  async deleteLayout(layout: AdminLayout): Promise<void> {
    await firstValueFrom(
      this.http.delete(this.context.apiUrl('coding-layouts', layout.layoutId), ifMatch(layout)),
    );
  }

  private fieldUrl(field: AdminField, ...rest: string[]): string {
    return this.context.apiUrl('fields', String(field.fieldId), ...rest);
  }
}

function ifMatch(resource: { readonly version: number }) {
  return { headers: { 'If-Match': `"${resource.version}"` } };
}

export function toField(r: FieldDefinitionResource): AdminField {
  return {
    ...r,
    fieldId: Number(r.fieldId),
    version: Number(r.version),
    decimalPrecision: r.decimalPrecision === null ? null : Number(r.decimalPrecision),
    decimalScale: r.decimalScale === null ? null : Number(r.decimalScale),
    choices:
      r.choices?.map((c) => ({
        choiceId: Number(c.choiceId),
        name: c.name,
        isActive: c.isActive,
        inUse: c.inUse,
      })) ?? null,
  };
}

export function toLayout(r: CodingLayoutDefinitionResource): AdminLayout {
  return { ...r, version: Number(r.version) };
}
