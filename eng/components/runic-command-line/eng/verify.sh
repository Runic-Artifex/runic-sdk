#!/usr/bin/env bash
set -euo pipefail

repository_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$repository_root"

solution="RunicCommandLine.slnx"
configuration="Release"

./eng/verify-identities.sh

dotnet restore "$solution" --locked-mode
dotnet build "$solution" --configuration "$configuration" --no-restore

test_projects=(
  "tests/RunicCommandLine.Contracts.Tests/RunicCommandLine.Contracts.Tests.csproj"
  "tests/RunicCommandLine.Tests/RunicCommandLine.Tests.csproj"
  "tests/RunicCommandLine.Hosting.Tests/RunicCommandLine.Hosting.Tests.csproj"
  "tests/RunicCommandLine.Processes.Tests/RunicCommandLine.Processes.Tests.csproj"
)

for project in "${test_projects[@]}"; do
  dotnet run --project "$project" --configuration "$configuration" --no-build
done

pwsh -NoProfile -File tests/RunicCommandLine.AotSmoke/Invoke-AotSmoke.ps1 \
  -Configuration "$configuration"

pwsh -NoProfile -File tests/RunicCommandLine.PackageConsumer/Invoke-PackageConsumer.ps1 \
  -Configuration "$configuration" \
  -PackageVersion 0.1.0-preview.local
