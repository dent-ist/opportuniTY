import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable, map } from 'rxjs';
import { ApiConfiguration } from '../api/generated/api-configuration';
import { validateQuery } from '../api/generated/fn/opportunity-api/validate-query';
import type { QueryValidationDiagnostic, QueryValidationResult } from '../api/generated/models';
import { WorkspaceContext } from '../workspace/workspace-context';

export interface QueryDiagnostic {
  readonly code: string;
  readonly message: string;
  readonly start: number;
  readonly end: number;
  readonly expected: readonly string[];
}

export interface QueryCheck {
  /** The text that was checked. */
  readonly query: string;
  readonly valid: boolean;
  /** Canonical text of the query as the server reads it (implicit AND made explicit). */
  readonly normalized: string | null;
  readonly errors: readonly QueryDiagnostic[];
  readonly warnings: readonly QueryDiagnostic[];
}

/**
 * Parses query text on the server (`POST …/query-validations`, ADR-008 R15) and returns positioned
 * diagnostics. Workspace-scoped: provided beside the component that uses it.
 */
@Injectable()
export class QueryValidator {
  private readonly http = inject(HttpClient);
  private readonly rootUrl = inject(ApiConfiguration).rootUrl;
  private readonly workspaceId = inject(WorkspaceContext).workspaceId;

  check(query: string): Observable<QueryCheck> {
    return validateQuery(this.http, this.rootUrl, {
      workspaceId: this.workspaceId,
      body: { query },
    }).pipe(map((response) => toCheck(query, response.body)));
  }
}

function toCheck(query: string, result: QueryValidationResult): QueryCheck {
  return {
    query,
    valid: result.valid,
    normalized: result.normalized ?? null,
    errors: result.errors.map(toDiagnostic),
    warnings: result.warnings.map(toDiagnostic),
  };
}

function toDiagnostic(d: QueryValidationDiagnostic): QueryDiagnostic {
  return {
    code: d.code,
    message: d.message,
    start: Number(d.span.start),
    end: Number(d.span.end),
    expected: d.expected ?? [],
  };
}
