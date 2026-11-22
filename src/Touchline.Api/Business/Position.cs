namespace Touchline.Api.Business;

/// <summary>Fantasy position, decoded from the Sportmonks position_id.</summary>
public enum Position
{
    Unknown = 0,
    GK,
    DEF,
    MID,
    ATT,
}

public static class PositionExtensions
{
    /// <summary>Maps a Sportmonks position_id (24–27) to a fantasy position.</summary>
    public static Position FromSportmonks(short? positionId) => positionId switch
    {
        SportmonksCodes.PositionIds.Goalkeeper => Position.GK,
        SportmonksCodes.PositionIds.Defender => Position.DEF,
        SportmonksCodes.PositionIds.Midfielder => Position.MID,
        SportmonksCodes.PositionIds.Attacker => Position.ATT,
        _ => Position.Unknown,
    };
}
