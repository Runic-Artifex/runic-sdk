# Quality gates

The required pull-request gate is `./eng/verify.sh`.

The prerelease and public-release workflows invoke the same
`eng/verify-release-candidate.sh` command available locally. It:

1. packs and validates seven MIT-licensed NuGet artifacts with repository
   commit provenance;
2. keeps exact cross-repository NuGet dependencies in a separate verification
   feed and runs isolated package plus NativeAOT consumers;
3. packs and validates the Application Bridge and Angular npm artifacts;
4. packs exact Desktop, Svelte, and Vite candidates from detached compatibility
   revisions and builds every generated template through a cold local registry;
5. applies the exact compatibility-authority public metadata contract when the
   public target is selected; and
6. leaves only the canonical seven NuGet and two npm artifacts for digesting and
   optional publication.

NativeAOT smoke projects remain registered in `eng/solution-exclusions.txt` and
can be executed with `eng/verify-native-aot.ps1` in an environment containing
the appropriate runtime packs. The real CS-WebUI browser canary is run from the
pinned Nix environment when native/browser behavior changes.

No product repository commits `packages.lock.json` or a custom package hash
replay catalog. Application/example repositories may keep lock files because
they own a resolved dependency graph.
