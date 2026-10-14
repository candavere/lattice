# One-command setup for the Lattice repo (Windows/PowerShell).
#
# Verifies the SDK requirement from Cli/Lattice.Cli.csproj, restores, builds,
# runs the full test suite, then reproduces one committed claim so the run
# exits with evidence rather than a bare build. Idempotent, no admin rights,
# and with no launcher switch it never installs anything.
#
# Opt-in launcher step (nothing below runs unless asked):
#   .\setup.ps1 -InstallLauncher [-AddToPath] [-DryRun]
#   .\setup.ps1 -UninstallLauncher [-DryRun]
# packs the CLI (a real build) and installs the lattice .NET tool into the
# owned directory %LOCALAPPDATA%\Lattice\bin -- never a shared tool directory
# and never -g -- records exactly what was done in
# %LOCALAPPDATA%\Lattice\install.json so -UninstallLauncher can undo it, and
# with -AddToPath adds the owned directory to the USER Path environment
# variable only (never machine scope). Without -AddToPath nothing is changed
# and the exact line to add is printed instead. A running shell or PowerShell
# session never sees the change; open a new terminal afterwards.
param(
    [switch]$InstallLauncher,
    [switch]$UninstallLauncher,
    [switch]$AddToPath,
    [switch]$DryRun
)

$ErrorActionPreference = "Stop"

$RepoRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
Set-Location $RepoRoot

# ============================================================================
# Opt-in lattice launcher (Windows). Only runs when a launcher switch above
# selected it. PowerShell 5.1 compatible: no ternary, no ??, no &&/|| chains.
# ============================================================================

function Get-LatticeOwnedParent {
    if ([string]::IsNullOrEmpty($env:LOCALAPPDATA)) {
        Write-Error "LOCALAPPDATA is not set; cannot choose the owned directory."
        exit 1
    }
    return (Join-Path $env:LOCALAPPDATA 'Lattice')
}

function Get-LatticeBinDir {
    return (Join-Path (Get-LatticeOwnedParent) 'bin')
}

function Get-LatticeManifestPath {
    return (Join-Path (Get-LatticeOwnedParent) 'install.json')
}

function Get-LatticeLockPath {
    return (Join-Path (Get-LatticeOwnedParent) '.setup-lock')
}

function Get-LatticeToolVersion {
    $content = Get-Content 'Cli/Lattice.Cli.csproj' -Raw
    $match = [regex]::Match($content, '<Version>([^<]+)</Version>')
    if (-not $match.Success) {
        Write-Error "Could not read <Version> from Cli/Lattice.Cli.csproj"
        exit 1
    }
    $ver = $match.Groups[1].Value
    if ($ver -notmatch '^[0-9A-Za-z._-]+$') {
        Write-Error "Invalid <Version> '$ver' in Cli/Lattice.Cli.csproj"
        exit 1
    }
    return $ver
}

function Test-LatticeDotnet {
    if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
        Write-Error ".NET SDK not found on PATH (needed to pack and install the launcher)."
        exit 1
    }
}

function Test-LatticeManifest {
    # Validates the manifest. On success sets script:ManifestVersion and
    # script:ManifestBinDir and returns $true; otherwise reports why and
    # returns $false. Any unexpected field or altered value is invalid.
    $manifest = Get-LatticeManifestPath
    if (-not (Test-Path -LiteralPath $manifest -PathType Leaf)) {
        Write-Error "no valid manifest at $manifest (nothing installed by this script)."
        return $false
    }
    try {
        $doc = Get-Content -LiteralPath $manifest -Raw | ConvertFrom-Json
    }
    catch {
        Write-Error "manifest does not parse: $($_.Exception.Message)"
        return $false
    }
    $top = @($doc.PSObject.Properties.Name)
    foreach ($key in $top) {
        if ($key -notin @('version', 'binDir', 'path')) {
            Write-Error "manifest has an unexpected field: $key"
            return $false
        }
    }
    if (-not ($doc.version -is [string]) -or [string]::IsNullOrEmpty($doc.version)) {
        Write-Error "manifest version is not a non-empty string"
        return $false
    }
    if (-not ($doc.binDir -is [string]) -or [string]::IsNullOrEmpty($doc.binDir)) {
        Write-Error "manifest binDir is not a non-empty string"
        return $false
    }
    if ($null -ne $doc.path) {
        if ($doc.path -isnot [PSCustomObject]) {
            Write-Error "manifest path record is not an object"
            return $false
        }
        $kind = $doc.path.kind
        if ($kind -eq 'profile-block') {
            $want = @('kind', 'file', 'block', 'existed', 'size', 'sha256')
        }
        elseif ($kind -eq 'user-env-path') {
            $want = @('kind', 'entry', 'alreadyExisted')
        }
        else {
            Write-Error "manifest path record has an unknown kind"
            return $false
        }
        $got = @($doc.path.PSObject.Properties.Name)
        if ($got.Count -ne $want.Count) {
            Write-Error "manifest path record has an unexpected shape"
            return $false
        }
        foreach ($key in $got) {
            if ($key -notin $want) {
                Write-Error "manifest path record has an unexpected field: $key"
                return $false
            }
        }
    }
    $binDir = $doc.binDir
    if (-not [IO.Path]::IsPathRooted($binDir)) {
        Write-Error "manifest binDir is not absolute: $binDir"
        return $false
    }
    $segments = $binDir.Split([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar)
    if ($segments -contains '..') {
        Write-Error "manifest binDir contains '..': $binDir"
        return $false
    }
    if (-not (Test-Path -LiteralPath $binDir)) {
        Write-Error "manifest binDir does not exist: $binDir"
        return $false
    }
    $item = Get-Item -LiteralPath $binDir -Force
    if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        Write-Error "manifest binDir is a symlink or junction: $binDir"
        return $false
    }
    $local = [IO.Path]::GetFullPath($env:LOCALAPPDATA)
    $binCanon = [IO.Path]::GetFullPath($binDir)
    if (-not $binCanon.StartsWith($local + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
        Write-Error "manifest binDir is not under the local app data directory: $binDir"
        return $false
    }
    $ownCanon = [IO.Path]::GetFullPath((Get-LatticeBinDir))
    if (-not $binCanon.Equals($ownCanon, [StringComparison]::OrdinalIgnoreCase)) {
        Write-Error "manifest binDir is not this script's owned directory ($(Get-LatticeBinDir)): $binDir"
        return $false
    }
    $script:ManifestVersion = $doc.version
    $script:ManifestBinDir = $doc.binDir
    return $true
}

function Write-LatticeManifest {
    param([string]$Version, [string]$BinDir, [object]$PathRecord)
    $manifest = Get-LatticeManifestPath
    $doc = [ordered]@{ version = $Version; binDir = $BinDir }
    if ($null -ne $PathRecord) {
        $doc['path'] = $PathRecord
    }
    $json = ([PSCustomObject]$doc | ConvertTo-Json -Depth 5) + "`n"
    $tmp = Join-Path (Get-LatticeOwnedParent) ("install." + [Guid]::NewGuid().ToString("N") + ".tmp")
    $utf8 = New-Object Text.UTF8Encoding $false
    [IO.File]::WriteAllText($tmp, $json, $utf8)
    Move-Item -LiteralPath $tmp -Destination $manifest -Force
}

function Invoke-LatticeInstall {
    param([bool]$WithPath, [bool]$Dry)
    $ver = Get-LatticeToolVersion
    $manifest = Get-LatticeManifestPath
    $owned = Get-LatticeBinDir

    if (Test-Path -LiteralPath $manifest -PathType Leaf) {
        if (Test-LatticeManifest) {
            Write-Error "lattice $($script:ManifestVersion) is already installed at $($script:ManifestBinDir); run -UninstallLauncher first."
            return 1
        }
        Write-Error "$manifest exists but is not valid; refusing to touch it."
        return 1
    }
    $found = Get-Command lattice -ErrorAction SilentlyContinue
    if ($null -ne $found) {
        Write-Error "a 'lattice' already resolves on PATH at $($found.Source); refusing to shadow it (not executed)."
        return 1
    }
    if ((Test-Path -LiteralPath $owned) -and (@(Get-ChildItem -LiteralPath $owned -Force | Select-Object -First 1).Count -gt 0)) {
        Write-Error "$owned already exists with contents not recorded in a valid manifest; refusing."
        Get-ChildItem -LiteralPath $owned -Force | ForEach-Object { Write-Error ("  " + $_.Name) }
        return 1
    }

    if ($Dry) {
        Write-Host "dry-run: would pack Cli (version $ver) and install the lattice tool into $owned."
        if ($WithPath) {
            Write-Host "dry-run: would add $owned to the USER Path environment variable."
        }
        else {
            Write-Host "dry-run: to use it, add $owned to your user PATH and open a new terminal."
        }
        Write-Host "dry-run: manifest would be written to $manifest. Nothing changed."
        return 0
    }

    Test-LatticeDotnet
    $parent = Get-LatticeOwnedParent
    New-Item -ItemType Directory -Force -Path $parent | Out-Null
    $lock = Get-LatticeLockPath
    $lockStream = $null
    try {
        try {
            $lockStream = [IO.File]::Open($lock, [IO.FileMode]::CreateNew, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
        }
        catch {
            Write-Error "another setup run holds the lock at $lock, or it cannot be taken; refusing."
            return 1
        }
        if (Test-Path -LiteralPath $manifest -PathType Leaf) {
            Write-Error "install state changed under the lock; refusing."
            return 1
        }

        $packDir = Join-Path ([IO.Path]::GetTempPath()) ("lattice-pack-" + [Guid]::NewGuid().ToString("N"))
        New-Item -ItemType Directory -Force -Path $packDir | Out-Null
        try {
            Write-Host "Packing the CLI (version $ver)..."
            & dotnet pack Cli -c Release --output $packDir --disable-build-servers
            if ($LASTEXITCODE -ne 0) {
                Write-Error "dotnet pack failed; removing only this run's temp pack folder."
                return 1
            }
            Write-Host "Installing the lattice tool into $owned..."
            & dotnet tool install --tool-path $owned --add-source $packDir --version $ver lattice
            if ($LASTEXITCODE -ne 0) {
                Write-Error "dotnet tool install failed."
                if ((Test-Path -LiteralPath $owned) -and (@(Get-ChildItem -LiteralPath $owned -Force).Count -eq 0)) {
                    Remove-Item -LiteralPath $owned -Force
                }
                return 1
            }
        }
        finally {
            Remove-Item -LiteralPath $packDir -Recurse -Force -ErrorAction SilentlyContinue
        }

        # The PATH record goes into the manifest BEFORE the registry edit, so a
        # crash between the two is detectable on uninstall.
        $pathRecord = $null
        $regKey = $null
        $regRaw = $null
        $regKind = $null
        $regNew = $null
        if ($WithPath) {
            try {
                $regKey = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey('Environment', $true)
            }
            catch {
                Write-Error "cannot open the user environment registry key: $($_.Exception.Message)"
                return 1
            }
            if ($null -eq $regKey) {
                Write-Error "cannot open the user environment registry key."
                return 1
            }
            try { $regKind = $regKey.GetValueKind('Path') } catch { $regKind = $null }
            try { $regRaw = $regKey.GetValue('Path', '', 'DoNotExpandEnvironmentNames') } catch { $regRaw = '' }
            if ($null -eq $regRaw) { $regRaw = '' }
            if (($null -ne $regKind) -and ($regKind -ne [Microsoft.Win32.RegistryValueKind]::String) -and ($regKind -ne [Microsoft.Win32.RegistryValueKind]::ExpandString)) {
                Write-Error "user Path has an unexpected registry kind; refusing to rewrite it."
                $regKey.Close()
                return 1
            }
            $entries = @($regRaw -split ';' | Where-Object { $_ -ne '' })
            $already = $false
            foreach ($entry in $entries) {
                if ($entry.Equals($owned, [StringComparison]::OrdinalIgnoreCase)) {
                    $already = $true
                }
            }
            $pathRecord = [PSCustomObject][ordered]@{
                kind = 'user-env-path'
                entry = $owned
                alreadyExisted = $already
            }
            Write-LatticeManifest -Version $ver -BinDir $owned -PathRecord $pathRecord
            if (-not $already) {
                if ([string]::IsNullOrEmpty($regRaw)) {
                    $regNew = $owned
                }
                elseif ($regRaw.EndsWith(';')) {
                    $regNew = $regRaw + $owned
                }
                else {
                    $regNew = $regRaw + ';' + $owned
                }
                if ($null -eq $regKind) {
                    $regKey.SetValue('Path', $regNew, [Microsoft.Win32.RegistryValueKind]::String)
                }
                else {
                    $regKey.SetValue('Path', $regNew, $regKind)
                }
                $regKey.Close()
                $regKey = $null
                $verify = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey('Environment', $false)
                $check = $verify.GetValue('Path', '', 'DoNotExpandEnvironmentNames')
                $verify.Close()
                if (-not $check.Equals($regNew, [StringComparison]::Ordinal)) {
                    Write-Error "user PATH edit did not verify; install itself is complete."
                    return 1
                }
            }
            if ($null -ne $regKey) {
                $regKey.Close()
            }
        }
        else {
            Write-LatticeManifest -Version $ver -BinDir $owned -PathRecord $null
        }

        Write-Host "Installed lattice $ver into $owned."
        if (-not $WithPath) {
            Write-Host "To use it, add $owned to your user PATH and open a new terminal."
        }
        else {
            Write-Host "User PATH updated. A running PowerShell session does not see the change; open a new terminal."
        }
        Write-Host "Note: on a Homebrew .NET install the launcher can fail with 'You must install"
        Write-Host ".NET to run this application.' even though 'dotnet' works; set DOTNET_ROOT to"
        Write-Host "the real runtime root (/opt/homebrew/opt/dotnet/libexec). See README."
        return 0
    }
    finally {
        if ($null -ne $lockStream) {
            $lockStream.Close()
            Remove-Item -LiteralPath $lock -Force -ErrorAction SilentlyContinue
        }
    }
}

function Invoke-LatticeUninstall {
    param([bool]$Dry)
    if (-not (Test-LatticeManifest)) {
        return 1
    }
    Test-LatticeDotnet
    $lock = Get-LatticeLockPath
    $lockStream = $null
    try {
        try {
            $null = New-Item -ItemType Directory -Force -Path (Get-LatticeOwnedParent)
            $lockStream = [IO.File]::Open($lock, [IO.FileMode]::CreateNew, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
        }
        catch {
            Write-Error "another setup run holds the lock at $lock, or it cannot be taken; refusing."
            return 1
        }
        if ($Dry) {
            Write-Host "dry-run: would uninstall lattice $($script:ManifestVersion) from $($script:ManifestBinDir) and remove what this script added. Nothing changed."
            return 0
        }

        $manifest = Get-LatticeManifestPath
        $owned = $script:ManifestBinDir
        Write-Host "Uninstalling lattice $($script:ManifestVersion) from $owned..."
        & dotnet tool uninstall --tool-path $owned lattice
        if ($LASTEXITCODE -ne 0) {
            Write-Error "dotnet tool uninstall failed; leaving the manifest for inspection."
            return 1
        }

        $doc = Get-Content -LiteralPath $manifest -Raw | ConvertFrom-Json
        if ($null -ne $doc.path) {
            if ($doc.path.kind -ne 'user-env-path') {
                Write-Error "manifest records a '$($doc.path.kind)' PATH entry this script cannot undo on Windows; leaving everything for inspection."
                return 1
            }
            $regKey = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey('Environment', $true)
            if ($null -eq $regKey) {
                Write-Error "cannot open the user environment registry key; leaving everything for inspection."
                return 1
            }
            $raw = $regKey.GetValue('Path', '', 'DoNotExpandEnvironmentNames')
            if ($null -eq $raw) { $raw = '' }
            if (-not $doc.path.alreadyExisted) {
                $kept = @($raw -split ';' | Where-Object { -not $_.Equals($owned, [StringComparison]::OrdinalIgnoreCase) })
                $had = @($raw -split ';' | Where-Object { $_.Equals($owned, [StringComparison]::OrdinalIgnoreCase) })
                if ($had.Count -eq 0) {
                    Write-Error "the user PATH entry we added is already gone; leaving the registry for inspection."
                    $regKey.Close()
                    return 1
                }
                $expected = $kept -join ';'
                if ($raw.EndsWith(';') -and $expected.Length -gt 0) {
                    $expected = $expected + ';'
                }
                try { $kind = $regKey.GetValueKind('Path') } catch { $kind = [Microsoft.Win32.RegistryValueKind]::String }
                $regKey.SetValue('Path', $expected, $kind)
                $regKey.Close()
                $verify = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey('Environment', $false)
                $check = $verify.GetValue('Path', '', 'DoNotExpandEnvironmentNames')
                $verify.Close()
                if ($null -eq $check) { $check = '' }
                if (-not $check.Equals($expected, [StringComparison]::Ordinal)) {
                    Write-Error "user PATH restore did not verify; leaving the registry for inspection."
                    return 1
                }
            }
            else {
                $regKey.Close()
            }
        }

        # Release the lock before the final cleanup: the emptiness checks below
        # must not see the lock file itself (removal only touches empty dirs,
        # so user content can never be lost here).
        if ($null -ne $lockStream) {
            $lockStream.Close()
            $lockStream = $null
            Remove-Item -LiteralPath $lock -Force -ErrorAction SilentlyContinue
        }

        Remove-Item -LiteralPath $manifest -Force
        # 'dotnet tool uninstall' leaves an empty .store chain behind; Remove-Item
        # on each level removes it only when empty, so user content is never lost.
        foreach ($level in @((Join-Path $owned '.store/.stage'), (Join-Path $owned '.store'))) {
            if ((Test-Path -LiteralPath $level) -and (@(Get-ChildItem -LiteralPath $level -Force).Count -eq 0)) {
                Remove-Item -LiteralPath $level -Force
            }
        }
        if ((Test-Path -LiteralPath $owned) -and (@(Get-ChildItem -LiteralPath $owned -Force).Count -eq 0)) {
            Remove-Item -LiteralPath $owned -Force
        }
        elseif (Test-Path -LiteralPath $owned) {
            Write-Error "The owned directory holds unrelated files; leaving it in place:"
            Get-ChildItem -LiteralPath $owned -Force | ForEach-Object { Write-Error ("  " + $_.Name) }
        }
        $parent = Get-LatticeOwnedParent
        if ((Test-Path -LiteralPath $parent) -and (@(Get-ChildItem -LiteralPath $parent -Force).Count -eq 0)) {
            Remove-Item -LiteralPath $parent -Force
        }
        elseif (Test-Path -LiteralPath $parent) {
            Write-Error "$parent holds other files; leaving it in place."
        }

        Write-Host "Uninstalled. A running PowerShell session still has the old PATH; open a new terminal."
        return 0
    }
    finally {
        if ($null -ne $lockStream) {
            $lockStream.Close()
            Remove-Item -LiteralPath $lock -Force -ErrorAction SilentlyContinue
        }
    }
}

# --- Launcher dispatch: with a launcher switch the repo flow below is skipped. ---
if ($InstallLauncher -and $UninstallLauncher) {
    Write-Error "--InstallLauncher and --UninstallLauncher cannot be combined."
    exit 1
}
if ($AddToPath -and (-not $InstallLauncher)) {
    Write-Error "-AddToPath needs -InstallLauncher."
    exit 1
}
if ($InstallLauncher -or $UninstallLauncher) {
    # Top-level net: any uncaught throw still exits non-zero with one message.
    $code = 1
    try {
        if ($InstallLauncher) {
            $code = Invoke-LatticeInstall -WithPath ([bool]$AddToPath) -Dry ([bool]$DryRun)
        }
        else {
            $code = Invoke-LatticeUninstall -Dry ([bool]$DryRun)
        }
    }
    catch {
        Write-Error "launcher step failed: $($_.Exception.Message)"
        $code = 1
    }
    exit $code
}

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