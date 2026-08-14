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

## Known issues

- The built-in `editor_status` is still broken while this library only routes around
  it — a library can't patch engine-assembly tools. Until upstream fixes it, trust
  `x_editor_status` or `list_scenes.IsActive`, not `editor_status`'s scene fields.
- Tool errors arrive with a full .NET stack trace appended after the message. That's
  the engine MCP server's error formatting (its own tools do the same); the first line
  is the actionable part.
- `x_open_scene` can't target a never-saved scene by path (it has no resource path
  yet) — use its name.
- Adding this library to a project doesn't take effect in an already-running editor:
  package mounting happens at project load, only source edits hotload. Restart once.
