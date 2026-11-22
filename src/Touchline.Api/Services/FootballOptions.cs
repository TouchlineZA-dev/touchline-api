using Touchline.Api.Business;

namespace Touchline.Api.Services;

/// <summary>Bound from the "Football" configuration section.</summary>
public sealed class FootballOptions
{
    public const string SectionName = "Football";

    /// <summary>Sportmonks competition id of the league the fantasy game runs on (PSL = 806).</summary>
    public long PslSportmonksCompetitionId { get; set; } = SportmonksCodes.PslCompetitionId;
}
