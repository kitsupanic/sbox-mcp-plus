using Microsoft.Win32.SafeHandles;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Editor.Mcp;

internal sealed class OwnedLocalInstance : IDisposable
{
	/// <summary>
	/// A retained launch may temporarily lack a creation identity after cleanup fails. Such an entry
	/// remains internal until GetProcessTimes supplies the exact timestamp required by the public contract.
	/// </summary>

	internal OwnedLocalInstance( SafeKernelHandle processHandle, SafeKernelHandle jobHandle, int processId,
		long? creationFileTime, string executablePath, bool windowed, int instanceNumber, string logDirectory,
		bool assignedToJob, LaunchAuthority authority )
	{
		if ( creationFileTime is null && authority != LaunchAuthority.RetainedUnidentified )
			throw new ArgumentException( "Only a retained unidentified launch may have no creation identity.",
				nameof( creationFileTime ) );

		ProcessHandle = processHandle;
		JobHandle = jobHandle;
		ProcessId = processId;
		CreationFileTime = creationFileTime;
		ExecutablePath = executablePath;
		Windowed = windowed;
		InstanceNumber = instanceNumber;
		LogDirectory = logDirectory;
		AssignedToJob = assignedToJob;
		Authority = authority;

		LaunchedAt = creationFileTime is long creation
			? DateTime.FromFileTimeUtc( creation ).ToString( "O", CultureInfo.InvariantCulture )
			: null;
	}

	internal SafeKernelHandle ProcessHandle { get; }
	internal SafeKernelHandle JobHandle { get; }
	internal int ProcessId { get; }

	/// <summary>Exact creation FILETIME captured at launch, or null when it could not be read.</summary>
	internal long? CreationFileTime { get; set; }
	internal string ExecutablePath { get; }
	internal string LaunchedAt { get; set; }
	internal bool Windowed { get; }
	internal int InstanceNumber { get; }
	internal string LogDirectory { get; }

	/// <summary>Whether the retained private job actually contains this process.</summary>
	internal bool AssignedToJob { get; }

	/// <summary>
	/// The authority this entry actually holds. A launch that never resumed and whose cleanup failed
	/// is retained in one of the failed-launch states instead of being abandoned, and is never
	/// presented as an ordinary launched process.
	/// </summary>
	internal LaunchAuthority Authority { get; private set; }

	/// <summary>
	/// A launch that was never resumed, so its only authority is the original process handle or its
	/// private job - never a PID, and never a normal launch identity.
	/// </summary>
	internal bool FailedLaunch => Authority != LaunchAuthority.Owned;

	/// <summary>
	/// Marks a never-resumed launch whose pre-resume cleanup failed as the retained authority of that
	/// process. Called by the launch scope while the entry is still unreachable to any other caller.
	/// </summary>
	internal void MarkRetainedFailedLaunch() => Authority = LaunchAuthority.RetainedFailedLaunch;

	/// <summary>Promotes hidden cleanup authority once its exact process creation identity is readable.</summary>
	internal void CaptureCreationIdentity( long creationFileTime )
	{
		if ( CreationFileTime is not null ) return;
		CreationFileTime = creationFileTime;
		LaunchedAt = DateTime.FromFileTimeUtc( creationFileTime ).ToString( "O", CultureInfo.InvariantCulture );
		Authority = LaunchAuthority.RetainedFailedLaunch;
	}


	public void Dispose()
	{
		ProcessHandle.Dispose();
		JobHandle?.Dispose();
	}
}

internal sealed class OwnedInstanceRegistry : IDisposable
{
	internal const ulong SyntheticSteamIdentityBase = 90071996842377216UL;

	private static readonly TimeSpan GracefulBudget = TimeSpan.FromSeconds( 2 );
	private static readonly TimeSpan ForcedBudget = TimeSpan.FromSeconds( 3 );
	private const int PollIntervalMilliseconds = 50;

	private readonly object _sync = new();
	private readonly Dictionary<int, OwnedLocalInstance> _entries = new();

	/// <summary>
	/// Retained launches whose PID key is already held by another entry. A live process can only
	/// collide with an entry whose root has already exited, and dropping either authority would
	/// abandon a live process or its tree, so these stay reachable by their exact (PID, token) pair.
	/// </summary>
	private readonly Dictionary<(int ProcessId, string Token), OwnedLocalInstance> _displaced = new();

	private bool _disposed;

	/// <summary>
	/// Fault seam for the focused harness: fires after a graceful poll delay completes and before
	/// the following cancellation check. Inert unless a test assigns it.
	/// </summary>
	internal Action GracefulDelayCompletedForTesting { get; set; }

	internal OwnedInstanceSnapshot LaunchSbox( bool windowed, Func<ulong, bool> visibleSyntheticIdentity )
	{
		if ( !OperatingSystem.IsWindows() )
			throw new OwnedInstanceException( "Local instance launching is supported only on Windows." );

		var currentExecutable = Environment.ProcessPath;
		if ( string.IsNullOrWhiteSpace( currentExecutable )
			|| !string.Equals( Path.GetFileName( currentExecutable ), "sbox-dev.exe", StringComparison.OrdinalIgnoreCase ) )
			throw new OwnedInstanceException( "Local instances can only be launched by sbox-dev.exe." );

		var directory = Path.GetDirectoryName( currentExecutable );
		var executable = directory is null ? null : Path.Combine( directory, "sbox.exe" );
		if ( executable is null || !File.Exists( executable ) )
			throw new OwnedInstanceException( "The sibling sbox.exe executable was not found." );

		var instanceNumber = AllocateInstanceNumber( visibleSyntheticIdentity );
		var arguments = new List<string> { "-joinlocal", "+instanceid", instanceNumber.ToString( CultureInfo.InvariantCulture ) };
		if ( windowed )
		{
			arguments.Add( "-sw" );
			arguments.Add( "-720" );
		}

		return LaunchContained( executable, arguments, directory, windowed, instanceNumber,
			ResolveLogDirectory(), LaunchFailureInjection.None );
	}

	internal OwnedInstanceSnapshot LaunchContainedForTesting( string executable, IReadOnlyList<string> arguments,
		string workingDirectory, bool windowed = false, int instanceNumber = 1,
		LaunchFailureInjection failure = LaunchFailureInjection.None )
	{
		return LaunchContained( Path.GetFullPath( executable ), arguments, Path.GetFullPath( workingDirectory ),
			windowed, instanceNumber, Path.GetFullPath( workingDirectory ), failure );
	}

	internal OwnedInstanceSnapshot[] List( bool pruneExited )
	{
		lock ( _sync )
		{
			ThrowIfDisposed();
			var rows = new List<(OwnedLocalInstance Entry, OwnedInstanceSnapshot Snapshot)>();
			foreach ( var entry in AllEntries().ToArray() )
			{
				if ( entry.CreationFileTime is null && !TryCaptureCreationIdentity( entry ) )
				{
					if ( pruneExited && IsSignaled( entry.ProcessHandle ) && ActiveProcessCount( entry ) == 0 )
						RemoveAndDispose( entry );
					continue;
				}

				rows.Add( (entry, Snapshot( entry )) );
			}

			if ( pruneExited )
			{
				foreach ( var row in rows )
				{
					if ( row.Snapshot.Exited && row.Snapshot.ActiveProcessCount == 0 )
						RemoveAndDispose( row.Entry );
				}
			}

			return rows.Select( row => row.Snapshot ).ToArray();
		}
	}

	internal bool TryGet( int processId, out OwnedLocalInstance instance )
	{
		lock ( _sync )
		{
			if ( _disposed )
			{
				instance = null;
				return false;
			}

			return _entries.TryGetValue( processId, out instance );
		}
	}

	internal async Task<OwnedTerminationOutcome> TerminateAsync( int processId, string launchTimestamp, bool abrupt,
		CancellationToken cancellationToken )
	{
		if ( processId <= 0 || processId == Environment.ProcessId )
			return OwnedTerminationOutcome.NotOwned( processId );

		OwnedLocalInstance entry;
		lock ( _sync )
		{
			ThrowIfDisposed();
			entry = FindOwned( processId, launchTimestamp );
			if ( entry is null )
				return AnyEntryFor( processId )
					? OwnedTerminationOutcome.IdentityMismatch( processId )
					: OwnedTerminationOutcome.NotOwned( processId );
		}

		cancellationToken.ThrowIfCancellationRequested();
		if ( !ValidateIdentity( entry, out _ ) )
			return OwnedTerminationOutcome.IdentityMismatch( processId );

		if ( IsSignaled( entry.ProcessHandle ) && ActiveProcessCount( entry ) == 0 )
		{
			RemoveAndDispose( entry );
			return OwnedTerminationOutcome.AlreadyExited( processId );
		}

		// A failed launch was never resumed: there is no window to close and nothing to wait for,
		// so it goes straight to the retained authority below.
		if ( !abrupt && !entry.FailedLaunch )
		{
			cancellationToken.ThrowIfCancellationRequested();
			TryCloseMainWindow( entry, IsSignaled( entry.ProcessHandle ) );

			var gracefulDeadline = Stopwatch.StartNew();
			while ( true )
			{
				// Honoured after every await, including the boundary where a poll delay completed
				// and its continuation was queued just as the lifetime was invalidated.
				cancellationToken.ThrowIfCancellationRequested();

				if ( ActiveProcessCount( entry ) == 0 )
				{
					RemoveAndDispose( entry );
					return OwnedTerminationOutcome.Terminated( processId, false, "The owned process job exited after the graceful close request." );
				}

				if ( gracefulDeadline.Elapsed >= GracefulBudget ) break;
				await Task.Delay( PollIntervalMilliseconds, cancellationToken );
				GracefulDelayCompletedForTesting?.Invoke();
			}

			// The last delay may have completed with the budget already spent; nothing may be
			// signaled until ownership is revalidated against a still-valid operation.
			cancellationToken.ThrowIfCancellationRequested();
		}

		if ( !ValidateIdentity( entry, out _ ) )
			return OwnedTerminationOutcome.IdentityMismatch( processId );

		cancellationToken.ThrowIfCancellationRequested();
		var (forced, forcedMessage) = TerminateOwnedTree( entry, processId );

		var forcedDeadline = Stopwatch.StartNew();
		while ( true )
		{
			cancellationToken.ThrowIfCancellationRequested();

			if ( ActiveProcessCount( entry ) == 0 && IsSignaled( entry.ProcessHandle ) )
			{
				RemoveAndDispose( entry );
				return OwnedTerminationOutcome.Terminated( processId, forced, forcedMessage );
			}

			if ( forcedDeadline.Elapsed >= ForcedBudget ) break;
			await Task.Delay( PollIntervalMilliseconds, cancellationToken );
		}

		throw new OwnedInstanceException( "Owned instance termination timed out.", processId );
	}



	internal void CorruptCreationIdentityForTesting( int processId )
	{
		lock ( _sync )
		{
			if ( _entries.TryGetValue( processId, out var entry ) && entry.CreationFileTime is long creation )
				entry.CreationFileTime = creation + 1;
		}
	}

	internal string ReplaceLaunchTimestampForTesting( int processId, string replacement )
	{
		lock ( _sync )
		{
			var entry = _entries[processId];
			var old = entry.LaunchedAt;
			entry.LaunchedAt = replacement;
			return old;
		}
	}

	public void Dispose()
	{
		OwnedLocalInstance[] entries;
		lock ( _sync )
		{
			if ( _disposed ) return;
			_disposed = true;
			entries = AllEntries().ToArray();
			_entries.Clear();
			_displaced.Clear();
		}

		foreach ( var entry in entries ) entry.Dispose();
	}

	private int AllocateInstanceNumber( Func<ulong, bool> visibleSyntheticIdentity )
	{
		for ( var attempt = 0; attempt < 256; attempt++ )
		{
			var candidate = RandomNumberGenerator.GetInt32( 1_000_000, int.MaxValue );
			lock ( _sync )
			{
				ThrowIfDisposed();
				if ( AllEntries().Any( x => x.InstanceNumber == candidate ) ) continue;
			}

			if ( visibleSyntheticIdentity?.Invoke( SyntheticSteamIdentityBase + (ulong)candidate ) == true ) continue;
			return candidate;
		}

		throw new OwnedInstanceException( "Could not allocate a unique local instance identity." );
	}

	private OwnedInstanceSnapshot LaunchContained( string executable, IReadOnlyList<string> arguments,
		string workingDirectory, bool windowed, int instanceNumber, string logDirectory,
		LaunchFailureInjection failure )
	{
		if ( !OperatingSystem.IsWindows() )
			throw new OwnedInstanceException( "Contained process launching is supported only on Windows." );
		if ( !File.Exists( executable ) )
			throw new OwnedInstanceException( "The local instance executable was not found." );

		SafeKernelHandle jobHandle = null;
		SafeKernelHandle processHandle = null;
		IntPtr threadHandle = IntPtr.Zero;
		OwnedLocalInstance entry = null;
		var processId = 0;
		var creationFileTime = 0L;
		var resolvedExecutable = (string)null;
		var assignedToJob = false;
		var resumed = false;

		try
		{
			jobHandle = NativeMethods.CreateJobObjectW( IntPtr.Zero, null );
			if ( jobHandle.IsInvalid )
				throw NativeFailure( "Could not create the private process job." );

			var startup = new NativeMethods.StartupInfo { Size = Marshal.SizeOf<NativeMethods.StartupInfo>() };
			var commandLine = new StringBuilder( BuildCommandLine( executable, arguments ) );
			if ( !NativeMethods.CreateProcessW( executable, commandLine, IntPtr.Zero, IntPtr.Zero, false,
				NativeMethods.CreateSuspended | NativeMethods.CreateNoWindow, IntPtr.Zero, workingDirectory,
				ref startup, out var processInfo ) )
				throw NativeFailure( "Could not create the suspended local instance." );

			processHandle = new SafeKernelHandle( processInfo.ProcessHandle, true );
			threadHandle = processInfo.ThreadHandle;
			processId = checked((int)processInfo.ProcessId);

			// Captured first and unconditionally, so every later failure path - including a failed
			// cleanup - either holds the exact creation-time token this process can be terminated by
			// or knows that none was ever captured and must not be invented.
			creationFileTime = failure.HasFlag( LaunchFailureInjection.CreationTimeCaptureFails )
				? throw new OwnedInstanceException( "Could not read the owned process creation identity.", processId, 5 )
				: GetCreationFileTime( processHandle, processId );

			if ( failure.HasFlag( LaunchFailureInjection.BeforeAssignment ) )
				throw new OwnedInstanceException( "Injected pre-assignment launch failure.", processId );

			var assigned = failure.HasFlag( LaunchFailureInjection.AssignmentFails )
				? false
				: NativeMethods.AssignProcessToJobObject( jobHandle, processHandle );
			if ( !assigned )
				throw new OwnedInstanceException( "Could not contain the local instance in its private process job.",
					processId, failure.HasFlag( LaunchFailureInjection.AssignmentFails ) ? 5 : Marshal.GetLastPInvokeError() );
			assignedToJob = true;

			if ( !NativeMethods.IsProcessInJob( processHandle, jobHandle, out var inJob ) || !inJob )
				throw NativeFailure( "Could not verify private process-job containment.", processId );

			if ( failure.HasFlag( LaunchFailureInjection.AfterAssignmentBeforeIdentity ) )
				throw new OwnedInstanceException( "Injected pre-identity launch failure.", processId );

			if ( failure.HasFlag( LaunchFailureInjection.ImagePathCaptureFails ) )
				throw new OwnedInstanceException( "Could not read the owned process image identity.", processId, 31 );
			resolvedExecutable = QueryImagePath( processHandle );

			var expectedExecutable = Path.GetFullPath( executable );
			if ( !string.Equals( NormalizePath( resolvedExecutable ), NormalizePath( expectedExecutable ), StringComparison.OrdinalIgnoreCase ) )
				throw new OwnedInstanceException( "The launched process image did not match the requested executable.", processId );

			entry = new OwnedLocalInstance( processHandle, jobHandle, processId, creationFileTime,
				resolvedExecutable, windowed, instanceNumber, Path.GetFullPath( logDirectory ), true,
				LaunchAuthority.Owned );
			processHandle = null;
			jobHandle = null;

			lock ( _sync )
			{
				ThrowIfDisposed();
				if ( _entries.ContainsKey( processId ) )
					throw new OwnedInstanceException( "The new process ID collided with an owned registry entry.", processId );
				_entries.Add( processId, entry );
			}

			if ( NativeMethods.ResumeThread( threadHandle ) == uint.MaxValue )
				throw NativeFailure( "Could not resume the contained local instance.", processId );
			resumed = true;
			return Snapshot( entry );
		}
		catch ( Exception launchError ) when ( !resumed )
		{
			var cleanup = CleanupSuspendedLaunch( entry, processHandle, jobHandle, processId,
				assignedToJob, creationFileTime, resolvedExecutable, windowed, instanceNumber, logDirectory, failure );
			if ( cleanup.RetainedProcess ) processHandle = null;
			if ( cleanup.RetainedJob ) jobHandle = null;

			if ( cleanup.Error != 0 )
				throw new OwnedInstanceException( "Local instance launch cleanup failed.", processId, cleanup.Error, launchError );
			throw;
		}
		finally
		{
			if ( threadHandle != IntPtr.Zero ) NativeMethods.CloseHandle( threadHandle );
			processHandle?.Dispose();
			jobHandle?.Dispose();
		}
	}

	/// <summary>
	/// Terminates a suspended, never-resumed launch through the authority actually held for it:
	/// its private job when assignment succeeded, otherwise the original process handle. On failure
	/// the available authority is retained in a distinct failed-launch state so it is not abandoned.
	/// </summary>
	private (int Error, bool RetainedProcess, bool RetainedJob) CleanupSuspendedLaunch( OwnedLocalInstance entry,
		SafeKernelHandle processHandle, SafeKernelHandle jobHandle, int processId, bool assignedToJob,
		long creationFileTime, string resolvedExecutable, bool windowed, int instanceNumber, string logDirectory,
		LaunchFailureInjection failure )
	{
		var error = 0;
		var job = entry?.JobHandle ?? jobHandle;
		var process = entry?.ProcessHandle ?? processHandle;
		var useJob = entry?.AssignedToJob ?? assignedToJob;

		if ( failure.HasFlag( LaunchFailureInjection.CleanupTerminationFails ) )
		{
			error = 5;
		}
		else if ( useJob && job is not null && !job.IsInvalid )
		{
			if ( !NativeMethods.TerminateJobObject( job, 1 ) ) error = Marshal.GetLastPInvokeError();
		}
		else if ( process is not null && !process.IsInvalid && !NativeMethods.TerminateProcess( process, 1 ) )
		{
			error = Marshal.GetLastPInvokeError();
		}

		if ( error == 0 && process is not null && !process.IsInvalid )
		{
			var wait = NativeMethods.WaitForSingleObject( process, 3_000 );
			if ( wait != NativeMethods.WaitObject0 )
				error = wait == NativeMethods.WaitFailed ? Marshal.GetLastPInvokeError() : 1460;
		}

		if ( error == 0 )
		{
			if ( entry is not null )
			{
				lock ( _sync )
				{
					if ( _entries.TryGetValue( processId, out var current ) && ReferenceEquals( current, entry ) )
						_entries.Remove( processId );
				}
				entry.Dispose();
			}
			return (0, false, false);
		}

		if ( entry is not null )
		{
			// The entry already holds both handles, so it becomes the retained authority itself.
			entry.MarkRetainedFailedLaunch();
			RetainFailedLaunch( entry );
			return (error, true, true);
		}

		if ( process is null || process.IsInvalid || processId <= 0 )
			return (error, false, false);

		// Retain what is actually held. The image path may be unavailable after a capture failure and
		// the process may never have joined its job; neither is required to keep cleanup authority,
		// and neither is presented as a contained, fully identified launch. A job handle that is not
		// taken into the entry stays with the launch scope, which releases it.
		var takeJob = useJob && job is not null && !job.IsInvalid;
		var candidate = new OwnedLocalInstance( process, takeJob ? job : null, processId,
			creationFileTime == 0 ? null : creationFileTime, resolvedExecutable, windowed, instanceNumber,
			Path.GetFullPath( logDirectory ), takeJob,
			creationFileTime == 0 ? LaunchAuthority.RetainedUnidentified : LaunchAuthority.RetainedFailedLaunch );
		RetainFailedLaunch( candidate, pidKeyOccupied: failure.HasFlag( LaunchFailureInjection.PidKeyOccupied ) );

		return (error, true, takeJob);
	}

	private OwnedInstanceSnapshot Snapshot( OwnedLocalInstance entry )
	{
		var exited = IsSignaled( entry.ProcessHandle );
		return new OwnedInstanceSnapshot( entry.ProcessId, entry.LaunchedAt, entry.Windowed,
			entry.LogDirectory, !exited, exited, checked((int)ActiveProcessCount( entry )) );
	}

	private static (bool Forced, string Message) TerminateOwnedTree( OwnedLocalInstance entry, int processId )
	{
		if ( entry.AssignedToJob )
		{
			if ( !NativeMethods.TerminateJobObject( entry.JobHandle, 1 ) )
				throw new OwnedInstanceException( "Owned instance termination failed.", processId, Marshal.GetLastPInvokeError() );
			return (true, "The owned process job was terminated.");
		}

		// Only a failed launch reaches here: it never joined its private job, so the original
		// suspended process handle is the authority, and it cannot be confused by PID reuse. No job
		// termination was invoked, so this must never be reported as a forced termination.
		if ( !NativeMethods.TerminateProcess( entry.ProcessHandle, 1 ) )
			throw new OwnedInstanceException( "Owned instance termination failed.", processId, Marshal.GetLastPInvokeError() );
		return (false, "The retained failed launch was terminated through its original process handle; no job termination was invoked.");
	}

	private bool ValidateIdentity( OwnedLocalInstance entry, out int nativeError )
	{
		nativeError = 0;
		if ( NativeMethods.GetProcessId( entry.ProcessHandle ) != (uint)entry.ProcessId )
		{
			nativeError = Marshal.GetLastPInvokeError();
			return false;
		}

		try
		{
			// A retained launch that never captured a creation time has nothing to compare against:
			// its authority is the retained handle itself, which the kernel pins to that exact
			// process object, and the job membership check below still applies when it was assigned.
			if ( entry.CreationFileTime is long creation
				&& GetCreationFileTime( entry.ProcessHandle, entry.ProcessId ) != creation )
				return false;

			// Windows no longer provides the image path after a process exits, and a failed launch
			// may never have captured one. Either way the retained handle, exact creation time and
			// job membership continue to pin that same process.
			if ( entry.ExecutablePath is not null && !IsSignaled( entry.ProcessHandle )
				&& !string.Equals( NormalizePath( QueryImagePath( entry.ProcessHandle ) ),
					NormalizePath( entry.ExecutablePath ), StringComparison.OrdinalIgnoreCase ) )
				return false;
		}
		catch ( OwnedInstanceException ex )
		{
			nativeError = ex.NativeError;
			return false;
		}

		// Membership is only meaningful for a process this registry actually assigned.
		if ( !entry.AssignedToJob ) return true;

		if ( !NativeMethods.IsProcessInJob( entry.ProcessHandle, entry.JobHandle, out var inJob ) )
		{
			nativeError = Marshal.GetLastPInvokeError();
			return false;
		}

		return inJob;
	}

	private static void TryCloseMainWindow( OwnedLocalInstance entry, bool rootExited )
	{
		if ( rootExited || entry.CreationFileTime is null ) return;

		// Post WM_CLOSE rather than Process.CloseMainWindow: PostMessage returns without waiting for
		// the child window thread, so an unresponsive client cannot stall the editor's main thread.
		NativeMethods.EnumWindows( (window, _) =>
		{
			NativeMethods.GetWindowThreadProcessId( window, out var processId );
			if ( processId == (uint)entry.ProcessId )
				NativeMethods.PostMessageW( window, NativeMethods.WindowMessageClose, IntPtr.Zero, IntPtr.Zero );
			return true;
		}, IntPtr.Zero );
	}

	/// <summary>Every retained entry, whether it holds its PID key or a displaced (PID, token) pair.</summary>
	private IEnumerable<OwnedLocalInstance> AllEntries() => _entries.Values.Concat( _displaced.Values );

	/// <summary>The entry whose exact (PID, token) pair matches, in either retention map.</summary>
	private OwnedLocalInstance FindOwned( int processId, string launchTimestamp )
	{
		if ( launchTimestamp is null ) return null;
		if ( _entries.TryGetValue( processId, out var primary )
			&& primary.CreationFileTime is not null
			&& string.Equals( primary.LaunchedAt, launchTimestamp, StringComparison.Ordinal ) )
			return primary;

		if ( _displaced.TryGetValue( (processId, launchTimestamp), out var displaced )
			&& displaced.CreationFileTime is not null )
			return displaced;

		return null;
	}

	/// <summary>Whether either retention map still claims this PID, whatever token was supplied.</summary>
	private bool AnyEntryFor( int processId )
	{
		if ( _entries.ContainsKey( processId ) ) return true;
		foreach ( var identity in _displaced.Keys )
		{
			if ( identity.ProcessId == processId ) return true;
		}

		return false;
	}

	/// <summary>
	/// Publishes a launch that never resumed as a retained failed launch. No caller of this method
	/// disposes or closes the candidate's handles: the authority is always published, and a PID key
	/// already held by another entry is never overwritten, because dropping either authority would
	/// abandon a live process or its tree. Such an entry stays reachable by its exact token, which
	/// termination already matches together with the PID. pidKeyOccupied is the harness seam that
	/// asserts another entry already holds that PID key.
	/// </summary>
	private void RetainFailedLaunch( OwnedLocalInstance candidate, bool pidKeyOccupied = false )
	{
		lock ( _sync )
		{
			if ( _entries.TryGetValue( candidate.ProcessId, out var current ) )
			{
				// Already published under its own PID key; nothing to move or duplicate.
				if ( ReferenceEquals( current, candidate ) ) return;
			}
			else if ( !pidKeyOccupied )
			{
				_entries.Add( candidate.ProcessId, candidate );
				return;
			}

			_displaced[(candidate.ProcessId, candidate.LaunchedAt)] = candidate;
		}
	}

	private void RemoveAndDispose( OwnedLocalInstance entry )
	{
		lock ( _sync )
		{
			if ( _entries.TryGetValue( entry.ProcessId, out var current ) && ReferenceEquals( current, entry ) )
				_entries.Remove( entry.ProcessId );
			else if ( !_displaced.Remove( (entry.ProcessId, entry.LaunchedAt) ) )
				return;
		}
		entry.Dispose();
	}

	private static uint ActiveProcessCount( OwnedLocalInstance entry )
	{
		if ( entry.AssignedToJob ) return GetActiveProcessCount( entry.JobHandle );
		return IsSignaled( entry.ProcessHandle ) ? 0u : 1u;
	}

	private bool TryCaptureCreationIdentity( OwnedLocalInstance entry )
	{
		try
		{
			var creation = GetCreationFileTime( entry.ProcessHandle, entry.ProcessId );
			var wasDisplaced = _displaced.Remove( (entry.ProcessId, entry.LaunchedAt) );
			entry.CaptureCreationIdentity( creation );
			if ( wasDisplaced ) _displaced.Add( (entry.ProcessId, entry.LaunchedAt), entry );
			return true;
		}
		catch ( OwnedInstanceException )
		{
			return false;
		}
	}

	private static uint GetActiveProcessCount( SafeKernelHandle jobHandle )
	{
		if ( !NativeMethods.QueryInformationJobObject( jobHandle, NativeMethods.JobObjectBasicAccountingInformation,
			out var accounting, (uint)Marshal.SizeOf<NativeMethods.JobBasicAccountingInformation>(), IntPtr.Zero ) )
			throw NativeFailure( "Could not inspect the owned process job." );
		return accounting.ActiveProcesses;
	}

	private static bool IsSignaled( SafeKernelHandle processHandle )
	{
		var result = NativeMethods.WaitForSingleObject( processHandle, 0 );
		if ( result == NativeMethods.WaitObject0 ) return true;
		if ( result == NativeMethods.WaitTimeout ) return false;
		throw NativeFailure( "Could not inspect the owned process state." );
	}

	private static long GetCreationFileTime( SafeKernelHandle processHandle, int processId = 0 )
	{
		if ( !NativeMethods.GetProcessTimes( processHandle, out var creation, out _, out _, out _ ) )
			throw new OwnedInstanceException( "Could not read the owned process creation identity.", processId,
				Marshal.GetLastPInvokeError() );
		return creation.ToInt64();
	}

	private static string QueryImagePath( SafeKernelHandle processHandle )
	{
		var capacity = 32768u;
		var buffer = new StringBuilder( checked((int)capacity) );
		if ( !NativeMethods.QueryFullProcessImageNameW( processHandle, 0, buffer, ref capacity ) )
			throw NativeFailure( "Could not read the owned process image identity." );
		return Path.GetFullPath( buffer.ToString() );
	}

	private static string NormalizePath( string path ) => Path.TrimEndingDirectorySeparator( Path.GetFullPath( path ));

	private static string ResolveLogDirectory()
	{
		var root = Environment.GetEnvironmentVariable( "FACEPUNCH_ENGINE", EnvironmentVariableTarget.User );
		if ( string.IsNullOrWhiteSpace( root ) ) root = AppContext.BaseDirectory;
		return Path.GetFullPath( Path.Combine( root, "logs" ) );
	}

	private static string BuildCommandLine( string executable, IReadOnlyList<string> arguments )
	{
		var builder = new StringBuilder();
		AppendQuotedArgument( builder, executable );
		foreach ( var argument in arguments )
		{
			builder.Append( ' ' );
			AppendQuotedArgument( builder, argument ?? string.Empty );
		}
		return builder.ToString();
	}

	private static void AppendQuotedArgument( StringBuilder builder, string argument )
	{
		builder.Append( '"' );
		var slashes = 0;
		foreach ( var character in argument )
		{
			if ( character == '\\' )
			{
				slashes++;
				continue;
			}
			if ( character == '"' )
			{
				builder.Append( '\\', slashes * 2 + 1 );
				builder.Append( '"' );
				slashes = 0;
				continue;
			}
			builder.Append( '\\', slashes );
			slashes = 0;
			builder.Append( character );
		}
		builder.Append( '\\', slashes * 2 );
		builder.Append( '"' );
	}

	private static OwnedInstanceException NativeFailure( string message, int processId = 0 )
	{
		var error = Marshal.GetLastPInvokeError();
		return new OwnedInstanceException( message, processId, error, new Win32Exception( error ));
	}

	private void ThrowIfDisposed()
	{
		if ( _disposed ) throw new ObjectDisposedException( nameof(OwnedInstanceRegistry) );
	}
}

/// <summary>
/// The authority a registry entry holds. A launch that never resumed and whose cleanup could not
/// confirm termination is retained in one of the failed-launch states instead of being abandoned.
/// </summary>
internal enum LaunchAuthority
{
	/// <summary>A fully identified launch whose process was resumed.</summary>
	Owned = 0,

	/// <summary>
	/// A never-resumed launch retained after its cleanup failed with its exact creation time known,
	/// so its token is that creation time and job membership still constrains it when assigned.
	/// </summary>
	RetainedFailedLaunch = 1,

	/// <summary>
	/// A never-resumed launch retained after cleanup failed before its creation time could be read.
	/// It remains internal until the retained handle yields the exact creation identity; it is never
	/// exposed with a fabricated or overloaded public timestamp.
	/// </summary>
	RetainedUnidentified = 2
}

/// <summary>Fault seams used by the focused ownership harness. Production always passes None.</summary>
[Flags]
internal enum LaunchFailureInjection
{
	None = 0,
	BeforeAssignment = 1,
	AfterAssignmentBeforeIdentity = 2,
	AssignmentFails = 4,
	ImagePathCaptureFails = 8,
	CleanupTerminationFails = 16,

	/// <summary>Fails the creation-time read, so no creation identity is ever captured.</summary>
	CreationTimeCaptureFails = 32,

	/// <summary>Retains a failed launch as if its PID key were already held by another entry.</summary>
	PidKeyOccupied = 64
}

internal sealed record OwnedInstanceSnapshot( int ProcessId, string LaunchedAt, bool Windowed,
	string LogDirectory, bool Alive, bool Exited, int ActiveProcessCount );

internal sealed record OwnedTerminationOutcome( int ProcessId, string Result, bool Forced, string Message )
{
	internal static OwnedTerminationOutcome Terminated( int processId, bool forced, string message ) =>
		new( processId, "terminated", forced, message );
	internal static OwnedTerminationOutcome AlreadyExited( int processId ) =>
		new( processId, "already exited", false, "The owned process job had already exited." );
	internal static OwnedTerminationOutcome NotOwned( int processId ) =>
		new( processId, "not owned", false, "No current in-memory ownership entry matches that process." );
	internal static OwnedTerminationOutcome IdentityMismatch( int processId ) =>
		new( processId, "identity mismatch", false, "The supplied or retained launch identity did not match; no process was signaled." );
}

internal sealed class OwnedInstanceException : Exception
{
	internal OwnedInstanceException( string message, int processId = 0, int nativeError = 0, Exception inner = null )
		: base( message, inner )
	{
		ProcessId = processId;
		NativeError = nativeError;
	}
	internal int ProcessId { get; }
	internal int NativeError { get; }
}

internal sealed class SafeKernelHandle : SafeHandleZeroOrMinusOneIsInvalid
{
	internal SafeKernelHandle() : base( true ) { }
	internal SafeKernelHandle( IntPtr handle, bool ownsHandle ) : base( ownsHandle ) => SetHandle( handle );
	protected override bool ReleaseHandle() => NativeMethods.CloseHandle( handle );
}

internal static class NativeMethods
{
	internal const uint CreateSuspended = 0x00000004;
	internal const uint CreateNoWindow = 0x08000000;
	internal const uint WaitObject0 = 0x00000000;
	internal const uint WaitTimeout = 0x00000102;
	internal const uint WaitFailed = 0xFFFFFFFF;
	internal const int JobObjectBasicAccountingInformation = 1;
	internal const uint WindowMessageClose = 0x0010;

	[StructLayout( LayoutKind.Sequential, CharSet = CharSet.Unicode )]
	internal struct StartupInfo
	{
		internal int Size;
		internal string Reserved;
		internal string Desktop;
		internal string Title;
		internal uint X;
		internal uint Y;
		internal uint XSize;
		internal uint YSize;
		internal uint XCountChars;
		internal uint YCountChars;
		internal uint FillAttribute;
		internal uint Flags;
		internal ushort ShowWindow;
		internal ushort Reserved2;
		internal IntPtr Reserved2Pointer;
		internal IntPtr StandardInput;
		internal IntPtr StandardOutput;
		internal IntPtr StandardError;
	}

	[StructLayout( LayoutKind.Sequential )]
	internal struct ProcessInformation
	{
		internal IntPtr ProcessHandle;
		internal IntPtr ThreadHandle;
		internal uint ProcessId;
		internal uint ThreadId;
	}

	[StructLayout( LayoutKind.Sequential )]
	internal struct FileTime
	{
		internal uint Low;
		internal uint High;
		internal long ToInt64() => unchecked((long)(((ulong)High << 32) | Low));
	}

	[StructLayout( LayoutKind.Sequential )]
	internal struct JobBasicAccountingInformation
	{
		internal long TotalUserTime;
		internal long TotalKernelTime;
		internal long ThisPeriodTotalUserTime;
		internal long ThisPeriodTotalKernelTime;
		internal uint TotalPageFaultCount;
		internal uint TotalProcesses;
		internal uint ActiveProcesses;
		internal uint TotalTerminatedProcesses;
	}

	internal delegate bool EnumWindowsCallback( IntPtr window, IntPtr state );

	[DllImport( "user32.dll", SetLastError = true )]
	[return: MarshalAs( UnmanagedType.Bool )]
	internal static extern bool EnumWindows( EnumWindowsCallback callback, IntPtr state );

	[DllImport( "user32.dll", SetLastError = true )]
	internal static extern uint GetWindowThreadProcessId( IntPtr window, out uint processId );

	[DllImport( "user32.dll", EntryPoint = "PostMessageW", SetLastError = true )]
	[return: MarshalAs( UnmanagedType.Bool )]
	internal static extern bool PostMessageW( IntPtr window, uint message, IntPtr wParam, IntPtr lParam );

	[DllImport( "kernel32.dll", EntryPoint = "CreateJobObjectW", CharSet = CharSet.Unicode, SetLastError = true )]
	internal static extern SafeKernelHandle CreateJobObjectW( IntPtr jobAttributes, string name );

	[DllImport( "kernel32.dll", EntryPoint = "CreateProcessW", CharSet = CharSet.Unicode, SetLastError = true )]
	[return: MarshalAs( UnmanagedType.Bool )]
	internal static extern bool CreateProcessW( string applicationName, StringBuilder commandLine,
		IntPtr processAttributes, IntPtr threadAttributes, [MarshalAs( UnmanagedType.Bool )] bool inheritHandles,
		uint creationFlags, IntPtr environment, string currentDirectory, ref StartupInfo startupInfo,
		out ProcessInformation processInformation );

	[DllImport( "kernel32.dll", SetLastError = true )]
	[return: MarshalAs( UnmanagedType.Bool )]
	internal static extern bool AssignProcessToJobObject( SafeKernelHandle job, SafeKernelHandle process );

	[DllImport( "kernel32.dll", SetLastError = true )]
	[return: MarshalAs( UnmanagedType.Bool )]
	internal static extern bool IsProcessInJob( SafeKernelHandle process, SafeKernelHandle job,
		[MarshalAs( UnmanagedType.Bool )] out bool result );

	[DllImport( "kernel32.dll", SetLastError = true )]
	[return: MarshalAs( UnmanagedType.Bool )]
	internal static extern bool GetProcessTimes( SafeKernelHandle process, out FileTime creation,
		out FileTime exit, out FileTime kernel, out FileTime user );

	[DllImport( "kernel32.dll", EntryPoint = "QueryFullProcessImageNameW", CharSet = CharSet.Unicode, SetLastError = true )]
	[return: MarshalAs( UnmanagedType.Bool )]
	internal static extern bool QueryFullProcessImageNameW( SafeKernelHandle process, uint flags,
		StringBuilder imagePath, ref uint size );

	[DllImport( "kernel32.dll", SetLastError = true )]
	internal static extern uint GetProcessId( SafeKernelHandle process );

	[DllImport( "kernel32.dll", SetLastError = true )]
	internal static extern uint ResumeThread( IntPtr thread );

	[DllImport( "kernel32.dll", SetLastError = true )]
	[return: MarshalAs( UnmanagedType.Bool )]
	internal static extern bool TerminateProcess( SafeKernelHandle process, uint exitCode );

	[DllImport( "kernel32.dll", SetLastError = true )]
	[return: MarshalAs( UnmanagedType.Bool )]
	internal static extern bool TerminateJobObject( SafeKernelHandle job, uint exitCode );

	[DllImport( "kernel32.dll", SetLastError = true )]
	[return: MarshalAs( UnmanagedType.Bool )]
	internal static extern bool QueryInformationJobObject( SafeKernelHandle job, int informationClass,
		out JobBasicAccountingInformation information, uint informationLength, IntPtr returnLength );

	[DllImport( "kernel32.dll", SetLastError = true )]
	internal static extern uint WaitForSingleObject( SafeKernelHandle handle, uint milliseconds );

	[DllImport( "kernel32.dll", SetLastError = true )]
	[return: MarshalAs( UnmanagedType.Bool )]
	internal static extern bool CloseHandle( IntPtr handle );
}
