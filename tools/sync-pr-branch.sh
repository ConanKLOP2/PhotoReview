#!/bin/bash
# usage: sync-pr-branch.sh <worktree-dir>
# Merges origin/master into the worktree's branch (never rebases, never force-pushes) and auto-resolves the conflicts that
# every parallel PR produces:
#   - docs/refactoring/OPEN-DECISIONS.md             -> take master's, then regenerate (it is a generated table)
#   - docs/refactoring/PERF-STATUS.md and
#     docs/refactoring/decisions/NOWPF-UNCONFIRMED-DECISIONS-*.md (append-only logs) -> keep BOTH sides
#   - Localization/Languages/{en,vi}[.notes].json    -> keep both sides (append-only)
# After the merge (clean or resolved) a duplicate `order:` in docs/refactoring/decisions/*.md is repaired: the fragments ADDED by
# this PR get the next free order (max + 10); fragments already on master are never renumbered.
# Exit codes: 0 ok, 2 NEEDS-HUMAN (other conflicts), 3 duplicate order that could not be repaired automatically.
set -u
cd "$1" || exit 1
git fetch origin -q

repair_orders() {
  local dups f max new
  dups=$(grep -h '^order:' docs/refactoring/decisions/*.md | sort | uniq -d)
  [ -z "$dups" ] && return 0
  # fragments this PR adds relative to master
  for f in $(git diff --name-only --diff-filter=A origin/master...HEAD -- docs/refactoring/decisions/); do
    [ -f "$f" ] || continue
    cur=$(grep -m1 -h '^order:' "$f" | sed 's/^order:[[:space:]]*//' | tr -d '\r')
    [ -z "$cur" ] && continue
    if grep -h '^order:' docs/refactoring/decisions/*.md | sed 's/^order:[[:space:]]*//' | tr -d '\r' | sort | uniq -d | grep -qx "$cur"; then
      max=$(grep -h '^order:' docs/refactoring/decisions/*.md | sed 's/^order:[[:space:]]*//' | tr -d '\r' | sort -n | tail -1)
      new=$((max + 10))
      sed -i "0,/^order:[[:space:]]*$cur\(\r\?\)\$/s//order: $new\1/" "$f"
      echo "RENUMBERED $f: order $cur -> $new"
    fi
  done
  powershell -NoProfile -ExecutionPolicy Bypass -File tools/generate-open-decisions.ps1 >/dev/null 2>&1
  dups=$(grep -h '^order:' docs/refactoring/decisions/*.md | sort | uniq -d | tr '\n' ' ')
  if [ -n "$dups" ]; then echo "DUPLICATE-ORDER: $dups"; return 3; fi
  return 0
}

if git merge origin/master >/tmp/sync-$$.log 2>&1; then
  echo "MERGED-CLEAN"
  repair_orders; rc=$?
  if [ $rc -ne 0 ]; then exit $rc; fi
  if [ -n "$(git status --porcelain -- docs)" ]; then
    git add -A -- docs
    git commit -q -m "Renumber decision fragment order after merge; regenerate OPEN-DECISIONS.md

Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>"
    echo "ORDER-REPAIRED"
  fi
else
  conflicts=$(git diff --name-only --diff-filter=U)
  other=$(echo "$conflicts" | grep -v -E '^docs/refactoring/OPEN-DECISIONS.md$|^docs/refactoring/PERF-STATUS.md$|^docs/refactoring/decisions/NOWPF-UNCONFIRMED-DECISIONS-[0-9]+\.md$|Localization/Languages/(en|vi)(\.notes)?\.json$|^$')
  if [ -n "$other" ]; then echo "NEEDS-HUMAN: $other"; exit 2; fi
  for f in $conflicts; do
    case "$f" in
      docs/refactoring/OPEN-DECISIONS.md) git checkout --theirs "$f" ;;
      docs/refactoring/PERF-STATUS.md|docs/refactoring/decisions/NOWPF-UNCONFIRMED-DECISIONS-*.md)
        # append-only logs: both sides added lines at the end; keep both, drop the markers
        sed -i -e '/^<<<<<<< /d' -e '/^=======\r\?$/d' -e '/^>>>>>>> /d' "$f" ;;
      *Localization/Languages/*.json)
        python - "$f" <<'PY'
import sys,json
p=sys.argv[1]
raw=open(p,'rb').read().decode('utf-8'); crlf='\r\n' in raw
L=raw.replace('\r\n','\n').split('\n'); out=[]; i=0
while i<len(L):
    if L[i].startswith('<<<<<<<'):
        m=next(j for j in range(i,len(L)) if L[j].startswith('======='))
        e=next(j for j in range(m,len(L)) if L[j].startswith('>>>>>>>'))
        h=L[i+1:m]; t=L[m+1:e]
        if h and not h[-1].rstrip().endswith(','): h[-1]=h[-1].rstrip()+','
        out+=h+t; i=e+1
    else: out.append(L[i]); i+=1
s='\n'.join(out)
json.loads(s)
if crlf: s=s.replace('\n','\r\n')
open(p,'wb').write(s.encode('utf-8'))
PY
        ;;
    esac
  done
  powershell -NoProfile -ExecutionPolicy Bypass -File tools/generate-open-decisions.ps1 >/dev/null 2>&1
  repair_orders; rc=$?
  if [ $rc -ne 0 ]; then exit $rc; fi
  git add -A -- . ':!native'
  git commit -q -m "Merge master; regenerate OPEN-DECISIONS.md

Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>"
  echo "MERGED-RESOLVED"
fi
[ "${SYNC_NO_PUSH:-}" = 1 ] && exit 0   # test hook: merge/repair locally, do not push
git push origin HEAD 2>&1 | tail -1
