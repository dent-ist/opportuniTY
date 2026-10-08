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
    public const string CsrfValidationFailed = "csrf-validation-failed";
    public const string StepUpRequired = "step-up-required";

    /// <summary>The query text does not parse or bind; the <c>queryErrors</c> extension lists positioned errors.</summary>
    public const string InvalidQuery = "invalid-query";

    /// <summary>The document is visible, but the requested rendition or page does not exist or may not be served.</summary>
    public const string ContentUnavailable = "content-unavailable";

    public const string RangeNotSatisfiable = "range-not-satisfiable";

    /// <summary>
    /// A coding propagation preview is older than 10 minutes, or the source's coding changed since: preview again
    /// (wave-12 contract spelling).
    /// </summary>
    public const string PreviewStale = "PREVIEW_STALE";

    /// <summary>
    /// ADR-015 D6.5: nobody changes a role, class grant or ethical wall that applies to themselves; another administrator
    /// must make the change.
    /// </summary>
    public const string SelfProtection = "self-protection";

    /// <summary>The change gives up one of the caller's own roles: repeat it with the explicit confirmation (E05-T08).</summary>
    public const string ConfirmationRequired = "confirmation-required";

    /// <summary>The change would remove the workspace's last Workspace Admin assignment (E05-T08).</summary>
    public const string LastAdministrator = "last-administrator";

    public static string TypeFor(string code) => TypePrefix + code;
}
