using System.Text.Json;

namespace Lattice.Tests.Fuzz;

/// <summary>
/// The typed-exception boundary the fuzz targets must not cross. Malformed or
/// adversarial inputs are allowed to reject gracefully with domain-level
/// exceptions (<see cref="JsonException"/> for corrupt JSON, etc.), but must
/// never escape with an unhandled runtime fault such as
/// <see cref="NullReferenceException"/> or <see cref="IndexOutOfRangeException"/>.
/// </summary>
internal static class ExceptionContract
{
    /// <summary>
    /// True when <paramref name="exception"/> is an intentional, typed rejection
    /// from the parser or validation layers rather than an unhandled crash.
    /// <see cref="ArgumentException"/> covers its
    /// <see cref="ArgumentOutOfRangeException"/> and
    /// <see cref="ArgumentNullException"/> descendants; <see cref="IOException"/>
    /// covers file-surface failures under a non-existent path.
    /// </summary>
    public static bool IsGraceful(Exception exception) => exception switch
    {
        InvalidDataException => true,
        JsonException => true,
        ArgumentException => true,
        InvalidOperationException => true,
        FormatException => true,
        OverflowException => true,
        IOException => true,
        NotSupportedException => true,
        _ => false,
    };
}