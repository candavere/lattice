namespace Lattice.Analytics.Benchmarking;

/// <summary>
/// The full measurements for one workload across all measured iterations: the
/// dispersion of per-iteration throughput (median/mean/stddev), the combined
/// per-step latency histogram (p50/p95), the per-step managed allocation, and
/// the process-wide GC collection deltas over the measurement window. Every
/// number is reproducible from the recorded <see cref="Metadata"/> and the
/// harness protocol (warm-up budget, iterations, steps per iteration).
/// </summary>
public sealed record WorkloadResult(
    string Name,
    string MapScale,
    int Agents,
    bool DynamicTopology,
    string MapShape,
    int Iterations,
    int StepsPerIteration,
    string ThroughputMetric,
    double MedianThroughputPerSecond,
    double MeanThroughputPerSecond,
    double StdDevThroughputPerSecond,
    double MedianStepLatencyMicros,
    double MeanStepLatencyMicros,
    double P95StepLatencyMicros,
    double AllocationsPerStepBytes,
    int TotalGcGen0,
    int TotalGcGen1,
    int TotalGcGen2);

/// <summary>
/// One reproducible benchmark suite run: the host/provenance metadata and the
/// per-workload results, in stable order. This is the artifact shape the CLI
/// writes and the CI regression gate reads.
/// </summary>
public sealed record BenchmarkSuiteResult(
    BenchmarkMetadata Metadata,
    IReadOnlyList<WorkloadResult> Workloads);