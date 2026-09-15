using Sandbox;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Text.Json;
using System.Threading.Tasks;

namespace Editor.Mcp;

public static partial class ExtrasTools
{
	private static ModelImportLifetime ModelImportLifetime = new();
	private static readonly IModelImportBackend ImportBackend = new SandboxModelImportBackend();
	private static readonly ModelImportBackendPipeline ImportPipeline = new( ImportBackend );

	/// <summary>
	/// Import a new external FBX, OBJ or DMX source into an isolated directory under the active
	/// project's Assets root. Native model creation and compilation are synchronous and can block
	/// the editor without a hard deadline. Returned evidence distinguishes observed asset state
	/// from unavailable operation-completion, dependency-coverage and writer-shutdown evidence.
	/// </summary>
	/// <param name="sourcePath">Absolute readable path to one .fbx, .obj or .dmx source.</param>
	/// <param name="targetDirectory">New final directory relative to Assets; no implicit subdirectory is appended.</param>
	/// <param name="modelName">Generated .vmdl stem only. Null or empty defaults to the source stem.</param>
	/// <param name="overwrite">Reserved. True always returns OverwriteUnsupported before mutation.</param>
	/// <param name="copySiblingTextures">Copy every allowlisted immediate sibling image; false does not enumerate siblings.</param>
	[McpTool( "x_import_model_source" )]
	public static async Task<ImportModelResult> ImportModelSource( string sourcePath, string targetDirectory,
		string modelName = "", bool overwrite = false, bool copySiblingTextures = true )
	{
		var result = new ImportModelResult();
		ModelImportTransaction transaction = null;
		var lease = ModelImportLifetime.TryEnter();
		if ( lease is null ) return BusyResult( result );

		try
		{
			if ( overwrite ) return result.Fail( "OverwriteUnsupported", "overwrite=true is unsupported in version 1." );
			var project = Project.Current;
			if ( project is null ) return result.Fail( "NoActiveProject", "No active project is available." );
			if ( Game.IsPlaying ) return result.Fail( "PlayMode", "Model import is unavailable while play mode is running." );
			var projectIdent = project.Config?.Ident;
			var assetsRoot = project.GetAssetsPath();
			ModelImportLifetime.ScanMarkers( assetsRoot );

			transaction = ModelImportTransaction.Plan( assetsRoot, sourcePath, targetDirectory, modelName, copySiblingTextures );
			result.SourceAsset = transaction.SourceAsset;
			result.ModelAsset = transaction.ModelAsset;
			if ( ModelImportLifetime.IsOwnedDestination( transaction.RelativeDirectory ) )
				throw new ImportContractException( "DestinationExists", "The destination is nested beneath an earlier import-owned directory." );
			EnsureOutputsAbsent( transaction );
			EnsureProjectState( project, projectIdent );

			result.Stage = "copy";
			transaction.ReserveDirectory();
			result.CopiedFiles.AddRange( transaction.CopyInputs() );
			transaction.WriteMarker( "not_started" );
			result.GeneratedFiles.Add( transaction.MarkerAsset );

			result.Stage = "registration";
			EnsureProjectState( project, projectIdent );
			result.WriterState = "unconfirmed";
			transaction.WriteMarker( "unconfirmed" );
			transaction.PrepareForEngineMutation();
			transaction.EngineMutationAttempted = true;
			ModelImportLifetime.RecordHistorical( transaction.PendingPaths( result.GeneratedFiles ) );

			foreach ( var copy in transaction.Copies.Where( x => !string.Equals( x.DestinationAsset, transaction.SourceAsset, StringComparison.OrdinalIgnoreCase ) ) )
				ImportPipeline.RegisterDependency( copy.DestinationAbsolute, copy.DestinationAsset );
			var sourceCopy = transaction.Copies.Single( x => string.Equals( x.DestinationAsset, transaction.SourceAsset, StringComparison.OrdinalIgnoreCase ) );
			var sourceAsset = ImportPipeline.RegisterSource( sourceCopy.DestinationAbsolute, transaction.SourceAsset );
			result.SourceRegistered = true;

			result.Stage = "model_creation";
			EnsureProjectState( project, projectIdent );
			var modelAbsolute = Path.Combine( transaction.AssetsRoot, transaction.ModelAsset.Replace( '/', Path.DirectorySeparatorChar ) );
			object modelAsset;
			try { modelAsset = ImportPipeline.CreateModel( sourceAsset, modelAbsolute, transaction.ModelAsset ); }
			finally { TryObserveGeneratedFiles( transaction, result ); }
			result.ModelRegistered = true;

			result.Stage = "compilation";
			var compilation = await ImportPipeline.ObserveCompilationAsync( modelAsset );
			CopyCompilationEvidence( compilation, result.Compilation );
			EnsureProjectState( project, projectIdent );

			result.Stage = "dependency_inspection";
			var dependencies = ImportPipeline.InspectDependencies( sourceAsset, modelAsset );
			CopyDependencyEvidence( dependencies, result );

			result.Succeeded = true;
			result.Stage = "complete";
			result.Error = null;
			result.RollbackStatus = "not_needed";
			ApplyPendingState( transaction, result );
			return result;
		}
		catch ( BackendPipelineException ex )
		{
			if ( ex.Evidence is BackendCompilationEvidence compilation ) CopyCompilationEvidence( compilation, result.Compilation );
			if ( ex.Evidence is BackendDependencyEvidence dependencies ) CopyDependencyEvidence( dependencies, result );
			result.Fail( ex.Code, ex.Message );
		}
		catch ( ImportContractException ex )
		{
			result.Fail( ex.Code, ex.Message );
		}
		catch ( Exception ex )
		{
			result.Fail( CodeForStage( result.Stage ), Safe( ex.Message, 1024 ) );
		}
		finally
		{
			try
			{
				if ( transaction is not null ) result.CopiedFiles = transaction.CompletedCopies.ToList();
				if ( transaction?.EngineMutationAttempted == true )
				{
					ApplyPendingState( transaction, result );
					transaction.Dispose();
				}
				else
				{
					var remaining = transaction?.CleanupPreEngine() ?? [];
					if ( remaining.Count > 0 )
					{
						result.RollbackStatus = "incomplete";
						result.PartialPaths.AddRange( remaining );
					}
					else if ( transaction is not null ) result.RollbackStatus = "complete";
				}
			}
			catch ( Exception ex )
			{
				result.RollbackStatus = "incomplete";
				AddWarning( result, $"Final import accounting was incomplete: {Safe( ex.Message, 180 )}" );
			}
			finally
			{
				result.FurtherImportsBlocked = false;
				lease.Dispose();
			}
		}
		return result;
	}

	private static void ApplyPendingState( ModelImportTransaction transaction, ImportModelResult result )
	{
		result.WriterState = "unconfirmed";
		result.FurtherImportsBlocked = false;
		result.PendingWriterPaths = transaction.PendingPaths( result.GeneratedFiles ).ToList();
		ModelImportLifetime.RecordHistorical( result.PendingWriterPaths );
		TryObserveGeneratedFiles( transaction, result );
		result.PendingWriterPaths = transaction.PendingPaths( result.GeneratedFiles ).ToList();
		ModelImportLifetime.RecordHistorical( result.PendingWriterPaths );
		if ( !result.Succeeded )
		{
			result.RollbackStatus = "incomplete";
			result.PartialPaths = result.PendingWriterPaths.ToList();
		}
	}

	private static void EnsureOutputsAbsent( ModelImportTransaction transaction )
	{
		foreach ( var copy in transaction.Copies )
			if ( File.Exists( copy.DestinationAbsolute ) || AssetSystem.FindByPath( copy.DestinationAsset ) is not null )
				throw new ImportContractException( "DestinationExists", $"Planned output '{copy.DestinationAsset}' already exists." );
		if ( AssetSystem.FindByPath( transaction.ModelAsset ) is not null )
			throw new ImportContractException( "DestinationExists", $"Planned output '{transaction.ModelAsset}' is already registered." );
		var prefix = transaction.RelativeDirectory.TrimEnd( '/' ) + "/";
		if ( AssetSystem.All.Any( asset => asset?.Path is string path &&
			path.StartsWith( prefix, StringComparison.OrdinalIgnoreCase ) ) )
			throw new ImportContractException( "DestinationExists", "The destination contains registered asset evidence." );
		var ancestor = transaction.RelativeDirectory;
		while ( ancestor.Contains( '/' ) )
		{
			ancestor = ancestor[..ancestor.LastIndexOf( '/' )];
			var ancestorPrefix = ancestor + "/";
			var direct = AssetSystem.All
				.Select( asset => asset?.Path?.Replace( '\\', '/' ) )
				.Where( path => path is not null && path.StartsWith( ancestorPrefix, StringComparison.OrdinalIgnoreCase ) &&
					!path[ancestorPrefix.Length..].Contains( '/' ) )
				.ToArray();
			var modelCount = direct.Count( path => Path.GetExtension( path ).Equals( ".vmdl", StringComparison.OrdinalIgnoreCase ) );
			var sourceCount = direct.Count( path => new[] { ".fbx", ".obj", ".dmx" }.Contains( Path.GetExtension( path ), StringComparer.OrdinalIgnoreCase ) );
			// A v1 import boundary owns exactly one source and one generated model. Directories
			// containing several independent pairs are collection roots, not isolated imports.
			if ( modelCount == 1 && sourceCount == 1 )
				throw new ImportContractException( "DestinationExists", "A destination ancestor contains registered source/model import evidence." );
		}
	}
	private static void EnsureProjectState( Project project, string ident )
	{
		if ( Project.Current is null || !ReferenceEquals( Project.Current, project ) || !string.Equals( Project.Current.Config?.Ident, ident, StringComparison.Ordinal ) )
			throw new ImportContractException( "NoActiveProject", "The active project changed during import." );
		if ( Game.IsPlaying ) throw new ImportContractException( "PlayMode", "Play mode started during import." );
	}

	private static void TryObserveGeneratedFiles( ModelImportTransaction transaction, ImportModelResult result )
	{
		try
		{
			if ( !Directory.Exists( transaction.FinalDirectory ) ) return;
			foreach ( var file in Directory.EnumerateFiles( transaction.FinalDirectory, "*", SearchOption.TopDirectoryOnly ) )
			{
				if ( (File.GetAttributes( file ) & FileAttributes.ReparsePoint) != 0 ) continue;
				var path = transaction.ToReportedPath( file );
				if ( result.CopiedFiles.Contains( path, StringComparer.OrdinalIgnoreCase ) ) continue;
				if ( !result.GeneratedFiles.Contains( path, StringComparer.OrdinalIgnoreCase ) ) result.GeneratedFiles.Add( path );
				if ( !transaction.OwnedFiles.Contains( file, StringComparer.OrdinalIgnoreCase ) ) transaction.OwnedFiles.Add( file );
			}
			result.GeneratedFiles.Sort( StringComparer.OrdinalIgnoreCase );
		}
		catch ( Exception ex ) { AddWarning( result, $"Generated output attribution was incomplete: {Safe( ex.Message, 180 )}" ); }
	}

	private static void AddWarning( ImportModelResult result, string warning )
	{
		if ( result.Warnings.Count < 16 ) result.Warnings.Add( Safe( warning, 256 ) );
	}


	private static void CopyCompilationEvidence( BackendCompilationEvidence source, CompilationEvidence target )
	{
		target.Status = source.Status;
		target.IsCompiled = source.IsCompiled;
		target.IsCompiledAndUpToDate = source.IsCompiledAndUpToDate;
		target.IsCompileFailed = source.IsCompileFailed;
		target.UnavailableEvidence = new Dictionary<string, string>( source.UnavailableEvidence, StringComparer.Ordinal );
	}

	private static void CopyDependencyEvidence( BackendDependencyEvidence source, ImportModelResult target )
	{
		target.DependencyInspection.Status = source.Status;
		target.DependencyInspection.InspectedAssets = source.InspectedAssets.ToList();
		target.DependencyInspection.References = source.References.ToList();
		target.DependencyInspection.InputDependencies = source.InputDependencies.ToList();
		target.DependencyInspection.AdditionalContentFiles = source.AdditionalContentFiles.ToList();
		target.DependencyInspection.UnavailableEvidence = new Dictionary<string, string>( source.UnavailableEvidence, StringComparer.Ordinal );
		target.UnresolvedReferences = source.UnresolvedReferences.ToList();
	}

	private static ImportModelResult BusyResult( ImportModelResult result )
	{
		result.FurtherImportsBlocked = true;
		return result.Fail( "ImportBusy", "Another model import request is currently executing." );
	}

	private static string CodeForStage( string stage ) => stage switch
	{
		"copy" => "CopyFailed", "registration" => "RegistrationFailed", "model_creation" => "ModelCreationFailed",
		"compilation" => "CompilationUnconfirmed", "dependency_inspection" => "DependencyInspectionFailed", _ => "InvalidInput"
	};
	internal static string Safe( string value, int maximum )
	{
		var text = string.IsNullOrWhiteSpace( value ) ? "The operation failed without a message." : value.Replace( '\r', ' ' ).Replace( '\n', ' ' );
		return text.Length <= maximum ? text : text[..maximum];
	}
}

public sealed class ImportModelResult
{
	public bool Succeeded { get; set; }
	public string Stage { get; set; } = "validation";
	public string SourceAsset { get; set; }
	public string ModelAsset { get; set; }
	public List<string> CopiedFiles { get; set; } = [];
	public List<string> GeneratedFiles { get; set; } = [];
	public bool SourceRegistered { get; set; }
	public bool ModelRegistered { get; set; }
	public CompilationEvidence Compilation { get; set; } = new();
	public DependencyInspectionEvidence DependencyInspection { get; set; } = new();
	public string WriterState { get; set; } = "not_started";
	public bool FurtherImportsBlocked { get; set; }
	public List<string> PendingWriterPaths { get; set; } = [];
	public List<string> UnresolvedReferences { get; set; } = [];
	public List<string> Warnings { get; set; } = [];
	public ImportModelError Error { get; set; }
	public string RollbackStatus { get; set; } = "not_needed";
	public List<string> PartialPaths { get; set; } = [];
	internal ImportModelResult Fail( string code, string message )
	{
		Succeeded = false; Error = new() { Code = code, Message = ExtrasTools.Safe( message, 1024 ) }; return this;
	}
}

public sealed class CompilationEvidence
{
	public string Status { get; set; } = "not_started";
	public bool? IsCompiled { get; set; }
	public bool? IsCompiledAndUpToDate { get; set; }
	public bool? IsCompileFailed { get; set; }
	public Dictionary<string, string> UnavailableEvidence { get; set; } = new( StringComparer.Ordinal );
}

public sealed class DependencyInspectionEvidence
{
	public string Status { get; set; } = "not_started";
	public List<string> InspectedAssets { get; set; } = [];
	public List<string> References { get; set; } = [];
	public List<string> InputDependencies { get; set; } = [];
	public List<string> AdditionalContentFiles { get; set; } = [];
	public Dictionary<string, string> UnavailableEvidence { get; set; } = new( StringComparer.Ordinal );
}

public sealed class ImportModelError
{
	public string Code { get; set; }
	public string Message { get; set; }
}

internal sealed class SandboxModelImportBackend : IModelImportBackend
{
	public object RegisterFile( string absolutePath ) => AssetSystem.RegisterFile( absolutePath );
	public object FindAsset( string assetPath ) => AssetSystem.FindByPath( assetPath );
	public string GetAssetPath( object asset ) => (asset as Asset)?.Path;
	public object CreateModel( object sourceAsset, string absoluteModelPath ) =>
		EditorUtility.CreateModelFromMeshFile( (Asset)sourceAsset, absoluteModelPath );
	public bool ReadIsCompiled( object asset ) => ((Asset)asset).IsCompiled;
	public bool ReadIsCompiledAndUpToDate( object asset ) => ((Asset)asset).IsCompiledAndUpToDate;
	public bool ReadIsCompileFailed( object asset ) => ((Asset)asset).IsCompileFailed;
	public async ValueTask ObserveCompilationIfNeededAsync( object asset ) => await ((Asset)asset).CompileIfNeededAsync( 30.0f );
	public IReadOnlyList<object> GetReferences( object asset ) => ((Asset)asset).GetReferences( false ).Cast<object>().ToArray();
	public IReadOnlyList<string> GetUnrecognizedReferences( object asset ) => ((Asset)asset).GetUnrecognizedReferencePaths();
	public IReadOnlyList<string> GetInputDependencies( object asset ) => ((Asset)asset).GetInputDependencies();
	public IReadOnlyList<string> GetAdditionalContentFiles( object asset ) => ((Asset)asset).GetAdditionalContentFiles();
}

internal sealed class ModelImportLifetime : IHotloadManaged
{
	private ModelImportRequestGate _gate = new();
	private int _valid = 1;
	private List<string> _pending = [];
	private List<string> _ownedDirectories = [];
	internal IReadOnlyList<string> PendingPaths => _pending;
	internal string BusyReason => "Another model import request is currently executing.";

	internal Lease TryEnter()
	{
		if ( Volatile.Read( ref _valid ) == 0 ) return null;
		var lease = _gate.TryEnter();
		return lease is null ? null : new Lease( lease );
	}
	internal void RecordHistorical( IEnumerable<string> paths )
	{
		_pending = _pending.Concat( paths ).Distinct( StringComparer.OrdinalIgnoreCase )
			.OrderBy( x => x, StringComparer.OrdinalIgnoreCase ).ToList();
	}
	internal void ScanMarkers( string assetsRoot )
	{
		var foundPending = new List<string>();
		var foundOwned = new List<string>();
		try
		{
			var stack = new Stack<string>(); stack.Push( assetsRoot );
			while ( stack.Count > 0 )
			{
				var directory = stack.Pop();
				foreach ( var child in Directory.EnumerateDirectories( directory ) )
					if ( (File.GetAttributes( child ) & FileAttributes.ReparsePoint) == 0 ) stack.Push( child );
				var marker = Path.Combine( directory, ModelImportTransaction.MarkerName );
				if ( !File.Exists( marker ) ) continue;
				var relative = Path.GetRelativePath( assetsRoot, directory ).Replace( '\\', '/' );
				foundOwned.Add( relative );
				var blocks = true;
				try
				{
					using var document = JsonDocument.Parse( File.ReadAllText( marker ) );
					var root = document.RootElement;
					blocks = !root.TryGetProperty( "Version", out var version ) || version.GetInt32() != 1 ||
						!root.TryGetProperty( "TransactionId", out var transactionId ) || !Guid.TryParse( transactionId.GetString(), out _ ) ||
						!root.TryGetProperty( "SourceAsset", out var sourceAsset ) || string.IsNullOrWhiteSpace( sourceAsset.GetString() ) ||
						!root.TryGetProperty( "ModelAsset", out var modelAsset ) || string.IsNullOrWhiteSpace( modelAsset.GetString() ) ||
						!root.TryGetProperty( "WriterState", out var writerState ) ||
						!string.Equals( writerState.GetString(), "stopped", StringComparison.Ordinal );
				}
				catch { blocks = true; }
				if ( blocks ) foundPending.Add( relative );
			}
			_ownedDirectories = foundOwned;
			_pending = foundPending;
		}
		catch ( Exception ex )
		{
			_ownedDirectories = [];
			_pending = [];
			throw new ImportContractException( "ImportBusy", $"Import ownership markers could not be scanned safely: {ExtrasTools.Safe( ex.Message, 700 )}" );
		}
	}

	internal bool IsOwnedDestination( string relativeDirectory ) => _ownedDirectories.Any( owned =>
		relativeDirectory.Equals( owned, StringComparison.OrdinalIgnoreCase ) ||
		relativeDirectory.StartsWith( owned.TrimEnd( '/' ) + "/", StringComparison.OrdinalIgnoreCase ) ||
		owned.StartsWith( relativeDirectory.TrimEnd( '/' ) + "/", StringComparison.OrdinalIgnoreCase ) );
	public void Destroyed( Dictionary<string, object> state )
	{
		ModelImportHotloadTransfer.Write( state, _gate, _pending, _ownedDirectories );
		Volatile.Write( ref _valid, 0 );
	}
	public void Created( IReadOnlyDictionary<string, object> state )
	{
		try
		{
			// The only legacy retained gate in this session belongs to the giant-lid invocation,
			// whose MCP call returned before this contract migration began.
			var snapshot = ModelImportHotloadTransfer.Read( state, legacyInvocationFinished: true );
			_pending = snapshot.PendingPaths.ToList();
			_ownedDirectories = snapshot.OwnedDirectories.ToList();
			_gate ??= new ModelImportRequestGate();
			_gate.Restore( snapshot.ActiveRequest );
			Volatile.Write( ref _valid, 1 );
		}
		catch
		{
			_gate.Restore( true );
			Volatile.Write( ref _valid, 0 );
		}
	}

	public void Persisted() { }
	public void Failed()
	{
		_gate.Restore( true );
		Volatile.Write( ref _valid, 0 );
	}

	internal sealed class Lease : IDisposable
	{
		private ModelImportRequestGate.Lease _lease;
		internal Lease( ModelImportRequestGate.Lease lease ) => _lease = lease;
		public void Dispose()
		{
			var lease = Interlocked.Exchange( ref _lease, null );
			lease?.Dispose();
		}
	}
}
