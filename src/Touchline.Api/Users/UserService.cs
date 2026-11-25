using Microsoft.EntityFrameworkCore;
using Npgsql;
using Touchline.Api.Data;
using Touchline.Api.Models;
using Touchline.Api.Services;

namespace Touchline.Api.Users;

/// <summary>
/// Links a Supabase Auth user to a row in app.users.
/// Supabase Auth holds credentials and does account linking; this API never sees or stores
/// passwords or provider (Google/Apple/Facebook) credentials. One Supabase user (one JWT "sub") maps
/// to exactly one app.users row, however many provider identities are linked to it. Email is
/// optional: it is stored when the token has one and kept up to date, but never used as the key.
/// Depends on docs/migrations/002 (auth_user_id); a missing email is stored as NULL once 009 is applied.
/// </summary>
public sealed class UserService(TouchlineDbContext db, SchemaFeatures schema)
{
    public async Task<AppUser> GetOrCreateAsync(Guid authUserId, string? email, CancellationToken ct = default)
    {
        email = string.IsNullOrWhiteSpace(email) ? null : email.Trim();
        try
        {
            // Keyed on the Supabase user id only. The email may change (Apple relay, an edit in Supabase)
            // or be missing without changing which profile, and so which teams, the fan owns.
            var existing = await FindAsync(authUserId, ct);
            if (existing is not null)
            {
                await ThrowIfDeletedAsync(existing.Id, ct);
                // A stored email is never rewritten by a later token (e.g. an Apple relay address after
                // linking). An account created without one gets it the first time a token carries it.
                if (email is not null && IsMissing(existing.Email))
                {
                    await TryFillEmailAsync(existing, email, ct);
                }
                return existing;
            }

            if (email is not null) await ThrowIfEmailTakenAsync(authUserId, email, ct);

            var user = new AppUser
            {
                AuthUserId = authUserId,
                Email = email ?? await NoEmailPlaceholderAsync(authUserId, ct),
                // Unique and within VARCHAR(50); the manager replaces it with a chosen username (PATCH /me).
                Username = $"user_{authUserId:N}",
                IsActive = true,
            };

            db.Users.Add(user);
            try
            {
                await db.SaveChangesAsync(ct);
                return user;
            }
            catch (DbUpdateException ex) when (Pg(ex)?.SqlState == PostgresErrorCodes.UniqueViolation)
            {
                db.Entry(user).State = EntityState.Detached;

                // Two first requests raced and the other one created the row. The unique
                // constraint on auth_user_id (docs/migrations/002) is what guarantees a single profile.
                var raced = await FindAsync(authUserId, ct);
                if (raced is not null)
                {
                    return raced;
                }

                throw new UserConflictException(
                    "Another Touchline account already uses this email address.",
                    UserConflictException.EmailInUse, ex);
            }
        }
        catch (Exception ex) when (IsSchemaNotReady(ex))
        {
            throw new SchemaNotReadyException(
                "app.users is not ready for Supabase sign-in: it needs a unique auth_user_id column and a " +
                "nullable password_hash. Apply docs/migrations/002_users_auth_link.sql in the Supabase SQL Editor.", ex);
        }
    }

    /// <summary>
    /// Before docs/migrations/009 email is NOT NULL, so a token without one gets a unique address on the
    /// reserved .invalid domain (RFC 2606): it can never receive mail and is replaced by a real address if
    /// a later token carries one. After 009 the column is simply left NULL.
    /// </summary>
    private async Task<string?> NoEmailPlaceholderAsync(Guid authUserId, CancellationToken ct) =>
        await schema.IsNullableAsync("app", "users", "email", ct) ? null : $"{authUserId:N}@no-email.touchline.invalid";

    private static bool IsMissing(string? email) =>
        email is null || email.EndsWith("@no-email.touchline.invalid", StringComparison.OrdinalIgnoreCase);

    /// <summary>Fills in a missing email, unless another account holds that address (then it's left missing).</summary>
    private async Task TryFillEmailAsync(AppUser user, string email, CancellationToken ct)
    {
        var lowered = email.ToLowerInvariant();
        if (await db.Users.AnyAsync(u => u.Id != user.Id && u.Email != null && u.Email.ToLower() == lowered, ct)) return;
        await db.Database.ExecuteSqlAsync($"UPDATE app.users SET email = {email} WHERE id = {user.Id}", ct);
        user.Email = email;
    }

    private async Task ThrowIfDeletedAsync(int userId, CancellationToken ct)
    {
        if (!await schema.HasColumnAsync("app", "users", "deleted_at", ct)) return;
        var deleted = await db.Database
            .SqlQuery<bool>($"SELECT deleted_at IS NOT NULL AS \"Value\" FROM app.users WHERE id = {userId}")
            .SingleAsync(ct);
        if (deleted)
        {
            throw new CodedException(StatusCodes.Status403Forbidden, CodedException.AccountDeleted, "Account deleted",
                "This Touchline account was deleted at its owner's request. Its sign-in can't be reused.");
        }
    }

    // Projected so the query doesn't need is_admin (docs/migrations/003); /me works with 002 alone.
    private Task<AppUser?> FindAsync(Guid authUserId, CancellationToken ct) =>
        db.Users.AsNoTracking()
            .Where(u => u.AuthUserId == authUserId)
            .Select(u => new AppUser
            {
                Id = u.Id,
                Email = u.Email,
                Username = u.Username,
                AuthUserId = u.AuthUserId,
                IsActive = u.IsActive,
                CreatedAt = u.CreatedAt,
                UpdatedAt = u.UpdatedAt,
            })
            .FirstOrDefaultAsync(ct);

    /// <summary>
    /// Refuses to create a second profile for an email that another row already holds.
    /// Compared case-insensitively, because app.users.email is UNIQUE but case-sensitive.
    /// Nothing is written, so the fan or an admin can resolve it and retry. Matching on email
    /// alone is never used to link accounts automatically; that would let anyone who controls
    /// the address in Supabase take over the existing profile and its fantasy teams.
    /// </summary>
    private async Task ThrowIfEmailTakenAsync(Guid authUserId, string email, CancellationToken ct)
    {
        var lowered = email.ToLowerInvariant();
        var holder = await db.Users.AsNoTracking()
            .Where(u => u.Email != null && u.Email.ToLower() == lowered && u.AuthUserId != authUserId)
            .Select(u => new { u.AuthUserId })
            .FirstOrDefaultAsync(ct);

        if (holder is null)
        {
            return;
        }

        throw holder.AuthUserId is null
            ? new UserConflictException(
                "An existing Touchline account already uses this email address but is not linked to a " +
                "sign-in yet. Contact support to link it; your existing teams are kept.",
                UserConflictException.LegacyEmailUnlinked)
            : new UserConflictException(
                "Another Touchline account already uses this email address. Sign in with the method you " +
                "used before, then link this provider from your account settings.",
                UserConflictException.EmailInUse);
    }

    private static bool IsSchemaNotReady(Exception ex) => Pg(ex) switch
    {
        // auth_user_id column missing (V033 not applied).
        { SqlState: PostgresErrorCodes.UndefinedColumn } => true,
        // password_hash still NOT NULL (V033 only partly applied).
        { SqlState: PostgresErrorCodes.NotNullViolation, ColumnName: "password_hash" } => true,
        _ => false,
    };

    private static PostgresException? Pg(Exception? ex)
    {
        for (var e = ex; e is not null; e = e.InnerException)
        {
            if (e is PostgresException pg) return pg;
        }
        return null;
    }
}
