using FluentAssertions;
using GeorgiaPlaces.Application.Places;
using Xunit;

namespace GeorgiaPlaces.Tests.Unit.Places;

public class PlaceFilterParserTests
{
    [Fact]
    public void All_null_returns_default_filter()
    {
        var r = PlaceFilterParser.Parse(null, null, null, null, null, null, null, null);
        r.IsSuccess.Should().BeTrue();
        r.Value!.Limit.Should().Be(50);
        r.Value!.Categories.Should().BeEmpty();
        r.Value!.Attrs.Should().BeEmpty();
    }

    [Fact]
    public void Bbox_parsed_correctly()
    {
        var r = PlaceFilterParser.Parse("41.0,44.0,42.0,45.0", null, null, null, null, null, null, null);
        r.IsSuccess.Should().BeTrue();
        r.Value!.Bbox.Should().NotBeNull();
        r.Value!.Bbox!.Value.MinLat.Should().Be(41.0);
        r.Value!.Bbox!.Value.MaxLng.Should().Be(45.0);
    }

    [Theory]
    [InlineData("not,a,bbox")]
    [InlineData("1,2,3")]
    [InlineData("44,45,41,40")]    // min > max
    [InlineData("999,0,0,0")]      // out of range
    public void Invalid_bbox_fails(string bbox)
    {
        var r = PlaceFilterParser.Parse(bbox, null, null, null, null, null, null, null);
        r.IsSuccess.Should().BeFalse();
        r.Error!.Value.Field.Should().Be("bbox");
    }

    [Fact]
    public void Near_with_radius_parsed()
    {
        var r = PlaceFilterParser.Parse(null, "41.7,44.8", "5", null, null, null, null, null);
        r.IsSuccess.Should().BeTrue();
        r.Value!.NearPoint.Should().Be((41.7, 44.8));
        r.Value!.RadiusKm.Should().Be(5);
    }

    [Fact]
    public void Near_without_radius_fails()
    {
        var r = PlaceFilterParser.Parse(null, "41.7,44.8", null, null, null, null, null, null);
        r.IsSuccess.Should().BeFalse();
        r.Error!.Value.Field.Should().Be("radius_km");
    }

    [Fact]
    public void Bbox_and_near_together_fails()
    {
        var r = PlaceFilterParser.Parse("41,44,42,45", "41.7,44.8", "5", null, null, null, null, null);
        r.IsSuccess.Should().BeFalse();
        r.Error!.Value.Field.Should().Be("bbox");
    }

    [Fact]
    public void Attrs_dsl_parses_key_value_pairs()
    {
        var r = PlaceFilterParser.Parse(null, null, null, null, "free:true,dogs:none,road:paved", null, null, null);
        r.IsSuccess.Should().BeTrue();
        r.Value!.Attrs.Should().HaveCount(3);
        r.Value!.Attrs["free"].Should().Be("true");
        r.Value!.Attrs["dogs"].Should().Be("none");
    }

    [Fact]
    public void Attrs_too_many_fails()
    {
        string many = string.Join(",", Enumerable.Range(0, 11).Select(i => $"k{i}:v{i}"));
        var r = PlaceFilterParser.Parse(null, null, null, null, many, null, null, null);
        r.IsSuccess.Should().BeFalse();
        r.Error!.Value.Field.Should().Be("attrs");
    }

    [Theory]
    [InlineData("monastery")]
    [InlineData("monastery,viewpoint,waterfall")]
    public void Known_categories_pass(string category)
    {
        var r = PlaceFilterParser.Parse(null, null, null, category, null, null, null, null);
        r.IsSuccess.Should().BeTrue();
        r.Value!.Categories.Should().NotBeEmpty();
    }

    [Fact]
    public void Unknown_category_fails()
    {
        var r = PlaceFilterParser.Parse(null, null, null, "definitely_not_a_real_category", null, null, null, null);
        r.IsSuccess.Should().BeFalse();
        r.Error!.Value.Field.Should().Be("category");
    }

    [Theory]
    [InlineData("0")]
    [InlineData("101")]
    [InlineData("-5")]
    [InlineData("notanumber")]
    public void Invalid_limit_fails(string limit)
    {
        var r = PlaceFilterParser.Parse(null, null, null, null, null, null, limit, null);
        r.IsSuccess.Should().BeFalse();
        r.Error!.Value.Field.Should().Be("limit");
    }
}
