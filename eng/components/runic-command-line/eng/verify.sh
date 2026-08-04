#!/usr/bin/env bash
set -euo pipefail

repository_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$repository_root"

solution="RunicCommandLine.slnx"
configuration="Release"

dotnet restore "$solution" --locked-mode
dotnet build "$solution" --configuration "$configuration" --no-restore

test_projects=(
  "tests/WebUIToolkit.CommandLine.Contracts.Tests/WebUIToolkit.CommandLine.Contracts.Tests.csproj"
  "tests/WebUIToolkit.CommandLine.Tests/WebUIToolkit.CommandLine.Tests.csproj"
  "tests/WebUIToolkit.CommandLine.Hosting.Tests/WebUIToolkit.CommandLine.Hosting.Tests.csproj"
  "tests/WebUIToolkit.CommandLine.Processes.Tests/WebUIToolkit.CommandLine.Processes.Tests.csproj"
)

for project in "${test_projects[@]}"; do
  dotnet run --project "$project" --configuration "$configuration" --no-build
done

pwsh -NoProfile -File tests/WebUIToolkit.CommandLine.AotSmoke/Invoke-AotSmoke.ps1 \
  -Configuration "$configuration"

pwsh -NoProfile -File tests/WebUIToolkit.CommandLine.PackageConsumer/Invoke-PackageConsumer.ps1 \
  -Configuration "$configuration" \
  -PackageVersion 0.1.0-preview.local
