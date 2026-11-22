namespace Touchline.Api.Services;

/// <summary>The database is missing a column or table this code expects (a migration has not been applied).</summary>
public sealed class SchemaNotReadyException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>A unique value (e.g. email) already belongs to another user.</summary>
public sealed class UserConflictException(string message, Exception? inner = null) : Exception(message, inner);
