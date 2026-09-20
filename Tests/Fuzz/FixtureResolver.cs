namespace Lattice.Tests.Fuzz;

/// <summary>
/// Resolves fixture paths repo-relative from the test output directory. Tests
/// run out of <c>Tests/bin/&lt;Config&gt;/net8.0</c>, so the resolver walks up
/// from <see cref="AppContext.BaseDirectory"/> looking for the
/// <c>Tests/fixtures</c> tree rather than depending on the ambient working
/// directory of the test host.
/// </summary>
internal static class FixtureResolver
{
    private const int MaxDepth = 12;

    /// <summary>
    /// The absolute path of <paramref name="relativePath"/> under
    /// <c>Tests/fixtures</c>, or a <see cref="FileNotFoundException"/> listing
    /// the search origin when it cannot be located.
    /// </summary>
    public static string Fixture(string relativePath)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        for (var depth = 0; directory is not null && depth < MaxDepth; depth++)
        {
            foreach (var candidate in new[]
                     {
                         Path.Combine(directory.FullName, "Tests", "fixtures", relativePath),
                         Path.Combine(directory.FullName, "fixtures", relativePath),
                     })
            {
                if (File.Exists(candidate))
                {
                    return Path.GetFullPath(candidate);
                }
            }

            directory = directory.Parent;
        }

        throw new FileNotFoundException(
            $"Fixture '{relativePath}' not found while searching upward from {AppContext.BaseDirectory}.");
    }
}