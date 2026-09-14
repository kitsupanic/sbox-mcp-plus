# Network and local-instance MCP tools

## Problem

The installed s&box editor can host and launch local multiplayer instances through editor APIs, but those operations are not available through its current MCP registry. An MCP client can start and stop ordinary play mode, but it cannot:

- inspect the active network session and connections;
- start editor hosting;
- launch a local client instance;
- trigger graceful host migration;
- disconnect from the session; or
- terminate a specifically owned child instance to test abrupt host loss safely.

This prevents automated multiplayer, handoff, rejoin, and recovery verification. Source inspection is not a substitute for runtime evidence.

## Proposed toolset

Add an editor-only MCP toolset named `extras` using the library's collision-safe `x_` prefix.

### `x_network_status`

Read-only. Return:

- whether networking is active;
- whether the editor is host or client;
- whether a connection attempt is active;
- local and host connection IDs; and
- every visible connection's ID, display name, host/local/active/connecting flags, and ping.

Do not return account credentials, chat contents, or voice contents.

### `x_network_start_hosting`

Start hosting through `EditorUtility.Network.StartHosting()`. Enter play mode through the supported editor path when necessary. Refuse when already connected and direct the caller to disconnect first.

Return the same state shape as `x_network_status`.

### `x_network_disconnect`

Disconnect through `EditorUtility.Network.Disconnect()`. Refuse when no network session is active.

Return the resulting network state.

### `x_network_spawn_instance`

Launch another local s&box client and record it in an in-memory ownership registry. Require the editor to be hosting first.

Approved behavioral choice: the launch is library-owned rather than delegated to `LocalInstances.Spawn(...)`. The installed editor exposes no public `LocalInstances` helper, and the source-checkout helper returns only a process ID, which cannot support identity-checked termination. This tool creates a private unnamed Windows Job Object, starts the sibling `sbox.exe` suspended with the fixed argument list `-joinlocal +instanceid <random positive instance number>` plus `-sw -720` when windowed applies, contains the process, captures its identity, and only then resumes it. It never accepts an executable, working directory, environment, or extra command-line arguments, and it deliberately does not replay the editor's arbitrary extra-instance preference arguments, which could override identity or containment assumptions.

Return:

- process ID;
- launch timestamp, taken from the exact process creation time and also serving as the termination token;
- whether the instance was requested windowed; and
- its log directory, which is the shared editor log root rather than a unique per-child directory.

A process is owned only when this tool launched and recorded it during the current editor session. Ownership is in-memory only, is never reconstructed from process enumeration, and is invalidated when the library assembly is replaced during a hotload.

Failures are reported as bounded fixed messages. A launch failure names the process ID and, when a native call failed, its numeric code; an engine or launch-setup failure never returns raw engine text, filesystem paths, or inner exceptions. A launch whose cleanup cannot confirm termination retains the available original handle or assigned private job instead of abandoning authority. If its exact creation time was not captured, that entry stays internal until the retained handle yields the genuine timestamp; it is never exposed with a fabricated or overloaded launch token.

### `x_network_migrate_to_new_instance`

The tool remains registered for a stable MCP contract, but the installed engine cannot safely complete this operation. Build `26.09.08e` selects host-migration successors from Steam lobby members; synthetic loopback clients do not qualify. The installed editor also exposes no supported API to target or certify a local successor. Therefore this tool validates `timeoutSeconds`, requires a stable hosting session with no remote peers, and then refuses before launching or disconnecting with `The installed engine cannot migrate hosting to a synthetic local client; no process was launched and the editor remains connected.`

The source checkout contains a newer handoff implementation, but source-only behavior is not an installed capability and must not be assumed. Do not replace this refusal with connection counting or an unverified `Disconnect()` handoff. When the installed engine provides a supported targeted migration API, the implementation can launch through the existing owned-instance path, correlate the child internally, and adopt that API after installed-runtime verification.

### `x_network_instances`

Read-only. Return only child processes recorded by this library: process ID, launch timestamp, whether it was launched windowed, alive/exited state, the shared log directory, and the private job's active process count. Enumeration covers registry entries only; OS processes are never enumerated or adopted.

A root may exit while its descendants live on. Such a row stays listed as exited with a non-zero active count so the remaining owned tree is still controllable, and listing itself never terminates anything. A fully exited tree returns its row once and is then pruned; after pruning the same request reports `not owned`, and before pruning a termination request reports `already exited`.

### `x_network_terminate_instance`

Terminate one library-owned child process tree to support controlled abrupt-loss tests.

Termination requires both the process ID and the exact creation-time token returned by spawn or listing, so a stale request for a PID that a later launch reused cannot terminate the newer process. A retained launch is not exposed until that timestamp is known. A supplied token that does not match exactly is an `identity mismatch` and never invalidates the legitimate entry.

Safety requirements:

- Accept only a process ID and timestamp pair present in the current ownership registry.
- Refuse the editor's own process ID and absent or non-positive IDs as `not owned`, without opening a process.
- Refuse arbitrary, stale, reused, or externally launched process IDs.
- Before termination, verify the retained handle's process ID, the exact creation time, the recorded executable path when one was captured, and - when the entry claims private-job containment - membership of that job. Any failed or unknown check is an `identity mismatch` with nothing signaled; authority is never reacquired by PID.
- Never represent a launch whose containment or identity capture failed as an ordinary contained instance. Such a launch is kept in an explicit failed-launch state whose only authority is the original suspended process handle or its successfully assigned private job, and whose identity checks use only what was actually captured.
- Never search for descendants by parent PID, and never enumerate or adopt unrelated processes.
- Terminate only that owned process tree through a bounded sequence.
- Return explicit `terminated`, `already exited`, `not owned`, or `identity mismatch` results.
- Never expose a general-purpose process-kill primitive.

Approved behavioral choice: the default mode is bounded graceful-then-forced, and an explicit `abrupt` mode skips the graceful stage. The default asks the root to close its main window and waits up to two seconds for the job to empty, then forcibly terminates the job and waits up to three more seconds. `abrupt` goes straight to the forced job termination. `Forced` is reported only when forced termination was actually invoked. Success is reported only once the whole owned job is empty; on native failure or a wait timeout, ownership and identity are retained, the entry stays listed, and the caller gets an explicit failure naming the PID.

Hotload cancellation still protects asynchronous owned-instance termination. Migration currently has no asynchronous or destructive stage because it refuses before launch on the unsupported installed engine.

### `x_network_instance_log`

Not implemented in this change. The engine writes one shared log tree rather than per-instance logs, so no bounded tail can be attributed to a specific owned child, and no path constraint could exclude unrelated or sensitive payloads reliably. If this is wanted later it needs a per-child log stream the engine does not currently provide.

## Public editor APIs

The editor-side network operations use these installed public members:

- `EditorUtility.Network.Active`
- `EditorUtility.Network.Hosting`
- `EditorUtility.Network.StartHosting()`
- `EditorUtility.Network.Disconnect()`
- `EditorPreferences.WindowedLocalInstances`
- `Networking.IsActive`
- `Networking.IsHost`
- `Networking.IsClient`
- `Networking.IsConnecting`
- `Connection.Local`
- `Connection.Host`
- `Connection.All`
- `Connection.Id`, `Connection.DisplayName`, `Connection.IsActive`, `Connection.IsConnecting`, `Connection.Ping`
- `Sandbox.IHotloadManaged` for ownership invalidation on hotload

Local launch and containment do not use an engine helper: they are implemented in the editor assembly with Windows job-object and `CreateProcessW` interop, because the installed editor exposes no public `LocalInstances` helper and the source-checkout one returns only a PID or a count-derived boolean. The library must compile against the installed editor API; source-checkout availability alone is insufficient.

## Compatibility

Keep every tool name prefixed with `x_`. If an equivalent stock tool appears later, retain the prefixed tool until callers migrate deliberately; do not collide with or silently shadow engine tools.

All code belongs in the editor assembly. Do not expose these operations to shipped game code.

## Acceptance checks

1. Restart the editor after first installing the updated library.
2. Confirm `list_toolsets` includes the seven new tools under `extras` and the three existing extras tools remain.
3. Confirm the editor assembly compiles with zero errors.
4. Call `x_network_status` while disconnected: inactive, non-host, non-client, no remote rows; `x_network_disconnect` refuses.
5. Call `x_network_start_hosting`; verify play mode and host state, and that a second call refuses without a new lobby.
6. Call `x_network_spawn_instance` with `windowed=true`; record PID, launch timestamp, windowed flag, and that the log directory is the shared engine log root. Confirm the child appears as an active client, and confirm the internal instance-number correlation matches the child's synthetic connection identity. A mismatch is an incompatibility, not permission to relax identity matching.
7. Confirm `x_network_instances` reports only the recorded child; terminate it with its exact timestamp using the default mode, then disconnect and rehost so no remote peers remain.
8. In a fresh hosting session with no remote peers, call `x_network_migrate_to_new_instance`; verify the fixed installed-engine incompatibility error, that the editor remains hosting, and that no child was launched. Calling it with an existing remote peer must retain the stricter precondition refusal.
9. Call `x_network_terminate_instance` with an owned child's PID, exact timestamp, and `abrupt=true`; verify forced termination, child exit, and that the editor and an unrelated manually launched process survive.
10. Call `x_network_disconnect` where applicable and verify the final disconnected state. Read-only tools stay callable during a mutation, and a competing mutation receives the busy refusal.
11. Confirm invalid, non-positive, editor-self, unrelated, wrong-timestamp, and already-pruned IDs are refused without signaling anything.
12. Confirm no account identifiers, chat contents, or voice contents appear in tool results, including a display name containing Steam2, Steam3, and 17-digit identifiers.
13. With one owned client, replace the library assembly: the client survives, the new registry is empty, and the old PID and timestamp report `not owned`. After an editor restart, previously launched processes are also refused.

## Evidence boundary

Local instances use synthetic/loopback identities. These tools can establish editor transport, callbacks, controlled process loss, and ownership invalidation. Installed build `26.09.08e` cannot establish local-client host migration because its successor selection depends on Steam lobby membership. They also do not establish real private Steam admission, separate-account authorization, controller or microphone behavior, or native account-service transaction guarantees.
