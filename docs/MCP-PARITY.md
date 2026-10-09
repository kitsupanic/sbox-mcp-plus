# Generic MCP parity and ownership

Comparison baseline: standalone `e046091`; Kampai `55b3d60` (2026-10-09).
The game embeds this library in `Libraries/kitsupanic.sbox_mcp_plus` (package marker
`1.0.381524`). The installed editor supplies the MCP HTTP server and discovers the
library's `extras` tools; `Editor/Playtesting` is a separate game-owned tool surface.
Neither repository contains repository-local `.codex` memories at this baseline.
The game instructions and offline-playtesting/parallel-workspace skills were read;
no memories, OMP configuration, package versions or MCP configuration were edited.

## Reconciled reusable work

| Area | Initial difference | Current ownership |
|---|---|---|
| `McpExtras.cs`, `OwnedAssetFile.cs` | Game-only exclusive diagnostic-scene reservation, ancestry locks, identity/length/SHA-256 handoff verification; retained committed evidence on engine failure | Generic, synchronized |
| `ModelImportTransaction.cs` | Game extracted common handle ownership/filename validation into `OwnedAssetFile`; import keeps its flush/close handoff | Generic, synchronized; no claim that import now uses scene handoff hashing |
| `Tests/Program.cs` | Game had 12 additional scene ownership/race regressions | Generic, synchronized; six bounded trace regressions added |
| Other original C# files | Import wrappers, skeleton, backend, networking and process ownership were already identical | Generic, synchronized without behavioral rewrite |
| `McpExtras.Input.cs`, `InputTraceBuffer.cs` | Focus/input/camera observation concepts existed only inside game diagnostics | Generic raw observation extracted; bounded editor-frame recorder added |
| Shared docs and harness | Scene documentation and tests had drifted; game omitted portable harness project | Synchronized; hand-maintained harness explicitly unignored locally |

Standalone owns future generic fixes. Port a reviewed generic fix to the vendored
copy in the same work item; run `Tests/Compare-Embedded.ps1` before handoff. The check
compares all direct `Editor/*.cs`, test `.cs`/`.ps1`/`.csproj`, test ignore rules, usage
docs and this document; only CRLF/LF differences are normalized. Added/missing shared
files fail, duplicate or undocumented tool names fail, and game dependencies fail.
`-SelfTest` exercises equal, line-ending, modified, missing and additional source
fixtures. It never synchronizes or overwrites files for the operator.

## Deliberate differences and exclusions

- Engine-generated editor projects retain machine-specific references/output paths;
  never copy one repository's generated project into the other. Package marker,
  sbproj metadata, MCP configs, art and project settings are outside parity scope.
- Kampai `PlaytestTools`, `NativePlaytestTools`, `WorldInputPlaytestTools` and their
  supervisors drive its tutorial, bot sessions, settings/HUD, glasses and Steal.
  They remain game-owned, including session/host/token/lease and resource guards.
- Kampai `NativeInputObservation`, `NativeInputTrace` and validation entry points
  retain HUD ordering, consumed-look hooks, game state, eligibility and adapter
  provenance. The generic tools observe engine values only; they cannot replace
  these integration assertions or certify a physical mouse/controller.
- The synthetic `WorldPlaytestInput` adapter modifies production consumers under a
  game-specific lease and disables Career. It is not a generic input tool and is
  deliberately not imported. Neither are accelerated clocks or direct gameplay intents.
- Settings persistence fixtures and standalone export/startup/profile tools rely on
  game-specific services and paths. The known crashing export is not launched.
- The process worker guard has related ownership principles, but is not a drop-in
  replacement for the library's already-identical `OwnedLocalInstance` implementation.
  No process/network launch or termination behavior was changed.

## Evidence limits

The source/contract harness is independent of the editor. Asset pipeline faults use
fake backends; Windows ownership tests operate on temporary fixture files. Buffer
fixtures test bounds, exact tokens, pagination, terminal retention and context/time
transitions, not sampling order inside the engine. Generic raw traces record once per
editor frame, not an atomic game/HUD frame. Scene identity is observed each callback;
this does not constitute game session ownership or detect unseen stop/restart cycles.

Use the installed editor MCP inventory and read-only project/scene/Play/compile
preflight before runtime checks. Preserve user Play and unsaved scenes. If tools are
unloaded or MCP is unavailable, report runtime verification as unperformed. No runtime
reload, Play start/stop, scene creation, import or physical-input run is implicit in
running the parity or contract checks.
