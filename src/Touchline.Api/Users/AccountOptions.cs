namespace Touchline.Api.Users;

/// <summary>Account settings ("Accounts" section).</summary>
public sealed class AccountOptions
{
    public const string SectionName = "Accounts";

    /// <summary>Days between username changes. The first choice (replacing the generated name) is always allowed.</summary>
    public int UsernameCooldownDays { get; set; } = 30;

    /// <summary>Sensitive actions (export, delete) need a token issued at most this many minutes ago.</summary>
    public int RecentAuthMinutes { get; set; } = 10;
}
