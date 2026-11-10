# Installing and running Lattice

There are two different install stories, and they are not interchangeable:

- **Published users** run the latest published release, **v3.1.0**, from its
  GitHub Release.
- **Candidate source** is **4.0.0, unreleased**. It exists only as this source
  tree and is installed only by packing it locally into a throwaway tool path.

Cloning the repository does not put `lattice` on `PATH`. The zero-install path
uses `dotnet run --project Cli --` against the checkout.

## Zero-install: SDK plus checkout

Requirements: the [.NET 10 SDK](https://dotnet.microsoft.com/download). Lattice
also runs on later .NET majors via `RollForward=LatestMajor`.

```sh
git clone https://github.com/candavere/lattice.git
cd lattice
./setup.sh              # macOS/Linux
# .\setup.ps1           # Windows
```

`setup.sh` and `setup.ps1` verify the SDK requirement read from the project
file, restore, build, run the full test suite, and reproduce one committed
claim: the golden trajectory replay. They print
`Reproduced: <claim> matches <artifact>` or a clear mismatch. They are
idempotent, use no `sudo`, and never install system software. `make setup` is
equivalent on macOS/Linux.

The manual equivalent after restore is:

```sh
dotnet build Lattice.sln -c Release
dotnet test Lattice.sln -c Release --no-build
dotnet run -c Release --project Cli -- replay Tests/fixtures/golden_trajectory.jsonl --verify
```

The commands run from a clean checkout at the repository root and require no
network access once dependencies are restored. That guarantee covers the
commands in this section; it does not cover `dotnet tool install`, which
contacts a NuGet source.

## Published release: v3.1.0 native binaries

The latest published release is **v3.1.0**, distributed as native binaries from
its GitHub Release (`lattice-linux-x64`, `lattice-osx-arm64`,
`lattice-win-x64.exe`, plus `SHA256SUMS.txt`):

<https://github.com/candavere/lattice/releases/tag/v3.1.0>

Download the binary for your platform, verify it against `SHA256SUMS.txt`, and
run it. There is **no registry package** for Lattice: no nuget.org (or other
registry) publication exists or is claimed. Publication of `v3.1.0` itself is
recorded only by that GitHub Release; it was not re-verified from the offline
session that wrote this page, so this page does not assert its current
availability beyond the published record.

## Candidate source: 4.0.0 local pack only

Version 4.0.0 exists only as this source tree. The commands below are valid only
after you build and pack *this* candidate, and they install exclusively from
your local package source into a throwaway tool path. They never fetch a
published 4.0.0 (there is none) and never touch your global installation:

```sh
dotnet pack Cli -c Release --no-build --output ./pkg-source   # local source only
dotnet tool install --tool-path ./tool-path --version 4.0.0 --add-source ./pkg-source lattice
./tool-path/lattice --version                               # 4.0.0+<source-commit>
./tool-path/lattice replay Tests/fixtures/golden_trajectory.jsonl --verify
```

That throwaway tool-path install is the only candidate install offered here: do
not substitute a global (`-g`) install, and do not present the unpublished 4.0.0
as a public install command.

`--add-source` points NuGet at the package you just built. NuGet may still
consult your other configured sources, so pass `--version 4.0.0` as well if you
want the install pinned to exactly what you packed. Publishing to nuget.org is a
**separate, optional maintainer action** and is not required to use any of this;
the `lattice` package ID there may already be taken, and the command name is
`lattice` either way.

### Packaging, not a second implementation

The tool is the same compiled binary with a launcher named `lattice`; every
command, flag, output and exit code is the one documented in the
[CLI reference](CLI.md), because it is the same code. It adds **no dependency**:
the production projects still declare zero `PackageReference` items, and the
packed `.nuspec` declares no dependencies at all. The package carries Lattice's
own assemblies (including `Protocol`) and asks for the .NET runtime, which you
already have for the zero-install path.

A package version saying 4.0.0 does not mean the v4 release has shipped:
**4.0.0 is a release candidate, not a released version**. The latest published
release remains `v3.1.0`
(<https://github.com/candavere/lattice/releases/tag/v3.1.0>).

CI proves the two entry points agree: the `build-test` matrix packs, installs the
tool into a throwaway tool path on Ubuntu, Windows and macOS, and fails if the
installed `lattice` and `dotnet run` differ by a single byte on the golden
replay.

## Opt-in launcher

`./setup.sh --install-launcher` (or `.\setup.ps1 -InstallLauncher` on Windows)
packs a real build and installs `lattice` into an owned per-user directory
(`$HOME/.lattice/bin` on macOS/Linux, `%LOCALAPPDATA%\Lattice\bin` on Windows).
With `--add-to-path` / `-AddToPath` it records and (on Unix) appends one marked
block to one profile, or (on Windows) appends the directory to the user `Path`
only. Without that flag it prints the exact line instead and changes no profile
or registry. Every effect is recorded in `install.json` and undone by
`--uninstall-launcher` / `-UninstallLauncher`; dry-run changes nothing. Open a
new terminal after PATH changes.

On some package-manager .NET installations the `lattice` launcher can fail with
`You must install .NET to run this application.` even though `dotnet` itself
works, because the launcher resolves the runtime from `DOTNET_ROOT` or a
registered location and such installations may provide neither. Set
`DOTNET_ROOT` to the real runtime root for your installation. `dotnet run` above
is unaffected, so if the launcher fails, use the zero-install path instead.

## Which path should I use?

| Situation | Path |
| :--- | :--- |
| I want to read, run, and verify the source | Zero-install: clone, then `./setup.sh` |
| I want the latest published binary | Download `v3.1.0` and verify `SHA256SUMS.txt` |
| I want to test the unreleased 4.0.0 candidate | Local pack, throwaway `--tool-path` install |
| I want `lattice` on my `PATH` from this checkout | Opt-in launcher, or add the tool path yourself |

For the full command surface, see the [CLI reference](CLI.md). For the claims
each command backs, see [Verification](VERIFICATION.md).
