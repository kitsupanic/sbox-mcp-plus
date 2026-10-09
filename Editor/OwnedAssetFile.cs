using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;

namespace Editor.Mcp;

/// <summary>
/// Owns a newly created asset source by its file handle and keeps its physical directory ancestry
/// locked against replacement until disposal.
/// </summary>
internal sealed class OwnedAssetFile : IDisposable
{
	private const uint GenericRead = 0x80000000;
	private const uint GenericWrite = 0x40000000;
	private const uint DeleteAccess = 0x00010000;
	private const uint FileShareRead = 0x00000001;
	private const uint FileShareWrite = 0x00000002;
	private const uint FileShareDelete = 0x00000004;
	private const uint CreateNew = 1;
	private const uint OpenExisting = 3;
	private const uint FileFlagBackupSemantics = 0x02000000;
	private const uint FileFlagOpenReparsePoint = 0x00200000;
	private const uint FileFlagSequentialScan = 0x08000000;
	private const uint FileAttributeReparsePoint = 0x00000400;
	private const int ErrorAlreadyExists = 183;
	private const int FileDispositionInfoClass = 4;

	private static readonly HashSet<string> ReservedNames = new( StringComparer.OrdinalIgnoreCase )
	{
		"CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
		"LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
	};

	private FileStream _content;
	private readonly List<DirectoryLease> _directoryLeases;
	private bool _committed;
	private bool _disposed;

	internal Stream Content
	{
		get
		{
			ObjectDisposedException.ThrowIf( _disposed, this );
			return _content;
		}
	}

	internal string RelativePath { get; }
	internal bool IsCommitted => _committed;
	internal string AbsolutePath { get; }

	private OwnedAssetFile( string relativePath, string absolutePath, FileStream content, List<DirectoryLease> directoryLeases )
	{
		RelativePath = relativePath;
		AbsolutePath = absolutePath;
		_content = content;
		_directoryLeases = directoryLeases;
	}

	internal static OwnedAssetFile Create( string assetsRoot, string relativePath ) => Create( assetsRoot, relativePath, null );

	/// <summary>A test seam invoked after ancestry is locked and immediately before CREATE_NEW.</summary>
	internal static OwnedAssetFile Create( string assetsRoot, string relativePath, Action<string> beforeDestinationCreate )
	{
		if ( !OperatingSystem.IsWindows() )
			throw new PlatformNotSupportedException( "Owned asset creation is only supported on Windows." );
		if ( string.IsNullOrWhiteSpace( assetsRoot ) || !Path.IsPathFullyQualified( assetsRoot ) )
			throw new IOException( "The active project has no absolute Assets directory." );

		var normalized = NormalizeDiagnosticScenePath( relativePath );
		var canonicalRoot = Path.TrimEndingDirectorySeparator( Path.GetFullPath( assetsRoot ) );
		if ( !Directory.Exists( canonicalRoot ) )
			throw new IOException( "The active project's Assets directory does not exist." );

		var absolute = Path.GetFullPath( Path.Combine( canonicalRoot, normalized.Replace( '/', Path.DirectorySeparatorChar ) ) );
		if ( !IsContained( canonicalRoot, absolute ) )
			throw new IOException( "Scene path escapes the active Assets directory." );

		var leases = LockDestinationAncestry( canonicalRoot, Path.GetDirectoryName( absolute )! );
		try
		{
			beforeDestinationCreate?.Invoke( absolute );
			return new OwnedAssetFile( normalized, absolute, OpenOwnedFile( absolute ), leases );
		}
		catch
		{
			DisposeLeases( leases );
			throw;
		}
	}

	/// <summary>Create a file inside a directory already protected by the caller's retained locks.</summary>
	internal static OwnedAssetFile CreateInLockedDirectory( string absolutePath )
	{
		if ( !OperatingSystem.IsWindows() )
			throw new PlatformNotSupportedException( "Owned asset creation is only supported on Windows." );
		return new OwnedAssetFile( null, Path.GetFullPath( absolutePath ), OpenOwnedFile( absolutePath ), [] );
	}

	internal void Flush()
	{
		ObjectDisposedException.ThrowIf( _disposed, this );
		_content.Flush( true );
	}

	/// <summary>Preserves the import transaction's existing flush-then-close handoff.</summary>
	internal void CommitForRelease()
	{
		ObjectDisposedException.ThrowIf( _disposed, this );
		if ( _committed ) return;
		_content.Flush( true );
		_committed = true;
	}

	internal void Commit() => Commit( null );

	/// <summary>A test seam invoked during the verified handle handoff, immediately after the original handle closes.</summary>
	internal void Commit( Action<string> afterOriginalClosed )
	{
		ObjectDisposedException.ThrowIf( _disposed, this );
		if ( _committed ) return;
		_content.Flush( true );

		var original = _content;
		var expectedIdentity = ReadIdentity( original.SafeFileHandle );
		var expectedLength = original.Length;
		var expectedHash = HashContents( original );
		var broad = ReopenReadHandle( original.SafeFileHandle, FileShareRead | FileShareWrite | FileShareDelete );
		_content = broad;
		_committed = true;
		original.Dispose();

		afterOriginalClosed?.Invoke( AbsolutePath );

		var restrictive = ReopenReadHandle( broad.SafeFileHandle, FileShareRead );
		_content = restrictive;
		broad.Dispose();
		VerifyCommittedPath( expectedIdentity, expectedLength, expectedHash );
	}

	public void Dispose()
	{
		if ( _disposed ) return;
		_disposed = true;

		Exception failure = null;
		if ( !_committed )
		{
			try
			{
				var disposition = new FileDispositionInfo { DeleteFile = true };
				if ( !SetFileInformationByHandle( _content.SafeFileHandle, FileDispositionInfoClass, ref disposition,
					(uint)Marshal.SizeOf<FileDispositionInfo>() ) )
				{
					failure = new IOException( $"Owned file cleanup failed (Windows error {Marshal.GetLastWin32Error()})." );
				}
			}
			catch ( Exception exception )
			{
				failure = exception;
			}
		}

		try { _content.Dispose(); }
		catch ( Exception exception ) { failure ??= exception; }

		for ( var i = _directoryLeases.Count - 1; i >= 0; --i )
		{
			try { _directoryLeases[i].Dispose(); }
			catch ( Exception exception ) { failure ??= exception; }
		}
		_directoryLeases.Clear();

		if ( failure is not null ) throw new IOException( $"Failed to release owned asset '{AbsolutePath}'.", failure );
	}

	internal static bool IsContained( string root, string path )
	{
		var canonicalRoot = Path.TrimEndingDirectorySeparator( Path.GetFullPath( root ) );
		var full = Path.GetFullPath( path );
		return full.StartsWith( canonicalRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase );
	}

	internal static void ValidateFileName( string name, bool extensionAllowed )
	{
		if ( string.IsNullOrWhiteSpace( name ) || name != name.Trim() || name.EndsWith( ".", StringComparison.Ordinal ) )
			throw new ArgumentException( "A planned filename is empty, whitespace-padded, or ends in a dot." );
		if ( name.IndexOfAny( Path.GetInvalidFileNameChars() ) >= 0 || name.Contains( '/' ) || name.Contains( '\\' ) )
			throw new ArgumentException( "A planned filename contains invalid characters or separators." );
		if ( !extensionAllowed && Path.GetFileName( name ) != name )
			throw new ArgumentException( "The name must be a filename stem." );
		var deviceStem = name.Split( '.', 2 )[0];
		if ( ReservedNames.Contains( deviceStem ) )
			throw new ArgumentException( "A planned filename is a reserved Windows device name." );
	}

	internal static DirectoryLease LockDirectory( string directory )
	{
		var handle = CreateFileHandle( directory, GenericRead, FileShareRead | FileShareWrite, IntPtr.Zero, OpenExisting,
			FileFlagBackupSemantics | FileFlagOpenReparsePoint, IntPtr.Zero );
		if ( handle.IsInvalid )
		{
			var error = Marshal.GetLastWin32Error();
			handle.Dispose();
			throw new IOException( $"Directory ancestry could not be locked (Windows error {error})." );
		}
		if ( !GetFileInformationByHandle( handle, out var information ) )
		{
			var error = Marshal.GetLastWin32Error();
			handle.Dispose();
			throw new IOException( $"Directory ancestry could not be verified (Windows error {error})." );
		}
		if ( (information.FileAttributes & FileAttributeReparsePoint) != 0 )
		{
			handle.Dispose();
			throw new ReparsePointException( "The destination chain contains or changed to a symbolic link or junction." );
		}
		return new DirectoryLease( handle );
	}

	internal static bool TryCreateDirectory( string path, out int error )
	{
		if ( CreateDirectoryW( path, IntPtr.Zero ) )
		{
			error = 0;
			return true;
		}
		error = Marshal.GetLastWin32Error();
		return false;
	}

	private static string NormalizeDiagnosticScenePath( string relativePath )
	{
		if ( string.IsNullOrWhiteSpace( relativePath ) || relativePath != relativePath.Trim() )
			throw new ArgumentException( "Give a project-relative .scene path beneath scenes/diagnostics/." );
		var normalized = relativePath.Replace( '\\', '/' );
		if ( Path.IsPathRooted( relativePath ) || normalized.StartsWith( "//", StringComparison.Ordinal ) || normalized.Contains( ':' ) )
			throw new ArgumentException( "Scene path must be project-relative and beneath scenes/diagnostics/." );

		var parts = normalized.Split( '/', StringSplitOptions.None );
		if ( parts.Length < 3 || !parts[0].Equals( "scenes", StringComparison.OrdinalIgnoreCase )
			|| !parts[1].Equals( "diagnostics", StringComparison.OrdinalIgnoreCase ) )
			throw new ArgumentException( "Scene path must be beneath scenes/diagnostics/." );
		if ( Array.Exists( parts, part => part.Length == 0 || part is "." or ".." ) )
			throw new ArgumentException( "Scene path contains an empty, current, or parent component." );
		foreach ( var part in parts ) ValidateFileName( part, true );
		var sceneStem = Path.GetFileNameWithoutExtension( parts[^1] );
		if ( !string.Equals( Path.GetExtension( parts[^1] ), ".scene", StringComparison.OrdinalIgnoreCase )
			|| string.IsNullOrEmpty( sceneStem )
			|| sceneStem.EndsWith( ".", StringComparison.Ordinal )
			|| sceneStem.EndsWith( " ", StringComparison.Ordinal ) )
			throw new ArgumentException( "Scene path must end in a named .scene file whose stem has no trailing dot or space." );
		return string.Join( '/', parts );
	}

	private static List<DirectoryLease> LockDestinationAncestry( string assetsRoot, string destinationParent )
	{
		var volumeRoot = Path.GetPathRoot( assetsRoot )
			?? throw new IOException( "The Assets directory has no filesystem root." );
		if ( !string.Equals( Path.GetPathRoot( destinationParent ), volumeRoot, StringComparison.OrdinalIgnoreCase ) )
			throw new IOException( "The destination is not on the Assets filesystem." );

		var leases = new List<DirectoryLease>();
		try
		{
			var current = Path.TrimEndingDirectorySeparator( volumeRoot ) + Path.DirectorySeparatorChar;
			leases.Add( LockDirectory( current ) );

			foreach ( var component in RelativeComponents( volumeRoot, assetsRoot ) )
			{
				current = Path.Combine( current, component );
				if ( !Directory.Exists( current ) )
					throw new IOException( "The active project's Assets ancestry does not exist." );
				leases.Add( LockDirectory( current ) );
			}

			foreach ( var component in RelativeComponents( assetsRoot, destinationParent ) )
			{
				current = Path.Combine( current, component );
				if ( !Directory.Exists( current ) )
				{
					if ( !TryCreateDirectory( current, out var error )
						&& (error != ErrorAlreadyExists || !Directory.Exists( current )) )
						throw new IOException( $"A destination ancestor could not be reserved (Windows error {error})." );
				}
				leases.Add( LockDirectory( current ) );
			}
			return leases;
		}
		catch
		{
			DisposeLeases( leases );
			throw;
		}
	}

	private static IEnumerable<string> RelativeComponents( string root, string descendant )
	{
		var relative = Path.GetRelativePath( root, descendant );
		if ( relative == "." ) yield break;
		foreach ( var component in relative.Split( [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries ) )
		{
			if ( component == ".." ) throw new IOException( "The destination ancestry escapes its expected root." );
			yield return component;
		}
	}

	private static FileStream OpenOwnedFile( string path )
	{
		var handle = CreateFileHandle( path, GenericRead | GenericWrite | DeleteAccess, FileShareRead,
			IntPtr.Zero, CreateNew, FileFlagSequentialScan, IntPtr.Zero );
		if ( handle.IsInvalid )
		{
			var error = Marshal.GetLastWin32Error();
			handle.Dispose();
			throw new IOException( $"Exclusive file creation failed (Windows error {error})." );
		}
		return new FileStream( handle, FileAccess.ReadWrite, 128 * 1024, false );
	}

	private static FileStream ReopenReadHandle( SafeFileHandle original, uint shareMode )
	{
		var handle = ReOpenFile( original, GenericRead, shareMode, FileFlagSequentialScan );
		if ( handle.IsInvalid )
		{
			var error = Marshal.GetLastWin32Error();
			handle.Dispose();
			throw new IOException( $"Owned file handle downgrade failed (Windows error {error})." );
		}
		try { return new FileStream( handle, FileAccess.Read, 128 * 1024, false ); }
		catch { handle.Dispose(); throw; }
	}

	private void VerifyCommittedPath( FileIdentity expectedIdentity, long expectedLength, byte[] expectedHash )
	{
		using var pathStream = OpenReadOnlyPath( AbsolutePath );
		if ( ReadIdentity( pathStream.SafeFileHandle ) != expectedIdentity )
			throw new IOException( "Committed source verification failed because the destination path was replaced during handle handoff." );
		if ( pathStream.Length != expectedLength )
			throw new IOException( "Committed source verification failed because its length changed during handle handoff." );
		var actualHash = HashContents( pathStream );
		if ( !CryptographicOperations.FixedTimeEquals( actualHash, expectedHash ) )
			throw new IOException( "Committed source verification failed because its bytes changed during handle handoff." );
	}

	private static FileStream OpenReadOnlyPath( string path )
	{
		var handle = CreateFileHandle( path, GenericRead, FileShareRead, IntPtr.Zero, OpenExisting,
			FileFlagSequentialScan | FileFlagOpenReparsePoint, IntPtr.Zero );
		if ( handle.IsInvalid )
		{
			var error = Marshal.GetLastWin32Error();
			handle.Dispose();
			throw new IOException( $"Committed source path could not be locked for verification (Windows error {error})." );
		}
		if ( !GetFileInformationByHandle( handle, out var information ) )
		{
			var error = Marshal.GetLastWin32Error();
			handle.Dispose();
			throw new IOException( $"Committed source path could not be verified (Windows error {error})." );
		}
		if ( (information.FileAttributes & FileAttributeReparsePoint) != 0 )
		{
			handle.Dispose();
			throw new ReparsePointException( "Committed source verification rejected a final reparse point." );
		}
		try { return new FileStream( handle, FileAccess.Read, 128 * 1024, false ); }
		catch { handle.Dispose(); throw; }
	}

	private static FileIdentity ReadIdentity( SafeFileHandle handle )
	{
		if ( !GetFileInformationByHandle( handle, out var information ) )
			throw new IOException( $"Owned file identity could not be read (Windows error {Marshal.GetLastWin32Error()})." );
		return new FileIdentity( information.VolumeSerialNumber, ((ulong)information.FileIndexHigh << 32) | information.FileIndexLow );
	}

	private static byte[] HashContents( FileStream stream )
	{
		var position = stream.Position;
		stream.Position = 0;
		try { return SHA256.HashData( stream ); }
		finally { stream.Position = position; }
	}

	private static void DisposeLeases( List<DirectoryLease> leases )
	{
		for ( var i = leases.Count - 1; i >= 0; --i ) leases[i].Dispose();
		leases.Clear();
	}

	internal sealed class DirectoryLease : IDisposable
	{
		private SafeFileHandle _handle;
		internal DirectoryLease( SafeFileHandle handle ) => _handle = handle;
		public void Dispose()
		{
			_handle?.Dispose();
			_handle = null;
		}
	}
	private readonly record struct FileIdentity( uint VolumeSerialNumber, ulong FileIndex );


	internal sealed class ReparsePointException : IOException
	{
		internal ReparsePointException( string message ) : base( message ) { }
	}

	[DllImport( "kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true )]
	[return: MarshalAs( UnmanagedType.Bool )]
	private static extern bool CreateDirectoryW( string path, IntPtr securityAttributes );

	[DllImport( "kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CreateFileW" )]
	private static extern SafeFileHandle CreateFileHandle( string fileName, uint desiredAccess, uint shareMode,
		IntPtr securityAttributes, uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile );


	[DllImport( "kernel32.dll", SetLastError = true )]
	private static extern SafeFileHandle ReOpenFile( SafeFileHandle originalFile, uint desiredAccess,
		uint shareMode, uint flagsAndAttributes );
	[DllImport( "kernel32.dll", SetLastError = true )]
	[return: MarshalAs( UnmanagedType.Bool )]
	private static extern bool GetFileInformationByHandle( SafeFileHandle handle, out ByHandleFileInformation information );

	[DllImport( "kernel32.dll", SetLastError = true )]
	[return: MarshalAs( UnmanagedType.Bool )]
	private static extern bool SetFileInformationByHandle( SafeFileHandle handle, int fileInformationClass,
		ref FileDispositionInfo fileInformation, uint bufferSize );

	[StructLayout( LayoutKind.Sequential )]
	private struct ByHandleFileInformation
	{
		public uint FileAttributes;
		public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime;
		public System.Runtime.InteropServices.ComTypes.FILETIME LastAccessTime;
		public System.Runtime.InteropServices.ComTypes.FILETIME LastWriteTime;
		public uint VolumeSerialNumber;
		public uint FileSizeHigh;
		public uint FileSizeLow;
		public uint NumberOfLinks;
		public uint FileIndexHigh;
		public uint FileIndexLow;
	}

	[StructLayout( LayoutKind.Sequential )]
	private struct FileDispositionInfo
	{
		[MarshalAs( UnmanagedType.Bool )]
		public bool DeleteFile;
	}
}
