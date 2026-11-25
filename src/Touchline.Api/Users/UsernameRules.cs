using System.Text;
using System.Text.RegularExpressions;

namespace Touchline.Api.Users;

/// <summary>
/// Public usernames (rulebook / docs/accounts/policies.md): 3–20 letters, digits or
/// underscore after Unicode NFKC normalisation; unique ignoring case (the canonical key is the
/// NFKC form, case-folded); some names are reserved.
/// </summary>
public static partial class UsernameRules
{
    public const int MinLength = 3;
    public const int MaxLength = 20;

    /// <summary>Reserved names and prefixes (compared on the canonical key).</summary>
    public static readonly IReadOnlyList<string> Reserved =
    [
        "admin", "administrator", "root", "system", "support", "help", "helpdesk", "moderator", "mod", "staff",
        "official", "touchline", "psl", "api", "me", "null", "undefined", "anonymous", "deleted", "user", "everyone",
    ];

    public static readonly IReadOnlyList<string> ReservedPrefixes = ["touchline", "admin", "deleted_", "user_", "official"];

    [GeneratedRegex("^[A-Za-z0-9_]+$")]
    private static partial Regex Allowed();

    public static string Normalise(string? input) => (input ?? "").Trim().Normalize(NormalizationForm.FormKC);

    /// <summary>The unique key: NFKC then case-folded (the allowed characters are ASCII, so invariant lower-casing is a full fold).</summary>
    public static string Canonical(string normalised) => normalised.ToLowerInvariant();

    /// <summary>Every problem with a proposed username, in plain language. Empty when it is valid.</summary>
    public static IReadOnlyList<string> Validate(string? input)
    {
        var name = Normalise(input);
        var errors = new List<string>();
        if (name.Length is < MinLength or > MaxLength) errors.Add($"Usernames are {MinLength} to {MaxLength} characters.");
        if (name.Length > 0 && !Allowed().IsMatch(name)) errors.Add("Use only letters, digits and underscores.");
        var canonical = Canonical(name);
        if (Reserved.Contains(canonical) || ReservedPrefixes.Any(canonical.StartsWith))
        {
            errors.Add("That username is reserved. Choose another.");
        }
        return errors;
    }

    /// <summary>The generated name a profile gets on first sign-in, before the manager chooses one.</summary>
    public static bool IsGenerated(string username) => GeneratedName().IsMatch(username);

    [GeneratedRegex("^user_[0-9a-f]{32}$")]
    private static partial Regex GeneratedName();

    /// <summary>The machine-readable rule name for one of <see cref="Validate"/>'s messages.</summary>
    public static string RuleFor(string message) =>
        message.Contains("reserved", StringComparison.Ordinal) ? "username_reserved"
        : message.StartsWith("Usernames are", StringComparison.Ordinal) ? "username_length"
        : message.StartsWith("Use only", StringComparison.Ordinal) ? "username_characters"
        : "username_invalid";
}
