using Lattice.Environment;
using Xunit;

namespace Lattice.Tests.Environment;

public class RngTests
{
    private const int Draws = 10_000;

    [Fact]
    public void SameUlongSeed_ProducesIdenticalSequences()
    {
        var a = new Rng(0xDEADBEEFCAFEF00DUL);
        var b = new Rng(0xDEADBEEFCAFEF00DUL);

        for (var i = 0; i < Draws; i++)
        {
            Assert.Equal(a.Next(), b.Next());
            Assert.Equal(a.NextDouble(), b.NextDouble());
        }
    }

    [Fact]
    public void SameIntSeed_ProducesIdenticalSequences()
    {
        var a = new Rng(123_456_789);
        var b = new Rng(123_456_789);

        for (var i = 0; i < Draws; i++)
        {
            Assert.Equal(a.Next(), b.Next());
            Assert.Equal(a.NextDouble(), b.NextDouble());
        }
    }

    [Fact]
    public void DifferentSeeds_DivergeWithinAWindow()
    {
        var a = new Rng(1UL);
        var b = new Rng(2UL);

        var diverged = false;
        for (var i = 0; i < 16 && !diverged; i++)
        {
            diverged = a.Next() != b.Next();
        }

        Assert.True(diverged);
    }

    [Fact]
    public void SameUlongSeed_BoundedNext_ProducesIdenticalSequences()
    {
        var a = new Rng(0x0DDBA11UL);
        var b = new Rng(0x0DDBA11UL);

        for (var i = 0; i < Draws; i++)
        {
            Assert.Equal(a.Next(0, 1024), b.Next(0, 1024));
            Assert.Equal(a.Next(3, 17), b.Next(3, 17));
        }
    }

    [Fact]
    public void BoundedNext_WithEqualBounds_ReturnsTheBound()
    {
        var rng = new Rng(42UL);
        for (var i = 0; i < Draws; i++)
        {
            Assert.Equal(7, rng.Next(7, 7));
        }
    }

    [Fact]
    public void HasNoPublicParameterlessConstructor()
    {
        var constructors = typeof(Rng).GetConstructors();
        Assert.DoesNotContain(constructors, c => c.GetParameters().Length == 0);
    }
}