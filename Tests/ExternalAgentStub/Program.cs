using System.Text;
using Lattice.Protocol;

namespace Lattice.ExternalAgentStub;

/// <summary>
/// A real, separate process that plays the external-agent protocol, so the runner
/// is exercised end to end over an actual pipe on every operating system.
/// </summary>
/// <remarks>
/// <para>
/// A child process is the only honest way to test the parts of the contract that
/// are about <em>processes</em>: a hang, a crash, an exit, a stderr flood, and an
/// over-long line are all properties of a process, and a same-process fake would
/// be asserting that the fake behaves. This stub is C# and references
/// <c>Lattice.Protocol</c> so it needs no interpreter on PATH, which is what makes
/// the same test run identically on Windows, macOS, and Linux.
/// </para>
/// <para>
/// The <c>conform</c> mode parses with <see cref="ProtocolParser"/> and writes with
/// <see cref="ProtocolWriter"/>, so a conforming stub is conforming by
/// construction. Every failure mode instead writes raw bytes, because the only way
/// to produce a line the library would refuse to write is not to use it.
/// </para>
/// <para>
/// Modes that hang or exit do so deliberately and the process is expected to be
/// killed by the host's kill-on-dispose; nothing here traps that.
/// </para>
/// </remarks>
public static class Program
{
    private static readonly byte[] LineFeed = [0x0A];

    public static int Main(string[] args)
    {
        var mode = args.Length > 0 ? args[0] : "conform";
        var argument = args.Length > 1 ? args[1] : null;

        // The §3.2 launch contract's one added variable. Read to prove it arrives,
        // and deliberately NOT used to decide anything: the handshake is what
        // negotiates, and this is informational.
        var advertised = System.Environment.GetEnvironmentVariable(ExternalAgentProtocolVariable);

        using var stdin = Console.OpenStandardInput();
        using var stdout = Console.OpenStandardOutput();

        try
        {
            return Play(mode, argument, stdin, stdout, advertised);
        }
        catch (ProtocolViolation violation)
        {
            Console.Error.WriteLine($"stub: refusing to continue after {violation.Reason.ToWireString()}: {violation.Detail}");
            return 3;
        }
    }

    /// <summary>The variable the host adds to the inherited environment (spec §3.2).</summary>
    private const string ExternalAgentProtocolVariable = "LATTICE_PROTOCOL";

    private static int Play(string mode, string? argument, Stream stdin, Stream stdout, string? advertised)
    {
        Console.Error.WriteLine($"stub: mode={mode} LATTICE_PROTOCOL={advertised ?? "<unset>"}");

        // Every mode begins with the handshake, so the failure under test is the
        // only thing that differs. silent-handshake is the one exception: it reads
        // hello and then says nothing, which is what a timeout_handshake is.
        var helloLine = ReadLineOrThrow(stdin);

        if (mode == "silent-handshake")
        {
            Console.Error.WriteLine("stub: deliberately sending no hello_ack");
            Thread.Sleep(Timeout.Infinite);
            return 0;
        }

        if (mode == "wrong-protocol")
        {
            WriteRaw(stdout, "{\"type\":\"hello_ack\",\"protocol\":2}");
            return 0;
        }

        // The step the mode should misbehave at, -1 meaning "never".
        var at = int.TryParse(argument, out var parsed) ? parsed : -1;
        var slowMs = mode == "slow" && parsed > 0 ? parsed : 0;

        // `stall-match` costs the given number of milliseconds before it answers
        // its first step, and it is used with a figure larger than the host's whole
        // match_timeout_ms. That is the difference from `slow`, which delays by
        // less than one step budget and so stays inside it: a delay that exceeds
        // the entire match budget cannot be waited out at any child startup speed,
        // which is what makes timeout_match reachable without a race.
        var stallMs = mode == "stall-match" && parsed > 0 ? parsed : 0;

        // The handshake is answered immediately. The delay is spent inside the
        // step loop instead, where the host is actually counting: with
        // match_timeout_ms >= step_timeout_ms x max_ticks, a budget the handshake
        // consumes is a budget the step loop does not have, so answering promptly
        // and then using nearly the whole step budget every step is what
        // eventually exhausts the match. Delaying the *handshake* as well would
        // work too, but only just -- the delay starts after process startup, so it
        // would be racing the host's own handshake budget from behind.
        Write(stdout, new HelloAckMessage { Protocol = ProtocolLimits.Version });

        // The wire step this exchange is for, counted from 0 and advanced once per
        // completed exchange. Kept separate from `at` above so a mode like
        // hang-at-step 1 answers step 0 and stalls on step 1, rather than
        // stalling before it has answered anything.
        var step = 0;
        while (true)
        {
            var observationLine = ReadLineOrThrow(stdin);
            var observation = ProtocolParser.ParseObservation(observationLine);

            // `slow` waits *before* answering, not after. The host's clock runs
            // across its wait for this action, so a delay here is time the host
            // spends inside a step budget -- which is what makes timeout_match
            // reachable at all. A delay after answering would be time the host
            // spends outside any wait, and the match budget would never bind.
            if (slowMs > 0)
            {
                Thread.Sleep(slowMs);
            }

            // The overrun is spent once, on the first exchange, and the host is
            // waiting on stdout throughout it. Sleep, not an infinite hang: the
            // figure is a known number the test can hold against match_timeout_ms,
            // so the mode says "more than the whole budget" rather than "forever",
            // and the host is still the one that gives up first.
            if (stallMs > 0 && step == 0)
            {
                Console.Error.WriteLine($"stub: stalling {stallMs} ms before the first action");
                Console.Error.Flush();
                Thread.Sleep(stallMs);
            }

            // The flood is orthogonal to the exchange: it happens, and then the
            // stub plays the step correctly as normal. Putting it before the
            // switch is what keeps "while playing correctly" true -- a flood that
            // skipped the action would not be a chatty agent, it would be a mute
            // one, and the test would be measuring a timeout.
            if (mode == "stderr-flood")
            {
                FloodStderr();
            }

            switch (mode)
            {
                case "hang-at-step" when step == at:
                    Console.Error.WriteLine($"stub: hanging at step {step} as asked");
                    Thread.Sleep(Timeout.Infinite);
                    return 0;

                case "exit-at-step" when step == at:
                    // A clean exit before the exchange completed is agent_exited.
                    Console.Error.WriteLine($"stub: exiting cleanly at step {step} as asked");
                    return 0;

                case "crash" when step == at:
                    // A non-zero exit is agent_crashed; .NET reports a
                    // signal-terminated child the same way.
                    Console.Error.WriteLine($"stub: crashing at step {step} as asked");
                    return 7;

                case "crash":
                    Console.Error.WriteLine("stub: crashing before the first exchange as asked");
                    return 7;

                case "oversize-line":
                    WriteOversize(stdout);
                    return 0;

                case "bad-json":
                    WriteRaw(stdout, "{\"type\":\"action\",\"step\":" + observation.Step + ",\"kind\":");
                    return 0;

                case "unknown-field":
                    WriteRaw(stdout, $"{{\"type\":\"action\",\"step\":{observation.Step},\"kind\":\"Wait\",\"mood\":\"happy\"}}");
                    return 0;

                case "wrong-step":
                    // Echoes a step it is not answering, which is step_mismatch.
                    WriteRaw(stdout, $"{{\"type\":\"action\",\"step\":{observation.Step + 100},\"kind\":\"Wait\"}}");
                    return 0;

                case "illegal-action":
                    // In shape, but outside the action space for any map: a Move to
                    // a zone id no map can have.
                    WriteRaw(stdout, $"{{\"type\":\"action\",\"step\":{observation.Step},\"kind\":\"Move\",\"zone_id\":999999}}");
                    return 0;

                default:
                    Write(stdout, new ActionMessage
                    {
                        Step = observation.Step,
                        Kind = ProtocolActionKind.Wait,
                    });
                    break;
            }

            step++;
        }
    }

    /// <summary>Writes more than 1 MiB to stderr while continuing to play correctly.</summary>
    private static void FloodStderr()
    {
        var chunk = new string('e', 64 * 1024);
        for (var i = 0; i < 20; i++)
        {
            Console.Error.Write(chunk);
        }

        // Flushed explicitly, because the point of the mode is that the host is
        // reading this *while* it waits on stdout, not after.
        Console.Error.Flush();
    }

    /// <summary>Writes one line over <c>max_line_bytes</c>, so the cap is enforced mid-read.</summary>
    /// <remarks>
    /// The padding is computed arithmetically rather than by growing a string and
    /// remeasuring it each time. The obvious loop -- append an 'x', re-check the
    /// length -- is quadratic over a megabyte, which is minutes of CPU: enough to
    /// starve the other stub processes running in parallel and turn a correct test
    /// into a timeout somewhere else entirely.
    /// </remarks>
    private static void WriteOversize(Stream stdout)
    {
        const string prefix = "{\"type\":\"action\",\"step\":0,\"kind\":\"Wait\",\"detail\":\"";
        const string suffix = "\"}";
        var padding = ProtocolLimits.MaxLineBytes + 1 - prefix.Length - suffix.Length;
        if (padding < 1)
        {
            throw new InvalidOperationException("the oversize envelope no longer fits under the cap.");
        }

        WriteRaw(stdout, string.Concat(prefix, new string('x', padding), suffix));
    }

    private static int StepArgument(string? argument) =>
        int.TryParse(argument, out var step) ? step : -1;

    private static byte[] ReadLineOrThrow(Stream stdin)
    {
        var line = ProtocolFraming.ReadValidatedLine(stdin)
            ?? throw new ProtocolViolation(
                ProtocolReason.MalformedJson,
                "the host closed stdin while the stub still expected a line.");

        return line;
    }

    private static void Write(Stream stdout, ProtocolMessage message) => WriteRaw(stdout, message);

    private static void WriteRaw(Stream stdout, ProtocolMessage message) =>
        WriteRaw(stdout, Encoding.UTF8.GetString(ProtocolWriter.Encode(message)));

    private static void WriteRaw(Stream stdout, string line)
    {
        var bytes = Encoding.UTF8.GetBytes(line);
        stdout.Write(bytes, 0, bytes.Length);
        stdout.Write(LineFeed, 0, 1);

        // Flushed per line: a stub that buffered would look exactly like a hanging
        // agent to the host, which would make every mode test a timeout test.
        stdout.Flush();
    }
}
