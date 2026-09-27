namespace Lattice.Cli;

/// <summary>
/// A command-line usage error: the invocation itself is wrong, so Lattice refuses
/// to start rather than to guess.
/// </summary>
/// <remarks>
/// <para>
/// This is a separate type from <see cref="ArgumentException"/> so that the
/// <c>--agent-cmd</c> surface can carry the exit status the spec assigns it —
/// <see cref="ExitCode"/> — without changing the status any other command
/// already returns for a bad argument. The distinction is not cosmetic: a
/// program that cannot be found, a command line that cannot be split, and a
/// candidate selector given twice are all decisions made <em>before</em> a single
/// match is played, while a missing <c>--trajectory</c> file is discovered in the
/// middle of doing the work. The first kind is a usage error, the second is a
/// runtime failure, and the exit status says which.
/// </para>
/// <para>
/// Nothing about this type is specific to external agents; it is the CLI's way of
/// saying "this command line is not runnable", and it is what makes §3.3's "no
/// artifact is written" enforceable — the throw happens on the way in, and the
/// artifact is written on the way out.
/// </para>
/// </remarks>
public sealed class UsageError : Exception
{
    /// <summary>The exit status a usage error reports (§3.3).</summary>
    public const int ExitCode = 2;

    /// <summary>Creates a usage error with the message shown to the user.</summary>
    public UsageError(string message)
        : base(message)
    {
    }

    /// <summary>Creates a usage error that wraps the failure it was caused by.</summary>
    public UsageError(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
