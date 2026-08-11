# Quality gates

The required pull-request gate is `./eng/verify.sh`.

The prerelease package workflow additionally:

1. packs and validates 18 MIT-licensed NuGet packages with repository commit
   provenance;
2. restores and runs an isolated package consumer;
3. installs and invokes both dotnet tools;
4. packs and validates six version-matched npm packages; and
5. uploads digest-protected workflow artifacts before any optional publish job.

NativeAOT smoke projects remain registered in `eng/solution-exclusions.txt` and
can be executed with `eng/verify-native-aot.ps1` in an environment containing
the appropriate runtime packs. The real CS-WebUI browser canary is run from the
pinned Nix environment when native/browser behavior changes.

No product repository commits `packages.lock.json` or a custom package hash
replay catalog. Application/example repositories may keep lock files because
they own a resolved dependency graph.
