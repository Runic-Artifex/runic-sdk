#!/usr/bin/env python3
"""The macOS job cap (D077.26(1)) and the runner-wait alarm (D077.25(4)), for the coordinator and train-feed-exit.yml.

Prints, across the four main repositories:
  - the macOS jobs queued or running now, and the runs they belong to (the cap: at most 5 macOS jobs at once,
    D077.26(1), which amends D052's 3 runs; GitHub runs at most 5 macOS jobs at once for the organization);
  - each runner type's queue wait (job created -> started) over the last WINDOW hours, and
    whether the alarm fires (macOS p90 > 10 min, Linux or Windows p90 > 5 min).
Exit code 0: room to open a PR (and a PR with N macOS jobs if the count plus N stays within the cap); 1: hold new PRs.

  python3 eng/ci/runner-wait.py [--hours 2] [--macos-jobs N] [--cap-only] [--wait-minutes M [--interval S]]
--macos-jobs: the macOS jobs the new PR's plan has (`node eng/ci/plan.mjs --files ...` in runic-sdk: Native /
osx-arm64 and Packaging / macos count one each per shard); exit 1 when they would not fit.
--cap-only: check only the cap, not the alarm (train-feed-exit.yml, before its pull request opens).
--wait-minutes: with --macos-jobs, poll every --interval seconds (default 120) until they fit, for up to M minutes.
A run's macOS jobs appear only once their `needs` are met, so a run still in its plan or build phase is not counted
yet: when one was just opened, leave room for it.
Uses `gh api` (the user's GitHub login, or GH_TOKEN in a workflow); read-only. A failed request is tried again twice,
10 seconds apart; when all three fail, the script exits with code 2 (not 1, which means "hold new PRs").
"""
import argparse, datetime as dt, json, os, subprocess, sys, time

ORG = "Runic-Artifex"
REPOS = ["runic-sdk", "runic-translations-sdk", "runic-cli-sdk", "runic-editors"]
MACOS_JOB_CAP = 5
ALARM = {"macos": 10.0, "linux": 5.0, "windows": 5.0}
GH_ATTEMPTS = 3
# The pause between attempts; the tests set it to 0.
GH_PAUSE = float(os.environ.get("RUNNER_WAIT_GH_PAUSE", "10"))


def gh(path):
    # GitHub's API fails a request now and then (a 502, a timeout). One of them must not end train-feed-exit.yml's
    # hour of waiting for room under the cap, or the coordinator's check with a traceback.
    for attempt in range(1, GH_ATTEMPTS + 1):
        result = subprocess.run(["gh", "api", path], capture_output=True, text=True)
        if result.returncode == 0:
            return json.loads(result.stdout)
        reason = (result.stderr.strip().splitlines() or [f"exit code {result.returncode}"])[-1]
        if attempt == GH_ATTEMPTS:
            print(f"gh api {path} failed {GH_ATTEMPTS} times ({reason}); the runner check could not run.", file=sys.stderr)
            sys.exit(2)
        print(f"gh api {path} failed ({reason}); trying again in {GH_PAUSE:g} s.", file=sys.stderr)
        time.sleep(GH_PAUSE)


def os_of(labels):
    text = ",".join(labels).lower()
    return "macos" if "macos" in text else "windows" if "windows" in text else "linux"


def parse(ts):
    return dt.datetime.fromisoformat(ts.replace("Z", "+00:00"))


def jobs(repo, run_id):
    return gh(f"repos/{ORG}/{repo}/actions/runs/{run_id}/jobs?per_page=100").get("jobs", [])


def active_macos_jobs():
    macos_jobs = {}
    for repo in REPOS:
        active = []
        for status in ("queued", "in_progress"):
            active += gh(f"repos/{ORG}/{repo}/actions/runs?status={status}&per_page=100").get("workflow_runs", [])
        for run in {run["id"]: run for run in active}.values():
            count = sum(1 for job in jobs(repo, run["id"])
                        if os_of(job.get("labels", [])) == "macos" and job.get("status") in ("queued", "in_progress"))
            if count:
                key = f"{repo}:{run.get('head_branch')}"
                macos_jobs[key] = macos_jobs.get(key, 0) + count
    return macos_jobs


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--hours", type=float, default=2.0)
    parser.add_argument("--macos-jobs", type=int, default=0,
                        help="macOS jobs the PR to be opened would add; exit 1 if they would exceed the cap")
    parser.add_argument("--cap-only", action="store_true", help="check only the macOS cap, not the runner-wait alarm")
    parser.add_argument("--wait-minutes", type=float, default=0,
                        help="with --macos-jobs: poll until they fit under the cap, for up to this many minutes")
    parser.add_argument("--interval", type=float, default=120.0, help="seconds between polls with --wait-minutes")
    args = parser.parse_args()
    since = dt.datetime.now(dt.timezone.utc) - dt.timedelta(hours=args.hours)

    deadline = time.monotonic() + args.wait_minutes * 60
    while True:
        macos_jobs = active_macos_jobs()
        used = sum(macos_jobs.values())
        fits = not args.macos_jobs or used + args.macos_jobs <= MACOS_JOB_CAP
        if fits or time.monotonic() + args.interval > deadline:
            break
        print(f"macOS jobs queued or running: {used} of {MACOS_JOB_CAP}; {args.macos_jobs} more would not fit. "
              f"Asking again in {args.interval:g} s.", flush=True)
        time.sleep(args.interval)

    waits = {}
    if not args.cap_only:
        for repo in REPOS:
            created = since.strftime("%Y-%m-%dT%H:%M:%SZ")
            for run in gh(f"repos/{ORG}/{repo}/actions/runs?created=>{created}&per_page=100").get("workflow_runs", []):
                for job in jobs(repo, run["id"]):
                    if not job.get("started_at") or job.get("conclusion") == "skipped":
                        continue
                    wait = (parse(job["started_at"]) - parse(job["created_at"])).total_seconds() / 60
                    if wait >= 0:
                        waits.setdefault(os_of(job.get("labels", [])), []).append(wait)

    alarm = False
    runs = ", ".join(f"{key} ({count})" for key, count in sorted(macos_jobs.items())) or "none"
    print(f"macOS jobs queued or running: {used} of {MACOS_JOB_CAP} ({runs})")
    for kind in () if args.cap_only else ("linux", "windows", "macos"):
        values = sorted(waits.get(kind, []))
        if not values:
            print(f"{kind:8} no jobs in the last {args.hours:g} h")
            continue
        p90 = values[max(0, int(len(values) * 0.9) - 1)]
        fires = p90 > ALARM[kind]
        alarm |= fires
        print(f"{kind:8} jobs={len(values):4} p50={values[len(values) // 2]:.1f} min p90={p90:.1f} min"
              f" (alarm above {ALARM[kind]:g}){'  ALARM' if fires else ''}")
    if alarm:
        print("Hold new PRs until the waits drop (D077.25(4)).")
        return 1
    if args.macos_jobs and used + args.macos_jobs > MACOS_JOB_CAP:
        waited = f" after waiting {args.wait_minutes:g} min" if args.wait_minutes else ""
        print(f"macOS cap: {used} + {args.macos_jobs} would exceed {MACOS_JOB_CAP}{waited}; wait before opening this PR.")
        return 1
    if used >= MACOS_JOB_CAP:
        print("macOS cap reached: wait before opening a PR whose plan has a macOS job.")
    else:
        print(f"Room for {MACOS_JOB_CAP - used} more macOS job(s).")
    return 0


if __name__ == "__main__":
    sys.exit(main())
