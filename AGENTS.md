# Working with sbox-mcp-plus

This repository is a reusable s&box editor library. Read the [README](README.md)
for setup, the [tool reference](docs/TOOL-REFERENCE.md) for behavior and limits,
and [CONTRIBUTING](CONTRIBUTING.md) for issue and pull-request guidance.

## Diagnose problems

- Before making changes, check the plugin revision and local Git changes. Preserve
  the user's edits, including changes in the host game and its submodules.
- Read the installed engine's instructions with `sbox-cli --skill sbox-cli`.
  Attach to the intended project's existing editor using `--connect`.
- Confirm the project, engine build, compile state, active scene, unsaved work,
  and Play state with `editor_status` and `x_editor_status` when available.
  If connection fails, diagnose it before attempting editor operations.
- Discover the current tool schemas through `search_tools` or the `extras`
  toolset. Do not guess engine APIs or tool arguments.
- Distinguish setup problems, documented guard refusals, engine limitations,
  and plugin defects. Use an isolated project when reproduction would change
  scenes, assets, Play state, or processes. Obtain authorization for that work.
- Preserve scene edits and retained import evidence. After a timed-out mutation,
  inspect state before retrying; the operation may already have completed.

## Help users report issues or contribute fixes

When a reproducible plugin defect remains unresolved, or you implement a reusable
fix, tell the user that it can help other users and suggest an upstream issue or
pull request. Do not recommend a new report for an expected, documented refusal.

1. Check existing issues when GitHub access is available. Otherwise disclose that
   duplicates were not checked; do not let unavailable access block local diagnosis.
2. Prepare a reviewable report using the checklist in [CONTRIBUTING](CONTRIBUTING.md):
   expected/actual behavior, minimal reproduction, plugin revision, engine build,
   relevant configuration, sanitized errors, and checks performed.
3. Suggest an issue when the cause or fix is uncertain. Suggest a PR when a focused
   fix is implemented and verified; reference an existing issue when appropriate.
4. Ask whether the user wants the report or fix submitted. Creating public issues,
   comments, PRs, or pushing commits requires the user's authorization. Honor
   authorization already given; do not ask again for the same approved action.
   If publishing tools are unavailable, provide the prepared text and submission link.

Never publish credentials, account identifiers, private project code, players'
chat/voice content, or unsanitized logs. Include only evidence needed to reproduce
the problem, replacing private paths and names with clear placeholders.

## Make and verify fixes

- Keep generic tools here and gameplay code/settings in the consuming game.
  Do not restore `ProjectSettings/` or commit generated IDE/build artifacts.
- Preserve the `x_` prefix and existing tool contracts. Do not weaken ownership,
  path, privacy, undo, or evidence guards to make a failing scenario pass.
- Use the smallest complete fix and relevant regression checks. Keep all tools
  documented in the README and update detailed behavior in the owning reference.
- Follow [development and checks](docs/DEVELOPMENT.md). Run the inventory check
  for tool/documentation changes, relevant contract tests for behavior changes,
  and native compilation/discovery checks for engine-facing changes when available.
- Report exact results and unperformed or blocked checks. Source inspection and
  simulated tests do not establish native behavior or physical-device coverage.
- Work on a branch for contributions, including from a detached submodule checkout.
  Keep unrelated host-game changes out of the PR. See the
  [submodule workflow](docs/MCP-PARITY.md) before updating a game's library pointer.
