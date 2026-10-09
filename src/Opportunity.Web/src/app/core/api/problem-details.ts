import { HttpErrorResponse } from '@angular/common/http';

/** RFC 9457 problem details as the API returns them (ADR-019 §2.4). */
export interface ProblemDetails {
  type?: string;
  title?: string;
  status?: number;
  detail?: string;
  instance?: string;
  /** Stable machine code; the suffix of `urn:opportunity:problem:<code>`. */
  code?: string;
  traceId?: string;
  /** Validation errors: field → messages. */
  errors?: Record<string, string[]>;
  [extension: string]: unknown;
}

const URN_PREFIX = 'urn:opportunity:problem:';

/** Client-side codes for failures that never reached the API. */
export const CLIENT_PROBLEM = {
  network: 'network-unavailable',
  unexpected: 'unexpected-response',
} as const;

/**
 * The single error type the UI handles for API calls: every HTTP failure is normalised to it by
 * `problemDetailsInterceptor`, so components never parse `HttpErrorResponse` themselves.
 */
export class ApiError extends Error {
  constructor(
    readonly status: number,
    readonly problem: ProblemDetails,
    /** Seconds from `Retry-After` on 429/503, if present. */
    readonly retryAfterSeconds?: number,
  ) {
    super(problem.title ?? `HTTP ${status}`);
    this.name = 'ApiError';
  }

  /** The stable problem code (`validation`, `version-conflict`, …) or a client code. */
  get code(): string {
    return (
      problemCode(this.problem) ??
      (this.status === 0 ? CLIENT_PROBLEM.network : `http-${this.status}`)
    );
  }

  get traceId(): string | undefined {
    return this.problem.traceId;
  }
}

export function problemCode(problem: ProblemDetails): string | undefined {
  if (problem.code) return problem.code;
  return problem.type?.startsWith(URN_PREFIX) ? problem.type.slice(URN_PREFIX.length) : undefined;
}

function isProblem(body: unknown): body is ProblemDetails {
  return (
    typeof body === 'object' &&
    body !== null &&
    ('title' in body || 'type' in body || 'status' in body)
  );
}

export function toApiError(error: unknown): ApiError {
  if (error instanceof ApiError) return error;
  if (!(error instanceof HttpErrorResponse)) {
    return new ApiError(0, { title: 'Unexpected error', code: CLIENT_PROBLEM.unexpected });
  }
  const retryAfter = Number(error.headers?.get('Retry-After'));
  const retryAfterSeconds = Number.isFinite(retryAfter) && retryAfter > 0 ? retryAfter : undefined;
  if (error.status === 0) {
    return new ApiError(0, { title: 'Cannot reach the server', code: CLIENT_PROBLEM.network });
  }
  let body: unknown = error.error;
  if (typeof body === 'string') {
    try {
      body = JSON.parse(body);
    } catch {
      body = undefined;
    }
  }
  // A 2xx/3xx "error" means the body could not be read as the expected JSON (e.g. an HTML page because /api is
  // not routed to the API); its status text ("OK") must never become the message.
  const problem: ProblemDetails = isProblem(body)
    ? body
    : error.status < 400
      ? {
          title: 'Unexpected response from the server',
          status: error.status,
          code: CLIENT_PROBLEM.unexpected,
        }
      : { title: error.statusText || `HTTP ${error.status}`, status: error.status };
  return new ApiError(error.status, problem, retryAfterSeconds);
}

export interface UserFacingError {
  title: string;
  detail: string;
  /** Support reference (the API trace id), shown so users can quote it. */
  reference?: string;
  retryable: boolean;
}

/**
 * Plain-language message for an API failure (ADR-018 §5). Never shows raw server text for 5xx; 404 is
 * phrased so it does not disclose whether a resource exists (ADR-019 §2.3).
 */
export function describeError(error: ApiError): UserFacingError {
  const reference = error.traceId;
  const base = { reference };
  switch (error.code) {
    case CLIENT_PROBLEM.network:
      return {
        ...base,
        title: 'Cannot reach the server',
        detail: 'Check your connection and try again.',
        retryable: true,
      };
    case CLIENT_PROBLEM.unexpected:
      return {
        ...base,
        title: 'Unexpected response from the server',
        detail:
          'The server answered with something the app could not read. Reload the page; if it keeps happening, the API may not be reachable at /api (check the reverse proxy).',
        retryable: true,
      };
    case 'validation':
      return {
        ...base,
        title: 'Some values need attention',
        detail: error.problem.detail ?? 'Correct the highlighted fields and try again.',
        retryable: false,
      };
    case 'version-conflict':
      return {
        ...base,
        title: 'Changed by someone else',
        detail:
          'This item was updated after you opened it. Reload to see the latest version, then reapply your change.',
        retryable: false,
      };
    case 'preservation-locked':
      return {
        ...base,
        title: 'Blocked by a legal hold',
        detail:
          'This workspace is under a legal hold. Nothing in it can be deleted until every hold is released.',
        retryable: false,
      };
    case 'second-person-required':
      return {
        ...base,
        title: 'Another person must approve',
        detail:
          error.problem.detail ??
          'You requested this release, so another person who manages legal holds must approve it.',
        retryable: false,
      };
    case 'idempotency-key-reuse':
      return {
        ...base,
        title: 'Request already submitted',
        detail: 'This request was already sent with different values. Start the action again.',
        retryable: false,
      };
  }
  switch (error.status) {
    case 401:
      return {
        ...base,
        title: 'Your session has ended',
        detail: 'Sign in again to continue.',
        retryable: false,
      };
    case 403:
      return {
        ...base,
        title: 'No access',
        detail: 'You do not have permission for this action.',
        retryable: false,
      };
    case 404:
      return {
        ...base,
        title: 'Not available',
        detail: 'It does not exist or you do not have access to it.',
        retryable: false,
      };
    case 412:
      return {
        ...base,
        title: 'Changed by someone else',
        detail: 'Reload to see the latest version, then reapply your change.',
        retryable: false,
      };
    case 429:
      return {
        ...base,
        title: 'Too many requests',
        detail: error.retryAfterSeconds
          ? `Try again in ${error.retryAfterSeconds} seconds.`
          : 'Wait a moment and try again.',
        retryable: true,
      };
  }
  if (error.status >= 500) {
    return {
      ...base,
      title: 'Something went wrong',
      detail:
        'The server could not complete the request. Try again; if it keeps failing, contact support with the reference below.',
      retryable: true,
    };
  }
  return {
    ...base,
    title: error.problem.title ?? 'Request failed',
    detail: error.problem.detail ?? '',
    retryable: false,
  };
}
