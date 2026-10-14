# CLI migration and tool usefulness audit

**Date:** 2026-10-14. **Disposition:** all 20 tool names retained.

Facepunch keeps MCP and custom `[McpTool]` discovery through `sbox-cli`; fixed-port
HTTP was replaced by per-instance named pipes and a stdio bridge. The sample configs
use `Scripts/Connect-Sbox.ps1`, which resolves the installed CLI and only attaches
with `--connect`. It never launches or owns an editor.

## Evidence and retirement policy

Read-only stock source baseline: `5e1cdc35291c38030bd6ff8443a6fe41063077b8`.
Inspected `engine/Sandbox.Tools/Mcp/{TopLevelTools,TypeForwards}.cs`,
`engine/Sandbox.Engine/Systems/Mcp/ToolRegistry.cs`, and
`game/addons/tools/Code/Mcp/{SceneFiles,Scene,Network,AssetSystem}.cs`.
The installed `sbox-cli --skill sbox-cli` and `references/authoring-tools.md`
confirm addon discovery; type forwarding retains the `Editor.Mcp` namespace.

Source overlap is not native verification. Retirement requires a recorded installed
build, loaded addon, clean compilation, matching behavior/guard failures and the
formerly broken scenario. Generic `run_csharp` does not replace ownership, bounded
input, undo or evidence contracts. Preserve names until clients have a migration.

## Complete inventory

| Tool | Stock overlap / distinct feature | Disposition and native replacement check |
|---|---|---|
| `x_open_scene` | Stock `open_scene`/`switch_scene` cover saved/open tabs. Extra accepts prefab paths and rejects Play/game sessions. | Retain compatibility candidate: two saved tabs, dirty/background tab, prefab/never-saved tab, missing path, Play refusal and save/reopen preservation. |
| `x_editor_status` | Stock now reads `SceneEditorSession.Active`; extra also reports open tab count. | Retain compatibility candidate: compare names/paths/dirty state with `list_scenes` in edit, tab switch, Play/pause/stop and no session. |
| `x_camera_screenshot` | Stock uses `RenderToBitmap`; extra captures live-size UI in Play then resizes. | Retain workaround: inspect actual Razor labels at live/smaller/larger sizes in edit and Play, including aspect ratio. PNG success alone is insufficient. |
| `x_create_scene` | Exclusive handle reservation, identity/hash handoff and retained fault evidence beyond generic asset creation. | Keep guarded diagnostic-scene creation; verify existing-file refusal and native registration failure retention. |
| `x_set_network_mode` | Inspected stock `set_game_object` lacks a saved network-mode parameter. | Keep: verify Never/Object/Snapshot persistence, one undo step and background/Play refusal. |
| `x_camera_look_at` | Stock transform tools take Euler angles, not guarded world-point aiming. | Keep: verify direction, undo, coincident/zero-up rejection and active-scene restriction. |
| `x_network_status` | Stock exposes connection IDs; extra returns bounded/redacted observations. | Keep privacy contract; compare host/client state without recording communication. |
| `x_network_start_hosting` | Stock uses the supported editor hosting path too. | Retain overlap candidate until connected/refusal/result behavior is compared. |
| `x_network_disconnect` | Stock disconnects too; extra preserves owned children and its guards. | Retain overlap candidate; verify disconnected/connected behavior and surviving clients. |
| `x_network_spawn_instance` | Extra owns a private Job Object plus exact process creation identity; stock returns PID/log root. | Keep: verify host guard, joining, ownership token and cleanup separately from account acceptance. |
| `x_network_instances` | Ownership-scoped inventory not found in stock. | Keep; verify only owned rows and hotload lifetime limits. |
| `x_network_terminate_instance` | Job Object/exact `LaunchedAt` termination not found in stock. | Keep; verify wrong-token/non-owned rejection and graceful/abrupt owned-tree cleanup. |
| `x_network_migrate_to_new_instance` | Stock has migration; extra refuses before launch based on historical build 26.09.08e constraints. | Keep refusal until actual successor/join/handoff and supported targeted ownership are demonstrated. A tool name proves no feasibility gate. |
| `x_model_skeleton` | Structured bones/attachments/material groups/animation evidence beyond stock metadata. | Keep; verify rigged/static models and unavailable-field reporting. |
| `x_import_model_source` | External-source reservation, texture allowlist, serialized lifetime and retained-stage evidence. | Keep; contract fixtures plus native owned-destination import and overwrite/fault refusal checks. |
| `x_input_observe` | Read-only focus/suppression/action/look/camera snapshot. | Keep; compare focused/unfocused native Play observations. |
| `x_input_trace_start` | Explicit scene identity and bounded time/frame recorder. | Keep; verify native sampling and context termination. |
| `x_input_trace_status` | Recorder state/token and retained terminal evidence. | Keep; verify terminal bounds/retention. |
| `x_input_trace_read` | Exact-token checked pagination. | Keep; verify wrong tokens and page limits. |
| `x_input_trace_stop` | Idempotent recorder-only stop. | Keep; verify it never stops Play. |

Findings cover the inspected stock surface, not every engine method/future release.
No tool is removed or silently redirected. Runtime checks do not certify physical
input devices, native account services or release acceptance.

## Checks

Run `Tests/Compare-Embedded.ps1 -InventoryOnly` for 20 unique documented tools and
`Tests/ModelImport.ContractTests.csproj` for independent ownership/import/trace
fixtures. Legacy comparison/self-test modes remain for older vendored users.
Symlink fixture failures remain environment blockers, never passed assertions.

`Tests/Test-CliTransport.py` is an opt-in native smoke through the stdio launcher:
handshake, project/build/compile status, extras discovery and `x_editor_status`.
It does not change scenes or Play. Run from the intended project:

```powershell
python Tests/Test-CliTransport.py --engine <engine-directory>
# Only for an isolated project where a CLI-owned editor is intended:
python Tests/Test-CliTransport.py --engine <engine-directory> --launch
```

The smoke does not pass the retirement scenarios above. The recorded transport smoke results follow.

On installed build **26.10.13**, the temporary minimal-game smoke passed the CLI
handshake, PowerShell byte-stream relay, idle successful compilation with zero errors,
all **20/20** extras and `x_editor_status`, followed by protocol-clean EOF and normal
launcher/owner exits. Missing-engine and unavailable-project checks also passed:
exit 1, diagnostics on stderr, empty stdout. Stock and extra status agreed on the clean
`minimal` scene. This verifies compatibility, not dirty-tab/Play/screenshot parity.
The earlier inheritance-only wrapper timed out; the explicit byte relay supersedes it.
`python Tests/test_cli_transport.py` protects against accepting a previous successful
compile while a new compilation remains active past the deadline.

### Verification after removing library ProjectSettings

On **2026-10-14**, cleanup commit `d3f624f` was verified on installed engine build
**26.10.13** using a temporary `game.minimal` project. Its library copy matched
all 11 source/project files in the checkout (10 editor C# files and the `.sbproj`).
The library's `ProjectSettings/` folder was absent before and after native loading;
the temporary host game retained its own project settings.

`Tests/Test-CliTransport.py --engine <engine-directory> --project <temporary-project> --launch`
passed the MCP handshake, PowerShell relay, settled successful compilation with
**zero compile errors**, discovery of all **20/20** expected `x_` tools, and an
`x_editor_status` call. Stock and extra status agreed on the clean `minimal` scene,
with Play and pause both off. Shutdown completed with protocol-clean EOF, and the
test-owned editor exited. The repository remained clean.

An initial isolated launch timed out before exposing its MCP endpoint; the retry
above completed successfully. A separate smoke against an existing game's library
copy also passed, but that copy still contained settings and was not used as evidence
for this removal. This check verifies loading and discovery without library settings;
it does not exercise every tool or establish the retirement scenarios above.
