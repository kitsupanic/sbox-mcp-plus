# Tool reference

Detailed behavior, limits, and original workaround history for the `extras` toolset.
See the [README](../README.md) for installation and the [CLI/tool audit](CLI-AUDIT.md) for current overlap and native replacement checks.

## Scenes and cameras

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
[native replacement checks](CLI-AUDIT.md) pass on the installed build.

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

### `x_compile_scene`

Compile the active scene through the editor's own `SceneCompileSession`. This is the same path
as Scene > Compile Scene (F9). The stock MCP tools have no scene-compile action.

**Refusals:** The tool refuses in each of these cases:
- Play mode is running.
- No tab is active, or the active tab is a prefab.
- The scene has unsaved changes.
- A compile is already running.
- The editor reports that it can't compile.

**Waiting:** The tool waits for completion for up to `timeoutSeconds` (default 600). On timeout it requests cancellation and reports the status.

**Result:** The tool returns:
- the editor's status (`Done`, `Nothing to compile`, `Failed` or `Cancelled`), error and summary
- scan counts
- warnings, with the owning object's name
- the compile log
- every generation folder under `<scene>_scene_data/compiled/`, with whether the compiled scene file references it

**Stale generations:** The installed engine (26.10.13) intends to delete previous generations after each compile. `SceneCompiler.CleanupPreviousGenerations` does this through `FileSystem.Mounted`, but that filesystem is read-only in the editor. The cleanup fails with `Could not clean up staged scene resources … This filesystem is read-only`, so old generations accumulate. Publishing collects all of them, together with the models and materials they reference, which can include assets the current scene no longer uses.

With `removeStale` (default `true`), the tool finishes that cleanup on disk after a `Done` compile. It deletes only folders that meet all of these conditions:
- The folder sits directly in that scene's `compiled/` folder.
- Its name is a 32-hex generation id.
- The compiled scene does not reference it.

Nothing is deleted when the compiled scene references no generation. Loose files in `compiled/` are left alone. The result lists `RemovedGenerations` and any `Notes`.

Deleting the generation that was live before the compile makes the resource system log one `ERROR_FILEOPEN` warning per mesh it had loaded from that folder. The current generation is unaffected.

**Verification on 26.10.13:** A 17-mesh scene compiled in about 0.5 s. Two stale generations were removed. All current meshes load through `Model.Load`.

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

## Models

### `x_import_model_source`

Import an external `.fbx`, `.obj`, or `.dmx` into a fresh directory beneath the
active project's `Assets`, create a `.vmdl`, and return compilation and dependency
evidence. Requires edit mode. Existing destinations are refused;
`overwrite=true` is unsupported.

The required arguments are `sourcePath` (an absolute source file path) and
`targetDirectory` (a fresh directory relative to `Assets`). Optional arguments
are `modelName`, `overwrite`, and `copySiblingTextures` (default true).
Texture copying includes every allowlisted immediate sibling image, so keep
unrelated images out of the source folder or disable it.

Native model creation and compilation can block the editor without a hard
deadline. Returned asset-state evidence does not prove that native writers have
stopped. A failed import can retain partial files and reports their paths.
See the [model import reference](../README-model-import.md) for the full input,
result, concurrency, and rollback contracts.

### `x_model_skeleton`

Inspect a model resource by asset path using the required `model` argument.
Returns model-space bounds, bones, attachments, material groups, and animation
names without changing a scene. Unavailable fields carry reasons; an empty
collection means the field was inspected and contained no entries.
See the [model reference](../README-model-import.md) for evidence details.

## Compatibility notes

Workaround descriptions above record the original defects. Current stock source
adds scene switching and fixes active-scene status; installed runtime parity is
tracked in the [CLI/tool audit](CLI-AUDIT.md). Source overlap alone is not a reason
to retire a compatibility tool.

An unsaved scene has no resource path yet; select it by name with `x_open_scene`.
In Play, screenshot detail is capped at the live viewport resolution. Larger
requested images are interpolated; the requested aspect ratio still applies.
Edit mode renders directly at the requested size.
