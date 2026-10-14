# Development and checks

This repository is the shared generic library. Make reusable tool changes here;
keep game-specific playtesting, HUD hooks, and gameplay assertions in the game.
For the existing consumer checkout and submodule workflow, see
[ownership and maintenance](MCP-PARITY.md).

## Local checks

Run these commands from this repository's root:

```powershell
pwsh -File Tests/Compare-Embedded.ps1 -InventoryOnly
dotnet run --project Tests/ModelImport.ContractTests.csproj
# Focused buffer tests, no engine or Play needed:
dotnet run --project Tests/ModelImport.ContractTests.csproj -- --input-trace-only
```

The inventory check verifies unique `x_` tool names and documentation coverage.
The .NET 10 harness uses real Windows file handles/races and fake asset backends.
Three symlink fixtures require existing symlink privilege; failure to create their
links is an environment blocker, never a passed rejection assertion. Do not alter
permissions automatically.

The comparison script also retains legacy vendored-checkout modes:

```powershell
pwsh -File Tests/Compare-Embedded.ps1 -SelfTest
pwsh -File Tests/Compare-Embedded.ps1 -EmbeddedRoot <vendored-checkout>
```

## Engine verification

Engine compilation, MCP discovery, actual tool behavior, physical devices, and
release acceptance require separate evidence. Contract tests alone do not verify
these behaviors.

The [CLI/tool audit](CLI-AUDIT.md) records stock-tool overlap, the opt-in native
transport smoke, and the checks required before retiring compatibility tools.
Use the installed engine's instructions with `sbox-cli --skill sbox-cli`.
Custom tools remain in the `extras` toolset with the `x_` prefix.

## Documentation

Keep the [README](../README.md) tool overview complete when adding tools.
Put detailed behavior and limits in the [tool reference](TOOL-REFERENCE.md);
model import contracts remain in the [model reference](../README-model-import.md).
