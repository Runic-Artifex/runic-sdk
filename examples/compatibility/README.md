# Runic Toolkit Examples

This repository preserves the filtered history of the examples extracted from
WebUIToolkit and now consumes the independently released Runic Artifex packages.
Every sample is standalone from the product source repositories: .NET projects
restore exact NuGet versions, browser projects restore exact npm versions, and
the package-only canaries under [`integrations/`](integrations/) guard each
product boundary.

## Package authentication

Runic packages are private during the migration. Local restores need a GitHub
personal access token with `read:packages` exposed to NuGet without committing it:

```bash
export NuGetPackageSourceCredentials_github="Username=YOUR_GITHUB_USER;Password=YOUR_TOKEN;ValidAuthenticationTypes=Basic"
dotnet restore integrations/RunicTextResources.Canary/RunicTextResources.Canary.csproj
dotnet restore integrations/RunicCommandLine.Canary/RunicCommandLine.Canary.csproj
dotnet restore integrations/RunicFlow.Canary/RunicFlow.Canary.csproj
dotnet restore integrations/RunicAssets.Canary/RunicAssets.Canary.csproj
```

The frontend packages use GitHub's npm registry through the committed `.npmrc`:

```bash
export NODE_AUTH_TOKEN="YOUR_TOKEN"
npm ci
```

After both package managers are authenticated, the full package-only check is:

```bash
dotnet restore samples/Todo.React/Todo.React.csproj
npm run verify
dotnet build samples/Todo.React/Todo.React.csproj --configuration Release
```

GitHub Actions uses its repository `GITHUB_TOKEN` with `packages: read`. Each
private NuGet or npm package must grant this repository Actions access before its
workflow can restore it.

## License

Runic Toolkit Examples is licensed under the [MIT License](LICENSE). Third-party
components retain their own licenses and attribution terms.
