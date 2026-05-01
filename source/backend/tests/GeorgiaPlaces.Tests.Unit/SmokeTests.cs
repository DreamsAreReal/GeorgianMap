using FluentAssertions;
using Xunit;

namespace GeorgiaPlaces.Tests.Unit;

/// <summary>
/// Trivial smoke tests proving xUnit + FluentAssertions wire up.
/// Real domain tests will replace these.
/// </summary>
public class SmokeTests
{
    [Fact]
    public void Domain_assembly_marker_exists()
    {
        typeof(GeorgiaPlaces.Domain.AssemblyMarker).Should().NotBeNull();
    }

    [Theory]
    [InlineData(1, 2, 3)]
    [InlineData(2, 3, 5)]
    public void Math_still_works(int a, int b, int expected)
    {
        (a + b).Should().Be(expected);
    }
}
