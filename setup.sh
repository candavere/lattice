#!/usr/bin/env bash
# One-command setup for the Lattice repo (macOS/Linux, bash 3.2+).
#
# Verifies the SDK requirement from Cli/Lattice.Cli.csproj, restores, builds,
# runs the full test suite, then reproduces one committed claim so the run
# exits with evidence rather than a bare build. Idempotent, no sudo, and with
# no launcher flag it never installs anything.
#
# Opt-in launcher step (nothing below runs unless asked):
#   ./setup.sh --install-launcher [--add-to-path] [--dry-run]
#   ./setup.sh --uninstall-launcher [--dry-run]
# packs the CLI (a real build) and installs the lattice .NET tool into the
# owned directory $HOME/.lattice/bin -- never the shared ~/.dotnet/tools and
# never -g -- records exactly what was done in $HOME/.lattice/install.json so
# --uninstall-launcher can undo it, and with --add-to-path appends one marked
# block to one shell profile (zsh ~/.zprofile, or bash ~/.bash_profile when it
# exists, else ~/.profile). Without --add-to-path nothing is edited and the
# exact line to add is printed instead. A running shell never sees the change;
# open a new terminal afterwards. See README for the full contract.
set -euo pipefail

CDIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
cd "$CDIR"

# --- Launcher flags (opt-in only; no flag means the repo setup below, unchanged) ---
LAUNCHER_INSTALL=0
LAUNCHER_UNINSTALL=0
LAUNCHER_ADD_TO_PATH=0
LAUNCHER_DRY_RUN=0
for launcher_arg in "$@"; do
  case "$launcher_arg" in
    --install-launcher) LAUNCHER_INSTALL=1 ;;
    --uninstall-launcher) LAUNCHER_UNINSTALL=1 ;;
    --add-to-path) LAUNCHER_ADD_TO_PATH=1 ;;
    --dry-run) LAUNCHER_DRY_RUN=1 ;;
    -h|--help)
      echo "usage: ./setup.sh [--install-launcher [--add-to-path] [--dry-run] | --uninstall-launcher [--dry-run]]"
      exit 0
      ;;
    *)
      echo "error: unknown argument '$launcher_arg' (see --help)." >&2
      exit 1
      ;;
  esac
done

if [ "$LAUNCHER_INSTALL" = 1 ] && [ "$LAUNCHER_UNINSTALL" = 1 ]; then
  echo "error: --install-launcher and --uninstall-launcher cannot be combined." >&2
  exit 1
fi

if [ "$LAUNCHER_ADD_TO_PATH" = 1 ] && [ "$LAUNCHER_INSTALL" != 1 ]; then
  echo "error: --add-to-path needs --install-launcher." >&2
  exit 1
fi

# The launcher dispatch lives at the end of this file, after the launcher
# functions are defined (bash runs top-down). With no launcher flag the repo
# setup below runs exactly as before.

# ============================================================================
# Opt-in lattice launcher (macOS/Linux). Only runs when a launcher flag above
# selected it. Bash 3.2 compatible: no associative arrays, no mapfile.
# ============================================================================

LATTICE_HOME_DIR="${HOME}/.lattice"
LATTICE_BIN_DIR="${LATTICE_HOME_DIR}/bin"
LATTICE_MANIFEST="${LATTICE_HOME_DIR}/install.json"
LATTICE_LOCK_DIR="${LATTICE_HOME_DIR}/.setup-lock"
LATTICE_MARK_BEGIN="# >>> lattice launcher >>>"
LATTICE_MARK_END="# <<< lattice launcher <<<"
LATTICE_PROFILE_LINE='export PATH="$HOME/.lattice/bin:$PATH"'

launcher_version() {
  local ver
  ver="$(sed -n 's:.*<Version>\([^<]*\)</Version>.*:\1:p' Cli/Lattice.Cli.csproj | head -n1)"
  case "$ver" in
    ''|*[!0-9A-Za-z._-]*)
      echo "error: could not read a valid <Version> from Cli/Lattice.Cli.csproj." >&2
      return 1
      ;;
  esac
  printf '%s' "$ver"
}

launcher_need_dotnet() {
  if ! command -v dotnet >/dev/null 2>&1; then
    echo "error: .NET SDK not found on PATH (needed to pack and install the launcher)." >&2
    return 1
  fi
}

launcher_canonical() {
  # Prints the canonical absolute path of an existing directory, or fails.
  if [ ! -d "$1" ]; then
    return 1
  fi
  (cd "$1" && pwd -P)
}

launcher_home_canonical() {
  (cd "$HOME" && pwd -P)
}

launcher_sha256_file() {
  if command -v sha256sum >/dev/null 2>&1; then
    sha256sum "$1" | sed 's/ .*//'
  elif command -v shasum >/dev/null 2>&1; then
    shasum -a 256 "$1" | sed 's/ .*//'
  else
    echo "error: need sha256sum or shasum on PATH to record the profile state." >&2
    return 1
  fi
}

launcher_file_size() {
  wc -c < "$1" | tr -d ' '
}

launcher_lock() {
  mkdir -p "$LATTICE_HOME_DIR" || return 1
  if ! mkdir "$LATTICE_LOCK_DIR" 2>/dev/null; then
    echo "error: another setup run holds the lock at $LATTICE_LOCK_DIR; refusing." >&2
    return 1
  fi
}

launcher_unlock() {
  rmdir "$LATTICE_LOCK_DIR" 2>/dev/null || true
}

# Reads and validates $LATTICE_MANIFEST. On success sets
# MANIFEST_VERSION and MANIFEST_BINDIR and returns 0; otherwise prints why and
# returns 1. A manifest with any unexpected field or altered value is invalid.
launcher_read_manifest() {
  MANIFEST_VERSION=""
  MANIFEST_BINDIR=""
  if [ ! -f "$LATTICE_MANIFEST" ]; then
    echo "no valid manifest at $LATTICE_MANIFEST (nothing installed by this script)." >&2
    return 1
  fi
  if ! command -v python3 >/dev/null 2>&1 && ! command -v python >/dev/null 2>&1; then
    echo "error: need python3 or python on PATH to validate $LATTICE_MANIFEST." >&2
    return 1
  fi
  local py
  py="$(command -v python3 2>/dev/null || command -v python)"
  local checked
  checked="$("$py" - "$LATTICE_MANIFEST" <<'PYEOF'
import json, sys
try:
    doc = json.load(open(sys.argv[1], encoding="utf-8"))
except Exception as e:
    print("manifest does not parse: %s" % e)
    sys.exit(1)
if not isinstance(doc, dict) or set(doc.keys()) - {"version", "binDir", "path"}:
    print("manifest has an unexpected shape or extra field")
    sys.exit(1)
if not isinstance(doc.get("version"), str) or not doc["version"]:
    print("manifest version is not a non-empty string")
    sys.exit(1)
if not isinstance(doc.get("binDir"), str) or not doc["binDir"]:
    print("manifest binDir is not a non-empty string")
    sys.exit(1)
path = doc.get("path")
if path is not None:
    if not isinstance(path, dict):
        print("manifest path record is not an object")
        sys.exit(1)
    kind = path.get("kind")
    if kind == "profile-block":
        if set(path.keys()) != {"kind", "file", "block", "existed", "size", "sha256"}:
            print("manifest path record has an unexpected shape or extra field")
            sys.exit(1)
    elif kind == "user-env-path":
        if set(path.keys()) != {"kind", "entry", "alreadyExisted"}:
            print("manifest path record has an unexpected shape or extra field")
            sys.exit(1)
    else:
        print("manifest path record has an unknown kind")
        sys.exit(1)
print("OK %s" % doc["binDir"])
PYEOF
)" || { echo "error: invalid manifest at $LATTICE_MANIFEST: $checked" >&2; return 1; }
  case "$checked" in
    OK\ *) MANIFEST_BINDIR="${checked#OK }" ;;
    *) echo "error: invalid manifest at $LATTICE_MANIFEST: $checked" >&2; return 1 ;;
  esac
  MANIFEST_VERSION="$("$py" -c 'import json,sys; print(json.load(open(sys.argv[1], encoding="utf-8"))["version"])' "$LATTICE_MANIFEST")"
  # The owned dir must be exactly the path this script would choose itself:
  # absolute, no '..', not a symlink, under the real home, canonical-equal.
  case "$MANIFEST_BINDIR" in
    /*) ;;
    *) echo "error: manifest binDir is not absolute: $MANIFEST_BINDIR" >&2; return 1 ;;
  esac
  case "$MANIFEST_BINDIR" in
    *"/../"*|*/..|*/../*) echo "error: manifest binDir contains '..': $MANIFEST_BINDIR" >&2; return 1 ;;
  esac
  if [ -L "$MANIFEST_BINDIR" ]; then
    echo "error: manifest binDir is a symlink: $MANIFEST_BINDIR" >&2; return 1
  fi
  home_canon="$(launcher_home_canonical)" || return 1
  bin_canon="$(launcher_canonical "$MANIFEST_BINDIR")" || {
    echo "error: manifest binDir does not exist: $MANIFEST_BINDIR" >&2
    return 1
  }
  case "$bin_canon/" in
    "$home_canon/"*) ;;
    *) echo "error: manifest binDir is not under the home directory: $MANIFEST_BINDIR" >&2; return 1 ;;
  esac
  own_canon="$home_canon/.lattice/bin"
  if [ "$bin_canon" != "$own_canon" ]; then
    echo "error: manifest binDir is not this script's owned directory ($LATTICE_BIN_DIR): $MANIFEST_BINDIR" >&2
    return 1
  fi
  return 0
}

launcher_pick_profile() {
  # Prints the profile file to edit, or nothing with instructions when no
  # profile may be edited. Never edits here; editing is the caller's job.
  local shell_name="${SHELL##*/}"
  case "$shell_name" in
    zsh) printf '%s' "${HOME}/.zprofile" ;;
    bash)
      if [ -e "${HOME}/.bash_profile" ]; then
        printf '%s' "${HOME}/.bash_profile"
      else
        printf '%s' "${HOME}/.profile"
      fi
      ;;
    *)
      echo "Your shell is '$shell_name': this script edits a profile only for zsh or bash."
      echo "Add this line yourself, then open a new terminal:"
      echo "  $LATTICE_PROFILE_LINE"
      return 2
      ;;
  esac
  return 0
}

launcher_block_bytes() {
  printf '%s\n%s\n%s\n' "$LATTICE_MARK_BEGIN" "$LATTICE_PROFILE_LINE" "$LATTICE_MARK_END"
}

launcher_tail_equals() {
  # Whether the last bytes of $1 are exactly $2. Uses cmp, because [ ] string
  # comparison strips trailing newlines asymmetrically (only the $( ) side).
  # Only for values already whole in bash (e.g. the just-built block). Values
  # read back from the manifest go through launcher_manifest_block_matches
  # instead: $( ) strips their trailing newline on the way in.
  local file="$1" block="$2" blen tmp rc
  blen="$(printf '%s' "$block" | wc -c | tr -d ' ')"
  tmp="$(mktemp "${TMPDIR:-/tmp}/lattice-tail.XXXXXX")" || return 1
  printf '%s' "$block" > "$tmp"
  tail -c "$blen" "$file" 2>/dev/null | cmp -s - "$tmp"
  rc=$?
  rm -f "$tmp"
  return $rc
}

launcher_manifest_block_matches() {
  # Whether the file's last bytes are exactly the manifest's recorded block.
  # Compared in python so the block's trailing newline survives (bash $( )
  # would strip it and shift the comparison by one).
  local py manifest="$1" file="$2"
  py="$(command -v python3 2>/dev/null || command -v python)"
  "$py" - "$manifest" "$file" <<'PYEOF'
import json, sys
block = json.load(open(sys.argv[1], encoding="utf-8"))["path"]["block"]
data = open(sys.argv[2], "rb").read()
sys.exit(0 if data.endswith(block.encode("utf-8")) else 1)
PYEOF
}

launcher_manifest_block_len() {
  # The recorded block's byte length, as an integer (newline-safe).
  local py
  py="$(command -v python3 2>/dev/null || command -v python)"
  "$py" -c 'import json,sys; print(len(json.load(open(sys.argv[1], encoding="utf-8"))["path"]["block"].encode("utf-8")))' "$1"
}
launcher_json_escape() {
  # Escapes one string for JSON double quotes. Pure bash (BSD sed cannot do
  # the multiline join portably). Non-ASCII bytes pass through unchanged via
  # the default branch, so this is correct even under byte-wise iteration.
  local input="$1" output="" i char
  for (( i = 0; i < ${#input}; i++ )); do
    char="${input:$i:1}"
    case "$char" in
      \\) output="${output}\\\\" ;;
      '"') output="${output}\\\"" ;;
      $'\n') output="${output}\\n" ;;
      *) output="${output}${char}" ;;
    esac
  done
  printf '%s' "$output"
}

launcher_write_manifest() {
  # $1 version, $2 bindir, then optionally: path-kind file block existed size sha256.
  local ver="$1" bindir="$2"
  local tmp
  tmp="$(mktemp "${LATTICE_HOME_DIR}/install.XXXXXX")" || return 1
  {
    printf '{\n  "version": "%s",\n  "binDir": "%s"' "$(launcher_json_escape "$ver")" "$(launcher_json_escape "$bindir")"
    if [ "${3:-}" = "profile-block" ]; then
      printf ',\n  "path": {\n    "kind": "profile-block",\n    "file": "%s",\n    "block": "%s",\n    "existed": %s,\n    "size": %s,\n    "sha256": "%s"\n  }' \
        "$(launcher_json_escape "$4")" "$(launcher_json_escape "$5")" "$6" "$7" "$8"
    fi
    printf '\n}\n'
  } > "$tmp"
  mv "$tmp" "$LATTICE_MANIFEST"
}

launcher_install() {
  local add_to_path="$1" dry_run="$2"
  local ver
  ver="$(launcher_version)" || return 1

  # Read-only checks first, so --dry-run creates nothing at all.
  if [ -f "$LATTICE_MANIFEST" ]; then
    if launcher_read_manifest >/dev/null 2>&1; then
      echo "lattice $MANIFEST_VERSION is already installed at $MANIFEST_BINDIR; run --uninstall-launcher first." >&2
      return 1
    fi
    echo "error: $LATTICE_MANIFEST exists but is not valid; refusing to touch it." >&2
    return 1
  fi
  other="$(command -v lattice 2>/dev/null || true)"
  if [ -n "$other" ]; then
    echo "error: a 'lattice' already resolves on PATH at $other; refusing to shadow it (not executed)." >&2
    return 1
  fi
  if [ -d "$LATTICE_BIN_DIR" ] && [ -n "$(ls -A "$LATTICE_BIN_DIR" 2>/dev/null)" ]; then
    echo "error: $LATTICE_BIN_DIR already exists with contents not recorded in a valid manifest; refusing." >&2
    echo "Unrelated files left in place:" >&2
    ls -A "$LATTICE_BIN_DIR" >&2
    return 1
  fi

  if [ "$dry_run" = 1 ]; then
    echo "dry-run: would pack Cli (version $ver) and install the lattice tool into $LATTICE_BIN_DIR."
    if [ "$add_to_path" = 1 ]; then
      profile="$(launcher_pick_profile 2>/dev/null || true)"
      if [ -n "$profile" ]; then
        echo "dry-run: would append one marked block to $profile."
      else
        echo "dry-run: would print PATH instructions (unsupported shell)."
      fi
    else
      echo "dry-run: to use it, add this line to your shell profile and open a new terminal:"
      echo "  $LATTICE_PROFILE_LINE"
    fi
    echo "dry-run: manifest would be written to $LATTICE_MANIFEST. Nothing changed."
    return 0
  fi

  launcher_need_dotnet || return 1
  mkdir -p "$LATTICE_HOME_DIR" || return 1
  launcher_lock || return 1
  trap launcher_unlock EXIT INT TERM

  # Re-validate under the lock.
  if [ -f "$LATTICE_MANIFEST" ]; then
    launcher_unlock; trap - EXIT INT TERM
    echo "error: install state changed under the lock; refusing." >&2
    return 1
  fi

  local packdir
  packdir="$(mktemp -d "${TMPDIR:-/tmp}/lattice-pack.XXXXXX")" || { launcher_unlock; trap - EXIT INT TERM; return 1; }

  echo "Packing the CLI (version $ver)..."
  if ! dotnet pack Cli -c Release --output "$packdir"; then
    echo "error: dotnet pack failed; removing only this run's temp pack folder." >&2
    rm -rf "$packdir"
    launcher_unlock; trap - EXIT INT TERM
    return 1
  fi

  echo "Installing the lattice tool into $LATTICE_BIN_DIR..."
  if ! dotnet tool install --tool-path "$LATTICE_BIN_DIR" --add-source "$packdir" --version "$ver" lattice; then
    echo "error: dotnet tool install failed; removing only this run's temp pack folder." >&2
    rm -rf "$packdir"
    if [ -d "$LATTICE_BIN_DIR" ] && [ -z "$(ls -A "$LATTICE_BIN_DIR" 2>/dev/null)" ]; then
      rmdir "$LATTICE_BIN_DIR" 2>/dev/null || true
    fi
    launcher_unlock; trap - EXIT INT TERM
    return 1
  fi
  rm -rf "$packdir"

  # PATH record goes into the manifest BEFORE the profile edit, so a crash
  # between the two is detectable on uninstall.
  local p_existed="" p_size="" p_sha="" p_file="" p_block=""
  if [ "$add_to_path" = 1 ]; then
    if ! p_file="$(launcher_pick_profile)"; then
      # Unsupported shell, symlink or unwritable target: instructions printed,
      # install itself left complete, manifest without a path record.
      launcher_write_manifest "$ver" "$LATTICE_BIN_DIR" || { launcher_unlock; trap - EXIT INT TERM; return 1; }
      echo "Installed lattice $ver into $LATTICE_BIN_DIR."
      echo "Open a new terminal after updating your PATH."
      launcher_unlock; trap - EXIT INT TERM
      return 0
    fi
    if [ -L "$p_file" ]; then
      echo "Profile $p_file is a symlink: editing nothing; install itself is complete." >&2
      echo "Add this line yourself, then open a new terminal:" >&2
      echo "  $LATTICE_PROFILE_LINE" >&2
      launcher_write_manifest "$ver" "$LATTICE_BIN_DIR" || { launcher_unlock; trap - EXIT INT TERM; return 1; }
      launcher_unlock; trap - EXIT INT TERM
      return 0
    fi
    if [ -e "$p_file" ] && [ ! -w "$p_file" ]; then
      echo "Profile $p_file is not writable: editing nothing; install itself is complete." >&2
      echo "Add this line yourself, then open a new terminal:" >&2
      echo "  $LATTICE_PROFILE_LINE" >&2
      launcher_write_manifest "$ver" "$LATTICE_BIN_DIR" || { launcher_unlock; trap - EXIT INT TERM; return 1; }
      launcher_unlock; trap - EXIT INT TERM
      return 0
    fi
    p_block="$(launcher_block_bytes; printf x)"
    p_block="${p_block%x}"
    if [ -e "$p_file" ]; then
      p_existed="true"; p_size="$(launcher_file_size "$p_file")" || { launcher_unlock; trap - EXIT INT TERM; return 1; }
      p_sha="$(launcher_sha256_file "$p_file")" || { launcher_unlock; trap - EXIT INT TERM; return 1; }
    else
      p_existed="false"; p_size="0"; p_sha="none"
    fi
    launcher_write_manifest "$ver" "$LATTICE_BIN_DIR" profile-block "$p_file" "$p_block" "$p_existed" "$p_size" "$p_sha" \
      || { launcher_unlock; trap - EXIT INT TERM; return 1; }
    # Append via temp file + atomic rename, preserving permissions.
    p_tmp="$(mktemp "$(dirname "$p_file")/.lattice-profile.XXXXXX")" || { launcher_unlock; trap - EXIT INT TERM; return 1; }
    if [ -e "$p_file" ]; then
      cp -p "$p_file" "$p_tmp" || { rm -f "$p_tmp"; launcher_unlock; trap - EXIT INT TERM; return 1; }
      if [ -s "$p_file" ] && [ -n "$(tail -c 1 "$p_file" | tr -d '\n')" ]; then
        # No trailing newline: the separator becomes part of the recorded bytes.
        p_block="$(printf '\n%s' "$p_block"; printf x)"
        p_block="${p_block%x}"
        launcher_write_manifest "$ver" "$LATTICE_BIN_DIR" profile-block "$p_file" "$p_block" "$p_existed" "$p_size" "$p_sha" \
          || { rm -f "$p_tmp"; launcher_unlock; trap - EXIT INT TERM; return 1; }
      fi
      printf '%s' "$p_block" >> "$p_tmp"
    else
      printf '%s' "$p_block" > "$p_tmp"
    fi
    mv "$p_tmp" "$p_file"
    # Verify the tail is exactly what the manifest records.
    if ! launcher_tail_equals "$p_file" "$p_block"; then
      echo "error: profile edit did not verify; inspect $p_file (install itself is complete)." >&2
      launcher_unlock; trap - EXIT INT TERM
      return 1
    fi
  else
    launcher_write_manifest "$ver" "$LATTICE_BIN_DIR" || { launcher_unlock; trap - EXIT INT TERM; return 1; }
  fi

  echo "Installed lattice $ver into $LATTICE_BIN_DIR."
  if [ "$add_to_path" != 1 ]; then
    echo "To use it, add this line to your shell profile and open a new terminal:"
    echo "  $LATTICE_PROFILE_LINE"
  else
    echo "Profile updated. A running shell does not see the change; open a new terminal."
  fi
  echo "Note: on a Homebrew .NET install the launcher can fail with 'You must install"
  echo ".NET to run this application.' even though 'dotnet' works; set DOTNET_ROOT to"
  echo "the real runtime root (/opt/homebrew/opt/dotnet/libexec). See README."
  launcher_unlock; trap - EXIT INT TERM
  return 0
}

launcher_uninstall() {
  local dry_run="$1"
  if ! launcher_read_manifest; then
    return 1
  fi
  launcher_need_dotnet || return 1
  launcher_lock || return 1
  trap launcher_unlock EXIT INT TERM

  if [ "$dry_run" = 1 ]; then
    echo "dry-run: would uninstall lattice $MANIFEST_VERSION from $MANIFEST_BINDIR and remove what this script added. Nothing changed."
    launcher_unlock; trap - EXIT INT TERM
    return 0
  fi

  echo "Uninstalling lattice $MANIFEST_VERSION from $MANIFEST_BINDIR..."
  if ! dotnet tool uninstall --tool-path "$MANIFEST_BINDIR" lattice; then
    echo "error: dotnet tool uninstall failed; leaving the manifest for inspection." >&2
    launcher_unlock; trap - EXIT INT TERM
    return 1
  fi

  # Restore a profile PATH edit, verifying byte-identity both ways.
  launcher_restore_path || { launcher_unlock; trap - EXIT INT TERM; return 1; }

  # Release the lock before the final cleanup: the emptiness checks below must
  # not see the lock directory itself (rmdir only removes empty dirs, so this
  # cannot delete user content; a failure here still runs the EXIT trap).
  launcher_unlock; trap - EXIT INT TERM

  rm -f "$LATTICE_MANIFEST"
  # 'dotnet tool uninstall' leaves an empty .store/.stage chain behind. rmdir
  # removes a level only when it is empty, so user content can never be lost here.
  rmdir "$MANIFEST_BINDIR/.store/.stage" 2>/dev/null || true
  rmdir "$MANIFEST_BINDIR/.store" 2>/dev/null || true
  if [ -d "$MANIFEST_BINDIR" ]; then
    if [ -z "$(ls -A "$MANIFEST_BINDIR" 2>/dev/null)" ]; then
      rmdir "$MANIFEST_BINDIR" 2>/dev/null || true
    else
      echo "The owned directory holds unrelated files; leaving it in place:" >&2
      ls -A "$MANIFEST_BINDIR" >&2
    fi
  fi
  if [ -d "$LATTICE_HOME_DIR" ]; then
    if [ -z "$(ls -A "$LATTICE_HOME_DIR" 2>/dev/null)" ]; then
      rmdir "$LATTICE_HOME_DIR" 2>/dev/null || true
    else
      echo "$LATTICE_HOME_DIR holds other files; leaving it in place." >&2
    fi
  fi

  echo "Uninstalled. A running shell still has the old PATH; open a new terminal."
  launcher_unlock; trap - EXIT INT TERM
  return 0
}

launcher_restore_path() {
  # Reads the path record from the manifest and undoes exactly those bytes.
  local py
  py="$(command -v python3 2>/dev/null || command -v python)"
  local kind
  kind="$("$py" -c 'import json,sys; d=json.load(open(sys.argv[1], encoding="utf-8")); p=d.get("path"); print(p["kind"] if p else "none")' "$LATTICE_MANIFEST")"
  if [ "$kind" = "none" ]; then
    return 0
  fi
  if [ "$kind" = "user-env-path" ]; then
    echo "error: manifest records a Windows user-PATH entry; this script cannot undo it." >&2
    return 1
  fi
  if [ "$kind" != "profile-block" ]; then
    echo "error: manifest records an unknown path kind: $kind" >&2
    return 1
  fi
  local file existed size sha
  file="$("$py" -c 'import json,sys; print(json.load(open(sys.argv[1], encoding="utf-8"))["path"]["file"])' "$LATTICE_MANIFEST")"
  existed="$("$py" -c 'import json,sys; v=json.load(open(sys.argv[1], encoding="utf-8"))["path"]["existed"]; print("true" if v else "false")' "$LATTICE_MANIFEST")"
  size="$("$py" -c 'import json,sys; print(json.load(open(sys.argv[1], encoding="utf-8"))["path"]["size"])' "$LATTICE_MANIFEST")"
  sha="$("$py" -c 'import json,sys; print(json.load(open(sys.argv[1], encoding="utf-8"))["path"]["sha256"])' "$LATTICE_MANIFEST")"
  if [ ! -e "$file" ]; then
    if [ "$existed" = "false" ]; then
      return 0
    fi
    echo "error: profile $file is gone but the manifest says it existed; leaving everything for inspection." >&2
    return 1
  fi
  if ! launcher_manifest_block_matches "$LATTICE_MANIFEST" "$file"; then
    echo "error: the marked block in $file was altered by the user; leaving the file for inspection." >&2
    return 1
  fi
  local flen keep blen
  flen="$(launcher_file_size "$file")"
  blen="$(launcher_manifest_block_len "$LATTICE_MANIFEST")" || return 1
  keep=$((flen - blen))
  local tmp
  tmp="$(mktemp "$(dirname "$file")/.lattice-profile.XXXXXX")" || return 1
  # cp -p first so the temp file carries the original mode/ownership; the
  # truncate below keeps them, and the atomic rename keeps them on the profile.
  cp -p "$file" "$tmp" || { rm -f "$tmp"; return 1; }
  : > "$tmp"
  if [ "$keep" -gt 0 ]; then
    head -c "$keep" "$file" > "$tmp" || { rm -f "$tmp"; return 1; }
  fi
  if [ "$existed" = "false" ]; then
    if [ "$keep" -gt 0 ]; then
      echo "error: profile $file did not exist before but removing our block leaves other content; leaving it." >&2
      rm -f "$tmp"
      return 1
    fi
    rm -f "$tmp"
    rm -f "$file"
    return 0
  fi
  # $tmp already carries the original mode/ownership (cp -p above);
  # mv renames it atomically onto the profile.
  mv "$tmp" "$file"
  # Byte-identity against the recorded original.
  if [ "$(launcher_file_size "$file")" != "$size" ]; then
    echo "error: profile $file size does not match the recorded original; leaving it." >&2
    return 1
  fi
  if [ "$sha" != "none" ] && [ "$(launcher_sha256_file "$file")" != "$sha" ]; then
    echo "error: profile $file content does not match the recorded original; leaving it." >&2
    return 1
  fi
  return 0
}



# --- Launcher dispatch: with a launcher flag the repo flow below is skipped. ---
if [ "$LAUNCHER_INSTALL" = 1 ] || [ "$LAUNCHER_UNINSTALL" = 1 ]; then
  if [ "$LAUNCHER_INSTALL" = 1 ]; then
    launcher_install "$LAUNCHER_ADD_TO_PATH" "$LAUNCHER_DRY_RUN"
  else
    launcher_uninstall "$LAUNCHER_DRY_RUN"
  fi
  exit $?
fi

CSPROJ="Cli/Lattice.Cli.csproj"
if [[ ! -f "$CSPROJ" ]]; then
  echo "error: $CSPROJ not found; run from the repository root" >&2
  exit 1
fi

# The SDK requirement is read from the project file, never hardcoded here.
TARGET_FRAMEWORK="$(sed -n 's:.*<TargetFramework>\([^<]*\)</TargetFramework>.*:\1:p' "$CSPROJ" | head -n1)"
REQUIRED_MAJOR="$(printf '%s' "$TARGET_FRAMEWORK" | sed 's/^net//; s/\..*//')"
ROLL_FORWARD="$(sed -n 's:.*<RollForward>\([^<]*\)</RollForward>.*:\1:p' "$CSPROJ" | head -n1)"

if [[ -z "$TARGET_FRAMEWORK" || -z "$REQUIRED_MAJOR" ]]; then
  echo "error: could not read TargetFramework from $CSPROJ" >&2
  exit 1
fi

if ! command -v dotnet >/dev/null 2>&1; then
  echo "error: .NET SDK not found on PATH." >&2
  echo "Install the .NET ${REQUIRED_MAJOR} SDK: https://dotnet.microsoft.com/download/dotnet/${REQUIRED_MAJOR}" >&2
  exit 1
fi

SDK_VERSION="$(dotnet --version 2>/dev/null || true)"
if [[ -z "$SDK_VERSION" ]]; then
  echo "error: no .NET SDK installed (a runtime alone is not enough)." >&2
  echo "Install the .NET ${REQUIRED_MAJOR} SDK: https://dotnet.microsoft.com/download/dotnet/${REQUIRED_MAJOR}" >&2
  exit 1
fi

SDK_MAJOR="${SDK_VERSION%%.*}"
if (( SDK_MAJOR < REQUIRED_MAJOR )); then
  echo "error: this repository targets $TARGET_FRAMEWORK (RollForward=$ROLL_FORWARD) but the installed SDK is $SDK_VERSION." >&2
  echo "Install the .NET ${REQUIRED_MAJOR} SDK or newer: https://dotnet.microsoft.com/download/dotnet/${REQUIRED_MAJOR}" >&2
  exit 1
fi

echo "Using .NET SDK $SDK_VERSION (repo requires $TARGET_FRAMEWORK, RollForward=$ROLL_FORWARD)."

echo "Restoring dependencies..."
dotnet restore Lattice.sln

echo "Building in Release..."
dotnet build Lattice.sln -c Release --no-restore

echo "Running the full test suite..."
dotnet test Lattice.sln -c Release --no-build

echo "Reproducing one committed claim: golden trajectory replay..."
GOLDEN="Tests/fixtures/golden_trajectory.jsonl"
if dotnet run -c Release --project Cli -- replay "$GOLDEN" --verify; then
  echo "Reproduced: golden trajectory replay --verify, per-step results match the committed $GOLDEN"
else
  echo "Mismatch: replay --verify did not reproduce $GOLDEN; see the error above." >&2
  exit 1
fi

echo "setup.sh complete."
