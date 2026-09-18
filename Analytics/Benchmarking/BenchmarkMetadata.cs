using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;

namespace Lattice.Analytics.Benchmarking;

/// <summary>
/// The provenance and host facts scoped to one benchmark suite run: the exact
/// source revision under test, when the run happened, the runtime and build
/// configuration, and the hardware the measurements were taken on. Keeping
/// this record in every artifact is what lets a later host decide whether its
/// numbers may be compared against a committed baseline directly (same
/// revision, same OS, same architecture) or must be treated as cross-machine
/// information only.
/// </summary>
public sealed record BenchmarkMetadata(
    string Commit,
    string Timestamp,
    string Runtime,
    string Configuration,
    string Os,
    string Cpu,
    string Architecture,
    int Cores,
    long RamBytes,
    string GcMode);

/// <summary>
/// Samples the host for <see cref="BenchmarkMetadata"/> using only pure .NET
/// runtime facilities: informational assembly versions for the commit,
/// <see cref="RuntimeInformation"/> for runtime/OS/architecture,
/// <see cref="GC.GetGCMemoryInfo"/> for addressable memory,
/// <see cref="System.Runtime.GCSettings"/> for the GC flavor, and
/// <c>/proc/cpuinfo</c> / <c>sysctl</c> / <c>PROCESSOR_IDENTIFIER</c> for the
/// CPU brand. Every probe degrades gracefully to a readable placeholder and
/// every source can be overridden by the caller, so a benchmark artifact is
/// never blocked on an exotic host.
/// </summary>
public static class EnvironmentSample
{
    private const string ReleaseConfiguration = "Release";

    /// <summary>
    /// Captures the environment as of the current process. The commit comes
    /// from <paramref name="commitOverride"/> when supplied, otherwise from the
    /// <c>+sha</c> suffix the SDK embeds in
    /// <see cref="AssemblyInformationalVersionAttribute"/> (falling back to
    /// <c>n/a</c>); the CPU brand comes from
    /// <paramref name="cpuOverride"/> when supplied, otherwise from a platform
    /// probe.
    /// </summary>
    public static BenchmarkMetadata Capture(string? commitOverride, string? cpuOverride)
    {
        return new BenchmarkMetadata(
            ResolveCommit(commitOverride),
            DateTimeOffset.UtcNow.ToString("o", CultureInfo.InvariantCulture),
            RuntimeInformation.FrameworkDescription,
            ReleaseConfiguration,
            RuntimeInformation.OSDescription.Trim(),
            ResolveCpuBrand(cpuOverride),
            RuntimeInformation.ProcessArchitecture.ToString(),
            System.Environment.ProcessorCount,
            GC.GetGCMemoryInfo().TotalAvailableMemoryBytes,
            System.Runtime.GCSettings.IsServerGC ? "Server" : "Workstation");
    }

    private static string ResolveCommit(string? overrideSha)
    {
        if (!string.IsNullOrWhiteSpace(overrideSha))
        {
            return overrideSha.Trim();
        }

        var version = Assembly.GetEntryAssembly()
            ?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion;
        if (version is not null)
        {
            var plus = version.IndexOf('+');
            var sha = plus >= 0 ? version[(plus + 1)..] : string.Empty;
            if (sha.Length > 0)
            {
                return sha;
            }
        }

        return "n/a";
    }

    private static string ResolveCpuBrand(string? cpuOverride)
    {
        if (!string.IsNullOrWhiteSpace(cpuOverride))
        {
            return cpuOverride.Trim();
        }

        var probe = ReadCpuBrand();
        return string.IsNullOrWhiteSpace(probe) ? "unknown" : probe.Trim();
    }

    private static string ReadCpuBrand()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            return RunProbe("/usr/sbin/sysctl", "-n machdep.cpu.brand_string");
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            try
            {
                foreach (var line in File.ReadLines("/proc/cpuinfo"))
                {
                    if (line.StartsWith("model name", StringComparison.Ordinal))
                    {
                        var colon = line.IndexOf(':');
                        return colon >= 0 ? line[(colon + 1)..].Trim() : line;
                    }
                }
            }
            catch (IOException)
            {
                return string.Empty;
            }
            catch (UnauthorizedAccessException)
            {
                return string.Empty;
            }
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return System.Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER") ?? string.Empty;
        }

        return string.Empty;
    }

    private static string RunProbe(string fileName, string arguments)
    {
        try
        {
            var start = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            using var process = Process.Start(start);
            if (process is null)
            {
                return string.Empty;
            }

            var stdout = process.StandardOutput.ReadToEnd();
            process.WaitForExit();
            return stdout.Trim();
        }
        catch (InvalidOperationException)
        {
            return string.Empty;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return string.Empty;
        }
    }
}