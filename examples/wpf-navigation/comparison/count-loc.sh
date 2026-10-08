#!/usr/bin/env bash
# Counts navigation-scenario lines in one comparison project.
# Rules (W240-007):
#  - Files: *.cs and *.xaml under the project, excluding bin/, obj/, the shared domain (../shared, or Domain/
#    in the Runic app) and, for the Runic app, its tests (Tests/) and this comparison folder.
#  - A line counts if it is not blank, not a comment-only line (//, ///, <!-- -->), not a `using` directive,
#    not a `namespace` line, and not an xmlns-only XAML attribute line.
#  - A line with a trailing tag [S1]..[S4] counts for that scenario. Otherwise the folder decides:
#    S1/, S2/, S3/ -> that scenario (the Runic app names them Notes/, Dialogs/, Settings/); App.* and Shell/
#    -> Setup. S4 exists only as tags.
# Usage: count-loc.sh <project dir>, e.g. count-loc.sh prism or count-loc.sh ../NotesNavigation
set -euo pipefail
project=${1:?project dir}
cd "$project"
find . -type f \( -name '*.cs' -o -name '*.xaml' \) -not -path './bin/*' -not -path './obj/*' \
  -not -path './Domain/*' -not -path './Tests/*' -not -path './comparison/*' | sort | while read -r f; do
  case "$f" in
    ./S1/* | ./Notes/*) default=S1 ;;
    ./S2/* | ./Dialogs/*) default=S2 ;;
    ./S3/* | ./Settings/*) default=S3 ;;
    *) default=Setup ;;
  esac
  case "$f" in *.xaml) lang=XAML ;; *) lang=CS ;; esac
  awk -v def="$default" -v lang="$lang" '
    { line=$0; sub(/^[ \t]+/, "", line); sub(/[ \t\r]+$/, "", line) }
    line == "" { next }
    line ~ /^\/\// { next }
    line ~ /^<!--.*-->$/ { next }
    line ~ /^using [A-Za-z_.]+;$/ { next }
    line ~ /^namespace / { next }
    line ~ /^xmlns(:[A-Za-z]+)?="[^"]*"$/ { next }
    {
      s = def
      if (match(line, /\[S[1-4]\]/)) s = substr(line, RSTART + 1, 2)
      print s "\t" lang
    }' "$f"
done | sort | uniq -c | awk '{ n[$2] += $1; l[$2 "\t" $3] = $1 } END {
  split("Setup S1 S2 S3 S4", order, " ")
  printf "%-6s %5s %5s %5s\n", "part", "C#", "XAML", "total"
  for (i = 1; i <= 5; i++) {
    k = order[i]; printf "%-6s %5d %5d %5d\n", k, l[k "\tCS"], l[k "\tXAML"], n[k]
    cs += l[k "\tCS"]; xaml += l[k "\tXAML"]
  }
  printf "%-6s %5d %5d %5d\n", "total", cs, xaml, cs + xaml
}'
