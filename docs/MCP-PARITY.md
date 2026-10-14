# Generic library ownership and submodule workflow

**Updated:** 2026-10-14. Kampai uses this repository as the Git submodule
`Libraries/kitsupanic.sbox_mcp_plus`, named `sbox-mcp-plus`. The parent records a
commit and the portable remote `https://github.com/kitsupanic/sbox-mcp-plus.git`.
The editor discovers its `extras` tools through `sbox-cli`; no second server runs.

## One local working checkout

On the current machine, `S:/game-dev/sbox/tools/sbox-mcp-plus` is a Windows directory
junction to `S:/game-dev/sbox/games/sbox-kampai-roulette/Libraries/kitsupanic.sbox_mcp_plus`.
Both paths address the same files and repository. Edit either path; no copy/parity
synchronization or background sync is needed. The junction is machine-local and
uncommitted. Relocating/deleting the game checkout affects the alias; check its
target before filesystem cleanup.

Collaborators use normal submodules and need no junction:

```powershell
git clone --recurse-submodules <kampai-repository-url>
# Or from an existing game checkout:
git submodule update --init --recursive
```

Commit generic changes inside the submodule first, push when authorized, then
stage/commit the changed library pointer in the game. A parent commit cannot
store dirty child files. Push the plugin commit before publishing its parent
revision. Each game revision pins a reviewed plugin commit; do not automatically
update from remote. Inspect local changes before submodule updates, particularly
on detached checkouts. Committing/pushing requires separate user authorization.

Conversion initially registers published commit
`1be6ee1013aceb2a2f3103aa46f8ef4fa9538253`; the CLI migration is initially
uncommitted in the child. The old `.version` package-cache marker is preserved
locally and ignored, rather than presented as the Git revision. Original vendored
bytes were backed up outside the game before conversion.

## Responsibilities and checks

Generic ownership includes all `Editor/*.cs`, contract fixtures, tool usage docs,
the CLI launcher and [the audit](CLI-AUDIT.md). Game-specific `Editor/Playtesting`,
HUD/consumed-input hooks, simulated commands, settings fixtures and supervisors
remain in Kampai. Generic raw input traces cannot certify devices or assert game
command acceptance.

Run `Tests/Compare-Embedded.ps1 -InventoryOnly` and
`dotnet run --project Tests/ModelImport.ContractTests.csproj`.
Legacy `-EmbeddedRoot`/`-SelfTest` modes remain for older vendored users.
Engine-generated projects are machine-local; do not copy/commit them.
The previously tracked `Editor/sbox_mcp_plus.editor.csproj` is removed from tracking
and left on disk for the installed editor to regenerate. The portable contract-test
project remains tracked. The library explicitly enables MCP communication in its
project AI settings; its host game's setting still controls that game's connection.

## Historical parity and evidence limits

The 2026-10-09 reconciliation brought generic scene ownership/race safeguards and
bounded input observation into both repositories. Immediately before conversion,
comparison passed for all 17 shared files (CRLF/LF normalized) and 20 documented
tools. This is historical evidence, not an ongoing two-copy maintenance rule.

Fixtures use temporary Windows files, fake asset backends and buffer state; they
do not prove native pipeline/sampling behavior. Symlink creation needs existing
privilege; its failures remain environment blockers. Use native CLI project,
scene, unsaved-work, Play and compile preflight. Preserve user scenes and Play;
an attach-only connection does not authorize their disposal.
