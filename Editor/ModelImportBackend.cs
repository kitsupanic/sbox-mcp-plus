using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;

namespace Editor.Mcp;

internal interface IModelImportBackend
{
	object RegisterFile( string absolutePath );
	object FindAsset( string assetPath );
	string GetAssetPath( object asset );
	object CreateModel( object sourceAsset, string absoluteModelPath );
	bool ReadIsCompiled( object asset );
	bool ReadIsCompiledAndUpToDate( object asset );
	bool ReadIsCompileFailed( object asset );
	ValueTask ObserveCompilationIfNeededAsync( object asset );
	IReadOnlyList<object> GetReferences( object asset );
	IReadOnlyList<string> GetUnrecognizedReferences( object asset );
	IReadOnlyList<string> GetInputDependencies( object asset );
	IReadOnlyList<string> GetAdditionalContentFiles( object asset );
}

internal sealed class ModelImportBackendPipeline
{
	private readonly IModelImportBackend _backend;
	internal ModelImportBackendPipeline( IModelImportBackend backend ) => _backend = backend;

	internal object RegisterDependency( string absolutePath, string expectedPath )
	{
		try
		{
			var asset = _backend.RegisterFile( absolutePath );
			if ( asset is null )
				throw new ImportContractException( "RegistrationFailed", $"Failed to register '{expectedPath}'." );
			return asset;
		}
		catch ( ImportContractException ) { throw; }
		catch ( Exception ex ) { throw new ImportContractException( "RegistrationFailed", $"Registration failed for '{expectedPath}': {Safe( ex.Message, 700 )}" ); }
	}

	internal object RegisterSource( string absolutePath, string expectedPath )
	{
		var asset = RegisterDependency( absolutePath, expectedPath );
		if ( !SamePath( asset, expectedPath ) )
			throw new ImportContractException( "RegistrationFailed", $"The source was not registered at '{expectedPath}'." );
		return asset;
	}

	internal object CreateModel( object source, string absolutePath, string expectedPath )
	{
		try
		{
			var created = _backend.CreateModel( source, absolutePath );
			var resolved = _backend.FindAsset( expectedPath );
			if ( created is null || resolved is null || !SamePath( resolved, expectedPath ) )
				throw new ImportContractException( "ModelCreationFailed", "The generated model could not be confirmed at its planned asset path." );
			return resolved;
		}
		catch ( ImportContractException ) { throw; }
		catch ( Exception ex ) { throw new ImportContractException( "ModelCreationFailed", $"Model creation failed: {Safe( ex.Message, 700 )}" ); }
	}

	internal async ValueTask<BackendCompilationEvidence> ObserveCompilationAsync( object model )
	{
		var result = new BackendCompilationEvidence();
		result.UnavailableEvidence["OperationCompletion"] = "Installed asset-state APIs do not expose operation completion.";
		result.UnavailableEvidence["WriterShutdown"] = "Installed asset-state APIs do not expose writer shutdown.";
		Exception helperError = null;
		if ( TryRead( () => _backend.ReadIsCompiled( model ), out var initiallyCompiled, result, "IsCompiled" ) && initiallyCompiled == false )
		{
			try { await _backend.ObserveCompilationIfNeededAsync( model ); }
			catch ( Exception ex ) { helperError = ex; }
		}
		TryRead( () => _backend.ReadIsCompiled( model ), out var isCompiled, result, "IsCompiled" );
		TryRead( () => _backend.ReadIsCompiledAndUpToDate( model ), out var isUpToDate, result, "IsCompiledAndUpToDate" );
		TryRead( () => _backend.ReadIsCompileFailed( model ), out var isFailed, result, "IsCompileFailed" );
		result.IsCompiled = isCompiled;
		result.IsCompiledAndUpToDate = isUpToDate;
		result.IsCompileFailed = isFailed;
		var missing = result.UnavailableEvidence.Keys.Count( x => x is "IsCompiled" or "IsCompiledAndUpToDate" or "IsCompileFailed" );
		result.Status = missing == 0 ? "observed" : missing == 3 ? "unavailable" : "partial";
		if ( result.IsCompileFailed == true ) throw new BackendPipelineException( "CompileFailed", "The model compiler reported failure.", result );
		if ( helperError is not null ) throw new BackendPipelineException( "CompilationUnconfirmed", $"Compilation observation failed: {Safe( helperError.Message, 900 )}", result );
		if ( result.Status != "observed" || result.IsCompiled != true || result.IsCompiledAndUpToDate != true || result.IsCompileFailed != false )
			throw new BackendPipelineException( "CompilationUnconfirmed", "Successful compilation could not be confirmed from all installed asset-state properties.", result );
		return result;
	}

	internal BackendDependencyEvidence InspectDependencies( object source, object model )
	{
		var result = new BackendDependencyEvidence();
		result.UnavailableEvidence["ExhaustiveSourceDependencies"] = "Installed APIs do not expose exhaustive source dependencies.";
		result.UnavailableEvidence["OptionalReferenceClassification"] = "Unrecognized references do not expose optionality.";
		result.UnavailableEvidence["SourceMaterialPreservation"] = "The model helper may substitute materials/default.vmat.";
		var pending = new Queue<object>( [source, model] );
		var seen = new HashSet<string>( StringComparer.OrdinalIgnoreCase );
		var failed = false;
		while ( pending.Count > 0 )
		{
			var asset = pending.Dequeue();
			string path;
			try { path = _backend.GetAssetPath( asset ); }
			catch ( Exception ex )
			{
				RecordUnavailable( result, $"Asset#{seen.Count}", "AssetPath", ex );
				failed = true;
				continue;
			}
			if ( string.IsNullOrWhiteSpace( path ) )
			{
				result.UnavailableEvidence[$"AssetPath:Asset#{seen.Count}"] = "The backend returned no asset path.";
				failed = true;
				continue;
			}
			if ( !seen.Add( path ) ) continue;
			var assetFailed = false;
			IReadOnlyList<string> inputs = [], additional = [];
			try
			{
				foreach ( var reference in _backend.GetReferences( asset ) ?? [] )
				{
					pending.Enqueue( reference );
					try
					{
						var referencePath = _backend.GetAssetPath( reference );
						if ( string.IsNullOrWhiteSpace( referencePath ) ) throw new Exception( "The backend returned no referenced asset path." );
						result.References.Add( referencePath );
					}
					catch ( Exception ex ) { RecordUnavailable( result, path, "ReferencePath", ex ); assetFailed = true; }
				}
			}
			catch ( Exception ex ) { RecordUnavailable( result, path, "References", ex ); assetFailed = true; }
			try { result.UnresolvedReferences.AddRange( _backend.GetUnrecognizedReferences( asset ) ?? [] ); }
			catch ( Exception ex ) { RecordUnavailable( result, path, "UnrecognizedReferences", ex ); assetFailed = true; }
			try { inputs = _backend.GetInputDependencies( asset ) ?? []; result.InputDependencies.AddRange( inputs ); }
			catch ( Exception ex ) { RecordUnavailable( result, path, "InputDependencies", ex ); assetFailed = true; }
			try { additional = _backend.GetAdditionalContentFiles( asset ) ?? []; result.AdditionalContentFiles.AddRange( additional ); }
			catch ( Exception ex ) { RecordUnavailable( result, path, "AdditionalContentFiles", ex ); assetFailed = true; }
			if ( assetFailed ) failed = true;
			else result.InspectedAssets.Add( path );
		}
		Normalize( result.InspectedAssets ); Normalize( result.References ); Normalize( result.InputDependencies );
		Normalize( result.AdditionalContentFiles ); Normalize( result.UnresolvedReferences );
		result.Status = failed ? result.InspectedAssets.Count == 0 ? "unavailable" : "partial" : "inspected";
		if ( result.Status != "inspected" ) throw new BackendPipelineException( "DependencyInspectionFailed", "The promised dependency inspection could not be completed.", result );
		if ( result.UnresolvedReferences.Count > 0 ) throw new BackendPipelineException( "UnresolvedDependencies", "One or more reported references could not be resolved; installed APIs do not expose optionality.", result );
		return result;
	}

	private bool SamePath( object asset, string expected ) => string.Equals( _backend.GetAssetPath( asset )?.Replace( '\\', '/' ), expected, StringComparison.OrdinalIgnoreCase );
	private static bool TryRead( Func<bool> read, out bool? value, BackendCompilationEvidence evidence, string key )
	{
		try { value = read(); evidence.UnavailableEvidence.Remove( key ); return true; }
		catch ( Exception ex ) { value = null; evidence.UnavailableEvidence[key] = Safe( ex.Message, 256 ); return false; }
	}
	private static void RecordUnavailable( BackendDependencyEvidence evidence, string asset, string query, Exception ex ) =>
		evidence.UnavailableEvidence[$"{query}:{Safe( asset, 150 )}"] = Safe( ex.Message, 256 );
	private static void Normalize( List<string> paths )
	{
		var values = paths.Where( x => !string.IsNullOrWhiteSpace( x ) ).Select( x => x.Replace( '\\', '/' ) )
			.Distinct( StringComparer.OrdinalIgnoreCase ).OrderBy( x => x, StringComparer.OrdinalIgnoreCase ).ToArray();
		paths.Clear(); paths.AddRange( values );
	}
	private static string Safe( string value, int maximum )
	{
		var text = string.IsNullOrWhiteSpace( value ) ? "The operation failed without a message." : value.Replace( '\r', ' ' ).Replace( '\n', ' ' );
		return text.Length <= maximum ? text : text[..maximum];
	}
}

internal sealed class BackendCompilationEvidence
{
	internal string Status { get; set; } = "not_started";
	internal bool? IsCompiled { get; set; }
	internal bool? IsCompiledAndUpToDate { get; set; }
	internal bool? IsCompileFailed { get; set; }
	internal Dictionary<string, string> UnavailableEvidence { get; } = new( StringComparer.Ordinal );
}

internal sealed class BackendDependencyEvidence
{
	internal string Status { get; set; } = "not_started";
	internal List<string> InspectedAssets { get; } = [];
	internal List<string> References { get; } = [];
	internal List<string> InputDependencies { get; } = [];
	internal List<string> AdditionalContentFiles { get; } = [];
	internal List<string> UnresolvedReferences { get; } = [];
	internal Dictionary<string, string> UnavailableEvidence { get; } = new( StringComparer.Ordinal );
}

internal sealed class BackendPipelineException : ImportContractException
{
	internal object Evidence { get; }
	internal BackendPipelineException( string code, string message, object evidence ) : base( code, message ) => Evidence = evidence;
}

internal sealed class ModelImportRequestGate
{
	private StrongBox<long> _active;
	internal ModelImportRequestGate() : this( new StrongBox<long>( 0 ) ) { }
	internal ModelImportRequestGate( StrongBox<long> active ) => _active = active ?? throw new ArgumentNullException( nameof( active ) );
	private StrongBox<long> Active => _active ??= new StrongBox<long>( 0 );
	internal StrongBox<long> State => Active;
	internal bool IsActive => System.Threading.Volatile.Read( ref Active.Value ) != 0;
	internal Lease TryEnter()
	{
		var state = Active;
		return System.Threading.Interlocked.CompareExchange( ref state.Value, 1, 0 ) == 0 ? new Lease( state ) : null;
	}
	internal void Restore( bool active ) => System.Threading.Volatile.Write( ref Active.Value, active ? 1 : 0 );

	internal sealed class Lease : IDisposable
	{
		private StrongBox<long> _state;
		internal Lease( StrongBox<long> state ) => _state = state;
		public void Dispose()
		{
			var state = System.Threading.Interlocked.Exchange( ref _state, null );
			if ( state is not null ) System.Threading.Volatile.Write( ref state.Value, 0 );
		}
	}
}
internal sealed record ModelImportHotloadSnapshot(
	int Version, bool ActiveRequest, string[] PendingPaths, string[] OwnedDirectories );

internal static class ModelImportHotloadTransfer
{
	internal static ModelImportHotloadSnapshot Read( IReadOnlyDictionary<string, object> state, bool legacyInvocationFinished )
	{
		var pendingValid = state.TryGetValue( "ModelImportPending", out var pendingValue ) && pendingValue is IEnumerable<string>;
		var ownedValid = state.TryGetValue( "ModelImportOwnedDirectories", out var ownedValue ) && ownedValue is IEnumerable<string>;
		var busyValid = state.TryGetValue( "ModelImportBusy", out var busyValue ) && busyValue is bool;
		if ( !pendingValid || !ownedValid || !busyValid )
			throw new ImportContractException( "ImportBusy", "Model import hotload state transfer was incomplete." );
		var version = state.TryGetValue( "ModelImportStateVersion", out var versionValue ) && versionValue is int parsed ? parsed : 1;
		var active = version >= 4 ? (bool)busyValue : !legacyInvocationFinished && (bool)busyValue;
		return new ModelImportHotloadSnapshot( version, active,
			((IEnumerable<string>)pendingValue).Distinct( StringComparer.OrdinalIgnoreCase ).ToArray(),
			((IEnumerable<string>)ownedValue).Distinct( StringComparer.OrdinalIgnoreCase ).ToArray() );
	}

	internal static void Write( Dictionary<string, object> state, ModelImportRequestGate gate, IEnumerable<string> pending, IEnumerable<string> owned )
	{
		state["ModelImportStateVersion"] = 4;
		state["ModelImportBusy"] = gate.IsActive;
		state["ModelImportGateState"] = gate.State;
		state["ModelImportPending"] = pending.Distinct( StringComparer.OrdinalIgnoreCase ).ToArray();
		state["ModelImportOwnedDirectories"] = owned.Distinct( StringComparer.OrdinalIgnoreCase ).ToArray();
	}
}