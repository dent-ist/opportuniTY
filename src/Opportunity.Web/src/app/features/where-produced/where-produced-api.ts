import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { firstValueFrom, map } from 'rxjs';
import { ApiConfiguration } from '../../core/api/generated/api-configuration';
import { getWhereProduced } from '../../core/api/generated/fn/productions/get-where-produced';
import type { WhereProducedResource } from '../../core/api/generated/models';
import { WorkspaceContext } from '../../core/workspace/workspace-context';

// Where-produced lookup and Bates cross-reference (E13-T04) as a port over `GET …/documents/{id}/where-produced`
// and the gateway download `GET …/productions/bates-cross-reference?documentId=`.

export type ProducedRelation = 'self' | 'family' | 'duplicate';

/** One document's Bates numbers in one produced (finalized or voided) production. */
export interface ProducedItem {
  readonly productionId: string;
  readonly productionName: string;
  readonly productionVersion: number;
  readonly voided: boolean;
  readonly producedAt: string;
  readonly voidedAt: string | null;
  readonly volumes: readonly string[];
  readonly documentId: string;
  readonly controlNumber: string;
  readonly relations: readonly ProducedRelation[];
  readonly output: 'image' | 'native' | 'placeholder';
  readonly prodBegBates: string;
  readonly prodEndBates: string;
  readonly prodBegAttach: string;
  readonly prodEndAttach: string;
}

export interface WhereProduced {
  readonly documentId: string;
  /** The document, its family and its duplicates the caller may see (others are omitted entirely, Q-52). */
  readonly documents: number;
  readonly items: readonly ProducedItem[];
  readonly truncated: boolean;
}

@Injectable()
export abstract class WhereProducedApi {
  abstract lookup(documentId: string): Promise<WhereProduced>;
  /** The lookup as CSV through the protected-content gateway (audited download). */
  abstract exportUrl(documentId: string): string;
}

@Injectable()
export class HttpWhereProducedApi extends WhereProducedApi {
  private readonly http = inject(HttpClient);
  private readonly rootUrl = inject(ApiConfiguration).rootUrl;
  private readonly context = inject(WorkspaceContext);

  lookup(documentId: string): Promise<WhereProduced> {
    return firstValueFrom(
      getWhereProduced(this.http, this.rootUrl, {
        workspaceId: this.context.workspaceId,
        documentId,
      }).pipe(map((r) => toWhereProduced(r.body))),
    );
  }

  exportUrl(documentId: string): string {
    return `${this.context.apiUrl('productions', 'bates-cross-reference')}?documentId=${encodeURIComponent(documentId)}`;
  }
}

export function toWhereProduced(r: WhereProducedResource): WhereProduced {
  return {
    documentId: String(r.documentId),
    documents: r.documents?.length ?? 0,
    truncated: !!r.truncated,
    items: (r.items ?? []).map((i) => ({
      productionId: String(i.productionId),
      productionName: i.productionName,
      productionVersion: Number(i.productionVersion),
      voided: i.productionStatus === 'voided',
      producedAt: String(i.producedAt),
      voidedAt: i.voidedAt ? String(i.voidedAt) : null,
      volumes: i.volumes ?? [],
      documentId: String(i.documentId),
      controlNumber: i.controlNumber ?? '',
      relations: (i.relations ?? []) as ProducedRelation[],
      output: i.output ?? 'image',
      prodBegBates: i.prodBegBates,
      prodEndBates: i.prodEndBates,
      prodBegAttach: i.prodBegAttach,
      prodEndAttach: i.prodEndAttach,
    })),
  };
}
