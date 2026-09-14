<p align="center">
  <img src="img/logo.png" alt="sbox-mcp-plus" width="200" />
</p>

# sbox-mcp-plus

Extensions to the s&box editor's built-in MCP server. The editor already runs an MCP
server on `http://127.0.0.1:7269/mcp` and discovers tools from library code live — this
library adds tools the stock set is missing, so agents get them today instead of waiting
on engine releases.

All tool names are prefixed `x_` so they can never collide with the engine's own once
the upstream equivalents land.

## Install

Drop this library into your project's `Libraries/` folder (or install it as a package
once published). Libraries are scanned at project load — restart the editor after first
install; after that, edits to the code hotload.

Find the tools from any MCP client via `list_toolsets` (toolset `extras`) or
`search_tools`.

## Tools

### `x_open_scene`

Make a scene the active editor tab, opening it from its asset path if it isn't open yet.
Accepts a scene name or resource path as `list_scenes` reports them.

Why it exists: the stock toolset can read and list background scenes but can't switch
which one is active, and every mutating tool (`set_component`, `save_scene`, `undo`)
targets the active scene. Worse, an edit aimed at a background scene mutates the object
but the dirty flag lands on the active tab — the background scene reloads from disk on
focus and the edit is silently lost. Upstream: issue
[#11637](https://github.com/Facepunch/sbox-public/issues/11637), PR
[#11638](https://github.com/Facepunch/sbox-public/pull/11638) (open).

- Explicit no-op message when the scene is already the active tab.
- Refuses during play mode — `play_stop` first.
- Refuses the running game session (it has no tab to switch to).

### `x_create_scene`

Create, save, and activate a new empty scene at a project-relative `.scene` path beneath
`scenes/diagnostics/`. The optional `name` defaults to the destination file name.

- Refuses play mode, absolute or traversing paths, paths outside the diagnostics folder,
  wrong extensions, and existing destinations.
- Saves directly through the editor asset system without a modal or file picker.
- Leaves every existing tab—including unsaved work—unchanged.
- Removes its temporary unsaved session and any partial asset if creation fails.


### `x_editor_status`

What the editor is doing right now, from the editor's point of view: active scene tab
name and path, unsaved-changes flag, open tab count, project ident, play/pause state.

Why it exists: the built-in `editor_status` reports the game scene handle, not the
active editor tab — while editing it returns a placeholder name (`"Scene"`) and a null
path, and its dirty flag can describe a different scene than its name. Upstream: issue
[#11639](https://github.com/Facepunch/sbox-public/issues/11639), PR
[#11640](https://github.com/Facepunch/sbox-public/pull/11640) (closed unmerged).

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

- The built-in `editor_status` is still broken while this library only routes around
  it — a library can't patch engine-assembly tools. Until upstream fixes it, trust
  `x_editor_status` or `list_scenes.IsActive`, not `editor_status`'s scene fields.
- Tool errors arrive with a full .NET stack trace appended after the message. That's
  the engine MCP server's error formatting (its own tools do the same); the first line
  is the actionable part.
- `x_open_scene` can't target a never-saved scene by path (it has no resource path
  yet) — use its name.
- `x_camera_screenshot` output detail is capped at the live viewport's resolution in play
  mode: it captures there and resizes, so a requested size larger than the viewport is
  interpolation, not extra detail. Aspect ratio still follows the requested size. Edit
  mode renders at the requested size directly and isn't capped.
- Adding this library to a project doesn't take effect in an already-running editor:
  package mounting happens at project load, only source edits hotload. Restart once.
