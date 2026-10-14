# Library ownership and submodule workflow

This repository contains the reusable s&box MCP library. Install it beneath a
game project's `Libraries/` folder as described in the [README](../README.md).
The editor discovers its `extras` tools through the existing `sbox-cli`
connection.

## Clone an existing game project

If the game already includes this library as a submodule, clone the game with its
submodules:

```powershell
git clone --recurse-submodules <game-repository-url>
# Or initialize submodules in an existing game checkout:
git submodule update --init --recursive
```

The game repository records the library's commit, not a copy of its files.
Collaborators need no machine-specific junctions, aliases, or synchronization.

## Change or update the library

Make reusable changes inside the library checkout. Commit and push the library
changes before publishing a game revision that references them, so collaborators
can fetch the recorded commit. Then commit the updated submodule pointer in the
game repository. A parent commit cannot store uncommitted files inside a submodule.

Each game revision pins a library commit. Inspect local changes before updating;
submodules are often checked out at a detached HEAD. Create a branch before
making new commits on a detached checkout. Choose updates deliberately rather
than automatically tracking the latest remote revision.

## Responsibilities

Keep editor tools, their launcher, generic tests, and tool documentation here.
Keep gameplay code, HUD hooks, project settings, and game-specific acceptance
checks in the consuming game.

The library does not ship a `ProjectSettings/` folder. Input, collision, and
platform configuration belong to the active game project. MCP communication is
controlled by the host game's AI settings in its `.sbproj`.

Engine-generated IDE projects and build artifacts are local files. The isolated
contract-test project is maintained by hand and remains tracked.

## Checks and evidence

See [development and checks](DEVELOPMENT.md) for local validation commands and
the [CLI/tool audit](CLI-AUDIT.md) for installed-runtime compatibility evidence.

Contract fixtures exercise temporary files, fake asset backends, and buffer
state. They do not establish native import behavior, physical-device coverage,
or gameplay acceptance. Symlink fixture failures remain environment blockers.
Native checks should confirm the active project, scene, unsaved work, Play state,
and compile state while preserving the user's existing session.
