<p align="center">
  <img src="img/logo.png" alt="sbox-mcp-plus" width="200" />
</p>

# sbox-mcp-plus

Extra tools for the s&box editor, discovered from library code through `sbox-cli`.
Facepunch replaced fixed-port HTTP with per-instance named pipes and a stdio MCP bridge;
the custom `[McpTool]` extension surface remains supported. This library adds guarded
operations and diagnostics beyond the stock tools. See the [CLI/tool audit](docs/CLI-AUDIT.md)
for overlap, evidence and the checks required before retiring a workaround.

All tool names are prefixed `x_` so they can never collide with the engine's own once
the upstream equivalents land.

## Install

Add the library as a Git submodule beneath the game project's `Libraries/` folder:

```powershell
git submodule add https://github.com/kitsupanic/sbox-mcp-plus.git Libraries/kitsupanic.sbox_mcp_plus
git submodule update --init --recursive
```

Libraries are scanned at project load — restart the editor after first install;
after that, edits to the code hotload. Package installation remains an alternative.

## Connect through the CLI

Run clients from the **game project folder** so the CLI selects that project's editor.
The sample `mcp.json` uses PowerShell 7 and `Scripts/Connect-Sbox.ps1`; when installing
it in a game, prefix the script path with `Libraries/kitsupanic.sbox_mcp_plus/`.
The launcher finds the engine through process/user `FACEPUNCH_ENGINE`, then the default
Steam location. It inherits the MCP streams and forwards the CLI exit code. It only
uses `--connect`: an unavailable editor fails without opening or owning a new one.
No fixed port or HTTP URL is used. Enable MCP communication in the project's AI settings.

Clients can also launch `<engine>/bin/managed/sbox-cli.exe` directly with `--connect`.
Use `sbox-cli --skill sbox-cli` for the installed build's instructions. One-shot examples:

```powershell
$cli = Join-Path ([Environment]::GetEnvironmentVariable('FACEPUNCH_ENGINE', 'User')) 'bin/managed/sbox-cli.exe'
& $cli --list
& $cli --call editor_status
& $cli --call search_tools '{"query":"x_"}'
& $cli --call x_editor_status
```

Find the tools from any MCP client via `list_toolsets` (toolset `extras`) or
`search_tools`.

## Tools

### `x_open_scene`

Make a scene the active editor tab, opening it from its asset path if it isn't open yet.
Accepts a scene name or resource path as `list_scenes` reports them.

Original reason: the old stock toolset could read and list background scenes but couldn't switch
which one is active, and every mutating tool (`set_component`, `save_scene`, `undo`)
targets the active scene. Worse, an edit aimed at a background scene mutates the object
but the dirty flag lands on the active tab — the background scene reloads from disk on
focus and the edit is silently lost. Upstream: issue
[#11637](https://github.com/Facepunch/sbox-public/issues/11637), PR
[#11638](https://github.com/Facepunch/sbox-public/pull/11638).
Current stock source supplies `open_scene` and `switch_scene`; this compatibility tool
also handles prefab paths and retains its Play/session guards. Keep it until the
[native replacement checks](docs/CLI-AUDIT.md) pass on the installed build.

- Explicit no-op message when the scene is already the active tab.
- Refuses during play mode — `play_stop` first.
- Refuses the running game session (it has no tab to switch to).

### `x_create_scene`

Create, save, and activate a new empty scene at a project-relative `.scene` path beneath
`scenes/diagnostics/`. The optional `name` defaults to the destination file name.

- Refuses play mode, unsafe or linked paths, paths outside the diagnostics folder,
  wrong extensions, and any destination another actor already created.
- Reserves the source with an owned `CREATE_NEW` handle and never adopts, truncates,
  overwrites, or path-deletes another actor's file.
- Before engine registration, verifies that the required handle-sharing transition retained the
  same file identity, length, and SHA-256 bytes. An in-flight writer or a completed mutation or
  replacement fails creation before engine exposure.
- Removes only its handle-owned partial source when failure occurs before durable commit. Once
  committed, registration, compilation, opening, or handoff-verification failures retain evidence
  for diagnosis and report the exact source path.
- Leaves every existing tab—including unsaved work—unchanged.


### `x_editor_status`

What the editor is doing right now, from the editor's point of view: active scene tab
name and path, unsaved-changes flag, open tab count, project ident, play/pause state.

Original reason: the old built-in `editor_status` reported the game scene handle, not the
active editor tab — while editing it returns a placeholder name (`"Scene"`) and a null
path, and its dirty flag can describe a different scene than its name. Upstream: issue
[#11639](https://github.com/Facepunch/sbox-public/issues/11639), PR
[#11640](https://github.com/Facepunch/sbox-public/pull/11640) (closed unmerged).

Current stock source now reads `SceneEditorSession.Active`; native comparisons remain
required before retiring this compatibility tool. Its open-tab count is still additional data.

`x_editor_status` reads `SceneEditorSession.Active` — the same source `list_scenes`
uses — so the two always agree.

Fields the built-in reports that this tool deliberately omits (tool count, compile
status, engine paths): they come from engine-internal API a library can't reach, and
the built-in `editor_status` still reports them correctly — use both.

### `x_set_network_mode`

Set an active-scene game object's saved networking mode to `Never`, `Object`, or
`Snapshot` by GUID. Refuses play mode and missing/background objects, records one undo
step, and returns the resulting mode.

### `x_camera_screenshot`

Render a scene camera to an image with UI text intact. Same arguments as the built-in
`camera_screenshot` — `camera` (a CameraComponent id, its game object's id, or empty for
the scene's main camera), `width`, `height`, `includeUi` — so it's a drop-in swap.

Why it exists: the built-in `camera_screenshot` renders every Razor text label as a flat
gray rectangle at any size other than the live viewport's. Upstream: issue
[#11585](https://github.com/Facepunch/sbox-public/issues/11585). Engine root cause:
`CameraComponent.ResizeUI` relayouts each screen panel for the offscreen size but omits
`RootPanel.BuildDescriptors()`, so labels resized by that relayout release their text
texture while the render descriptors still point at the dead one — `ScreenshotService`
has the correct sequence (`PreLayout`/`CalculateLayout`/`PostLayout`/`BuildDescriptors`).

In play mode `x_camera_screenshot` sidesteps it by capturing at exactly the current screen
size, where the relayout changes no panel's size and no texture is released, then
downscaling the capture to the requested dimensions. In edit mode there is no game
viewport, so no live screen-size UI exists whose text textures a relayout could destroy,
and it renders directly at the requested size — full detail, no resize step. Either way
it works wherever `camera_screenshot` does.

### `x_camera_look_at`

Aim a camera at a world-space point with the engine's `Rotation.LookAt` rather than
requiring callers to derive Euler angles. Arguments are `camera` (a CameraComponent or
camera game-object GUID), `target` (`x,y,z`), and optional `up` (`x,y,z`, default
`0,0,1`). The camera must belong to the active editor scene.

The operation rejects coincident camera/target positions and zero up vectors, records
one undo step, and returns the resulting position, target, and angles.

### Network and local instances

Seven editor-only tools expose network state and safely manage local clients:

- `x_network_status` — read the active session and privacy-limited connection rows.
- `x_network_start_hosting` — start hosting through the supported editor path.
- `x_network_disconnect` — disconnect without terminating owned clients.
- `x_network_spawn_instance` — launch a fixed local client in a private Windows Job
  Object. No arbitrary executable, environment, working directory, or arguments.
- `x_network_instances` — list only clients launched by this library during the current
  hotload lifetime.
- `x_network_terminate_instance` — terminate an owned process tree using its PID and
  exact `LaunchedAt` token. Default mode requests a graceful close before forcing the
  private job; `abrupt=true` skips directly to forced termination.
- `x_network_migrate_to_new_instance` — currently refuses before launching anything.
  Installed engine build `26.09.08e` selects migration successors through Steam lobby
  membership, which synthetic local clients cannot satisfy, and exposes no supported
  targeted-handoff API.

Ownership is in-memory and handle-based. The library starts each child suspended,
assigns it to a private Job Object, captures its exact creation identity, and only then
resumes it. It never reconstructs ownership from process enumeration or accepts a PID
alone. Hotloading or restarting the library deliberately resets ownership without
killing surviving clients; close those clients manually afterward.

Network snapshots omit Steam/account identifiers, party data, credentials, chat, and
voice. Display names are bounded, stripped of control characters, and redact common
Steam identifier formats. `LogDirectory` identifies the shared engine log root; there
is intentionally no instance-log tool because shared logs cannot be attributed safely
to one child.

## Known issues

- Workaround descriptions record the original defects. Current stock source fixes
  active-scene status and adds scene switching; installed runtime parity is tracked in
  [the audit](docs/CLI-AUDIT.md), rather than assumed from source or old issue status.
- Legacy clients configured for `http://127.0.0.1:7269/mcp` must migrate to the CLI.
- `x_open_scene` can't target a never-saved scene by path (it has no resource path
  yet) — use its name.
- `x_camera_screenshot` output detail is capped at the live viewport's resolution in play
  mode: it captures there and resizes, so a requested size larger than the viewport is
  interpolation, not extra detail. Aspect ratio still follows the requested size. Edit
  mode renders at the requested size directly and isn't capped.
- Adding this library to a project doesn't take effect in an already-running editor:
  package mounting happens at project load, only source edits hotload. Restart once.

## Input observation and bounded traces

- `x_input_observe(action="Attack1")` reads focus, suppression, mouse visibility,
  action edges, numeric AnalogLook, scene identity and camera rotation.
- `x_input_trace_start(sceneId, frameLimit=300, seconds=15, label="look-click", action="Attack1")`
  records that raw snapshot once per editor frame in an already running, explicitly
  identified Play scene. Obtain `sceneId` from `x_input_observe` after checking the
  active project with `editor_status` and `x_editor_status`.
- `x_input_trace_status()` reports the token, terminal reason, count and limits.
- `x_input_trace_read(token, offset=0, count=60)` pages at most 90 snapshots.
- `x_input_trace_stop(token)` stops only this recorder; repeated stop is safe.

Bounds are 2..600 frames, 1..30 real seconds, and labels/action names up to 64
characters. Duplicate active starts and wrong tokens fail. Recording terminates on
observed Play exit, project/scene replacement, time or frame limit; it owns no Play
session and never stops one. Terminal evidence remains until the next valid start
or library hotload. It releases scene references when finished. Traces stay in memory;
read/export before hotload. Paused Play can still record editor frames.

These are **raw engine observations**: injected input cannot be distinguished
universally from physical input. They do not instrument game consumers, assert
command acceptance, or correlate screenshots atomically. Use project-specific hooks
for HUD-before/after, consumed look, gameplay revisions and eligibility. No input is
injected, clocks advanced, settings changed, processes launched or scenes modified.

## Shared-source maintenance and checks

The submodule is the canonical generic library checkout. Kampai's tools-directory
alias points to that same checkout, so edits require no file copying. See
[ownership and submodule workflow](docs/MCP-PARITY.md). Run:

```powershell
pwsh -File Tests/Compare-Embedded.ps1 -InventoryOnly
pwsh -File Tests/Compare-Embedded.ps1 -SelfTest
dotnet run --project Tests/ModelImport.ContractTests.csproj
# Focused buffer tests, no engine or Play needed:
dotnet run --project Tests/ModelImport.ContractTests.csproj -- --input-trace-only
```

The .NET 10 harness uses real Windows file handles/races and fake asset backends.
Three symlink fixtures require existing symlink privilege; failure to create their
links is an environment blocker, never a passed rejection assertion. Do not alter
permissions automatically. Engine compilation, MCP discovery, actual tool behavior,
physical devices and release acceptance require separate evidence.
