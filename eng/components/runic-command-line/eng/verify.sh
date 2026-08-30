#!/usr/bin/env bash
set -euo pipefail

repository_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$repository_root"

solution="Runic.CommandLine.slnx"
configuration="Release"

./eng/verify-identities.sh

dotnet restore "$solution"
dotnet build "$solution" --configuration "$configuration" --no-restore

test_projects=(
  "tests/Runic.CommandLine.Contracts.Tests/Runic.CommandLine.Contracts.Tests.csproj"
  "tests/Runic.CommandLine.Tests/Runic.CommandLine.Tests.csproj"
  "tests/Runic.CommandLine.Hosting.Tests/Runic.CommandLine.Hosting.Tests.csproj"
  "tests/Runic.CommandLine.Processes.Tests/Runic.CommandLine.Processes.Tests.csproj"
)

for project in "${test_projects[@]}"; do
  dotnet run --project "$project" --configuration "$configuration" --no-build
done

pwsh -NoProfile -File tests/Runic.CommandLine.AotSmoke/Invoke-AotSmoke.ps1 \
  -Configuration "$configuration"

pwsh -NoProfile -File tests/Runic.CommandLine.PackageConsumer/Invoke-PackageConsumer.ps1 \
  -Configuration "$configuration" \
  -PackageVersion 0.2.0-preview.local
