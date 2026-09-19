# ADR-0001 — Governing product thesis and evidentiary standards

**Status:** accepted

## Context

Lattice is an auditable multi-agent research and benchmarking environment for
deterministic experiments. The product is not the agent software; it is the
instrumentation: a pure step-contract simulation engine, a seeded procedural
generator, perception-filtered agents, and a paired statistical evaluation
harness — all committed source, all replayable, all seeded. For such a
repository to read as a coherent research instrument rather than an
accumulation of patches, every claim it makes must be attached to evidence the
reader can reproduce with a command, and every design decision must answer to
a single governing thesis: the fewer hidden claims, the more trustworthy the
measurement.

The governing thesis is that the repository's value is its *evidentiary
standard*: what a reader can verify by running the committed code on the
committed inputs. Anything that weakens that standard — an uncommitted
benchmark number, an undated evaluation, a claim with no artifact behind it —
is a defect in the product, not a documentation afterthought.

## Decision

Adopt the following governing thesis and five evidentiary principles as the
decision authority for feature intake:

**Governing product thesis.** Lattice is simulation-as-instrument. Repeated,
independently-verifiable measurement of decision policies under deterministic,
contention-bearing conditions is the product; agents are a thin demonstration
layer, and any policy or search enhancement is a hypothesis the evaluation
harness must test, not a feature to be asserted.

The five evidentiary principles are:

1. **Auditability.** Every claim must be traceable to committed artifacts:
   trajectories, `benchmarks/*.json`, or evaluation results, each produced by
   a documented command line with a recorded seed. A claim without a committed
   artifact is not yet a claim.
2. **Policies as evaluation subjects.** A policy, planner, or search
   enhancement is an evaluation subject, not a deliverable. Its value is
   established by the paired evaluation protocol — mirrored seatings, fixed
   seed suites, a stated decision rule — never by a narrative assertion.
3. **Negative results as valid evidence.** A failing verdict (a decision rule
   that does not clear) is first-class evidence and is committed as a
   reference baseline. It is a working verdict, not a dead-end, and superseding
   it requires clearing the same rule under the same protocol.
4. **Empirical provenance.** Performance and evaluation claims are scoped to
   the host, runtime, build configuration, and source revision that produced
   them, exactly as recorded in the artifact's metadata. Cross-platform
   determinism is a property of the code exercised by the CI matrix, not a
   claim asserted in prose.
5. **Contractual terminology.** Vocabulary is load-bearing and must never
   drift. Three distinct guarantees are named precisely: *transition
   determinism* (same state + same actions → same next state), *per-step
   serialized `StepResult` equivalence* (a replay's reconstructed ticks match
   the recorded ticks' serialized results), and *raw JSONL byte identity* (two
   fresh runs from the same seed and actions produce bit-for-bit identical
   files). Reachability is a graph property — BFS reachability over zones and
   chokes — never geometric distance.

## Rationale

The thesis and principles exist because the repository must remain
believable without a human witness. The paired evaluation protocol already
cancels positional spawn bias, the replay gate already pins determinism, and
the benchmark harness already records provenance; the evidentiary principles
make those mechanisms a *contract on future work* rather than a happy accident
of the current code. Negative results keep the record honest when a policy
loses, and contractual terminology keeps verifier capabilities (which the code
actually asserts today) from being overstated in documentation.

## Consequences

Feature intake now gates on the evidentiary standard:

- Any performance claim must ship a reproducible `benchmarks/*.json` artifact
  produced by the committed harness, with host metadata intact; a claim
  without it is rejected (see `CONTRIBUTING.md`).
- Any new policy, planner, or search enhancement must evaluate on the canonical
  dev and held-out seed suites under mirrored-seat pairings before it can be
  described as an improvement; the decision rule is the verdict.
- Any state or replay equivalence claim must specify which level it means —
  transition-deterministic, serialized `StepResult` equivalent, or raw JSONL
  byte identical — and the verifier's documented capability must match.
- Documentation (README, `docs/`, `CONTRIBUTING.md`) must cite committed
  evidence for numbers and must not claim capabilities the committed verifier
  does not implement yet. Where a capability is slated for a future engine
  revision — as with a formal canonical simulation-state hash tree — the
  documentation says so.