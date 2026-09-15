using Editor.Mcp;

var checks = new List<(string Name, Action Run)>
{
	("selects exact immediate allowlist", SelectsAllowlist),
	("false skips sibling enumeration", SkipsSiblings),
	("refuses 129 sibling images", RefusesImageOverflow),
	("accepts exact byte boundary", AcceptsExactByteBoundary),
	("refuses byte overflow", RefusesByteOverflow),
	("rejects unsafe targets and names", RejectsUnsafeInputs),
	("exclusive reservation preserves sentinel", PreservesSentinel),
	("copy race preserves foreign file", PreservesRacedCopy),
	("marker race preserves foreign marker", PreservesRacedMarker),
	("pre-engine cleanup retains owned directories safely", RetainsOwnedDirectories),
	("request gate serializes and releases", RequestGateSerializesAndReleases),
	("post-engine completion releases request gate", PostEngineCompletionReleasesGate),
	("registration failure is staged", RegistrationFailureIsStaged),
	("compilation fault preserves evidence", CompilationFaultPreservesEvidence),
	("dependency fault preserves earlier evidence", DependencyFaultPreservesEvidence),
	("legacy retained state migrates without active lock", LegacyStateMigrates),
	("versioned active request survives hotload", ActiveRequestSurvivesHotload),
	("malformed hotload state fails closed", MalformedHotloadFails),
	("post-engine transaction retains outputs", PostEngineTransactionRetainsOutputs),
	("active lease releases shared hotload gate", ActiveLeaseReleasesSharedGate),
	("thrown dependency path preserves evidence", ThrownDependencyPathPreservesEvidence),
	("missing dependency path fails inspection", MissingDependencyPathFailsInspection)
};
var failures = new List<string>();
foreach ( var check in checks )
{
	try { check.Run(); Console.WriteLine( $"PASS {check.Name}" ); }
	catch ( Exception ex ) { failures.Add( $"FAIL {check.Name}: {ex.Message}" ); }
}
foreach ( var failure in failures ) Console.Error.WriteLine( failure );
return failures.Count == 0 ? 0 : 1;

static void SelectsAllowlist()
{
	using var root = Fixture();
	File.WriteAllText( Path.Combine( root.Source, "mesh.OBJ" ), "v 0 0 0\nv 1 0 0\nv 0 1 0\nf 1 2 3" );
	File.WriteAllText( Path.Combine( root.Source, "shot.PNG" ), "x" );
	File.WriteAllText( Path.Combine( root.Source, "mesh.mtl" ), "ignored" );
	Directory.CreateDirectory( Path.Combine( root.Source, "nested" ) );
	File.WriteAllText( Path.Combine( root.Source, "nested", "nested.png" ), "ignored" );
	var plan = ModelImportTransaction.Plan( root.Assets, Path.Combine( root.Source, "mesh.OBJ" ), "models/test", "", true );
	Equal( new[] { "models/test/mesh.OBJ", "models/test/shot.PNG" }, plan.Copies.Select( x => x.DestinationAsset ).ToArray() );
}

static void SkipsSiblings()
{
	using var root = Fixture();
	var source = Path.Combine( root.Source, "mesh.fbx" ); File.WriteAllText( source, "mesh" );
	File.WriteAllText( Path.Combine( root.Source, "image.png" ), "x" );
	var plan = ModelImportTransaction.Plan( root.Assets, source, "models/solo", null, false );
	Equal( new[] { "models/solo/mesh.fbx" }, plan.Copies.Select( x => x.DestinationAsset ).ToArray() );
}

static void RefusesImageOverflow()
{
	using var root = Fixture();
	var source = Path.Combine( root.Source, "mesh.dmx" ); File.WriteAllText( source, "mesh" );
	for ( var i = 0; i < 129; ++i ) File.WriteAllText( Path.Combine( root.Source, $"{i:D3}.png" ), "" );
	Throws( "LimitExceeded", () => ModelImportTransaction.Plan( root.Assets, source, "models/count", "", true ) );
}

static void AcceptsExactByteBoundary()
{
	using var root = Fixture();
	var source = Path.Combine( root.Source, "mesh.fbx" );
	using ( var stream = File.Create( source ) ) stream.SetLength( ModelImportTransaction.MaximumBytes );
	var plan = ModelImportTransaction.Plan( root.Assets, source, "models/exact", "", false );
	Assert( plan.Copies.Single().PreflightLength == ModelImportTransaction.MaximumBytes, "exact byte limit was not accepted" );
}

static void RefusesByteOverflow()
{
	using var root = Fixture();
	var source = Path.Combine( root.Source, "mesh.fbx" );
	using ( var stream = File.Create( source ) ) stream.SetLength( ModelImportTransaction.MaximumBytes + 1 );
	Throws( "LimitExceeded", () => ModelImportTransaction.Plan( root.Assets, source, "models/over", "", false ) );
}

static void RejectsUnsafeInputs()
{
	using var root = Fixture();
	var source = Path.Combine( root.Source, "mesh.fbx" ); File.WriteAllText( source, "mesh" );
	foreach ( var target in new[] { "../escape", "Assets/models/x", "models//x", "C:\\outside", "/outside" } )
		Throws( "InvalidInput", () => ModelImportTransaction.Plan( root.Assets, source, target, "", false ) );
	foreach ( var name in new[] { "CON", "bad.vmdl", " padded", "trailing." } )
		Throws( "InvalidInput", () => ModelImportTransaction.Plan( root.Assets, source, "models/name", name, false ) );
}

static void PreservesSentinel()
{
	using var root = Fixture();
	var source = Path.Combine( root.Source, "mesh.obj" ); File.WriteAllText( source, "mesh" );
	var occupied = Path.Combine( root.Assets, "models", "occupied" ); Directory.CreateDirectory( occupied );
	var sentinel = Path.Combine( occupied, "sentinel.txt" ); File.WriteAllText( sentinel, "keep" );
	Throws( "DestinationExists", () => ModelImportTransaction.Plan( root.Assets, source, "models/occupied", "", false ) );
	Assert( File.ReadAllText( sentinel ) == "keep", "sentinel changed" );
}

static void PreservesRacedCopy()
{
	using var root = Fixture();
	var source = Path.Combine( root.Source, "mesh.obj" ); File.WriteAllText( source, "source" );
	var plan = ModelImportTransaction.Plan( root.Assets, source, "models/raced-copy", "", false );
	plan.ReserveDirectory();
	var destination = plan.Copies.Single().DestinationAbsolute;
	File.WriteAllText( destination, "foreign" );
	Throws( "CopyFailed", () => plan.CopyInputs() );
	var remaining = plan.CleanupPreEngine();
	Assert( File.ReadAllText( destination ) == "foreign", "foreign raced file was deleted" );
	Assert( remaining.Contains( "models/raced-copy", StringComparer.OrdinalIgnoreCase ), "occupied owned directory was not reported" );
}

static void PreservesRacedMarker()
{
	using var root = Fixture();
	var source = Path.Combine( root.Source, "mesh.obj" ); File.WriteAllText( source, "source" );
	var plan = ModelImportTransaction.Plan( root.Assets, source, "models/raced-marker", "", false );
	plan.ReserveDirectory(); plan.CopyInputs();
	var marker = Path.Combine( plan.FinalDirectory, ModelImportTransaction.MarkerName );
	File.WriteAllText( marker, "foreign" );
	try { plan.WriteMarker( "not_started" ); } catch ( IOException ) { }
	var remaining = plan.CleanupPreEngine();
	Assert( File.ReadAllText( marker ) == "foreign", "foreign marker was overwritten or deleted" );
	Assert( remaining.Contains( "models/raced-marker", StringComparer.OrdinalIgnoreCase ), "occupied marker directory was not reported" );
}

static void RetainsOwnedDirectories()
{
	using var root = Fixture();
	var source = Path.Combine( root.Source, "mesh.obj" ); File.WriteAllText( source, "mesh" );
	var plan = ModelImportTransaction.Plan( root.Assets, source, "models/cleanup", "", false );
	plan.ReserveDirectory(); plan.CopyInputs(); plan.WriteMarker( "not_started" );
	var remaining = plan.CleanupPreEngine();
	Assert( remaining.Contains( "models/cleanup", StringComparer.OrdinalIgnoreCase ), "retained final directory was not reported" );
	Assert( !File.Exists( Path.Combine( plan.FinalDirectory, "mesh.obj" ) ), "owned copied file remains" );
	Assert( !File.Exists( Path.Combine( plan.FinalDirectory, ModelImportTransaction.MarkerName ) ), "owned marker remains" );
}

static void RequestGateSerializesAndReleases()
{
	var gate = new ModelImportRequestGate();
	using var first = gate.TryEnter();
	Assert( first is not null && gate.IsActive, "first request did not acquire the gate" );
	Assert( gate.TryEnter() is null, "concurrent request was admitted" );
	first.Dispose();
	using var second = gate.TryEnter();
	Assert( second is not null, "gate did not release after invocation completion" );
}

static void PostEngineCompletionReleasesGate()
{
	var gate = new ModelImportRequestGate();
	var lease = gate.TryEnter();
	var writerState = "unconfirmed";
	var pending = new[] { "models/a", "models/a/a.vmdl" };
	lease.Dispose();
	Assert( !gate.IsActive, "unconfirmed writer evidence retained the request gate" );
	Assert( writerState == "unconfirmed" && pending.Length == 2, "retained evidence changed when the request released" );
}

static void RegistrationFailureIsStaged()
{
	var backend = new FakeBackend { ThrowRegister = true };
	var pipeline = new ModelImportBackendPipeline( backend );
	Throws( "RegistrationFailed", () => pipeline.RegisterSource( "source.fbx", "models/a/source.fbx" ) );
}

static void CompilationFaultPreservesEvidence()
{
	var backend = new FakeBackend { IsCompiled = false, IsUpToDate = false, IsFailed = false, ThrowCompileObservation = true };
	var pipeline = new ModelImportBackendPipeline( backend );
	try { pipeline.ObserveCompilationAsync( backend.Model ).AsTask().GetAwaiter().GetResult(); }
	catch ( BackendPipelineException ex )
	{
		var evidence = (BackendCompilationEvidence)ex.Evidence;
		Assert( ex.Code == "CompilationUnconfirmed", "wrong compilation fault code" );
		Assert( evidence.Status == "observed" && evidence.IsCompiled == false && evidence.IsCompileFailed == false, "compile evidence was discarded" );
		return;
	}
	throw new Exception( "expected compilation fault" );
}

static void DependencyFaultPreservesEvidence()
{
	var backend = new FakeBackend { ThrowInputDependencies = true };
	backend.References[backend.Source] = [backend.Material];
	var pipeline = new ModelImportBackendPipeline( backend );
	try { pipeline.InspectDependencies( backend.Source, backend.Model ); }
	catch ( BackendPipelineException ex )
	{
		var evidence = (BackendDependencyEvidence)ex.Evidence;
		Assert( ex.Code == "DependencyInspectionFailed", "wrong dependency fault code" );
		Assert( evidence.References.Contains( "materials/test.vmat" ), "successful reference evidence was discarded" );
		Assert( evidence.UnavailableEvidence.Keys.Any( x => x.StartsWith( "InputDependencies:", StringComparison.Ordinal ) ), "failed query was not identified" );
		return;
	}
	throw new Exception( "expected dependency inspection fault" );
}

static void LegacyStateMigrates()
{
	var state = new Dictionary<string, object>
	{
		["ModelImportBusy"] = true,
		["ModelImportPending"] = new[] { "models/kampai/giant-lid" },
		["ModelImportOwnedDirectories"] = new[] { "models/kampai/giant-lid" }
	};
	var snapshot = ModelImportHotloadTransfer.Read( state, legacyInvocationFinished: true );
	Assert( !snapshot.ActiveRequest, "completed legacy invocation retained the revised request lock" );
	Assert( snapshot.PendingPaths.Single() == "models/kampai/giant-lid", "legacy pending evidence was lost" );
}

static void ActiveRequestSurvivesHotload()
{
	var gate = new ModelImportRequestGate();
	using var lease = gate.TryEnter();
	var state = new Dictionary<string, object>();
	ModelImportHotloadTransfer.Write( state, gate, new[] { "models/a" }, new[] { "models/a" } );
	var snapshot = ModelImportHotloadTransfer.Read( state, legacyInvocationFinished: false );
	Assert( snapshot.Version == 4 && snapshot.ActiveRequest, "versioned active request was unlocked" );
}

static void MalformedHotloadFails()
{
	try { ModelImportHotloadTransfer.Read( new Dictionary<string, object>(), legacyInvocationFinished: false ); }
	catch ( ImportContractException ex ) when ( ex.Code == "ImportBusy" ) { return; }
	throw new Exception( "malformed hotload state did not fail closed" );
}

static void PostEngineTransactionRetainsOutputs()
{
	using var root = Fixture();
	var source = Path.Combine( root.Source, "mesh.obj" ); File.WriteAllText( source, "mesh" );
	var plan = ModelImportTransaction.Plan( root.Assets, source, "models/retained", "", false );
	plan.ReserveDirectory(); plan.CopyInputs(); plan.WriteMarker( "unconfirmed" );
	plan.PrepareForEngineMutation();
	plan.EngineMutationAttempted = true;
	plan.Dispose();
	Assert( File.Exists( Path.Combine( plan.FinalDirectory, "mesh.obj" ) ), "post-engine source was deleted" );
	Assert( File.Exists( Path.Combine( plan.FinalDirectory, ModelImportTransaction.MarkerName ) ), "post-engine marker was deleted" );
}

static void ActiveLeaseReleasesSharedGate()
{
	var oldGate = new ModelImportRequestGate();
	var lease = oldGate.TryEnter();
	var newGate = new ModelImportRequestGate( oldGate.State );
	Assert( newGate.IsActive, "replacement gate did not share active state" );
	lease.Dispose();
	Assert( !newGate.IsActive && newGate.TryEnter() is not null, "old invocation completion did not release replacement gate" );
}

static void ThrownDependencyPathPreservesEvidence()
{
	var backend = new FakeBackend { ThrowMaterialPath = true };
	backend.References[backend.Source] = [backend.Material];
	var pipeline = new ModelImportBackendPipeline( backend );
	try { pipeline.InspectDependencies( backend.Source, backend.Model ); }
	catch ( BackendPipelineException ex )
	{
		var evidence = (BackendDependencyEvidence)ex.Evidence;
		Assert( ex.Code == "DependencyInspectionFailed", "thrown path used wrong error" );
		Assert( evidence.InspectedAssets.Contains( "models/a/model.vmdl" ), "evidence from another completed asset was discarded" );
		Assert( evidence.UnavailableEvidence.Keys.Any( x => x.StartsWith( "ReferencePath:", StringComparison.Ordinal ) ), "thrown reference path was not identified" );
		return;
	}
	throw new Exception( "expected thrown path inspection failure" );
}

static void MissingDependencyPathFailsInspection()
{
	var backend = new FakeBackend { MissingMaterialPath = true };
	backend.References[backend.Source] = [backend.Material];
	var pipeline = new ModelImportBackendPipeline( backend );
	try { pipeline.InspectDependencies( backend.Source, backend.Model ); }
	catch ( BackendPipelineException ex )
	{
		var evidence = (BackendDependencyEvidence)ex.Evidence;
		Assert( ex.Code == "DependencyInspectionFailed", "missing path used wrong error" );
		Assert( evidence.UnavailableEvidence.Keys.Any( x => x.StartsWith( "ReferencePath:", StringComparison.Ordinal ) ), "missing reference path was not identified" );
		return;
	}
	throw new Exception( "expected missing path inspection failure" );
}

static Root Fixture() => new();
static void Assert( bool condition, string message ) { if ( !condition ) throw new Exception( message ); }
static void Equal( string[] expected, string[] actual ) => Assert( expected.SequenceEqual( actual, StringComparer.OrdinalIgnoreCase ), $"expected [{string.Join(",", expected)}], got [{string.Join(",", actual)}]" );
static void Throws( string code, Action action )
{
	try { action(); }
	catch ( ImportContractException ex ) when ( ex.Code == code ) { return; }
	throw new Exception( $"expected {code}" );
}

sealed class Root : IDisposable
{
	public string PathRoot { get; } = Path.Combine( Path.GetTempPath(), "sbox-model-import-contract", Guid.NewGuid().ToString( "N" ) );
	public string Assets => Path.Combine( PathRoot, "Assets" );
	public string Source => Path.Combine( PathRoot, "Source" );
	public Root() { Directory.CreateDirectory( Assets ); Directory.CreateDirectory( Source ); }
	public void Dispose() { try { Directory.Delete( PathRoot, true ); } catch { } }
}

sealed class FakeBackend : IModelImportBackend
{
	internal object Source { get; } = new();
	internal object Model { get; } = new();
	internal object Material { get; } = new();
	internal Dictionary<object, List<object>> References { get; } = [];
	internal bool ThrowRegister { get; set; }
	internal bool ThrowCompileObservation { get; set; }
	internal bool ThrowInputDependencies { get; set; }
	internal bool ThrowMaterialPath { get; set; }
	internal bool MissingMaterialPath { get; set; }
	internal bool IsCompiled { get; set; } = true;
	internal bool IsUpToDate { get; set; } = true;
	internal bool IsFailed { get; set; }
	public object RegisterFile( string absolutePath )
	{
		if ( ThrowRegister ) throw new IOException( "registration fault" );
		return Source;
	}
	public object FindAsset( string assetPath ) => Model;
	public string GetAssetPath( object asset )
	{
		if ( ReferenceEquals( asset, Material ) && ThrowMaterialPath ) throw new IOException( "asset path fault" );
		if ( ReferenceEquals( asset, Material ) && MissingMaterialPath ) return null;
		return ReferenceEquals( asset, Source ) ? "models/a/source.fbx" :
			ReferenceEquals( asset, Material ) ? "materials/test.vmat" : "models/a/model.vmdl";
	}
	public object CreateModel( object sourceAsset, string absoluteModelPath ) => Model;
	public bool ReadIsCompiled( object asset ) => IsCompiled;
	public bool ReadIsCompiledAndUpToDate( object asset ) => IsUpToDate;
	public bool ReadIsCompileFailed( object asset ) => IsFailed;
	public ValueTask ObserveCompilationIfNeededAsync( object asset ) => ThrowCompileObservation
		? ValueTask.FromException( new IOException( "compile observation fault" ) ) : ValueTask.CompletedTask;
	public IReadOnlyList<object> GetReferences( object asset ) => References.TryGetValue( asset, out var values ) ? values : [];
	public IReadOnlyList<string> GetUnrecognizedReferences( object asset ) => [];
	public IReadOnlyList<string> GetInputDependencies( object asset )
	{
		if ( ThrowInputDependencies ) throw new IOException( "input dependency fault" );
		return [];
	}
	public IReadOnlyList<string> GetAdditionalContentFiles( object asset ) => [];
}
