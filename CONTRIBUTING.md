# Contributing to sbox-mcp-plus

Bug reports, documentation improvements, and focused fixes are welcome. This
library adds editor tools to s&box's existing MCP connection; reusable plugin
changes belong here, while gameplay code and project settings belong in your game.

## Report a problem

First check the [README troubleshooting](README.md#limitations-and-troubleshooting),
[tool reference](docs/TOOL-REFERENCE.md), and [CLI/tool audit](docs/CLI-AUDIT.md).
Some tools deliberately refuse unsafe operations, and host migration is currently
unavailable. An unexpected failure of a documented capability is worth reporting.

Search [existing issues](https://github.com/kitsupanic/sbox-mcp-plus/issues), then
[open an issue](https://github.com/kitsupanic/sbox-mcp-plus/issues/new) with:

- A short description and the expected and actual behavior.
- Minimal steps to reproduce, including the tool name and sanitized arguments.
- Your plugin commit (`git rev-parse HEAD` from the library folder), s&box engine
  build, Windows version, and MCP client/version.
- Whether the problem occurs in edit or Play mode, and relevant connection or
  compilation state. Explain whether it reproduces in a minimal project.
- The exact relevant error and checks already tried. Identify checks you could
  not run instead of reporting them as passing.

Include a small screenshot or log excerpt when useful. Remove credentials,
account identifiers, private code, personal paths, and player chat/voice content.
Avoid uploading whole project archives or full logs when a small example suffices.

You do not need to implement a fix before opening an issue. For a larger feature
or a change to an existing tool contract, discuss the approach in an issue first.

## Submit a fix

1. Fork the repository and create a branch for your change. If working inside a
   game's submodule, check for local changes and a detached HEAD first; keep the
   game's unrelated changes separate. See the [submodule workflow](docs/MCP-PARITY.md).
2. Make a focused change using existing patterns. Keep tool names prefixed with
   `x_` and preserve ownership, path validation, privacy, undo, and evidence rules.
   Do not commit generated projects, build outputs, or gameplay settings.
3. Update documentation for changed behavior and add a relevant regression test
   for a behavior fix. Documentation-only changes need document checks.
4. Run the relevant checks in [development and checks](docs/DEVELOPMENT.md).
   The inventory check is:

   ```powershell
   pwsh -File Tests/Compare-Embedded.ps1 -InventoryOnly
   ```

   Behavior changes should run the affected contract tests. Engine-facing changes
   also need editor compilation and native verification when available. Report
   environment blockers, such as missing symlink privileges, separately.
5. Open a pull request against `main`. Explain the problem, resulting behavior,
   relevant issue, checks and exact outcomes, and remaining limitations. Separate
   source inspection, simulated tests, and native editor results.

For the attach-only native smoke, run the following with your intended game open
in the editor. Replace both placeholder paths; keep your scene and Play state intact:

```powershell
python Tests/Test-CliTransport.py --engine <engine-directory> --project <game-project-directory>
```

This checks transport, compilation, tool discovery, and editor status. It does
not exercise every tool. Use `--launch` only for an isolated test project whose
editor can safely be closed when the test finishes.

## Working with an AI assistant

Point your assistant to [AGENTS.md](AGENTS.md) for debugging and contribution
guidance. It should prepare a useful issue or PR and ask before publishing it.
Review the reproduction, patch, and shared evidence before approving submission.
