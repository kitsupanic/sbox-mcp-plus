# External model import MCP tool

## Goal

`x_import_model_source` synchronously imports a new external `.fbx`, `.obj`, or `.dmx` into a fresh isolated directory beneath the active project's `Assets`, creates and observes a `.vmdl`, and returns explicit evidence. `x_model_skeleton` reads model structure without touching a scene.

Native model creation and compilation may synchronously block the editor for an unbounded duration. Installed APIs expose asset state but do not prove native writer shutdown. Version 1 accepts that native background work from a completed invocation may overlap later imports. Directory isolation prevents direct destination-path reuse; it is not proof of engine-wide isolation.

## Public APIs

```csharp
[McpTool("x_import_model_source")]
public static Task<ImportModelResult> ImportModelSource(
    string sourcePath, string targetDirectory, string modelName = "",
    bool overwrite = false, bool copySiblingTextures = true);

[McpTool.ReadOnly("x_model_skeleton")]
public static ModelSkeletonResult GetModelSkeleton(string model);
```

Installed editor calls include `AssetSystem.RegisterFile`, `EditorUtility.CreateModelFromMeshFile`, `Asset.CompileIfNeededAsync`, the three compile-state properties, references, unrecognized references, input dependencies and additional content files. `CreateModelFromMeshFile` invokes compilation synchronously. The 30-second argument to `CompileIfNeededAsync` applies only to its observation loop after any synchronous compilation call; it is not a hard deadline or cancellation guarantee.

## Inputs

|Field|Required|Meaning|
|---|---:|---|
|`sourcePath`|yes|Absolute readable regular `.fbx`, `.obj`, or `.dmx`; source links resolve to their regular-file target.|
|`targetDirectory`|yes|Fresh final directory relative to `Assets`; no implicit subdirectory.|
|`modelName`|no|Generated `.vmdl` stem only. Null/empty defaults to the source stem; whitespace does not.|
|`overwrite`|no|Default false. True always returns `OverwriteUnsupported` before mutation.|
|`copySiblingTextures`|no|Default true. Copy every immediate sibling `.png`, `.tga`, `.jpg`, `.jpeg`, `.bmp`, `.tif`, `.tiff`, `.exr`, or `.hdr`, including screenshots.|

No executable invocation, arbitrary destination root, timeout option, force unlock, reconciliation, or project switching is exposed.

## Result contract

```text
Succeeded: bool
Stage: validation | copy | registration | model_creation | compilation | dependency_inspection | complete
SourceAsset: string?
ModelAsset: string?
CopiedFiles: string[]
GeneratedFiles: string[]
SourceRegistered: bool
ModelRegistered: bool
Compilation:
  Status: not_started | observed | partial | unavailable
  IsCompiled: bool?
  IsCompiledAndUpToDate: bool?
  IsCompileFailed: bool?
  UnavailableEvidence: map<string,string>
DependencyInspection:
  Status: not_started | inspected | partial | unavailable
  InspectedAssets: string[]
  References: string[]
  InputDependencies: string[]
  AdditionalContentFiles: string[]
  UnavailableEvidence: map<string,string>
WriterState: not_started | unconfirmed | stopped
FurtherImportsBlocked: bool
PendingWriterPaths: string[]
UnresolvedReferences: string[]
Warnings: string[]
Error: { Code: string, Message: string }?
RollbackStatus: not_needed | complete | incomplete
PartialPaths: string[]
```

All handled failures return `ImportModelResult`; arrays and maps are present. Unknown booleans are null, never false. MCP schema nullability remains a separate installed generator issue: `SchemaForType` currently unwraps `Nullable<T>` and advertises only the underlying non-null type.

### Writer and lock semantics

`WriterState` is evidence:

- `not_started`: no engine mutation was attempted.
- `unconfirmed`: registration/model creation/compilation was attempted and installed APIs do not prove native writer shutdown.
- `stopped`: reserved for a future supported affirmative signal; synchronous v1 does not produce it.

`FurtherImportsBlocked` describes only the active MCP request lock. The lock serializes admitted import invocations and is released when server-side invocation handling finishes, after result accounting, on success or failure. It is never retained to represent native writer lifetime.

- A concurrently dispatched request returns `ImportBusy`, `FurtherImportsBlocked=true`, and performs no mutation.
- A completed success or failure returns `FurtherImportsBlocked=false`, even when `WriterState=unconfirmed`.
- If native code blocks the editor, another request may not be dispatched promptly.
- Caller disconnect does not cancel native work; release occurs when server-side invocation execution finishes.

`PendingWriterPaths` records exact known invocation-owned paths native work might still access. It remains populated after the request lock releases. It does not globally block fresh destinations.

Successful observed-state example:

```json
{
  "Succeeded": true,
  "Stage": "complete",
  "SourceAsset": "models/kampai/player/player.fbx",
  "ModelAsset": "models/kampai/player/player.vmdl",
  "CopiedFiles": ["models/kampai/player/player.fbx"],
  "GeneratedFiles": ["models/kampai/player/.sbox-mcp-import.json", "models/kampai/player/player.vmdl"],
  "SourceRegistered": true,
  "ModelRegistered": true,
  "Compilation": {
    "Status": "observed", "IsCompiled": true, "IsCompiledAndUpToDate": true, "IsCompileFailed": false,
    "UnavailableEvidence": {
      "OperationCompletion": "Installed asset-state APIs do not expose operation completion.",
      "WriterShutdown": "Installed asset-state APIs do not expose writer shutdown."
    }
  },
  "DependencyInspection": {
    "Status": "inspected", "InspectedAssets": [], "References": [],
    "InputDependencies": [], "AdditionalContentFiles": [],
    "UnavailableEvidence": {
      "ExhaustiveSourceDependencies": "Installed APIs do not expose exhaustive source dependencies.",
      "OptionalReferenceClassification": "Unrecognized references do not expose optionality.",
      "SourceMaterialPreservation": "The model helper may substitute materials/default.vmat."
    }
  },
  "WriterState": "unconfirmed",
  "FurtherImportsBlocked": false,
  "PendingWriterPaths": ["models/kampai/player", "models/kampai/player/player.fbx", "models/kampai/player/player.vmdl"],
  "UnresolvedReferences": [], "Warnings": [], "Error": null,
  "RollbackStatus": "not_needed", "PartialPaths": []
}
```

A post-engine failure retains all possible writer-accessible outputs, sets `RollbackStatus=incomplete`, copies retained paths into `PartialPaths`, reports `WriterState=unconfirmed`, and still releases the request lock. The original stage/error remains authoritative.
`UnresolvedReferences` is populated only from `GetUnrecognizedReferencePaths`, the installed API that explicitly reports unresolved asset references. Input-dependency and additional-content paths are retained as evidence even when their current disk path is absent: installed assets report optional `.meta`, provenance and source-authoring paths without an optionality or requiredness classification. Their absence is disclosed by the limited-coverage evidence and does not by itself fail an otherwise observed import.
Successful import does not guarantee that original material bindings were preserved. `CreateModelFromMeshFile` may bind `materials/default.vmat` even when sibling textures were copied; material creation, rebinding and provenance verification are separate follow-up work.



## Request serialization and historical evidence

One process-local nonblocking gate protects only active MCP import calls. Every admitted request disposes its lease in an unconditional `finally`; this is intentional under the narrowed lock definition.

Historical retained-import evidence is separate from the gate. It contains transaction identity, target directory, source/model paths, writer state and pending paths. It survives supported hotload transfer and can be reconstructed from `.sbox-mcp-import.json` markers. Historical records never set the active-request flag.

The legacy giant-lid retained gate may be migrated only after the original MCP invocation is known to have returned. Migration preserves its marker and assets byte-for-byte, records the directory as occupied and `WriterState=unconfirmed`, and clears only the obsolete request-lock state. This is not writer-shutdown evidence or reconciliation.

A failed/corrupt hotload transfer remains fail-closed for request execution until marker reconstruction succeeds. A normal versioned transfer preserves whether an active invocation existed; historical pending paths alone never become active-request state.

## Isolation and ownership

- Every invocation uses a new final directory. Existing directories always refuse.
- Any valid, stopped, unconfirmed, or malformed marker owns its directory. Reuse and nesting beneath it refuse.
- Reverse overlap with an existing owned directory refuses.
- Registered source/model evidence at an ownership boundary refuses.
- Fresh sibling directories are allowed even when earlier markers remain unconfirmed.
- Reject absolute, drive-relative, UNC, alternate-root, traversal, `Assets/`-prefixed, invalid-component, prefix-escape and reparse destinations.
- Preserve source/image filenames. Validate all names case-insensitively for collisions and Windows device names.
- Allow source plus at most 128 images and at most 1,073,741,824 selected bytes, equality included. Enforce bytes again while streaming.
- Reserve outputs exclusively; never adopt, overwrite or delete another actor's paths.
- Never save, close, reload, bind or mutate a scene.

Directory isolation limits direct output collision only. Engine-global caches, compiler queues and other native resources may overlap across completed invocations.

## Transaction and retention

1. Acquire the active-request gate nonblocking.
2. Capture and validate project/play state, source, destination, ownership, collisions and limits.
3. Reserve the directory and copy inputs exclusively.
4. Write `.sbox-mcp-import.json` with `Version=1`, GUID transaction ID, source/model asset paths and `WriterState=not_started`.
5. Immediately before first engine mutation, persist `WriterState=unconfirmed`.
6. Register images/source, create the model, observe compilation, and inspect supported dependency evidence on the editor context.
7. Return observed success only when registrations, all compile properties and selected inspections succeed with no unresolved references.
8. Record historical retained evidence and release the request lease in `finally`.

Before engine mutation, exclusively owned temporary files may be removed through their exact retained handles. Directories are retained when identity-bound deletion cannot be established and reported as incomplete cleanup.

After any engine mutation, perform no automatic rollback or deletion: no `Asset.Delete`, `File.Delete`, directory deletion, delayed cleanup, cancellation, timer, stopped-writer simulation or reconciliation. Retain outputs and report exact evidence. A test backend may simulate engine failures and ongoing background work, but production never treats simulation as writer-shutdown evidence.

Stable errors include `ImportBusy`, `InvalidInput`, `OverwriteUnsupported`, `DestinationExists`, `LimitExceeded`, `PlayMode`, `NoActiveProject`, `ApiUnavailable`, `CopyFailed`, `RegistrationFailed`, `ModelCreationFailed`, `CompileFailed`, `CompilationUnconfirmed`, `DependencyInspectionFailed`, and `UnresolvedDependencies`.

## Read-only skeleton evidence

`x_model_skeleton` resolves an asset, loads a non-error `Model`, and returns:

```text
ModelAsset: string
Bounds: { Space: "model", Mins: Vector3, Maxs: Vector3 }?
Bones: { Index: int, Name: string, ParentIndex: int }[]?
Attachments: { Name: string, BoneIndex: int? }[]?
MaterialGroups: { Index: int, Name: string, Materials: string[] }[]?
AnimationSequences: string[]?
HasSkinningData: bool?
UnavailableFields: map<string,string>
```

Complete-field inspection failure returns null with a bounded reason; inspected empty collections remain `[]`. Installed public APIs expose no direct skinning-presence query, so `HasSkinningData=null` with an explicit reason. Never infer skinning or gaze controls from bone count or filenames.

## Acceptance checks

1. README and registered tools match the revised request-lock contract; editor compilation has zero errors.
2. Concurrent active calls serialize; a concurrent dispatch receives `ImportBusy` without mutation.
3. Every completed invocation releases the request lock, including post-engine success and failure with `WriterState=unconfirmed`.
4. Same-directory, nested, reverse-overlap, marker-owned and registered-output collisions refuse; fresh siblings are admitted.
5. The existing giant-lid marker/assets remain byte-for-byte unchanged while its legacy gate becomes historical evidence after confirmed invocation completion.
6. Post-engine fault cases retain all outputs and never invoke asset/filesystem deletion.
7. Engine-backend faults cover registration, creation, compilation observation and dependency inspection while preserving stage/evidence.
8. Hotload tests cover versioned active state, historical records, malformed transfer and legacy retained-state migration.
9. Filesystem tests cover allowlist, limits, growing/partial copies, sentinels and ownership races.
10. Installed MCP imports giant-lid repeat refusal, then player, bartender, valid OBJ and documented valid DMX into fresh isolated directories.
11. Stock asset tools cross-check every successful model; limited dependency/material provenance remains explicit.
12. Skeleton inspection covers player and bartender plus missing lookup.
13. Active scene, open tabs and dirty flags remain unchanged around live batches.
14. Every completed live import reports `WriterState=unconfirmed`, `FurtherImportsBlocked=false` and complete pending paths.
15. Independent high-risk review approves request release, retention, overlap refusal, hotload migration and honest evidence.

## Fixtures

```text
D:/sbox/assets/blender-models-generator/output/giant-lid/giant-lid.fbx
D:/sbox/assets/blender-models-generator/output/player/player.fbx
D:/sbox/assets/blender-models-generator/output/bartender/bartender.fbx
```

Use their canonical destinations or first unused `-acceptance-N` siblings. Create a self-contained OBJ triangle externally. DMX requires a documented valid source or supported exporter; never invent its schema.

## Completion

Feature completion requires all revised checks and successful installed-editor FBX/OBJ/DMX imports. Kampai readiness separately requires actual player/bartender rig and eye-material evidence. Schema nullability remains a separately reported engine limitation and is not changed by this v1 behavior revision.
