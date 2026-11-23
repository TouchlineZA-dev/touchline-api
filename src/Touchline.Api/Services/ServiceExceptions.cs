namespace Touchline.Api.Services;

/// <summary>The database is missing a column or table this code expects (a migration has not been applied).</summary>
public sealed class SchemaNotReadyException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>A unique value (e.g. email) already belongs to another user.</summary>
/// <param name="code">Stable machine-readable reason, returned to clients as problem-details "code".</param>
public sealed class UserConflictException(string message, string code, Exception? inner = null) : Exception(message, inner)
{
    public const string LegacyEmailUnlinked = "legacy_email_unlinked";
    public const string EmailInUse = "email_in_use";

    public string Code { get; } = code;
}

/// <summary>One or more fantasy rules are broken. Returned as 400 listing every broken rule.</summary>
public sealed class RuleViolationException(IReadOnlyList<string> errors, string title = "Some rules aren't met")
    : Exception(string.Join(" ", errors))
{
    public IReadOnlyList<string> Errors { get; } = errors;
    public string Title { get; } = title;

    public RuleViolationException(string error) : this([error]) { }
}

/// <summary>404.</summary>
public sealed class NotFoundException(string message) : Exception(message);

/// <summary>403: signed in, but not allowed (e.g. not an admin, not a league member).</summary>
public sealed class ForbiddenException(string message) : Exception(message);

/// <summary>409. Current carries the latest state when the client's copy is out of date (If-Match).</summary>
public sealed class ConflictException(string message, string title = "Conflict", object? current = null) : Exception(message)
{
    public string Title { get; } = title;
    public object? Current { get; } = current;
}

/// <summary>
/// The lineup and substitution codes in Business/SportmonksCodes.cs aren't confirmed against the
/// database yet, so minutes (and everything built on them) can't be worked out.
/// </summary>
public sealed class SportmonksCodesNotConfirmedException() : Exception(
    "Minutes can't be worked out yet: the lineup type and substitution codes in Business/SportmonksCodes.cs " +
    "haven't been confirmed against the database. Scoring, live points and pricing are unavailable until they are.");

/// <summary>
/// An error with a stable machine-readable "code" in the problem details, so the app doesn't parse
/// English text (e.g. username_taken, account_suspended, account_deleted, reauthentication_required).
/// </summary>
public sealed class CodedException(int status, string code, string title, string detail) : Exception(detail)
{
    public const string UsernameTaken = "username_taken";
    public const string AccountSuspended = "account_suspended";
    public const string AccountDeleted = "account_deleted";
    public const string ReauthenticationRequired = "reauthentication_required";

    public int Status { get; } = status;
    public string Code { get; } = code;
    public string Title { get; } = title;
}
