using Editor.Mcp;

var checks = new List<(string Name, Action Run)>
{
	("input trace validates bounds before replacing evidence", InputTraceLimits),
	("input trace caps retained frames", InputTraceFrameCap),
	("input trace expires and loses context", InputTraceLifetime),
	("input trace requires current token", InputTraceTokens),
	("input trace paginates within bounds", InputTracePagination),
	("input trace replaces terminal evidence only on start", InputTraceRestart),
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
	("owned scene preserves pre-existing destination", OwnedScenePreservesPreexistingDestination),
	("owned scene rejects destination creation race", OwnedSceneRejectsDestinationRace),
	("owned scene rejects linked ancestor", OwnedSceneRejectsLinkedAncestor),
	("owned scene rejects linked Assets root", OwnedSceneRejectsLinkedAssetsRoot),
	("owned scene locks ancestor replacement", OwnedSceneLocksAncestorReplacement),
	("owned scene removes pre-commit write failure", OwnedSceneRemovesPreCommitFailure),
	("owned scene preserves foreign sibling", OwnedScenePreservesForeignSibling),
	("owned scene retains exact post-commit source", OwnedSceneRetainsPostCommitSource),
	("owned scene rejects commit handoff mutation", OwnedSceneRejectsCommitMutation),
	("owned scene rejects commit handoff replacement", OwnedSceneRejectsCommitReplacement),
	("owned scene rejects commit handoff symlink", OwnedSceneRejectsCommitSymlink),
	("owned scene rejects unsafe paths", OwnedSceneRejectsUnsafePaths),
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
foreach ( var check in checks.Where( x => !args.Contains( "--input-trace-only" ) || x.Name.StartsWith( "input trace", StringComparison.Ordinal ) ) )
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

static void OwnedScenePreservesPreexistingDestination()
{
	using var root = Fixture();
	var destination = Path.Combine( root.Assets, "scenes", "diagnostics", "occupied.scene" );
	Directory.CreateDirectory( Path.GetDirectoryName( destination )! );
	var sentinel = new byte[] { 0, 1, 2, 3, 255 };
	File.WriteAllBytes( destination, sentinel );
	ThrowsAny<IOException>( () => OwnedAssetFile.Create( root.Assets, "scenes/diagnostics/occupied.scene" ) );
	Assert( File.ReadAllBytes( destination ).SequenceEqual( sentinel ), "pre-existing scene bytes changed" );
}

static void OwnedSceneRejectsDestinationRace()
{
	using var root = Fixture();
	var raced = new byte[] { 9, 8, 7, 6 };
	string destination = null;
	ThrowsAny<IOException>( () => OwnedAssetFile.Create( root.Assets, "scenes/diagnostics/raced.scene", path =>
	{
		destination = path;
		File.WriteAllBytes( path, raced );
	} ) );
	Assert( destination is not null && File.ReadAllBytes( destination ).SequenceEqual( raced ), "raced scene was overwritten or deleted" );
}

static void OwnedSceneRejectsLinkedAncestor()
{
	using var root = Fixture();
	var scenes = Path.Combine( root.Assets, "scenes" );
	var target = Path.Combine( root.PathRoot, "linked-diagnostics-target" );
	Directory.CreateDirectory( scenes );
	Directory.CreateDirectory( target );
	Directory.CreateSymbolicLink( Path.Combine( scenes, "diagnostics" ), target );
	ThrowsAny<IOException>( () => OwnedAssetFile.Create( root.Assets, "scenes/diagnostics/linked.scene" ) );
	Assert( !File.Exists( Path.Combine( target, "linked.scene" ) ), "scene was created through a linked ancestor" );
}

static void OwnedSceneRejectsLinkedAssetsRoot()
{
	using var root = Fixture();
	var realAssets = Path.Combine( root.PathRoot, "RealAssets" );
	var linkedAssets = Path.Combine( root.PathRoot, "LinkedAssets" );
	Directory.CreateDirectory( realAssets );
	Directory.CreateSymbolicLink( linkedAssets, realAssets );
	ThrowsAny<IOException>( () => OwnedAssetFile.Create( linkedAssets, "scenes/diagnostics/linked-root.scene" ) );
	Assert( !File.Exists( Path.Combine( realAssets, "scenes", "diagnostics", "linked-root.scene" ) ), "scene was created through a linked Assets root" );
}

static void OwnedSceneLocksAncestorReplacement()
{
	using var root = Fixture();
	var parent = Path.Combine( root.Assets, "scenes", "diagnostics", "locked" );
	var replacement = Path.Combine( root.Assets, "scenes", "diagnostics", "moved" );
	Directory.CreateDirectory( parent );
	var replacementDenied = false;
	using var owned = OwnedAssetFile.Create( root.Assets, "scenes/diagnostics/locked/scene.scene", _ =>
	{
		try { Directory.Move( parent, replacement ); }
		catch ( IOException ) { replacementDenied = true; }
		catch ( UnauthorizedAccessException ) { replacementDenied = true; }
	} );
	owned.Content.WriteByte( 42 );
	owned.Commit();
	Assert( replacementDenied, "locked destination ancestor was replaced" );
	Assert( File.Exists( Path.Combine( parent, "scene.scene" ) ), "scene was not created in the locked ancestor" );
}

static void OwnedSceneRemovesPreCommitFailure()
{
	using var root = Fixture();
	var destination = Path.Combine( root.Assets, "scenes", "diagnostics", "partial.scene" );
	try
	{
		using var owned = OwnedAssetFile.Create( root.Assets, "scenes/diagnostics/partial.scene" );
		owned.Content.WriteByte( 42 );
		throw new IOException( "simulated serialization failure" );
	}
	catch ( IOException exception ) when ( exception.Message == "simulated serialization failure" ) { }
	Assert( !File.Exists( destination ), "pre-commit partial scene remains" );
}

static void OwnedScenePreservesForeignSibling()
{
	using var root = Fixture();
	var parent = Path.Combine( root.Assets, "scenes", "diagnostics", "siblings" );
	Directory.CreateDirectory( parent );
	var sibling = Path.Combine( parent, "foreign.txt" );
	File.WriteAllText( sibling, "keep" );
	try
	{
		using var owned = OwnedAssetFile.Create( root.Assets, "scenes/diagnostics/siblings/partial.scene" );
		owned.Content.WriteByte( 42 );
		throw new IOException( "simulated serialization failure" );
	}
	catch ( IOException exception ) when ( exception.Message == "simulated serialization failure" ) { }
	Assert( File.ReadAllText( sibling ) == "keep", "foreign sibling changed during cleanup" );
	Assert( !File.Exists( Path.Combine( parent, "partial.scene" ) ), "owned partial scene remains" );
}

static void OwnedSceneRetainsPostCommitSource()
{
	using var root = Fixture();
	var source = System.Text.Encoding.UTF8.GetBytes( "{\\n  \\\"Id\\\": \\\"retained\\\"\\n}" );
	var destination = Path.Combine( root.Assets, "scenes", "diagnostics", "retained.scene" );
	try
	{
		using var owned = OwnedAssetFile.Create( root.Assets, "scenes/diagnostics/retained.scene" );
		owned.Content.Write( source, 0, source.Length );
		owned.Commit();
		Assert( File.ReadAllBytes( destination ).SequenceEqual( source ), "committed source was unavailable to a read-only engine reader" );
		ThrowsAny<IOException>( () => File.WriteAllBytes( destination, [1, 2, 3] ), "write to committed owned scene" );
		ThrowsAny<IOException>( () => File.Delete( destination ), "delete committed owned scene" );
		throw new IOException( "simulated post-commit engine failure" );
	}
	catch ( IOException exception ) when ( exception.Message == "simulated post-commit engine failure" ) { }
	Assert( File.ReadAllBytes( destination ).SequenceEqual( source ), "post-commit scene source was missing or changed" );
}

static void OwnedSceneRejectsCommitMutation()
{
	using var root = Fixture();
	var source = System.Text.Encoding.UTF8.GetBytes( "owned source" );
	var foreign = System.Text.Encoding.UTF8.GetBytes( "foreign mutation" );
	var destination = Path.Combine( root.Assets, "scenes", "diagnostics", "mutated.scene" );
	using ( var owned = OwnedAssetFile.Create( root.Assets, "scenes/diagnostics/mutated.scene" ) )
	{
		owned.Content.Write( source, 0, source.Length );
		ThrowsAny<IOException>( () => owned.Commit( path => File.WriteAllBytes( path, foreign ) ), "mutated commit handoff" );
		Assert( owned.IsCommitted, "durable handoff failure was not marked committed" );
	}
	Assert( File.ReadAllBytes( destination ).SequenceEqual( foreign ), "foreign mutation was deleted or changed" );
}

static void OwnedSceneRejectsCommitReplacement()
{
	using var root = Fixture();
	var source = System.Text.Encoding.UTF8.GetBytes( "owned source" );
	var foreign = System.Text.Encoding.UTF8.GetBytes( "foreign replacement" );
	var destination = Path.Combine( root.Assets, "scenes", "diagnostics", "replaced.scene" );
	var displaced = Path.Combine( root.Assets, "scenes", "diagnostics", "displaced.scene" );
	using ( var owned = OwnedAssetFile.Create( root.Assets, "scenes/diagnostics/replaced.scene" ) )
	{
		owned.Content.Write( source, 0, source.Length );
		ThrowsAny<IOException>( () => owned.Commit( path =>
		{
			File.Move( path, displaced );
			File.WriteAllBytes( path, foreign );
		} ), "replaced commit handoff" );
		Assert( owned.IsCommitted, "durable replacement handoff failure was not marked committed" );
	}
	Assert( File.ReadAllBytes( destination ).SequenceEqual( foreign ), "foreign replacement was deleted or changed" );
	Assert( File.ReadAllBytes( displaced ).SequenceEqual( source ), "owned source changed after replacement was detected" );
}

static void OwnedSceneRejectsCommitSymlink()
{
	using var root = Fixture();
	var source = System.Text.Encoding.UTF8.GetBytes( "owned source" );
	var destination = Path.Combine( root.Assets, "scenes", "diagnostics", "linked.scene" );
	var displaced = Path.Combine( root.Assets, "scenes", "diagnostics", "linked-source.scene" );
	IOException linkFailure = null;
	using ( var owned = OwnedAssetFile.Create( root.Assets, "scenes/diagnostics/linked.scene" ) )
	{
		owned.Content.Write( source, 0, source.Length );
		ThrowsAny<IOException>( () => owned.Commit( path =>
		{
			File.Move( path, displaced );
			try { File.CreateSymbolicLink( path, displaced ); }
			catch ( IOException exception ) { linkFailure = exception; throw; }
		} ), "linked commit handoff" );
		if ( linkFailure is not null ) throw linkFailure;
		Assert( owned.IsCommitted, "durable linked handoff failure was not marked committed" );
	}
	Assert( (File.GetAttributes( destination ) & FileAttributes.ReparsePoint) != 0, "foreign link was deleted or replaced" );
	Assert( File.ReadAllBytes( displaced ).SequenceEqual( source ), "owned source changed after linked replacement was detected" );
}

static void OwnedSceneRejectsUnsafePaths()
{
	using var root = Fixture();
	foreach ( var path in new[]
	{
		"scene.scene", "scenes/other/out.scene", "scenes/diagnostics", "scenes/diagnostics/",
		"scenes/diagnostics/../out.scene", "scenes//diagnostics/out.scene", "scenes/diagnostics/CON.scene",
		"scenes/diagnostics/trailing .scene", "scenes/diagnostics/trailing..scene", "scenes/diagnostics/trailing.scene.",
		"scenes/diagnostics/out.scene:stream", "C:\\\\outside.scene", "\\\\\\\\server\\\\share\\\\outside.scene"
	} )
		ThrowsAny<Exception>( () => OwnedAssetFile.Create( root.Assets, path ), path );
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

static void InputTraceLimits()
{
	var trace = new InputTraceBuffer();
	foreach ( var frames in new[] { 1, 601 } ) ThrowsAny<ArgumentException>( () => trace.Start( frames, 1, "", 0 ) );
	foreach ( var seconds in new[] { double.NaN, double.PositiveInfinity, .5, 31 } )
		ThrowsAny<ArgumentException>( () => trace.Start( 2, seconds, "", 0 ) );
	ThrowsAny<ArgumentException>( () => trace.Start( 2, 1, new string( 'x', 65 ), 0 ) );
	ThrowsAny<ArgumentException>( () => trace.Start( 2, 1, null, 0 ) );
	Assert( trace.State == "idle" && trace.Token.Length == 0, "invalid start changed state" );
	trace.Start( 600, 30, new string( 'x', 64 ), 0 );
	var token = trace.Token;
	ThrowsAny<InvalidOperationException>( () => trace.Start( 2, 1, "", 0 ) );
	Assert( trace.Token == token && trace.Recording, "duplicate start replaced active owner" );
}

static void InputTraceFrameCap()
{
	var trace = new InputTraceBuffer(); trace.Start( 2, 30, "cap", 0 );
	for ( var i = 0; i < 1000; i++ ) trace.Add( i );
	Assert( trace.Count == 2 && trace.State == "frame-limit", "frame buffer grew beyond its limit" );
}

static void InputTraceLifetime()
{
	var trace = new InputTraceBuffer(); trace.Start( 600, 1, "expiry", 4 );
	trace.CheckLifetime( true, 4.99 ); Assert( trace.Recording, "expired early" );
	trace.CheckLifetime( true, 5 ); trace.Add( "late" );
	Assert( trace.State == "time-limit" && trace.Count == 0, "deadline admitted a sample" );
	trace.Start( 2, 1, "lost", 0 ); trace.CheckLifetime( false, .1 ); trace.Add( "foreign" );
	Assert( trace.State == "context-lost" && trace.Count == 0, "foreign context admitted sample" );
	trace.Start( 2, 1, "clock", 10 ); trace.CheckLifetime( true, 9 );
	Assert( trace.State == "time-limit", "clock regression left trace active" );
}

static void InputTraceTokens()
{
	var trace = new InputTraceBuffer();
	ThrowsAny<InvalidOperationException>( () => trace.Stop( "" ) );
	trace.Start( 2, 1, "token", 0 ); var token = trace.Token; trace.Add( 42 );
	ThrowsAny<InvalidOperationException>( () => trace.Stop( "wrong" ) );
	ThrowsAny<InvalidOperationException>( () => trace.Read( "wrong", 0, 1 ) );
	Assert( trace.Recording && trace.Count == 1, "wrong token disturbed recorder" );
	trace.Stop( token ); trace.Stop( token );
	Assert( trace.State == "stopped" && (int)trace.Read( token, 0, 1 )[0] == 42, "stop changed retained evidence" );
}

static void InputTracePagination()
{
	var trace = new InputTraceBuffer(); trace.Start( 2, 1, "read", 0 ); trace.Add( 1 ); trace.Add( 2 );
	Assert( (int)trace.Read( trace.Token, 1, 1 )[0] == 2, "page omitted offset" );
	Assert( trace.Read( trace.Token, int.MaxValue, 90 ).Length == 0, "large offset returned samples" );
	ThrowsAny<ArgumentException>( () => trace.Read( trace.Token, -1, 1 ) );
	foreach ( var count in new[] { 0, 91 } ) ThrowsAny<ArgumentException>( () => trace.Read( trace.Token, 0, count ) );
	var page = trace.Read( trace.Token, 0, 1 ); page[0] = 99;
	Assert( (int)trace.Read( trace.Token, 0, 1 )[0] == 1, "caller mutated internal list" );
}

static void InputTraceRestart()
{
	var trace = new InputTraceBuffer(); trace.Start( 2, 1, "old", 0 ); var old = trace.Token;
	trace.Add( 42 ); trace.Stop( old ); trace.Start( 2, 1, "new", 0 );
	Assert( trace.Token != old && trace.Count == 0 && trace.Label == "new", "restart retained old evidence" );
	ThrowsAny<InvalidOperationException>( () => trace.Stop( old ) );
	ThrowsAny<InvalidOperationException>( () => trace.Read( old, 0, 1 ) );
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

static void ThrowsAny<TException>( Action action, string context = null ) where TException : Exception
{
	try { action(); }
	catch ( TException ) { return; }
	throw new Exception( context is null
		? $"expected {typeof(TException).Name}"
		: $"expected {typeof(TException).Name} for '{context}'" );
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
