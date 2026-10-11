import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { firstValueFrom, map } from 'rxjs';
import type {
  CreateSnapshotRequest,
  CursorPageOfSavedSearchSummary,
  SnapshotResource,
  SnapshotResourcePurpose,
} from '../../../core/api/generated/models';
import { WorkspaceContext } from '../../../core/workspace/workspace-context';

// The source step shared by New Export and New Production (familiarity guide §5.5/§5.6): a saved search or an existing
// frozen set becomes a frozen set (`POST/GET …/snapshots`), whose ID and document count are shown before anything
// runs. Provided by the pages that use it, so it is workspace-scoped and replaceable in tests.

export interface SavedSearchChoice {
  readonly savedSearchId: string;
  readonly name: string;
  readonly lastHitCount: number | null;
}

/** A frozen set (snapshot) as the source step and the detail pages show it. */
export interface FrozenSet {
  readonly snapshotId: string;
  readonly name: string;
  readonly purpose: SnapshotResourcePurpose;
  readonly status: SnapshotResource['status'];
  readonly statusReason: string | null;
  /** Documents frozen; null while a large set is still materializing. */
  readonly documentCount: number | null;
  /** Documents added by Include: Family / Duplicates / Email thread. */
  readonly related: number;
  readonly frozenAt: string | null;
}

export interface FreezeRequest {
  readonly savedSearchId: string;
  readonly family: boolean;
  readonly duplicates: boolean;
  readonly name?: string;
}

@Injectable()
export abstract class FrozenSetApi {
  /** `GET …/saved-searches` (the caller's own and shared ones). */
  abstract savedSearches(): Promise<readonly SavedSearchChoice[]>;
  /** `POST …/snapshots` for the purpose; may answer while the set still materializes (202). */
  abstract freeze(
    request: FreezeRequest,
    purpose: SnapshotResourcePurpose,
    idempotencyKey: string,
  ): Promise<FrozenSet>;
  /** `GET …/snapshots/{id}`. */
  abstract frozenSet(snapshotId: string): Promise<FrozenSet>;
}

@Injectable()
export class HttpFrozenSetApi extends FrozenSetApi {
  private readonly http = inject(HttpClient);
  private readonly context = inject(WorkspaceContext);

  savedSearches(): Promise<readonly SavedSearchChoice[]> {
    return firstValueFrom(
      this.http
        .get<CursorPageOfSavedSearchSummary>(this.context.apiUrl('saved-searches'), {
          params: { limit: 500 },
        })
        .pipe(
          map((page) =>
            page.items.map((s) => ({
              savedSearchId: s.savedSearchId,
              name: s.name,
              lastHitCount: s.lastHitCount === null ? null : Number(s.lastHitCount),
            })),
          ),
        ),
    );
  }

  freeze(
    request: FreezeRequest,
    purpose: SnapshotResourcePurpose,
    idempotencyKey: string,
  ): Promise<FrozenSet> {
    const body: CreateSnapshotRequest = {
      purpose,
      savedSearchId: request.savedSearchId,
      expand: { family: request.family, duplicates: request.duplicates },
      ...(request.name ? { name: request.name } : {}),
    };
    return firstValueFrom(
      this.http
        .post<SnapshotResource>(this.context.apiUrl('snapshots'), body, {
          headers: { 'Idempotency-Key': idempotencyKey },
        })
        .pipe(map(toFrozenSet)),
    );
  }

  frozenSet(snapshotId: string): Promise<FrozenSet> {
    return firstValueFrom(
      this.http
        .get<SnapshotResource>(this.context.apiUrl('snapshots', snapshotId))
        .pipe(map(toFrozenSet)),
    );
  }
}

export function toFrozenSet(s: SnapshotResource): FrozenSet {
  return {
    snapshotId: s.snapshotId,
    name: s.name,
    purpose: s.purpose,
    status: s.status,
    statusReason: s.statusReason ?? null,
    documentCount: s.documentCount === null ? null : Number(s.documentCount),
    related: ['Family', 'Duplicate', 'Thread'].reduce(
      (sum, reason) => sum + Number(s.inclusionCounts?.[reason] ?? 0),
      0,
    ),
    frozenAt: (s.selectedAt ?? s.materializedAt ?? s.createdAt ?? null) as string | null,
  };
}

/** A fresh key per user action; a retry of the same action reuses it. */
export function newIdempotencyKey(): string {
  return (
    globalThis.crypto?.randomUUID?.() ?? `${Date.now()}-${Math.random().toString(36).slice(2)}`
  );
}
