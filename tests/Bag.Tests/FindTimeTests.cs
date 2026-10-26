using Bag;

namespace Bag.Tests;

public sealed class FindTimeTests
{
    [Theory]
    [InlineData("2026-10-26 13:00")]
    [InlineData("2026-10-26T13:00:00")]
    [InlineData("2026-10-26T13:00:00+09:00")]
    [InlineData("2026-10-26T04:00:00Z")]
    [InlineData("2026-10-25T23:00:00-05:00")]
    public void InputDefaultsToJapanAndExplicitOffsetsUseTheSameInstant(string text)
    {
        Assert.True(FindTime.TryParseInput(text, out DateTimeOffset actual));
        Assert.Equal(new DateTimeOffset(2026, 10, 26, 4, 0, 0, TimeSpan.Zero), actual);
        Assert.Equal(TimeSpan.Zero, actual.Offset);
        Assert.Equal("2026-10-26 13:00:00 +09:00", FindTime.FormatJapan(actual));
    }

    [Fact]
    public void DateOnlyMeansJapanMidnightAndFractionalSecondsArePreserved()
    {
        Assert.True(FindTime.TryParseInput("2026-10-26", out DateTimeOffset midnight));
        Assert.Equal(new DateTimeOffset(2026, 10, 25, 15, 0, 0, TimeSpan.Zero), midnight);
        Assert.True(FindTime.TryParseInput("2026-10-26 13:00:00.1234567", out DateTimeOffset precise));
        Assert.Equal("2026-10-26 13:00:00.1234567 +09:00", FindTime.FormatJapan(precise));
    }

    [Theory]
    [InlineData("")]
    [InlineData("tomorrow")]
    [InlineData("13:00")]
    [InlineData("10/26/2026")]
    [InlineData("2026-02-30 13:00")]
    [InlineData("2026-10-26T13:00:00+25:00")]
    [InlineData("0001-01-01 00:00")]
    public void InvalidOrAmbiguousInputIsRejected(string text)
    {
        Assert.False(FindTime.TryParseInput(text, out _));
    }

    [Fact]
    public void StoredValuesNeverInferAMissingTimezone()
    {
        Assert.Null(FindTime.ParseStored("2026-10-26T13:00:00"));
        Assert.NotNull(FindTime.ParseStored("2026-10-26T13:00:00Z"));
    }

    [Fact]
    public void StoredHighPrecisionTimestampUsesTheImportersInterpretation()
    {
        DateTimeOffset? time = FindTime.ParseStored("2026-10-26T04:00:00.123456789Z");
        Assert.NotNull(time);
        Assert.Equal("2026-10-26 13:00:00.1234568 +09:00", FindTime.FormatJapan(time.Value));
    }

    [Fact]
    public void StoredTimeOutsideTheJapanCalendarRangeIsUnknown()
    {
        Assert.Null(FindTime.ParseStored("9999-12-31T23:00:00Z"));
        Assert.NotNull(FindTime.ParseStored("9999-12-31T23:00:00+09:00"));
    }
}
