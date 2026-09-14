# Graph Report - sbox-mcp-plus  (2026-09-14)

## Corpus Check
- 6 files · ~42,450 words
- Verdict: corpus is large enough that graph structure adds value.

## Summary
- 199 nodes · 401 edges · 13 communities (11 shown, 2 thin omitted)
- Extraction: 99% EXTRACTED · 1% INFERRED · 0% AMBIGUOUS · INFERRED: 4 edges (avg confidence: 0.8)
- Token cost: 0 input · 0 output

## Graph Freshness
- Built from commit: `dfa55c7d`
- Run `git rev-parse HEAD` and compare to check if the graph is stale.
- Run `graphify update .` after code changes (no API cost).

## Community Hubs (Navigation)
- ExtrasTools
- NativeMethods
- NetworkToolLifetime
- OwnedInstanceRegistry
- Proposed toolset
- .TerminateAsync
- OwnedLocalInstance
- .LaunchContained
- .IsSignaled
- sbox-mcp-plus
- .OpenSceneTab
- mcp.json

## God Nodes (most connected - your core abstractions)
1. `OwnedInstanceRegistry` - 42 edges
2. `ExtrasTools` - 32 edges
3. `NativeMethods` - 23 edges
4. `NetworkToolLifetime` - 19 edges
5. `OwnedLocalInstance` - 19 edges
6. `SafeKernelHandle` - 19 edges
7. `Proposed toolset` - 9 edges
8. `OwnedInstanceSnapshot` - 7 edges
9. `Network and local-instance MCP tools` - 7 edges
10. `OwnedTerminationOutcome` - 6 edges

## Surprising Connections (you probably didn't know these)
- `NetworkToolLifetime` --references--> `OwnedInstanceRegistry`  [EXTRACTED]
  Editor/McpExtras.Network.cs → Editor/OwnedLocalInstance.cs

## Import Cycles
- None detected.

## Communities (13 total, 2 thin omitted)

### Community 0 - "ExtrasTools"
Cohesion: 0.08
Nodes (22): CameraComponent, Connection, ConnectionState, Editor.Mcp, ReadOnly, EditorStatusExtras, SceneTab, Exception (+14 more)

### Community 1 - "NativeMethods"
Cohesion: 0.12
Nodes (21): DllImport, int, long, string, FileTime, JobBasicAccountingInformation, NativeMethods, ProcessInformation (+13 more)

### Community 2 - "NetworkToolLifetime"
Cohesion: 0.13
Nodes (11): CancellationTokenSource, CancellationToken, Dictionary, IDisposable, int, long, string, MutationLease (+3 more)

### Community 3 - "OwnedInstanceRegistry"
Cohesion: 0.15
Nodes (9): Action, bool, Dictionary, OwnedInstanceRegistry, Func, IEnumerable, object, TimeSpan (+1 more)

### Community 4 - "Proposed toolset"
Cohesion: 0.12
Nodes (15): Acceptance checks, Compatibility, Evidence boundary, Network and local-instance MCP tools, Problem, Proposed toolset, Public editor APIs, `x_network_disconnect` (+7 more)

### Community 5 - ".TerminateAsync"
Cohesion: 0.21
Nodes (5): CancellationToken, Task, OwnedTerminationOutcome, Forced, Message

### Community 6 - "OwnedLocalInstance"
Cohesion: 0.25
Nodes (5): OwnedLocalInstance, Error, IDisposable, RetainedJob, RetainedProcess

### Community 7 - ".LaunchContained"
Cohesion: 0.43
Nodes (4): LaunchAuthority, LaunchFailureInjection, OwnedInstanceSnapshot, IReadOnlyList

### Community 9 - "sbox-mcp-plus"
Cohesion: 0.25
Nodes (7): Install, Known issues, sbox-mcp-plus, Tools, `x_camera_screenshot`, `x_editor_status`, `x_open_scene`

### Community 10 - ".OpenSceneTab"
Cohesion: 0.47
Nodes (3): McpTool, SceneEditorSession, SceneTab

## Knowledge Gaps
- **23 isolated node(s):** `InstanceState`, `InstanceTermination`, `SceneTab`, `EditorStatusExtras`, `sbox` (+18 more)
  These have ≤1 connection - possible missing edges or undocumented components.
- **2 thin communities (<3 nodes) omitted from report** — run `graphify query` to explore isolated nodes.

## Suggested Questions
_Questions this graph is uniquely positioned to answer:_

- **Why does `OwnedInstanceRegistry` connect `OwnedInstanceRegistry` to `NativeMethods`, `NetworkToolLifetime`, `.TerminateAsync`, `OwnedLocalInstance`, `.LaunchContained`, `.IsSignaled`, `.ValidateIdentity`?**
  _High betweenness centrality (0.406) - this node is a cross-community bridge._
- **Why does `NetworkToolLifetime` connect `NetworkToolLifetime` to `ExtrasTools`, `OwnedInstanceRegistry`?**
  _High betweenness centrality (0.343) - this node is a cross-community bridge._
- **Why does `ExtrasTools` connect `ExtrasTools` to `.OpenSceneTab`, `NetworkToolLifetime`?**
  _High betweenness centrality (0.280) - this node is a cross-community bridge._
- **What connects `InstanceState`, `InstanceTermination`, `SceneTab` to the rest of the system?**
  _23 weakly-connected nodes found - possible documentation gaps or missing edges._
- **Should `ExtrasTools` be split into smaller, more focused modules?**
  _Cohesion score 0.08194905869324474 - nodes in this community are weakly interconnected._
- **Should `NativeMethods` be split into smaller, more focused modules?**
  _Cohesion score 0.11666666666666667 - nodes in this community are weakly interconnected._
- **Should `NetworkToolLifetime` be split into smaller, more focused modules?**
  _Cohesion score 0.13157894736842105 - nodes in this community are weakly interconnected._