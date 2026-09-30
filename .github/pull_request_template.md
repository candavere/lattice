# Pull request

Thanks for contributing to Lattice. This project is an evidence instrument: a
claim is accepted only when it maps to committed code, deterministic tests, CI
workflows, or benchmark artifacts. The checklist below is that bar, not a
formality.

Please read [`CONTRIBUTING.md`](../blob/main/CONTRIBUTING.md) for the reasoning
behind each item, and [`CODE_OF_CONDUCT.md`](../blob/main/CODE_OF_CONDUCT.md)
for the standards expected of everyone taking part.

## Checklist

- [ ] **Linked issue.** This PR references an issue that states the problem. If
      there is none, say why the change is self-evident.
- [ ] **Determinism preserved.** `dotnet run --project Cli -- replay <file> --verify`
      passes for every affected recording, and no committed artifact changed
      behaviourally. If a recording legitimately changed, the PR says so
      explicitly and explains why — a behavioural change is not a schema migration.
- [ ] **Tests added.** New behaviour ships with a test that fails on the old code
      and passes on the new one. A fix that cannot fail on the old behaviour does
      not demonstrate a fix.
- [ ] **Numbers come only from committed artifacts.** Every figure in the diff
      traces to a committed file at an identified revision. No re-measured number
      presented as a historical one, and no threshold or baseline quietly widened
      to make a run pass.
- [ ] **README, CHANGELOG and docs updated in the same PR.** A change in behaviour,
      a claim, a command, or a version lands with its documentation, not later.
- [ ] **No new dependencies without prior discussion.** Production assemblies stay
      pure .NET 10 BCL. Any dependency proposal was raised in an issue first.
- [ ] **`dotnet build Lattice.sln -c Release` is clean**, with zero warnings.
- [ ] **`./setup.sh` (or `.\setup.ps1`) passes**, which runs restore, build, the
      full suite, and the golden-trajectory reproduction.

## Negative results are welcome

A measurement that did not reproduce, a calibration that failed, or a hypothesis
the evidence does not support is a real result and belongs in the changelog. The
repository already records cases where the cause was unestablished and the honest
move was to say so rather than re-baseline. Please do the same: if you cannot
explain a result, report the result and what you ruled out.

## Notes for the reviewer

<!-- What should a reviewer look at first? What did you try that did not work? -->
