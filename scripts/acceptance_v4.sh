#!/usr/bin/env bash
# Lattice v4 acceptance report. Runs the slices below, records works / broken /
# not-tested per slice with command, exit code and an evidence file, and stops at
# the first broken slice (remaining slices are recorded as not-tested: not reached).
# It never reports "v4 is usable"; that call belongs to the person reading the report.
#
# Usage: scripts/acceptance_v4.sh [OUT_DIR]
#   OUT_DIR  new directory for evidence (default: .evidence/acceptance-v4-<UTC>);
#            it must not exist yet. Nothing in it is ever overwritten.
# Optional local-model slice (loopback only, never a hosted provider). To run WITHOUT it, unset the three
# LATTICE_ACCEPT_* variables first: env -u LATTICE_ACCEPT_LOCAL_ENDPOINT -u LATTICE_ACCEPT_LOCAL_MODEL -u LATTICE_ACCEPT_STEP_MS scripts/acceptance_v4.sh
#
#   LATTICE_ACCEPT_LOCAL_ENDPOINT  full chat-completions URL: http://127.0.0.1|localhost|[::1]:PORT/path, no credentials, no query
#   LATTICE_ACCEPT_LOCAL_MODEL     model id to send
#   LATTICE_ACCEPT_STEP_MS         per-step budget, default 30000
# The endpoint you start is trusted: the adapter uses urllib, which follows HTTP redirects, and this script
# does not claim redirect safety. It only checks that the URL you give is a plain loopback http URL.
# The hosted-model slice is always not-tested here: it needs a provider choice and
# cost terms settled by a person.
# No network use: build uses --no-restore, so a missing cache is a stop, not a download.
# Portable to the bash 3.2 that ships with macOS: no associative arrays or other bash 4 features.
# S1 accepts two tree states: clean, or only the two Stage E paths (this script, docs/CLI.md)
# differing before they are committed. Anything else tracked and changed is broken.
set -u
cd "$(dirname "$0")/.." || exit 3
OUT="${1:-.evidence/acceptance-v4-$(date -u +%Y%m%dT%H%M%SZ)}"
if [ -e "$OUT" ]; then echo "error: $OUT already exists; refusing to overwrite" >&2; exit 3; fi
mkdir -p "$OUT" || exit 3
ROWS="$OUT/rows.tsv"; : > "$ROWS"
CLI=(dotnet run --no-build -c Release --project Cli --)
STOPPED=""
ALL="S1 S2 S3 S4 S5 S6 S7 S8 S9 S10 S11 S12 S13"
DONE=" "

row() { # id status command exit evidence note
  printf '%s\t%s\t%s\t%s\t%s\t%s\n' "$1" "$2" "$3" "$4" "$5" "$6" >> "$ROWS"; DONE="$DONE$1 "
  echo "[$1] $2 (exit $4) $6"
  if [ "$2" = broken ]; then STOPPED="$1"; fi
}
finish() {
  for id in $ALL; do
    case "$DONE" in *" $id "*) ;; *)
      if [ -n "$STOPPED" ]; then row "$id" not-tested "-" "-" "-" "not reached: stopped at $STOPPED"
      else row "$id" not-tested "-" "-" "-" "no executed command"; fi
    ;; esac
  done
  python3 - "$ROWS" "$OUT" "$STOPPED" <<'PY'
import sys
rows=[l.rstrip("\n").split("\t") for l in open(sys.argv[1])]
order=lambda r:int(r[0][1:])
rows.sort(key=order)
out=["# Lattice v4 acceptance report","","| slice | status | command | exit | evidence | note |","|---|---|---|---|---|---|"]
for r in rows: out.append("| "+" | ".join(c.replace("|","/") for c in r)+" |")
out+=["",("Stopped at "+sys.argv[3]+"." if sys.argv[3] else "No slice stopped the run."),
"","Usable v4 not claimed. Hosted-model slice is not-tested here; the local-model slice is not-tested unless it shows works above.","Push: not covered by this script."]
open(sys.argv[2]+"/ACCEPTANCE.md","w").write("\n".join(out)+"\n")
print("\n".join(out))
PY
  [ -z "$STOPPED" ]; exit $?
}
# artifact_ok JSON DIR: exactly 2 completed matches, no void runs, no agent failures, exactly 2 recordings
artifact_ok() {
  [ "$(ls "$2"/*.jsonl 2>/dev/null | wc -l | tr -d ' ')" = "2" ] && \
  python3 -c "import json,sys;d=json.load(open(sys.argv[1]));s=d['Studies'][0];assert d['VoidRuns']==0 and not d['AgentFailures'] and s['Statistics']['Matches']==2 and 'Decision' in s" "$1" 2>> "$3"
}
run() { # id label expected-exit cmd... ; evidence in $OUT/<id>.log; returns exit via $RC
  local id=$1; shift; local want=$1; shift
  "$@" > "$OUT/$id.log" 2>&1; RC=$?
  CMDSTR="$*"; [ "$RC" -eq "$want" ]
}

# S1 identity and tree state. Every git command must succeed before anything is classified.
s1fail() { echo "git failed: $1" > "$OUT/S1.log"; row S1 broken "git $1" 1 S1.log "git command failed; not classified (a failing git is never reported as clean)"; finish; }
GHEAD=$(git rev-parse HEAD) || s1fail "rev-parse HEAD"
GBR=$(git branch --show-current) || s1fail "branch --show-current"
GST=$(git status --porcelain --untracked-files=no) || s1fail "status --porcelain"
GLOG=$(git log --format='%h %an <%ae> | %cn <%ce>' eb13fade0a02dbe90c1626fa8536c680966b09ba..HEAD) || s1fail "log eb13fad..HEAD"
GIDS=$(git log --format='%an <%ae>%n%cn <%ce>' eb13fade0a02dbe90c1626fa8536c680966b09ba..HEAD) || s1fail "log identities"
[ -n "$GLOG" ] || s1fail "log eb13fad..HEAD returned no commits"
GIDS=$(printf '%s\n' "$GIDS" | sort -u) || s1fail "sort identities"
STATE=clean; DIRTY=0
while IFS= read -r line; do
  [ -z "$line" ] && continue
  case "${line:3}" in
    scripts/acceptance_v4.sh|docs/CLI.md) STATE="prep (only scripts/acceptance_v4.sh and docs/CLI.md differ)";;
    *) DIRTY=1;;
  esac
done <<EOS
$GST
EOS
{ echo "HEAD: $GHEAD"; echo "branch: $GBR"; echo "tree state: $STATE"; echo "tracked status:"; printf '%s\n' "$GST"
  echo "log eb13fad..HEAD (author | committer):"; printf '%s\n' "$GLOG"; } > "$OUT/S1.log"
if [ "$DIRTY" -eq 0 ] && [ "$GIDS" = "candavere <304146049+candavere@users.noreply.github.com>" ]; then
  row S1 works "git rev-parse/branch/status/log (read-only)" 0 S1.log "tree state: $STATE; author and committer are candavere noreply only"
else row S1 broken "git rev-parse/branch/status/log (read-only)" 1 S1.log "a tracked path outside the two Stage E paths differs, or an identity is not candavere noreply"; finish; fi

# S2 build (no restore: a missing cache is a stop)
if run S2 0 dotnet build Lattice.sln -c Release --no-restore; then row S2 works "$CMDSTR" 0 S2.log "0 errors"
else row S2 broken "$CMDSTR" "$RC" S2.log "build failed (if restore assets are missing the script does not restore)"; finish; fi

# S3 dotnet suite, no rerun
if run S3 0 dotnet test Lattice.sln -c Release --no-build; then
  N=$(grep -E "^(Passed|Failed)!" "$OUT/S3.log" | tail -1 | tr -s ' ')
  row S3 works "$CMDSTR" 0 S3.log "${N:-counts not parsed}"
else row S3 broken "$CMDSTR" "$RC" S3.log "suite red; not rerun; read S3.log"; finish; fi

# S4 adapter unit tests
cp_run() { (cd examples/python && PYTHONDONTWRITEBYTECODE=1 python3 -m unittest -v test_lattice_model_agent test_lattice_model_agent_acceptance) > "$OUT/S4.log" 2>&1; RC=$?; }
cp_run; CMDSTR="(cd examples/python && python3 -m unittest -v test_lattice_model_agent test_lattice_model_agent_acceptance)"
if [ "$RC" -eq 0 ]; then row S4 works "$CMDSTR" 0 S4.log "$(grep -E '^Ran ' "$OUT/S4.log")"
else row S4 broken "$CMDSTR" "$RC" S4.log "adapter tests red"; finish; fi

# S5 reference agent match with recording (plumbing only; the verdict is not a quality claim)
EP="$OUT/ref-episodes"
if run S5 0 "${CLI[@]}" evaluate --scenario standard --seed-set dev --seeds 1 --agent-cmd "python3 examples/python/lattice_agent.py" --record-episodes "$EP" --out "$OUT/ref.json" \
   ; CRC=$RC; [ "$CRC" -eq 0 ] && artifact_ok "$OUT/ref.json" "$EP" "$OUT/S5.log"; then
  row S5 works "$CMDSTR" 0 "S5.log ref.json ref-episodes/" "2 episode files; VoidRuns 0; AgentFailures empty; Decision: $(python3 -c "import json,sys;print(json.load(open(sys.argv[1]))['Studies'][0]['Decision'][:70])" "$OUT/ref.json")"
else if [ "$CRC" -ne 0 ]; then row S5 broken "$CMDSTR" "$CRC" S5.log "command exit $CRC"; else row S5 broken "$CMDSTR" "$CRC" S5.log "command exit 0 but artifact validation failed (VoidRuns, AgentFailures, match count or recordings): see S5.log"; fi; finish; fi

# S6 replay --verify on each recording
OK=1; for f in "$EP"/*.jsonl; do "${CLI[@]}" replay "$f" --verify >> "$OUT/S6.log" 2>&1 || OK=0; done
if [ $OK -eq 1 ]; then row S6 works "replay <file> --verify (each episode)" 0 S6.log "both recordings verify"
else row S6 broken "replay <file> --verify (each episode)" 1 S6.log "a recording failed verify"; finish; fi

# S7 expected-negative: second run into the same dir must refuse (exit 1) and write no artifact
if run S7 1 "${CLI[@]}" evaluate --scenario standard --seed-set dev --seeds 1 --agent-cmd "python3 examples/python/lattice_agent.py" --record-episodes "$EP" --out "$OUT/ref-second.json" \
   && grep -q "refusing to overwrite" "$OUT/S7.log" && [ ! -e "$OUT/ref-second.json" ]; then
  row S7 works "$CMDSTR" 1 S7.log "expected-negative: refused, named existing files, no artifact"
else row S7 broken "$CMDSTR" "$RC" S7.log "expected exit 1, got $RC; if exit was 1 then the named-files message or the no-artifact check failed"; finish; fi

# S8 expected-negative: --record-episodes without --agent-cmd is a usage error (exit 2)
if run S8 2 "${CLI[@]}" evaluate --scenario standard --seed-set dev --seeds 1 --record-episodes "$OUT/none-expected" \
   && grep -q "requires --agent-cmd" "$OUT/S8.log" && [ ! -e "$OUT/none-expected" ]; then
  row S8 works "$CMDSTR" 2 S8.log "expected-negative: usage error, nothing written"
else row S8 broken "$CMDSTR" "$RC" S8.log "expected exit 2, got $RC; if exit was 2 then the message or the nothing-written check failed"; finish; fi

# S9 expected-negative: named key variable unset -> exit 1 before the handshake, no stdout
VAR=LATTICE_ACCEPT_DEFINITELY_UNSET_VAR; unset "$VAR"
python3 examples/python/lattice_model_agent.py --endpoint http://127.0.0.1:9/v1/chat/completions --model x --prompt-version acceptance --api-key-env "$VAR" < /dev/null > "$OUT/S9.stdout" 2> "$OUT/S9.log"; RC=$?
CMDSTR="python3 examples/python/lattice_model_agent.py --endpoint http://127.0.0.1:9/v1/chat/completions --model x --prompt-version acceptance --api-key-env $VAR </dev/null"
XERR="lattice_model_agent: --api-key-env names '$VAR', which is not set to a non-empty value"
if [ "$RC" -eq 1 ] && [ ! -s "$OUT/S9.stdout" ] && [ "$(cat "$OUT/S9.log")" = "$XERR" ]; then
  row S9 works "$CMDSTR" "$RC" "S9.log S9.stdout" "expected-negative: exit 1, empty stdout, the single documented stderr line"
else row S9 broken "$CMDSTR" "$RC" "S9.log" "unset key variable did not fail with the documented line and exit 1"; finish; fi

# S10 local-model match (Stage C): only on a loopback endpoint the person already started
EPT="${LATTICE_ACCEPT_LOCAL_ENDPOINT:-}"; MOD="${LATTICE_ACCEPT_LOCAL_MODEL:-}"
if [ -z "$EPT" ] || [ -z "$MOD" ]; then row S10 not-tested "-" "-" "-" "LATTICE_ACCEPT_LOCAL_ENDPOINT/MODEL not set (no local model server was started by this script)"
elif ! printf '%s' "$EPT" | grep -Eq '^http://(127\.0\.0\.1|localhost|\[::1\]):[0-9]{1,5}/[A-Za-z0-9._/-]*$'; then row S10 not-tested "-" "-" "-" "endpoint refused: must be http://127.0.0.1|localhost|[::1]:PORT/path with no credentials, query, spaces or shell characters"
elif ! printf '%s' "$MOD" | grep -Eq '^[A-Za-z0-9._:/-]+$'; then row S10 not-tested "-" "-" "-" "model id refused: only letters, digits and . _ : / - allowed"
elif ! printf '%s' "$OUT" | grep -Eq '^[A-Za-z0-9._/-]+$'; then row S10 not-tested "-" "-" "-" "output path refused for the agent command line: only letters, digits and . _ / - allowed"
else
  STEP="${LATTICE_ACCEPT_STEP_MS:-30000}"; LOCEP="$OUT/local-episodes"
  AGENT="python3 examples/python/lattice_model_agent.py --endpoint $EPT --model $MOD --prompt-version acceptance --request-timeout-ms 20000 --sidecar $OUT/local-sidecar.json"
  if run S10 0 "${CLI[@]}" evaluate --scenario standard --seed-set dev --seeds 1 --agent-step-timeout-ms "$STEP" --agent-cmd "$AGENT" --record-episodes "$LOCEP" --out "$OUT/local.json" \
     ; CRC=$RC; [ "$CRC" -eq 0 ] && artifact_ok "$OUT/local.json" "$LOCEP" "$OUT/S10.log"; then
    OKL=1; for f in "$LOCEP"/*.jsonl; do "${CLI[@]}" replay "$f" --verify >> "$OUT/S10.replay.log" 2>&1 || OKL=0; done
    if [ $OKL -eq 1 ]; then row S10 works "$CMDSTR" 0 "S10.log local.json local-episodes/ S10.replay.log" "2 completed matches, VoidRuns 0, no AgentFailures, 2 recordings replay-verified; plumbing only, says nothing about model quality"
    else row S10 broken "$CMDSTR" 1 "S10.replay.log" "local recording failed replay --verify"; finish; fi
  else if [ "$CRC" -ne 0 ]; then row S10 broken "$CMDSTR" "$CRC" S10.log "command exit $CRC"; else row S10 broken "$CMDSTR" "$CRC" S10.log "command exit 0 but artifact validation failed (void run, agent failure, match count or recordings): a finding, not a retry"; fi; finish; fi
fi

# S11 hosted-model match (Stage D): never run by this script
row S11 not-tested "-" "-" "-" "needs a provider choice, verified cost terms and an existing key variable; no provider call made"

# S12 secret hygiene: tracked tree and eb13fad..HEAD diff; subprocess capture only, matched text never printed or written.
# Any git failure, unreadable tracked file or empty file list is broken, never a clean result.
python3 - "$OUT/S12.log" <<'PY'
import os,re,subprocess,sys
pats={"private-key":r"-----BEGIN [A-Z ]*PRIVATE KEY-----","aws":r"\b(AKIA|ASIA)[0-9A-Z]{16}\b","github":r"\b(ghp|gho|ghu|ghs|ghr)_[A-Za-z0-9]{30,}|github_pat_[A-Za-z0-9_]{20,}","slack":r"xox[abprs]-[A-Za-z0-9-]{10,}","google":r"AIza[0-9A-Za-z_\-]{30,}","nvapi":r"nvapi-[A-Za-z0-9_\-]{20,}","sk":r"\bsk-[A-Za-z0-9_\-]{16,}","gsk":r"\bgsk_[A-Za-z0-9]{20,}","jwt":r"eyJ[A-Za-z0-9_\-]{10,}\.[A-Za-z0-9_\-]{10,}\."}
cp={k:re.compile(v) for k,v in pats.items()}
hits=set(); problems=[]
def git(*a):
    r=subprocess.run(["git"]+list(a),capture_output=True)
    if r.returncode!=0: problems.append("git %s failed rc=%d"%(a[0],r.returncode))
    return r.stdout
files=[f for f in git("ls-files","-z").split(b"\0") if f]
if not files: problems.append("tracked file list is empty")
scanned=0
for f in files:
    name=f.decode("utf-8","replace")
    if os.path.isdir(f): problems.append("tracked path is a directory or submodule, not scanned: "+name); continue
    try: t=open(f,"rb").read().decode("utf-8","ignore")
    except OSError: problems.append("unreadable tracked file: "+name); continue
    scanned+=1
    for k,p in cp.items():
        if p.search(t): hits.add(("tree",name,k))
d=git("diff","eb13fade0a02dbe90c1626fa8536c680966b09ba","HEAD").decode("utf-8","ignore")
cur="?"
for line in d.splitlines():
    if line.startswith("+++ b/"): cur=line[6:]
    elif line.startswith("+") and not line.startswith("+++"):
        for k,p in cp.items():
            if p.search(line): hits.add(("diff",cur,k))
with open(sys.argv[1],"w") as o:
    o.write("scanned %d of %d tracked files and the eb13fad..HEAD diff; patterns: %s\n"%(scanned,len(files),", ".join(pats)))
    for pr in problems: o.write("PROBLEM "+pr+"\n")
    for h in sorted(hits): o.write("HIT %s %s %s\n"%h)
    o.write("hits: %d problems: %d\n"%(len(hits),len(problems)))
sys.exit(2 if problems else (1 if hits else 0))
PY
RC=$?
if [ $RC -eq 0 ]; then row S12 works "python scan of git ls-files and git diff (names only)" 0 S12.log "tracked files read and net diff eb13fad..HEAD scanned, zero token-shaped hits; per-commit history is NOT covered and a value that is not token-shaped is not detected"
elif [ $RC -eq 1 ]; then row S12 broken "python scan of git ls-files and git diff (names only)" "$RC" S12.log "token-shaped hit(s): paths and pattern names in S12.log, values not recorded"
else row S12 broken "python scan of git ls-files and git diff (names only)" "$RC" S12.log "scan incomplete (git failure or unreadable file): not a clean result"; fi

# S13 fixture literal: classification is a human decision; this script cannot make it
row S13 not-tested "-" "-" "-" "test fixture literal in examples/python/test_lattice_model_agent.py is unclassified (dummy or real); publication stays blocked until a person decides"
finish
