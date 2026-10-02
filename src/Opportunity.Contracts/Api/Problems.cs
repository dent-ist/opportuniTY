namespace Opportunity.Contracts.Api;

/// <summary>
/// Stable RFC 9457 problem codes (ADR-019 §2.4). The problem <c>type</c> is <c>urn:opportunity:problem:&lt;code&gt;</c>
/// and the code is repeated in the <c>code</c> extension. Codes are part of the public API: add, never rename.
/// </summary>
public static class ProblemCodes
{
    public const string TypePrefix = "urn:opportunity:problem:";

    public const string BadRequest = "bad-request";
    public const string Validation = "validation";
    public const string Unauthorized = "unauthorized";
    public const string Forbidden = "forbidden";
    public const string NotFound = "not-found";
    public const string MethodNotAllowed = "method-not-allowed";
    public const string NotAcceptable = "not-acceptable";
    public const string Conflict = "conflict";
    public const string Gone = "gone";
    public const string PayloadTooLarge = "payload-too-large";
    public const string UnsupportedMediaType = "unsupported-media-type";
    public const string UnprocessableContent = "unprocessable-content";
    public const string VersionConflict = "version-conflict";
    public const string PreconditionRequired = "precondition-required";
    public const string IdempotencyKeyMissing = "idempotency-key-missing";
    public const string IdempotencyKeyInvalid = "idempotency-key-invalid";
    public const string IdempotencyKeyReuse = "idempotency-key-reuse";
    public const string IdempotencyKeyInProgress = "idempotency-key-in-progress";
    public const string RateLimited = "rate-limited";
    public const string Internal = "internal-error";
    public const string ServiceUnavailable = "service-unavailable";

    public static string TypeFor(string code) => TypePrefix + code;
}
