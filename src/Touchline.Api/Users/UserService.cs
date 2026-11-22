using Microsoft.EntityFrameworkCore;
using Npgsql;
using Touchline.Api.Data;
using Touchline.Api.Models;
using Touchline.Api.Services;

namespace Touchline.Api.Users;

/// <summary>
/// Links a Supabase Auth user to a row in app.users.
/// Supabase Auth holds credentials; this API never sees or stores passwords.
/// Depends on proposed Flyway migration V033 (docs/proposed-migrations).
/// </summary>
public sealed class UserService(TouchlineDbContext db)
{
    public async Task<AppUser> GetOrCreateAsync(Guid authUserId, string email, CancellationToken ct = default)
    {
        try
        {
            var existing = await FindAsync(authUserId, ct);
            if (existing is not null)
            {
                return existing;
            }

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

                // Two first requests raced: the other one created the row.
                var raced = await FindAsync(authUserId, ct);
                if (raced is not null)
                {
                    return raced;
                }

                // The email belongs to an existing app.users row with no auth link.
                // Not linked automatically: matching accounts on email alone is unsafe.
                throw new UserConflictException(
                    "An existing account already uses this email address. Ask an admin to link it.", ex);
            }
        }
        catch (Exception ex) when (Pg(ex)?.SqlState == PostgresErrorCodes.UndefinedColumn)
        {
            throw new SchemaNotReadyException(
                "app.users has no auth_user_id column yet. Apply migration V033 (see docs/proposed-migrations).", ex);
        }
    }

    // Projected so the query doesn't need is_admin (proposed V034); /me works with V033 alone.
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

    private static PostgresException? Pg(Exception? ex)
    {
        for (var e = ex; e is not null; e = e.InnerException)
        {
            if (e is PostgresException pg) return pg;
        }
        return null;
    }
}
