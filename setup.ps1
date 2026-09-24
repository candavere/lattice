# One-command setup for the Lattice repo (Windows/PowerShell).
#
# Verifies the SDK requirement from Cli/Lattice.Cli.csproj, restores, builds,
# runs the full test suite, then reproduces one committed claim so the run
# exits with evidence rather than a bare build. Idempotent, no admin rights,
# never installs system software.
$ErrorActionPreference = "Stop"

$RepoRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
Set-Location $RepoRoot

$CSPROJ = "Cli/Lattice.Cli.csproj"
if (-not (Test-Path $CSPROJ)) {
    Write-Error "$CSPROJ not found; run from the repository root"
    exit 1
}

# The SDK requirement is read from the project file, never hardcoded here.
$content = Get-Content $CSPROJ -Raw
$tfMatch = [regex]::Match($content, '<TargetFramework>([^<]+)</TargetFramework>')
$rfMatch = [regex]::Match($content, '<RollForward>([^<]+)</RollForward>')
if (-not $tfMatch.Success) {
    Write-Error "Could not read TargetFramework from $CSPROJ"
    exit 1
}
$tf = $tfMatch.Groups[1].Value
$rf = if ($rfMatch.Success) { $rfMatch.Groups[1].Value } else { "(none)" }
$requiredMajor = [int]($tf -replace '^net', '' -replace '\..*$', '')

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    Write-Error ".NET SDK not found on PATH. Install the .NET ${requiredMajor} SDK: https://dotnet.microsoft.com/download/dotnet/${requiredMajor}"
    exit 1
}

$sdkVersion = (& dotnet --version 2>$null)
if ([string]::IsNullOrWhiteSpace($sdkVersion)) {
    Write-Error "No .NET SDK installed (a runtime alone is not enough). Install the .NET ${requiredMajor} SDK: https://dotnet.microsoft.com/download/dotnet/${requiredMajor}"
    exit 1
}

$sdkMajor = [int](($sdkVersion -split '\.')[0])
if ($sdkMajor -lt $requiredMajor) {
    Write-Error "This repository targets $tf (RollForward=$rf) but the installed SDK is $sdkVersion. Install the .NET ${requiredMajor} SDK or newer: https://dotnet.microsoft.com/download/dotnet/${requiredMajor}"
    exit 1
}

Write-Host "Using .NET SDK $sdkVersion (repo requires $tf, RollForward=$rf)."

Write-Host "Restoring dependencies..."
& dotnet restore Lattice.sln
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

Write-Host "Building in Release..."
& dotnet build Lattice.sln -c Release --no-restore
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

Write-Host "Running the full test suite..."
& dotnet test Lattice.sln -c Release --no-build
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

Write-Host "Reproducing one committed claim: golden trajectory replay..."
$golden = "Tests/fixtures/golden_trajectory.jsonl"
& dotnet run -c Release --project Cli -- replay $golden --verify
if ($LASTEXITCODE -eq 0) {
    Write-Host "Reproduced: golden trajectory replay --verify, per-step results match the committed $golden"
} else {
    Write-Error "Mismatch: replay --verify did not reproduce $golden; see the error above."
    exit $LASTEXITCODE
}

Write-Host "setup.ps1 complete."