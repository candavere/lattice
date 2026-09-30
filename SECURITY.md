# Security Policy

## Supported Versions

| Version | Supported          |
| ------- | ------------------ |
| 3.x     | :white_check_mark: |
| 2.x     | :x:                |
| 1.x     | :x:                |

## Reporting a Vulnerability

Lattice is a headless multi-agent research and benchmarking environment designed for local execution and deterministic evaluation. All production assemblies reference only the .NET base class library and pull in zero external NuGet runtime packages; the test project and development tooling do use external packages, and those are out of scope here. It does not bind network sockets, expose web APIs, or store user credentials.

If you identify a potential security issue (such as an uncontrolled memory allocation vector, parser denial-of-service via malformed JSONL trajectories, or algorithmic complexity vulnerabilities during graph verification):

1. **Do NOT open a public GitHub issue.**
2. Report the vulnerability privately via [GitHub Security Advisories](https://github.com/candavere/lattice/security/advisories/new).
3. Provide:
   - A description of the vulnerability and its potential impact.
   - Minimal reproduction steps, including relevant seeds, CLI arguments, or sample payload files.
   - Operating system and .NET runtime environment details.

Reports will be acknowledged within 48 hours, followed by private triage, reproduction, and a coordinated patch release.