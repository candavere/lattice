# Scenario files

A **scenario file** is a declarative JSON document that describes an
environment: its topology, its resources, its agent roster, its tick budget,
and which of the engine's existing win and scoring rules apply. It is the
auditable, diffable, hashable form of an experiment setup.

Scenario files are **data only**. There are no scripts, no expressions, no
reflection, no dynamic type loading, no external commands, and no
environment-variable-dependent behaviour. A scenario file can select from
mechanics the engine already implements; it cannot introduce a new one. If a
win or score rule you want is not in the closed menu below, it does not exist
in the engine today, and the honest response is to say so rather than to have
the loader quietly accept a name it cannot honour.

Validate one before using it:

```sh
dotnet run -c Release --project Cli -- validate-scenario scenarios/collection-skirmish.json
```

[`CLI.md`](CLI.md#validate-scenario--check-a-declarative-scenario-descriptor)
documents that command; this page is the format itself.

## The two ways to declare a map

A scenario's map is declared in one of two mutually exclusive forms, chosen by
`Map.Source`. Both are first-class: a scenario can be built **on a seeded
generator** and narrow it, or **hand-authored outright** with no generator at
all.

Mixing the forms is an error rather than a precedence rule — a descriptor
saying `"Source": "generated"` next to a hand-authored `Zones` array is
contradictory, and both readings are reported rather than one silently winning.

### `static` — a hand-authored environment, no generator

Everything the map is, is in the file:

```json
{
  "Map": {
    "Source": "static",
    "Zones": [
      { "Id": 0, "X": 0, "Y": 0, "Role": "EntryHall" },
      { "Id": 1, "X": 6, "Y": 0, "Role": "TreasureVault" }
    ],
    "ChokePoints": [
      { "Id": 0, "FromZoneId": 0, "ToZoneId": 1, "MaxOccupancy": 1, "Role": "Portcullis" }
    ],
    "Resources": [
      { "Id": 0, "ZoneId": 0, "X": 1, "Y": 0 },
      { "Id": 1, "ZoneId": 1, "X": 5, "Y": 0 },
      { "Id": 2, "ZoneId": 1, "X": 7, "Y": 0 }
    ]
  }
}
```

A `static` map is identical for every seed. That is the point: the topology is
authored, not drawn, so it is exactly the environment the file describes.
`scenarios/gated-vault-duel.json` is a complete worked example — the smallest
useful starting point for a new environment.

### `generated` — a seeded generator as a skeleton, with full control

`Map.Generator.Family` names one of the seeded families the engine already
ships. The family supplies the base topology **and its seed variation**, and
the descriptor narrows it:

| Family | Base |
| :--- | :--- |
| `standard` | The seeded `MapGenerator` family, built with the same configuration `generate --seed` uses |
| `bottleneck` | The seeded capacity-1 contention family, the same one `evaluate --scenario bottleneck` plays |

```json
{
  "Map": {
    "Source": "generated",
    "Generator": { "Family": "standard" },
    "Overrides": {
      "ZoneMaxOccupancy": [ { "ZoneId": 0, "MaxOccupancy": 1 } ]
    }
  }
}
```

The family's own seeded generation is **untouched**. An override is a
statement about one zone *of this run's map*, not a change to the generator, so
two adjacent seeds still produce genuinely different topologies with the
override applied. `scenarios/skeleton-with-override.json` is the worked
example: it narrows one zone to single-occupancy and leaves everything else to
the family.

**Overrides** (`Map.Overrides.ZoneMaxOccupancy`) replace a field on an existing
element, matched by id and applied in array order. **Additions**
(`Map.AddZones`, `Map.AddResources`, `Map.AddChokePoints`) append new elements
with explicit ids.

Overriding the base rather than regenerating it is the deliberate choice here.
Regenerating with a different configuration would change the family that every
other seed already drew from, and a scenario that quietly re-drew the map would
stop being the family it names.

## Field reference

### `Map`

| Field | Type | Notes |
| :--- | :--- | :--- |
| `Source` | `"generated"` or `"static"` | Required; selects the form |
| `Zones` | array | `static` only. `{ "Id", "X", "Y", "MaxOccupancy"?, "Role"? }` |
| `Resources` | array | `static` only. `{ "Id", "ZoneId", "X", "Y", "Role"? }` |
| `ChokePoints` | array | `static` only. `{ "Id", "FromZoneId", "ToZoneId", "MaxOccupancy"?, "Role"? }` |
| `Generator` | object | `generated` only. `{ "Family": "standard" \| "bottleneck" }` |
| `Overrides` | object | `generated` only. `{ "ZoneMaxOccupancy": [ { "ZoneId", "MaxOccupancy" } ] }` |
| `AddZones` | array | `generated` only. Same shape as `Zones` |
| `AddResources` | array | `generated` only. Same shape as `Resources` |
| `AddChokePoints` | array | `generated` only. Same shape as `ChokePoints` |

`MaxOccupancy` is optional and unbounded when absent. `0` means **impassable**
— that is how an obstacle is expressed. A resource whose only route passes
through an impassable choke or zone is unreachable and the descriptor is
rejected.

`Role` on a zone, resource, or choke is demonstration-layer metadata: a
semantic label the viewer and reports read, and that the step contract never
reads. Labelling cannot change how an episode plays.

### The rest

`SchemaVersion`, `Id`, `Name`, `Description`, `Simulation`, `Slots`, `Victory`,
and `Scoring` are documented field-by-field in
[`CLI.md`](CLI.md#validate-scenario--check-a-declarative-scenario-descriptor).

## What is rejected, and why

Every rule below names the field it rejects. The loader reports **all** faults
in one pass, not just the first, and each carries its JSON path so you can go
straight to it.

**Structural**

- Malformed JSON, a document that is not an object, a trailing comma before a
  following member, or a `//` comment — all rejected. A descriptor is data, and
  permissive extensions are not part of the contract.
- Any unknown field, at any level, naming both the field and the known ones.
- A missing `Id`, or an `Id` that is not lower-case kebab-case.
- A `SchemaVersion` other than `1`.
- A number that is not a 32-bit integer: `1.5` is rejected rather than
  truncated. Parsing is culture-invariant by construction, so a descriptor
  written on a machine whose locale renders decimals with a comma is refused
  rather than silently reinterpreted.

**Topology**

- Duplicate ids, in any of the three arrays.
- Duplicate authored coordinates — two zones or two resources at the same
  point — in a `static` map. A generated family keeps its own seeded placement
  contract, which the loader does not second-guess.
- A choke endpoint or a resource's zone that is not declared.
- A choke whose `FromZoneId` equals its `ToZoneId`. The step contract cannot
  traverse it.
- A negative `MaxOccupancy`, or a zone declared impassable (`0`): an agent can
  never occupy it, so declaring it a room is unreachable by construction.
- A disconnected topology, or a zone with no incident choke point.

**Roster and budgets**

- `AgentCount` outside 2–4, `StepLimit` outside 1–100000, or a `TransitSpeed`
  that is neither `0` nor at least `1`.
- A roster whose length disagrees with `AgentCount`, or whose `Slot` values are
  not `0..AgentCount-1` in array order.
- A `RivalSlot` missing where the policy needs one, present where it does not,
  equal to the slot itself, or out of range.
- A `Vision` that is not `-1` or at least `1`, or one named on a policy that
  does not take a perception cone.

**Bounds.** Descriptors are capped so a file cannot request a pathological
allocation: 1 MiB on the file (checked *before* parsing), 256 zones, 4096
resources, 8192 choke points, 100000 ticks. Exceeding a bound is a rejection
naming the bound, not a truncated map.

## The SHA-256 contract

Every descriptor has a digest, printed by `validate-scenario` and carried by any
recording made from that file:

- Computed over the **exact raw source bytes**, with the file read **once** —
  so there is no window in which the bytes hashed could differ from the bytes
  parsed.
- **Lowercase hex**, 64 characters.
- Never derived from the path, the mtime, or a re-serialization.

**Whitespace and line endings are significant to the digest.** Two descriptors
that are semantically identical but differ by one byte of indentation, or by
CRLF versus LF, hash differently. This is intentional: the digest identifies
the **file**, not the meaning, so a reader holding a recording can name the
exact bytes that produced it. Semantic equivalence and digest equality are
separate properties, and conflating them would let a reformat silently
re-identify a recording.

The digest is also the recording's provenance anchor, and is distinct from the
per-tick `SimulationStateHash` a trajectory carries: the one names the
scenario *file*, the other names the simulation *state* at a tick. They are
computed over different things and are never compared to each other.

## Determinism

A scenario resolves to a fixed environment for a given seed:

- A `static` map is identical for every seed.
- A `generated` map is a pure function of the seed and the descriptor, because
  the family's generation is untouched and overrides and additions are applied
  in declared array order.
- Array order is preserved everywhere; ids, roster order, and element order are
  never re-sorted, so two runs of the same descriptor produce the same map in
  the same order.
- Numbers are parsed culture-invariantly and identifiers are compared
  ordinally, so a descriptor means the same thing on every machine.

## Writing a new scenario

1. Copy `scenarios/gated-vault-duel.json` (hand-authored) or
   `scenarios/skeleton-with-override.json` (generated skeleton) as a starting
   point.
2. Give it a kebab-case `Id` and set `SchemaVersion` to `1`.
3. Declare the map in one form only.
4. Fill `Slots` with exactly `AgentCount` entries, each `Slot` equal to its
   array position.
5. Pick `Victory.Condition` and `Scoring.Scheme` from the closed menus.
6. Run `validate-scenario` and fix whatever it names.

Committed descriptors live in [`scenarios/`](../scenarios); the invalid
fixtures the tests drive live in
[`Tests/fixtures/scenarios/invalid/`](../Tests/fixtures/scenarios/invalid) and
double as worked examples of what gets rejected.
