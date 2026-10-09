using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace Editor.Mcp;

internal sealed class ModelImportTransaction
{
	internal const long MaximumBytes = 1_073_741_824;
	internal const int MaximumImages = 128;
	internal const string MarkerName = ".sbox-mcp-import.json";
	private static readonly HashSet<string> ImageExtensions = new( StringComparer.OrdinalIgnoreCase )
	{
		".png", ".tga", ".jpg", ".jpeg", ".bmp", ".tif", ".tiff", ".exr", ".hdr"
	};
	private static readonly HashSet<string> SourceExtensions = new( StringComparer.OrdinalIgnoreCase )
	{
		".fbx", ".obj", ".dmx"
	};

	internal string TransactionId { get; } = Guid.NewGuid().ToString( "D" );
	internal string AssetsRoot { get; }
	internal string FinalDirectory { get; }
	internal string RelativeDirectory { get; }
	internal string SourceAsset { get; }
	internal string ModelAsset { get; }
	internal string MarkerAsset => JoinAsset( RelativeDirectory, MarkerName );
	internal IReadOnlyList<CopyPlan> Copies { get; }
	internal List<string> OwnedFiles { get; } = [];
	internal List<string> CompletedCopies { get; } = [];
	private readonly List<(string Path, OwnedAssetFile.DirectoryLease Handle)> _directoryHandles = [];
	private readonly Dictionary<string, OwnedAssetFile> _ownedFiles = new( StringComparer.OrdinalIgnoreCase );
	private OwnedAssetFile _markerFile;
	internal List<string> CreatedDirectories { get; } = [];
	internal bool EngineMutationAttempted { get; set; }

	private ModelImportTransaction( string assetsRoot, string relativeDirectory, string finalDirectory,
		string sourceAsset, string modelAsset, IReadOnlyList<CopyPlan> copies )
	{
		AssetsRoot = assetsRoot;
		RelativeDirectory = relativeDirectory;
		FinalDirectory = finalDirectory;
		SourceAsset = sourceAsset;
		ModelAsset = modelAsset;
		Copies = copies;
	}

	internal static ModelImportTransaction Plan( string assetsRoot, string sourcePath, string targetDirectory,
		string modelName, bool copySiblingTextures )
	{
		if ( !OperatingSystem.IsWindows() )
			throw new ImportContractException( "ApiUnavailable", "Exclusive destination reservation is only supported on Windows." );
		if ( string.IsNullOrEmpty( assetsRoot ) )
			throw new ImportContractException( "NoActiveProject", "The active project has no Assets directory." );
		if ( string.IsNullOrWhiteSpace( sourcePath ) || !Path.IsPathFullyQualified( sourcePath ) )
			throw new ImportContractException( "InvalidInput", "sourcePath must be an absolute file path." );

		string canonicalSource;
		try { canonicalSource = Path.GetFullPath( sourcePath ); }
		catch ( Exception ) { throw new ImportContractException( "InvalidInput", "sourcePath is not a valid absolute path." ); }
		var sourceInfo = new FileInfo( canonicalSource );
		if ( !sourceInfo.Exists || (sourceInfo.Attributes & FileAttributes.Directory) != 0 )
			throw new ImportContractException( "InvalidInput", "sourcePath must identify a readable regular file." );
		if ( (sourceInfo.Attributes & FileAttributes.ReparsePoint) != 0 )
		{
			var target = sourceInfo.ResolveLinkTarget( true );
			if ( target is not FileInfo targetFile || !targetFile.Exists )
				throw new ImportContractException( "InvalidInput", "sourcePath link does not resolve to a readable regular file." );
			sourceInfo = targetFile;
			canonicalSource = targetFile.FullName;
		}
		if ( !SourceExtensions.Contains( sourceInfo.Extension ) )
			throw new ImportContractException( "InvalidInput", "sourcePath must have an .fbx, .obj, or .dmx extension." );
		using ( File.Open( canonicalSource, FileMode.Open, FileAccess.Read, FileShare.Read ) ) { }

		var effectiveName = string.IsNullOrEmpty( modelName ) ? Path.GetFileNameWithoutExtension( sourceInfo.Name ) : modelName;
		ValidateFileName( effectiveName, false );
		if ( effectiveName.EndsWith( ".vmdl", StringComparison.OrdinalIgnoreCase ) )
			throw new ImportContractException( "InvalidInput", "modelName must not include a .vmdl suffix." );
		ValidateFileName( sourceInfo.Name, true );

		var relative = NormalizeTarget( targetDirectory );
		var canonicalRoot = Path.TrimEndingDirectorySeparator( Path.GetFullPath( assetsRoot ) );
		var final = Path.GetFullPath( Path.Combine( canonicalRoot, relative.Replace( '/', Path.DirectorySeparatorChar ) ) );
		EnsureContained( canonicalRoot, final );
		EnsureNoReparsePoints( canonicalRoot, final );
		if ( Directory.Exists( final ) || File.Exists( final ) )
			throw new ImportContractException( "DestinationExists", "The final import directory already exists." );

		var candidates = new List<FileInfo> { sourceInfo };
		if ( copySiblingTextures )
		{
			IEnumerable<FileInfo> siblings;
			try { siblings = sourceInfo.Directory!.EnumerateFiles().Where( x => ImageExtensions.Contains( x.Extension ) ); }
			catch ( Exception ) { throw new ImportContractException( "InvalidInput", "Sibling image files could not be enumerated." ); }
			candidates.AddRange( siblings.OrderBy( x => x.Name, StringComparer.OrdinalIgnoreCase ) );
		}
		if ( candidates.Count - 1 > MaximumImages )
			throw new ImportContractException( "LimitExceeded", "The import selects more than 128 sibling images." );

		var names = new HashSet<string>( StringComparer.OrdinalIgnoreCase );
		long total = 0;
		var copies = new List<CopyPlan>( candidates.Count );
		foreach ( var file in candidates )
		{
			ValidateFileName( file.Name, true );
			if ( (file.Attributes & FileAttributes.ReparsePoint) != 0 )
				throw new ImportContractException( "InvalidInput", $"Selected file '{file.Name}' is a link." );
			if ( !names.Add( file.Name ) )
				throw new ImportContractException( "InvalidInput", "Selected filenames collide case-insensitively." );
			try { total = checked(total + file.Length); }
			catch ( OverflowException ) { throw new ImportContractException( "LimitExceeded", "Selected files exceed the byte limit." ); }
			if ( total > MaximumBytes )
				throw new ImportContractException( "LimitExceeded", "Selected files exceed 1,073,741,824 bytes." );
			copies.Add( new CopyPlan( file.FullName, Path.Combine( final, file.Name ), JoinAsset( relative, file.Name ), file.Length ) );
		}

		var modelFile = effectiveName + ".vmdl";
		ValidateFileName( modelFile, true );
		if ( !names.Add( modelFile ) || !names.Add( MarkerName ) )
			throw new ImportContractException( "InvalidInput", "Planned output filenames collide case-insensitively." );
		return new ModelImportTransaction( canonicalRoot, relative, final,
			JoinAsset( relative, sourceInfo.Name ), JoinAsset( relative, modelFile ), copies );
	}

	internal void ReserveDirectory()
	{
		var parent = Path.GetDirectoryName( FinalDirectory )!;
		var chain = new Stack<string>();
		for ( var cursor = parent; ; cursor = Path.GetDirectoryName( cursor )! )
		{
			chain.Push( cursor );
			if ( string.Equals( cursor, AssetsRoot, StringComparison.OrdinalIgnoreCase ) ) break;
		}
		while ( chain.Count > 0 )
		{
			var directory = chain.Pop();
			if ( !Directory.Exists( directory ) )
			{
				if ( OwnedAssetFile.TryCreateDirectory( directory, out var error ) ) CreatedDirectories.Add( directory );
				else if ( error != 183 || !Directory.Exists( directory ) )
					throw new ImportContractException( "CopyFailed", $"A destination ancestor could not be reserved (Windows error {error})." );
			}
			LockDirectory( directory );
		}
		if ( !OwnedAssetFile.TryCreateDirectory( FinalDirectory, out var finalError ) )
		{
			throw new ImportContractException( finalError == 183 ? "DestinationExists" : "CopyFailed",
				finalError == 183 ? "The final import directory was created by another operation." : $"The final import directory could not be reserved (Windows error {finalError})." );
		}
		CreatedDirectories.Add( FinalDirectory );
		LockDirectory( FinalDirectory );
	}

	internal IReadOnlyList<string> CopyInputs()
	{
		long streamed = 0;
		var buffer = new byte[128 * 1024];
		foreach ( var copy in Copies )
		{
			try
			{
				using var input = new FileStream( copy.SourceAbsolute, FileMode.Open, FileAccess.Read, FileShare.Read, buffer.Length, FileOptions.SequentialScan );
				var owned = OwnedAssetFile.CreateInLockedDirectory( copy.DestinationAbsolute );
				OwnedFiles.Add( copy.DestinationAbsolute );
				_ownedFiles.Add( copy.DestinationAbsolute, owned );
				var output = owned.Content;
				int read;
				while ( (read = input.Read( buffer, 0, buffer.Length )) != 0 )
				{
					streamed = checked(streamed + read);
					if ( streamed > MaximumBytes )
						throw new ImportContractException( "LimitExceeded", "Selected files grew beyond 1,073,741,824 bytes while copying." );
					output.Write( buffer, 0, read );
				}
				owned.Flush();
				CompletedCopies.Add( copy.DestinationAsset );
			}
			catch ( ImportContractException ) { throw; }
			catch ( Exception ) { throw new ImportContractException( "CopyFailed", $"Failed to copy '{copy.DestinationAsset}'." ); }
		}
		return CompletedCopies;
	}

	internal void WriteMarker( string writerState )
	{
		var marker = Path.Combine( FinalDirectory, MarkerName );
		var json = JsonSerializer.Serialize( new Marker( 1, TransactionId, SourceAsset, ModelAsset, writerState ) );
		if ( _markerFile is null )
		{
			_markerFile = OwnedAssetFile.CreateInLockedDirectory( marker );
			OwnedFiles.Add( marker );
			_ownedFiles.Add( marker, _markerFile );
		}
		_markerFile.Content.Position = 0;
		_markerFile.Content.SetLength( 0 );
		using ( var writer = new StreamWriter( _markerFile.Content, System.Text.Encoding.UTF8, 1024, true ) )
		{
			writer.Write( json );
			writer.Flush();
		}
		_markerFile.Flush();
	}

	internal IReadOnlyList<string> CleanupPreEngine()
	{
		var remaining = new List<string>();
		var handleOwned = _ownedFiles.Keys.ToHashSet( StringComparer.OrdinalIgnoreCase );
		foreach ( var owned in _ownedFiles.ToArray() )
		{
			try { owned.Value.Dispose(); }
			catch { remaining.Add( ToReportedPath( owned.Key ) ); }
		}
		_ownedFiles.Clear();
		_markerFile = null;
		foreach ( var file in OwnedFiles.Where( x => !handleOwned.Contains( x ) ) )
			if ( File.Exists( file ) ) remaining.Add( ToReportedPath( file ) );
		foreach ( var directory in CreatedDirectories )
			if ( Directory.Exists( directory ) ) remaining.Add( ToReportedPath( directory ) );
		foreach ( var entry in _directoryHandles.ToArray() )
		{
			try { entry.Handle.Dispose(); }
			catch { remaining.Add( ToReportedPath( entry.Path ) ); }
		}
		_directoryHandles.Clear();
		return remaining.Distinct( StringComparer.OrdinalIgnoreCase ).OrderBy( x => x, StringComparer.OrdinalIgnoreCase ).ToArray();
	}

	internal string[] PendingPaths( IEnumerable<string> generated ) => new[] { RelativeDirectory }
		.Concat( OwnedFiles.Select( ToReportedPath ) ).Concat( generated )
		.Append( SourceAsset ).Append( ModelAsset )
		.Distinct( StringComparer.OrdinalIgnoreCase ).OrderBy( x => x, StringComparer.OrdinalIgnoreCase ).ToArray();

	internal string ToReportedPath( string absolute ) => OwnedAssetFile.IsContained( AssetsRoot, absolute )
		? Path.GetRelativePath( AssetsRoot, absolute ).Replace( '\\', '/' ) : absolute;

	internal static string NormalizeTarget( string target )
	{
		if ( string.IsNullOrWhiteSpace( target ) || target != target.Trim() )
			throw new ImportContractException( "InvalidInput", "targetDirectory must be a non-empty relative path without surrounding whitespace." );
		var normalized = target.Replace( '\\', '/' );
		if ( Path.IsPathRooted( target ) || normalized.StartsWith( "//", StringComparison.Ordinal ) || normalized.Contains( ':' ) )
			throw new ImportContractException( "InvalidInput", "targetDirectory must be relative to Assets." );
		var parts = normalized.Split( '/', StringSplitOptions.None );
		if ( parts.Length == 0 || parts.Any( x => x.Length == 0 || x is "." or ".." ) )
			throw new ImportContractException( "InvalidInput", "targetDirectory contains an empty, current, or parent component." );
		if ( parts[0].Equals( "Assets", StringComparison.OrdinalIgnoreCase ) )
			throw new ImportContractException( "InvalidInput", "targetDirectory must not include an Assets prefix." );
		foreach ( var part in parts ) ValidateFileName( part, true );
		return string.Join( '/', parts );
	}

	internal static void ValidateFileName( string name, bool extensionAllowed )
	{
		try { OwnedAssetFile.ValidateFileName( name, extensionAllowed ); }
		catch ( ArgumentException exception ) { throw new ImportContractException( "InvalidInput", exception.Message ); }
	}

	internal static void EnsureNoReparsePoints( string root, string destination )
	{
		for ( var cursor = destination; !string.Equals( cursor, root, StringComparison.OrdinalIgnoreCase ); cursor = Path.GetDirectoryName( cursor )! )
		{
			if ( !Directory.Exists( cursor ) ) continue;
			if ( (File.GetAttributes( cursor ) & FileAttributes.ReparsePoint) != 0 )
				throw new ImportContractException( "InvalidInput", "The destination chain contains a symbolic link or junction." );
		}
	}
	private void LockDirectory( string directory )
	{
		try { _directoryHandles.Add( (directory, OwnedAssetFile.LockDirectory( directory )) ); }
		catch ( OwnedAssetFile.ReparsePointException exception )
		{
			throw new ImportContractException( "InvalidInput", exception.Message );
		}
		catch ( IOException )
		{
			throw new ImportContractException( "CopyFailed", "The destination ancestry could not be locked against replacement." );
		}
	}



	private void DisposeOwnedFiles()
	{
		foreach ( var file in _ownedFiles.Values ) file.Dispose();
		_ownedFiles.Clear();
		_markerFile = null;
	}

	private void DisposeHandles()
	{
		DisposeOwnedFiles();
		foreach ( var entry in _directoryHandles ) entry.Handle.Dispose();
		_directoryHandles.Clear();
	}

	internal void Dispose() => DisposeHandles();
	internal void PrepareForEngineMutation()
	{
		foreach ( var file in _ownedFiles.Values ) file.CommitForRelease();
		DisposeOwnedFiles();
	}


	internal static bool IsContained( string root, string path ) => OwnedAssetFile.IsContained( root, path );

	private static void EnsureContained( string root, string path )
	{
		if ( !IsContained( root, path ) ) throw new ImportContractException( "InvalidInput", "targetDirectory escapes the active Assets root." );
	}

	private static string JoinAsset( string directory, string name ) => $"{directory.TrimEnd( '/' )}/{name}";






	internal sealed record CopyPlan( string SourceAbsolute, string DestinationAbsolute, string DestinationAsset, long PreflightLength );
	internal sealed record Marker( int Version, string TransactionId, string SourceAsset, string ModelAsset, string WriterState );
}

internal class ImportContractException : Exception
{
	internal string Code { get; }
	internal ImportContractException( string code, string message ) : base( message ) => Code = code;
}
