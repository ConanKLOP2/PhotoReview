#!/bin/bash
# usage: sync-pr.sh <worktree-dir>
# Merges origin/master into the worktree's branch. Auto-resolves OPEN-DECISIONS.md (take master, regenerate) and the
# append-only i18n JSON files (keep both sides). Any other conflict is left for a human and the script exits 2.
set -u
cd "$1" || exit 1
git fetch origin -q
if git merge origin/master >/tmp/sync-$$.log 2>&1; then echo "MERGED-CLEAN"; else
  conflicts=$(git diff --name-only --diff-filter=U)
  other=$(echo "$conflicts" | grep -v -E '^docs/refactoring/OPEN-DECISIONS.md$|Localization/Languages/(en|vi)(\.notes)?\.json$|^$')
  if [ -n "$other" ]; then echo "NEEDS-HUMAN: $other"; exit 2; fi
  for f in $conflicts; do
    case "$f" in
      docs/refactoring/OPEN-DECISIONS.md) git checkout --theirs "$f" ;;
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
  dups=$(grep -h '^order:' docs/refactoring/decisions/*.md | sort | uniq -d | tr '\n' ' ')
  if [ -n "$dups" ]; then echo "DUPLICATE-ORDER: $dups"; exit 3; fi
  git add -A -- . ':!native'
  git commit -q -m "Merge master; regenerate OPEN-DECISIONS.md

Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>"
  echo "MERGED-RESOLVED"
fi
git push origin HEAD 2>&1 | tail -1
