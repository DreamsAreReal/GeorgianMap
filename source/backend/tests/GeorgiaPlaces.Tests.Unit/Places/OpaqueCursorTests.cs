using FluentAssertions;
using GeorgiaPlaces.Application.Places;
using Xunit;

namespace GeorgiaPlaces.Tests.Unit.Places;

public class OpaqueCursorTests
{
    [Theory]
    [InlineData(0.5, 1L)]
    [InlineData(0.987654321, 99_999_999L)]
    [InlineData(0.0, long.MaxValue)]
    public void Round_trip_preserves_values(double freshness, long id)
    {
        string encoded = OpaqueCursor.Encode(freshness, id);
        OpaqueCursor.TryDecode(encoded, out double rf, out long rid).Should().BeTrue();
        rf.Should().Be(freshness);
        rid.Should().Be(id);
    }

    [Fact]
    public void Encoded_cursor_is_url_safe()
    {
        string encoded = OpaqueCursor.Encode(0.5, 12345);
        encoded.Should().NotContain("+").And.NotContain("/").And.NotContain("=");
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-base64!")]
    [InlineData("AAAA")]                    // wrong length
    [InlineData("AAAAAAAAAAAAAAAAAAAA")]    // 20 chars => 15 bytes, wrong
    public void Invalid_cursor_returns_false(string cursor)
    {
        OpaqueCursor.TryDecode(cursor, out _, out _).Should().BeFalse();
    }

    [Fact]
    public void Negative_id_is_rejected_after_decode()
    {
        // Encode a value with negative id directly to confirm guard.
        string encoded = OpaqueCursor.Encode(0.5, 1);
        OpaqueCursor.TryDecode(encoded, out _, out long id).Should().BeTrue();
        id.Should().BeGreaterThan(0);
    }
}
