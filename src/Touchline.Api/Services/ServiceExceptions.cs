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
