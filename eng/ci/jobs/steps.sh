# Shared by the job scripts in this directory. A job script defines one function per step,
# lists them in `steps`, and ends with `run_steps "$@"`: without arguments every step runs
# in order and the first failure stops the job; with step names only those run. The CI
# workflow calls one step per named GitHub step; runic-ci (.runic/ci.nix) runs whole jobs.
set -euo pipefail
cd "$(dirname "${BASH_SOURCE[0]}")/../../.."

run_steps() {
  if [[ ${1:-} == --list ]]; then printf '%s\n' "${steps[@]}"; return; fi
  local selected=("$@") step
  [[ ${#selected[@]} -gt 0 ]] || selected=("${steps[@]}")
  for step in "${selected[@]}"; do
    if [[ ! " ${steps[*]} " == *" $step "* ]]; then echo "unknown step $step; steps: ${steps[*]}" >&2; return 2; fi
    echo "::group::$step"
    SECONDS=0
    "step_${step//-/_}"
    echo "::endgroup::"
    echo "step $step passed in ${SECONDS}s"
  done
}
