# Runic Toolkit Examples

This repository preserves the filtered history of the examples extracted from
WebUIToolkit. The examples are being migrated incrementally to released packages
owned by the Runic Artifex organization.

Most historical samples still reference Toolkit projects that have not been
published independently. They remain useful migration material, but are not yet a
standalone build. Package-only integration canaries under [`integrations/`](integrations/)
are the executable proof points during that transition.

## Package authentication

Runic packages are private during the migration. Local restores need a GitHub
personal access token with `read:packages` exposed to NuGet without committing it:

```bash
export NuGetPackageSourceCredentials_github="Username=YOUR_GITHUB_USER;Password=YOUR_TOKEN;ValidAuthenticationTypes=Basic"
dotnet restore integrations/RunicTextResources.Canary/RunicTextResources.Canary.csproj --locked-mode
dotnet restore integrations/RunicCommandLine.Canary/RunicCommandLine.Canary.csproj --locked-mode
dotnet restore integrations/RunicFlow.Canary/RunicFlow.Canary.csproj --locked-mode
```

GitHub Actions uses its repository `GITHUB_TOKEN` with `packages: read`. Each
private package must grant this repository Actions access before the canary job can
restore it.

## License

Runic Toolkit Examples is licensed under the [MIT License](LICENSE). Third-party
components retain their own licenses and attribution terms.
