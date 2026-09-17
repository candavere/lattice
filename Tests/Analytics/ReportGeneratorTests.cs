using System.Globalization;
using Lattice.Analytics;
using Xunit;

namespace Lattice.Tests.Analytics;

public class ReportGeneratorTests
{
    [Fact]
    public void Fixture1_Markdown_PinMatchesRecordedTrajectoryByteForByte()
    {
        var expected =
            "# Lattice Tactical Report\n" +
            "\n" +
            "seed=42 | zones=4 | resources=4 | chokes=4 | agents=2 | ticks=6 | result=completed | winner=agent 0\n" +
            "\n" +
            "## Summary\n" +
            "| agent | score | moves | efficiency | rating | archetype | idle | contended |\n" +
            "|-------|-------|-------|------------|--------|-----------|------|-----------|\n" +
            "| 0 | 3 | 3 | 3/3 (1.00) | A | Rusher | 0 | 0-0 |\n" +
            "| 1 | 1 | 1 | 1/1 (1.00) | A | Rusher | 4 | 0-0 |\n" +
            "\n" +
            "## Contention Events\n" +
            "none\n" +
            "\n" +
            "## Turning Points\n" +
            "- tick 6: agent 0 unassailable (lead 2 > 0 resources left)\n" +
            "\n" +
            "## Resource Timeline\n" +
            "agent 0: t2:R0, t4:R2, t6:R3\n" +
            "agent 1: t2:R1\n" +
            "\n" +
            "## Zone Occupancy (agent-steps)\n" +
            "zone 0:   0  \n" +
            "zone 1:   2  ██\n" +
            "zone 2:   8  ████████\n" +
            "zone 3:   2  ██\n" +
            "\n" +
            "## Edge Traversals\n" +
            "choke 0 (0 -> 1):   1  ████\n" +
            "choke 1 (1 -> 2):   2  ████████\n" +
            "choke 2 (2 -> 3):   1  ████\n" +
            "choke 3 (3 -> 0):   0  \n" +
            "\n" +
            "## Steps Timeline\n" +
            "agent 0: MRMRMR\n" +
            "agent 1: MR....";

        Assert.Equal(expected, ReportGenerator.Report(AnalyticsFixtures.Fixture1()));
    }

    [Fact]
    public void Fixture1_Terminal_PinMatchesRecordedTrajectoryByteForByte()
    {
        var expected =
            "# Lattice Tactical Report\n" +
            "\n" +
            "seed=42 | zones=4 | resources=4 | chokes=4 | agents=2 | ticks=6 | result=completed | winner=agent 0\n" +
            "\n" +
            "## Summary\n" +
            "| agent | score | moves | efficiency | rating | archetype | idle | contended |\n" +
            "|-------|-------|-------|------------|--------|-----------|------|-----------|\n" +
            "| 0 | 3 | 3 | 3/3 (1.00) | A | Rusher | 0 | 0-0 |\n" +
            "| 1 | 1 | 1 | 1/1 (1.00) | A | Rusher | 4 | 0-0 |\n" +
            "\n" +
            "## Contention Events\n" +
            "none\n" +
            "\n" +
            "## Turning Points\n" +
            "- tick 6: agent 0 unassailable (lead 2 > 0 resources left)\n" +
            "\n" +
            "## Resource Timeline\n" +
            "agent 0: t2:R0, t4:R2, t6:R3\n" +
            "agent 1: t2:R1\n" +
            "\n" +
            "## Zone Occupancy (agent-steps)\n" +
            "zone 0:   0  \n" +
            "zone 1:   2  ##\n" +
            "zone 2:   8  ########\n" +
            "zone 3:   2  ##\n" +
            "\n" +
            "## Edge Traversals\n" +
            "choke 0 (0 -> 1):   1  ####\n" +
            "choke 1 (1 -> 2):   2  ########\n" +
            "choke 2 (2 -> 3):   1  ####\n" +
            "choke 3 (3 -> 0):   0  \n" +
            "\n" +
            "## Steps Timeline\n" +
            "agent 0: MRMRMR\n" +
            "agent 1: MR....";

        Assert.Equal(expected, ReportGenerator.TerminalReport(AnalyticsFixtures.Fixture1()));
    }

    [Fact]
    public void Fixture1_Report_IsDeterministicAcrossRuns()
    {
        var first = ReportGenerator.Report(AnalyticsFixtures.Fixture1());
        var second = ReportGenerator.Report(AnalyticsFixtures.Fixture1());
        Assert.Equal(first, second);
    }

    [Fact]
    public void Fixture2_ClassifiesHarvesterAndWanderer()
    {
        var report = ReportGenerator.Report(AnalyticsFixtures.Fixture2());
        Assert.Contains("| 0 | 1 | 1 | 1/1 (1.00) | A | Harvester | 2 | 0-0 |", report);
        Assert.Contains("| 1 | 1 | 3 | 1/3 (0.33) | D | Passive Wanderer | 0 | 0-0 |", report);
    }

    [Fact]
    public void Fixture2_ReportIsInvariantToCulture()
    {
        var original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");
            var report = ReportGenerator.Report(AnalyticsFixtures.Fixture2());
            Assert.Contains("1/3 (0.33)", report);
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    [Fact]
    public void Fixture5_ReportShowsContentionAndComeback()
    {
        var report = ReportGenerator.Report(AnalyticsFixtures.Fixture5());
        Assert.Contains("| 2 | 0 | [0,1] | 0 | [1] |", report);
        Assert.Contains("- tick 5: agent 1 unassailable (lead 1 > 0 resources left)", report);
        Assert.Contains("| 0 | 1 | 2 | 1/2 (0.50) | C | Harvester | 2 | 1-0 |", report);
        Assert.Contains("| 1 | 2 | 1 | 1/1 (1.00) | A | Harvester | 1 | 0-1 |", report);
    }

    [Fact]
    public void Fixture6_ThreeWayScramble_ReportsOneWinnerTwoLosers()
    {
        var report = ReportGenerator.Report(AnalyticsFixtures.Fixture6());
        Assert.Contains("| 2 | 0 | [0,1,2] | 0 | [1,2] |", report);
    }

    [Fact]
    public void Fixture4_ReportListsEveryInsurmountableTick()
    {
        var report = ReportGenerator.Report(AnalyticsFixtures.Fixture4());
        Assert.Contains("- tick 5: agent 0 unassailable (lead 3 > 2 resources left)", report);
        Assert.Contains("- tick 8: agent 0 unassailable (lead 5 > 0 resources left)", report);
        Assert.DoesNotContain("tick 4", report);
    }
}