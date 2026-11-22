using Microsoft.EntityFrameworkCore;
using Npgsql;
using Touchline.Api.Data;
using Touchline.Api.Models;
using Touchline.Api.Services;

namespace Touchline.Api.Users;

/// <summary>
/// Links a Supabase Auth user to a row in app.users.
/// Supabase Auth holds credentials and does account linking; this API never sees or stores
/// passwords or provider (Google/Apple) credentials. One Supabase user (one JWT "sub") maps to
/// exactly one app.users row, however many provider identities are linked to it.
/// Depends on proposed Flyway migration V033 (docs/proposed-migrations).
/// </summary>
public sealed class UserService(TouchlineDbContext db)
{
    public async Task<AppUser> GetOrCreateAsync(Guid authUserId, string email, CancellationToken ct = default)
    {
        try
        {
            // The profile is keyed on the Supabase user id only. The email in the token may
            // change (e.g. an Apple relay address, or a fan editing it in Supabase) without
            // changing which profile, and therefore which fantasy teams, the fan owns.
            var existing = await FindAsync(authUserId, ct);
            if (existing is not null)
            {
                return existing;
            }

            await ThrowIfEmailTakenAsync(authUserId, email, ct);

            var user = new AppUser
            {
                AuthUserId = authUserId,
                Email = email,
                // Unique and within VARCHAR(50). A rename endpoint can come later.
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
                // constraint on auth_user_id (V033) is what guarantees a single profile.
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
                "nullable password_hash. Apply migration V033 (see docs/proposed-migrations).", ex);
        }
    }

    private Task<AppUser?> FindAsync(Guid authUserId, CancellationToken ct) =>
        db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.AuthUserId == authUserId, ct);

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
            .Where(u => u.Email.ToLower() == lowered && u.AuthUserId != authUserId)
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
