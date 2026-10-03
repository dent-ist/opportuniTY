import { HttpErrorResponse, HttpHeaders } from '@angular/common/http';
import { ApiError, describeError, problemCode, toApiError } from './problem-details';

describe('problem details', () => {
  it('reads the stable code from the extension or the type URN', () => {
    expect(problemCode({ code: 'validation' })).toBe('validation');
    expect(problemCode({ type: 'urn:opportunity:problem:version-conflict' })).toBe(
      'version-conflict',
    );
    expect(problemCode({ type: 'about:blank' })).toBeUndefined();
  });

  it('normalises an RFC 9457 error body', () => {
    const error = toApiError(
      new HttpErrorResponse({
        status: 412,
        error: {
          type: 'urn:opportunity:problem:version-conflict',
          title: 'Precondition failed',
          traceId: 't-1',
        },
      }),
    );
    expect(error).toBeInstanceOf(ApiError);
    expect(error.status).toBe(412);
    expect(error.code).toBe('version-conflict');
    expect(error.traceId).toBe('t-1');
  });

  it('parses a problem body delivered as text', () => {
    const error = toApiError(
      new HttpErrorResponse({
        status: 400,
        error: '{"title":"Bad","code":"validation","errors":{"name":["Required"]}}',
      }),
    );
    expect(error.code).toBe('validation');
    expect(error.problem.errors).toEqual({ name: ['Required'] });
  });

  it('maps a network failure and keeps Retry-After', () => {
    expect(toApiError(new HttpErrorResponse({ status: 0 })).code).toBe('network-unavailable');
    const limited = toApiError(
      new HttpErrorResponse({ status: 429, headers: new HttpHeaders({ 'Retry-After': '7' }) }),
    );
    expect(limited.retryAfterSeconds).toBe(7);
    expect(describeError(limited).detail).toContain('7 seconds');
  });

  it('never shows server text for 5xx and keeps the trace id as a reference', () => {
    const view = describeError(
      new ApiError(500, { title: 'NullReferenceException at Foo.Bar', traceId: 'abc' }),
    );
    expect(view.title).toBe('Something went wrong');
    expect(view.detail).not.toContain('NullReference');
    expect(view.reference).toBe('abc');
    expect(view.retryable).toBe(true);
  });

  it('phrases 404 without disclosing existence', () => {
    expect(describeError(new ApiError(404, {})).detail).toBe(
      'It does not exist or you do not have access to it.',
    );
  });
});
