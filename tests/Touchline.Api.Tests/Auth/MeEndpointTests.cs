using System.Data.Common;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;
using Touchline.Api.Data;
using Touchline.Api.Services;
using Touchline.Api.Users;

namespace Touchline.Api.Tests.Auth;

/// <summary>
/// GET /api/v1/me end to end (real pipeline, real token validation) against an isolated
/// PostgreSQL database. Every test uses fresh Supabase user ids and emails, so tests are
/// independent while sharing one container.
/// </summary>
public sealed class MeEndpointTests(PostgresFixture db) : IClassFixture<PostgresFixture>
{
    private sealed record MeResponse(int Id, string Username, string Email, Guid AuthUserId);

    private static string NewEmail(string domain = "example.com") => $"fan-{Guid.NewGuid():N}@{domain}";

    private async Task<HttpResponseMessage> GetMe(string token, TestApiFactory? api = null) =>
        await (api ?? db.ReadyApi).ClientWithToken(token).GetAsync("/api/v1/me");

    private async Task<MeResponse> GetMeOk(string token)
    {
        var response = await GetMe(token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<MeResponse>())!;
    }

    private Task<long> ProfileCount(Guid sub) =>
        db.ScalarAsync<long>("SELECT count(*) FROM app.users WHERE auth_user_id = $1", sub);

    private Task<int> CreateFantasyTeam(int userId) => db.ScalarAsync<int>("""
        WITH season AS (INSERT INTO football.seasons DEFAULT VALUES RETURNING id)
        INSERT INTO app.fantasy_teams (user_id, season_id, name)
        SELECT $1, id, 'Test XI' FROM season RETURNING id
        """, userId);

    private Task<int> TeamOwner(int teamId) =>
        db.ScalarAsync<int>("SELECT user_id FROM app.fantasy_teams WHERE id = $1", teamId);

    [SkippableFact]
    public async Task First_sign_in_creates_one_profile()
    {
        Skip.If(db.SkipReason is not null, db.SkipReason);
        var sub = Guid.NewGuid();
        var email = NewEmail();

        var me = await GetMeOk(db.Tokens.Create(sub, email, provider: "google"));

        Assert.True(me.Id > 0);
        Assert.Equal(sub, me.AuthUserId);
        Assert.Equal(email, me.Email);
        Assert.Equal($"user_{sub:N}", me.Username);
        Assert.Equal(1, await ProfileCount(sub));
        Assert.Null(await db.ScalarAsync<object>("SELECT password_hash FROM app.users WHERE id = $1", me.Id) as string);
    }

    [SkippableFact]
    public async Task Response_contract_is_unchanged()
    {
        Skip.If(db.SkipReason is not null, db.SkipReason);
        var response = await GetMe(db.Tokens.Create(Guid.NewGuid(), NewEmail()));

        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(["id", "username", "email", "authUserId"],
            json.RootElement.EnumerateObject().Select(p => p.Name).ToArray());
    }

    [SkippableFact]
    public async Task Repeat_sign_in_returns_the_same_profile()
    {
        Skip.If(db.SkipReason is not null, db.SkipReason);
        var sub = Guid.NewGuid();
        var email = NewEmail();

        var first = await GetMeOk(db.Tokens.Create(sub, email));
        var second = await GetMeOk(db.Tokens.Create(sub, email)); // a new session's token

        Assert.Equal(first, second);
        Assert.Equal(1, await ProfileCount(sub));
    }

    [SkippableFact]
    public async Task Concurrent_first_requests_create_exactly_one_profile()
    {
        Skip.If(db.SkipReason is not null, db.SkipReason);
        var sub = Guid.NewGuid();
        var token = db.Tokens.Create(sub, NewEmail(), provider: "apple");

        var responses = await Task.WhenAll(Enumerable.Range(0, 25).Select(_ => GetMe(token)));

        Assert.All(responses, r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));
        var ids = await Task.WhenAll(responses.Select(r => r.Content.ReadFromJsonAsync<MeResponse>()));
        Assert.Single(ids.Select(m => m!.Id).Distinct());
        Assert.Equal(1, await ProfileCount(sub));
    }

    /// <summary>
    /// Forces the losing side of the race deterministically: just before our INSERT runs, a
    /// competing request commits the same profile on another connection. PostgreSQL's unique
    /// constraint rejects ours and the service must return the winner's row.
    /// </summary>
    [SkippableFact]
    public async Task Losing_a_first_sign_in_race_returns_the_winning_profile()
    {
        Skip.If(db.SkipReason is not null, db.SkipReason);
        var sub = Guid.NewGuid();
        var email = NewEmail();
        var competitor = new CompetingInsertInterceptor(db.ReadyConnectionString, sub, email);

        await using var context = new TouchlineDbContext(new DbContextOptionsBuilder<TouchlineDbContext>()
            .UseNpgsql(db.ReadyConnectionString)
            .AddInterceptors(competitor)
            .Options);

        var user = await new UserService(context).GetOrCreateAsync(sub, email);

        Assert.NotNull(competitor.WinnerId);
        Assert.Equal(competitor.WinnerId, user.Id);
        Assert.Equal(1, await ProfileCount(sub));
    }

    [SkippableFact]
    public async Task Two_distinct_fans_get_distinct_profiles()
    {
        Skip.If(db.SkipReason is not null, db.SkipReason);
        var alice = await GetMeOk(db.Tokens.Create(Guid.NewGuid(), NewEmail("gmail.com"), provider: "google"));
        var bob = await GetMeOk(db.Tokens.Create(Guid.NewGuid(), NewEmail("privaterelay.appleid.com"), provider: "apple"));

        Assert.NotEqual(alice.Id, bob.Id);
        Assert.NotEqual(alice.AuthUserId, bob.AuthUserId);
        Assert.NotEqual(alice.Username, bob.Username);
    }

    [SkippableFact]
    public async Task Linked_google_and_apple_identities_share_one_profile_and_its_teams()
    {
        Skip.If(db.SkipReason is not null, db.SkipReason);
        var sub = Guid.NewGuid();
        var googleEmail = NewEmail("gmail.com");

        // Fan signs up with Google and builds a team.
        var beforeLink = await GetMeOk(db.Tokens.Create(sub, googleEmail, provider: "google"));
        var teamId = await CreateFantasyTeam(beforeLink.Id);

        // Fan links Apple in Supabase (same Supabase user, same sub), then signs in with Apple.
        // The token's email may differ (Apple relay); the profile is keyed on sub, not email.
        var afterLink = await GetMeOk(db.Tokens.Create(sub, NewEmail("privaterelay.appleid.com"),
            provider: "google", linkedProviders: ["google", "apple"]));

        Assert.Equal(beforeLink.Id, afterLink.Id);
        Assert.Equal(googleEmail, afterLink.Email); // stored profile is not rewritten
        Assert.Equal(beforeLink.Id, await TeamOwner(teamId));
        Assert.Equal(1, await ProfileCount(sub));
    }

    [SkippableFact]
    public async Task Apple_private_relay_email_creates_a_profile()
    {
        Skip.If(db.SkipReason is not null, db.SkipReason);
        var sub = Guid.NewGuid();
        var relay = $"{Guid.NewGuid():N}{Guid.NewGuid():N}"[..40] + "@privaterelay.appleid.com";

        var me = await GetMeOk(db.Tokens.Create(sub, relay, provider: "apple"));

        Assert.Equal(relay, me.Email);
        Assert.Equal(me, await GetMeOk(db.Tokens.Create(sub, relay, provider: "apple")));
    }

    [SkippableTheory]
    [InlineData(false)]
    [InlineData(true)] // different case: app.users.email UNIQUE is case-sensitive
    public async Task Email_held_by_unlinked_legacy_profile_is_a_recoverable_conflict(bool differentCase)
    {
        Skip.If(db.SkipReason is not null, db.SkipReason);
        var email = NewEmail();
        var legacyId = await db.ScalarAsync<int>("""
            INSERT INTO app.users (email, username, password_hash)
            VALUES ($1, $2, 'legacy-hash') RETURNING id
            """, email, $"legacy_{Guid.NewGuid():N}"[..30]);
        var teamId = await CreateFantasyTeam(legacyId);
        var sub = Guid.NewGuid();

        var response = await GetMe(db.Tokens.Create(sub, differentCase ? email.ToUpperInvariant() : email));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(UserConflictException.LegacyEmailUnlinked, problem.RootElement.GetProperty("code").GetString());

        // Nothing changed: no new profile, legacy row still unlinked, team still owned by it.
        Assert.Equal(0, await ProfileCount(sub));
        Assert.True(await db.ScalarAsync<bool>("SELECT auth_user_id IS NULL FROM app.users WHERE id = $1", legacyId));
        Assert.Equal(legacyId, await TeamOwner(teamId));

        // Recoverable: once support links the legacy row (V033 column), the same token signs in
        // to the legacy profile and keeps its team.
        await db.ScalarAsync<int>("UPDATE app.users SET auth_user_id = $1 WHERE id = $2 RETURNING id", sub, legacyId);
        var linked = await GetMeOk(db.Tokens.Create(sub, email));
        Assert.Equal(legacyId, linked.Id);
        Assert.Equal(legacyId, await TeamOwner(teamId));
    }

    [SkippableFact]
    public async Task Email_held_by_another_signed_in_profile_is_a_conflict()
    {
        Skip.If(db.SkipReason is not null, db.SkipReason);
        var email = NewEmail();
        var first = await GetMeOk(db.Tokens.Create(Guid.NewGuid(), email, provider: "google"));

        // A separate, unlinked Supabase user with the same address.
        var otherSub = Guid.NewGuid();
        var response = await GetMe(db.Tokens.Create(otherSub, email, provider: "apple"));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(UserConflictException.EmailInUse, problem.RootElement.GetProperty("code").GetString());
        Assert.Equal(0, await ProfileCount(otherSub));
        Assert.Equal(first, await GetMeOk(db.Tokens.Create(first.AuthUserId, email)));
    }

    [SkippableFact]
    public async Task Token_without_email_is_400()
    {
        Skip.If(db.SkipReason is not null, db.SkipReason);
        var sub = Guid.NewGuid();
        var response = await GetMe(db.Tokens.Create(sub, email: null));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, await ProfileCount(sub));
    }

    [SkippableFact]
    public async Task Schema_without_V033_returns_503()
    {
        Skip.If(db.SkipReason is not null, db.SkipReason);
        var response = await GetMe(db.Tokens.Create(Guid.NewGuid(), NewEmail()), db.PreV033Api);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Contains("Schema not ready", await response.Content.ReadAsStringAsync());
    }

    [SkippableFact]
    public async Task Schema_with_required_password_hash_returns_503()
    {
        Skip.If(db.SkipReason is not null, db.SkipReason);
        var response = await GetMe(db.Tokens.Create(Guid.NewGuid(), NewEmail()), db.PartialV033Api);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Contains("Schema not ready", await response.Content.ReadAsStringAsync());
    }

    private sealed class CompetingInsertInterceptor(string connectionString, Guid sub, string email) : DbCommandInterceptor
    {
        public int? WinnerId { get; private set; }

        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            if (WinnerId is null && command.CommandText.Contains("INSERT INTO app.users", StringComparison.OrdinalIgnoreCase))
            {
                await using var conn = new NpgsqlConnection(connectionString);
                await conn.OpenAsync(cancellationToken);
                await using var insert = new NpgsqlCommand("""
                    INSERT INTO app.users (auth_user_id, email, username) VALUES ($1, $2, $3) RETURNING id
                    """, conn);
                insert.Parameters.Add(new NpgsqlParameter { Value = sub });
                insert.Parameters.Add(new NpgsqlParameter { Value = email });
                insert.Parameters.Add(new NpgsqlParameter { Value = $"user_{sub:N}" });
                WinnerId = (int)(await insert.ExecuteScalarAsync(cancellationToken))!;
            }
            return result;
        }
    }
}
