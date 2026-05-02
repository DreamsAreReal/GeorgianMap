using FluentAssertions;
using GeorgiaPlaces.Domain.Places;
using Xunit;

namespace GeorgiaPlaces.Tests.Unit.Places;

public class PlaceTests
{
    [Fact]
    public void Create_initializes_freshness_to_half()
    {
        var p = Place.Create("Икалто", new Coordinates(41.85, 45.21), PlaceCategory.From("monastery"));
        p.DataFreshnessScore.Should().Be(0.5);
        p.Hidden.Should().BeFalse();
        p.Attributes.Should().BeEmpty();
    }

    [Fact]
    public void Create_with_attributes_copies_them()
    {
        var attrs = new Dictionary<string, object?> { ["free"] = true, ["dogs"] = "none" };
        var p = Place.Create("Test", new Coordinates(42, 44), PlaceCategory.From("park"), attrs);
        p.Attributes["free"].Should().Be(true);
        p.Attributes["dogs"].Should().Be("none");
    }

    [Fact]
    public void Hide_sets_flag_and_reason_and_updates_timestamps()
    {
        var p = Place.Create("Test", new Coordinates(42, 44), PlaceCategory.From("park"));
        var before = p.UpdatedAt;
        p.Hide("dmca");
        p.Hidden.Should().BeTrue();
        p.HiddenReason.Should().Be("dmca");
        p.HiddenAt.Should().NotBeNull();
        p.UpdatedAt.Should().BeOnOrAfter(before);
    }

    [Fact]
    public void Hide_is_idempotent()
    {
        var p = Place.Create("Test", new Coordinates(42, 44), PlaceCategory.From("park"));
        p.Hide("dmca");
        var first = p.HiddenAt;
        p.Hide("other reason");
        p.HiddenReason.Should().Be("dmca");           // not overwritten
        p.HiddenAt.Should().Be(first);                  // timestamp not bumped
    }

    [Fact]
    public void Unhide_clears_state()
    {
        var p = Place.Create("Test", new Coordinates(42, 44), PlaceCategory.From("park"));
        p.Hide("dmca");
        p.Unhide();
        p.Hidden.Should().BeFalse();
        p.HiddenReason.Should().BeNull();
        p.HiddenAt.Should().BeNull();
    }

    [Theory]
    [InlineData(91, 0)]
    [InlineData(-91, 0)]
    [InlineData(0, 181)]
    [InlineData(0, -181)]
    public void Coordinates_out_of_range_throw(double lat, double lng)
    {
        Action act = () => _ = new Coordinates(lat, lng);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Coordinates_at_exact_bounds_pass()
    {
        new Coordinates(90, 180).Latitude.Should().Be(90);
        new Coordinates(-90, -180).Longitude.Should().Be(-180);
    }

    [Theory]
    [InlineData("monastery")]
    [InlineData("waterfall")]
    [InlineData("aquapark")]
    public void Known_category_creates(string value)
    {
        PlaceCategory.From(value).Value.Should().Be(value);
        PlaceCategory.IsKnown(value).Should().BeTrue();
    }

    [Fact]
    public void Unknown_category_throws()
    {
        Action act = () => PlaceCategory.From("not_a_real_category");
        act.Should().Throw<ArgumentException>();
    }
}
