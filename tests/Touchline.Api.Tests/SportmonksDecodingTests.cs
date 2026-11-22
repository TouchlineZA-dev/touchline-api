using Touchline.Api.Business;

namespace Touchline.Api.Tests;

public class SportmonksDecodingTests
{
    [Theory]
    [InlineData((short)24, Position.GK)]
    [InlineData((short)25, Position.DEF)]
    [InlineData((short)26, Position.MID)]
    [InlineData((short)27, Position.ATT)]
    [InlineData((short)99, Position.Unknown)]
    public void Position_is_decoded_from_sportmonks_id(short positionId, Position expected) =>
        Assert.Equal(expected, PositionExtensions.FromSportmonks(positionId));

    [Fact]
    public void Missing_position_is_unknown() =>
        Assert.Equal(Position.Unknown, PositionExtensions.FromSportmonks(null));

    [Theory]
    [InlineData((short)5, true)]
    [InlineData((short)7, true)]
    [InlineData((short)8, true)]
    [InlineData((short)1, false)]
    [InlineData((short)22, false)]
    [InlineData((short)10, false)]
    public void Only_states_5_7_8_are_final(short stateId, bool expected) =>
        Assert.Equal(expected, MatchStatusExtensions.IsFinal(stateId));

    [Theory]
    [InlineData((short)5, MatchStatus.Finished)]
    [InlineData((short)22, MatchStatus.Live)]
    [InlineData((short)10, MatchStatus.NoResult)]
    [InlineData((short)12, MatchStatus.NoResult)]
    [InlineData((short)15, MatchStatus.NoResult)]
    [InlineData((short)1, MatchStatus.Scheduled)]
    public void Status_matches_netlify_fixtures_mapping(short stateId, MatchStatus expected) =>
        Assert.Equal(expected, MatchStatusExtensions.FromStateId(stateId));
}
