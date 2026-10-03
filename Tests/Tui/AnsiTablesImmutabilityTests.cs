using Lattice.Tui;
using Xunit;

namespace Lattice.Tests.Tui;

/// <summary>
/// The two public colour tables are process-wide globals that
/// <see cref="ColorMapper"/> reads on every styled cell. Published as bare
/// arrays they were mutable through the public surface: a caller could cast back
/// to <c>Rgb[]</c> and rewrite an entry, silently changing every subsequent
/// nearest-colour decision for the life of the process.
///
/// The check goes through <see cref="System.Collections.IList"/> rather than
/// through a compile-time type test, because an <c>is</c> check against the
/// declared type folds to a constant and would assert nothing.
/// </summary>
public class AnsiTablesImmutabilityTests
{
    [Fact]
    public void TheBaseTableIsNotABareArray()
    {
        Assert.NotEqual(typeof(Rgb[]), AnsiTables.Ansi16.GetType());
    }

    [Fact]
    public void ThePaletteIsNotABareArray()
    {
        Assert.NotEqual(typeof(Rgb[]), AnsiTables.Palette256.GetType());
    }

    [Fact]
    public void WritingToTheBaseTableThroughItsPublicSurfaceIsRefused()
    {
        var table = (System.Collections.IList)AnsiTables.Ansi16;

        Assert.Throws<NotSupportedException>(() => table[0] = new Rgb(1, 2, 3));
    }

    [Fact]
    public void AddingToTheBaseTableThroughItsPublicSurfaceIsRefused()
    {
        var table = (System.Collections.IList)AnsiTables.Ansi16;

        Assert.Throws<NotSupportedException>(() => table.Add(new Rgb(1, 2, 3)));
    }

    [Fact]
    public void WritingToThePaletteThroughItsPublicSurfaceIsRefused()
    {
        var table = (System.Collections.IList)AnsiTables.Palette256;

        Assert.Throws<NotSupportedException>(() => table[0] = new Rgb(1, 2, 3));
        Assert.Throws<NotSupportedException>(() => table.Add(new Rgb(1, 2, 3)));
    }

    [Fact]
    public void TheBaseTableStillHasSixteenEntriesInOrder()
    {
        Assert.Equal(16, AnsiTables.Ansi16.Length);
        Assert.Equal(new Rgb(0x00, 0x00, 0x00), AnsiTables.Ansi16[0]);
        Assert.Equal(new Rgb(0x80, 0x00, 0x00), AnsiTables.Ansi16[1]);
        Assert.Equal(new Rgb(0xFF, 0xFF, 0xFF), AnsiTables.Ansi16[15]);
    }

    [Fact]
    public void ThePaletteStillHasTwoHundredAndFiftySixEntries()
    {
        Assert.Equal(256, AnsiTables.Palette256.Length);
        Assert.Equal(AnsiTables.Ansi16[0], AnsiTables.Palette256[0]);
        Assert.Equal(AnsiTables.Ansi16[15], AnsiTables.Palette256[15]);
    }

    [Fact]
    public void RepeatedReadsAgree()
    {
        // The values the mapper depends on must not drift between calls.
        Assert.Equal(AnsiTables.Ansi16[1], AnsiTables.Ansi16[1]);
        Assert.Equal(AnsiTables.Palette256[16], AnsiTables.Palette256[16]);
    }

    [Fact]
    public void TheNearestLookupsStillAgreeWithTheTables()
    {
        Assert.Equal(0, AnsiTables.NearestIndex16(new Rgb(0x00, 0x00, 0x00)));
        Assert.Equal(1, AnsiTables.NearestIndex16(new Rgb(0x80, 0x00, 0x00)));
        Assert.Equal(9, AnsiTables.NearestIndex16(new Rgb(0xFF, 0x00, 0x00)));
        Assert.Equal(15, AnsiTables.NearestIndex256(new Rgb(0xFF, 0xFF, 0xFF)));
    }
}