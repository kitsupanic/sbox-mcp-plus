<p align="center">
  <img src="img/logo.png" alt="sbox-mcp-plus" width="200" />
</p>

# sbox-mcp-plus

Extra tools for AI assistants working in the **s&box editor**.

Install this library in your game project to let an MCP-compatible assistant
switch scene tabs, capture cameras with UI text, inspect models, manage local
multiplayer clients, and record input diagnostics.

The tools run inside the editor and connect through s&box's built-in
`sbox-cli` MCP bridge. They appear in the `extras` toolset, with an `x_` prefix
to keep their names separate from built-in tools.

## Requirements

- Windows with s&box installed and a game project.
- An s&box build with the `sbox-cli` MCP bridge.
- Git and PowerShell 7 (`pwsh`) for the setup below.
- An MCP client that supports local stdio servers.

## Quick start

1. **Install the library.** Open a terminal in your **game project folder**
   (the folder containing its `.sbproj`) and run:

   ```powershell
   git submodule add https://github.com/kitsupanic/sbox-mcp-plus.git Libraries/kitsupanic.sbox_mcp_plus
   git submodule update --init --recursive
   ```

2. **Enable MCP communication.** In the game's editor, enable
   **Project Settings > AI > Enable MCP Communication**.

3. **Restart the editor with your game project open.** Libraries are loaded at
   project startup, so the first installation needs a restart. Later source edits
   hotload.

4. **Configure your MCP client (optional).** If your client is already connected
   to this s&box project, skip to step 5. The library adds its tools to the existing
   connection; no additional server configuration is needed.

   Otherwise, add this server to your client's MCP configuration:

   ```json
   {
     "mcpServers": {
       "sbox": {
         "type": "stdio",
         "command": "pwsh",
         "args": [
           "-NoProfile",
           "-File",
           "Libraries/kitsupanic.sbox_mcp_plus/Scripts/Connect-Sbox.ps1"
         ]
       }
     }
   }
   ```

   Set the server's **working directory to your game project folder** using your
   client's working-directory setting, or launch the client from that folder.
   The JSON above follows the included [sample configuration](mcp.json);
   configuration location and accepted fields depend on your client.

5. **Connect and discover the tools.** Start or restart the client's MCP server.
   Ask your assistant to check `editor_status`, then discover the `extras`
   toolset with `list_toolsets` or search for `x_` with `search_tools`.

The launcher looks for the engine in `FACEPUNCH_ENGINE` (process scope, then
Windows user scope), then the default Steam installation folder. For a custom
Steam library, set `FACEPUNCH_ENGINE` to the folder containing `sbox.exe`, or
append `"-EngineDirectory", "D:/SteamLibrary/steamapps/common/sbox"` to the
configuration's `args`, replacing the example path with your installation.

The connection uses **stdio**, with no HTTP URL or port to configure. The launcher
only attaches to an already-running editor using `--connect`; it does not open
an editor, and disconnecting leaves the editor running.

### Try asking your assistant

- “Show the active scene, whether it has unsaved changes, and how many tabs are open.”
- “Open my menu scene and take a camera screenshot that includes the UI.”
- “Inspect this player model's bones, attachments, and animations.”
- “Record a short input trace in the current Play scene to help investigate mouse look.”

The assistant should discover each tool's current input schema before calling it.
For complete behavior and limits, see the [tool reference](docs/TOOL-REFERENCE.md).

## Tools

### Scenes and cameras

| Tool | What it does |
| --- | --- |
| `x_open_scene` | Open or activate a scene or prefab tab. Requires edit mode. |
| `x_create_scene` | Create and activate a new empty `.scene` beneath `scenes/diagnostics/`, without overwriting files. |
| `x_editor_status` | Read the active tab, unsaved state, open-tab count, project, and Play/pause state. |
| `x_camera_screenshot` | Capture a scene camera, preserving UI text when resizing a capture in Play. |
| `x_camera_look_at` | Aim an active-scene camera at a world-space point, with undo. |
| `x_set_network_mode` | Set an active-scene object's saved networking mode to `Never`, `Object`, or `Snapshot`, with undo. |
| `x_compile_scene` | Run Scene > Compile Scene on the saved active scene, report the result, and remove unreferenced compiled generations. |

### Networking and local clients

| Tool | What it does |
| --- | --- |
| `x_network_status` | Read session state and connection rows with account and communication data omitted. |
| `x_network_start_hosting` | Start hosting through the editor's supported network path. |
| `x_network_disconnect` | Disconnect the editor while leaving launched clients running. |
| `x_network_spawn_instance` | Launch a local s&box client while hosting, with owned process-tree tracking. |
| `x_network_instances` | List clients owned by this library during its current hotload lifetime. |
| `x_network_terminate_instance` | Close an owned client tree using its PID and exact launch token. |
| `x_network_migrate_to_new_instance` | **Currently unavailable:** refuses before launching a client or transferring hosting. |

### Models

| Tool | What it does |
| --- | --- |
| `x_import_model_source` | Import an external FBX, OBJ, or DMX into a fresh Assets directory and return compilation/dependency evidence. |
| `x_model_skeleton` | Read model bounds, bones, attachments, material groups, and animation names. |

### Input diagnostics

| Tool | What it does |
| --- | --- |
| `x_input_observe` | Read focus, input suppression, mouse visibility, action state, look input, and camera state. |
| `x_input_trace_start` | Start a bounded trace in an explicitly identified, already-running Play scene. |
| `x_input_trace_status` | Read recording state, token, sample count, and limits. |
| `x_input_trace_read` | Read recorded samples in pages using the trace token. |
| `x_input_trace_stop` | Stop the recorder without stopping Play. |

## Limitations and troubleshooting

| Situation | What to check |
| --- | --- |
| The client cannot connect | Open the game in the editor, enable MCP communication, and check the client's working directory. Restart its MCP server after restarting the editor. |
| `sbox-cli.exe` is not found | Set `FACEPUNCH_ENGINE` or pass `-EngineDirectory` as described above. |
| `pwsh` is not found | Install PowerShell 7 and ensure it is available on the client's PATH. |
| The `extras` tools are missing | Restart the editor after installing the library, check for compilation errors with `editor_status`, and rediscover the tools. |
| An older configuration uses `http://127.0.0.1:7269/mcp` | Replace it with the stdio configuration above. |
| An unsaved scene cannot be opened by path | Select it by its tab name; it has no saved resource path yet. |

A few limits matter when using the tools:

- **Screenshots in Play** capture at the live viewport resolution and resize to
  the requested dimensions. Larger output does not add detail. Edit mode renders
  directly at the requested size.
- **Local-client ownership resets on hotload or restart.** Surviving clients
  keep running but are no longer listed or terminable by this library; close them
  manually. Termination requires both the PID and exact launch token.
- **Model imports require edit mode and a fresh destination.** Overwriting is
  unsupported. Native compilation can block the editor without a hard deadline;
  failures may retain partial files and report their paths.
- **Input traces observe raw engine state.** They do not inject input or prove
  physical-device input or gameplay acceptance. Traces are held in memory; read
  them before starting another trace or hotloading. Recording is limited to
  2–600 editor frames and 1–30 seconds.

Some tools overlap with newer built-in tools. Their original workaround history
and installed-runtime verification are documented in the
[CLI/tool audit](docs/CLI-AUDIT.md).

## Further documentation

- [Contributing](CONTRIBUTING.md) — report a problem or submit a fix.
- [Agent guidance](AGENTS.md) — help your assistant diagnose issues and prepare contributions.
- [Tool reference](docs/TOOL-REFERENCE.md) — detailed behavior, guards, and workaround history.
- [Model import reference](README-model-import.md) — inputs, results, compilation evidence, and failure handling.
- [CLI/tool audit](docs/CLI-AUDIT.md) — compatibility and native replacement checks.
- [Development and checks](docs/DEVELOPMENT.md) — contributor workflow and validation commands.

Clients may also launch `<engine>/bin/managed/sbox-cli.exe --connect` directly.
For your installed build's instructions, run `sbox-cli --skill sbox-cli`.

## License

[MIT](LICENSE) © 2026 kitsupanic.
