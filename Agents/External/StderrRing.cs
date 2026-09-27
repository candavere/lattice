using System.Text;

namespace Lattice.Agents.External;

/// <summary>
/// The bounded stderr ring of spec §1.1 (U-4): a fixed 64 KiB circular buffer
/// holding the <b>last</b> 64 KiB an agent wrote to stderr, so the tail of a
/// chatty agent's diagnostics survives into the failure report.
/// </summary>
/// <remarks>
/// <para>
/// The capacity is <see cref="CapacityBytes"/>, a byte count and not a line or
/// character count, and the buffer discards oldest-first so "the last 64 KiB" is
/// a property of the type rather than of how full it happens to be.
/// </para>
/// <para>
/// This type is only the <em>buffer</em>. The requirement that actually matters
/// — that stderr is drained continuously on its own reader for the whole life of
/// the process, so an agent can never fill the OS pipe buffer and deadlock — is
/// a property of the pump that writes into it, not of the ring. A ring that is
/// correct but never drained would still deadlock a chatty agent, which is why
/// the drain is started before the handshake and runs to process exit.
/// </para>
/// <para>
/// Every method is safe to call from the drain's reader and from the failure
/// path concurrently; the two run on different threads for the life of the
/// match.
/// </para>
/// </remarks>
public sealed class StderrRing
{
    /// <summary>
    /// The spec §1.1 capacity, U-4: <c>65536</c> bytes (64 KiB).
    /// </summary>
    public const int CapacityBytes = 65_536;

    private readonly byte[] _buffer = new byte[CapacityBytes];
    private readonly object _gate = new();
    private int _start;
    private int _count;
    private long _totalBytes;

    /// <summary>
    /// The total number of stderr bytes the agent has written over the life of
    /// the process, including bytes already discarded from the ring. This is
    /// what proves a flood was actually drained rather than merely survived: it
    /// is unbounded and monotonically increasing, so it is the only witness to
    /// output that no longer fits.
    /// </summary>
    public long TotalBytes
    {
        get
        {
            lock (_gate)
            {
                return _totalBytes;
            }
        }
    }

    /// <summary>Appends <paramref name="bytes"/>, discarding the oldest bytes once the ring is full.</summary>
    public void Append(ReadOnlySpan<byte> bytes)
    {
        if (bytes.IsEmpty)
        {
            return;
        }

        lock (_gate)
        {
            _totalBytes += bytes.Length;

            // More than a ringful at once: only the tail can survive, so skip
            // straight to it rather than copying megabytes through a 64 KiB hole.
            if (bytes.Length >= CapacityBytes)
            {
                bytes[^CapacityBytes..].CopyTo(_buffer);
                _start = 0;
                _count = CapacityBytes;
                return;
            }

            var writeAt = (_start + _count) % CapacityBytes;
            var firstRun = Math.Min(bytes.Length, CapacityBytes - writeAt);
            bytes[..firstRun].CopyTo(_buffer.AsSpan(writeAt));
            if (firstRun < bytes.Length)
            {
                bytes[firstRun..].CopyTo(_buffer.AsSpan(0));
            }

            _count = Math.Min(_count + bytes.Length, CapacityBytes);
            if (_count == CapacityBytes)
            {
                _start = 0;
            }
        }
    }

    /// <summary>
    /// The bytes currently held: the last <c>min(64 KiB, everything written)</c>
    /// the agent produced. A snapshot, so a later flood cannot mutate a tail that
    /// has already been attached to a failure record.
    /// </summary>
    public byte[] Snapshot()
    {
        lock (_gate)
        {
            if (_count == 0)
            {
                return [];
            }

            var snapshot = new byte[_count];
            var firstRun = Math.Min(_count, CapacityBytes - _start);
            Array.Copy(_buffer, _start, snapshot, 0, firstRun);
            if (firstRun < _count)
            {
                Array.Copy(_buffer, 0, snapshot, firstRun, _count - firstRun);
            }

            return snapshot;
        }
    }

    /// <summary>
    /// <see cref="Snapshot"/> decoded as text for the failure record. Invalid
    /// UTF-8 is replaced rather than thrown, because a failure record must be
    /// producible whatever the agent wrote.
    /// </summary>
    public string Tail() => Encoding.UTF8.GetString(Snapshot());
}
