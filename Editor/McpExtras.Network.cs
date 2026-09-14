using Sandbox;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Editor.Mcp;

public static partial class ExtrasTools
{
	private static NetworkToolLifetime NetworkLifetime = new();
	private static readonly Regex Steam2Pattern = new( @"STEAM_[0-5]:[01]:\d+", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds( 100 ) );
	private static readonly Regex Steam3Pattern = new( @"\[[A-Za-z]:\d+:\d+\]", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds( 100 ) );
	private static readonly Regex Steam64Pattern = new( @"(?<!\d)\d{17}(?!\d)", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds( 100 ) );

	/// <summary>Inspect the active network session without exposing account, party, chat, voice, or credential data.</summary>
	[McpTool.ReadOnly( "x_network_status" )]
	public static NetworkState GetNetworkStatus()
	{
		return SnapshotNetwork();
	}

	/// <summary>Start hosting through the editor's supported network path and return the observed state.</summary>
	[McpTool( "x_network_start_hosting" )]
	public static NetworkState StartNetworkHosting()
	{
		var lifetime = CurrentLifetime();
		using var operation = lifetime.EnterMutation();
		if ( EditorUtility.Network.Active || Networking.IsConnecting )
			throw new Exception( "Already connected or connecting; disconnect first." );

		try
		{
			EditorUtility.Network.StartHosting();
		}
		catch ( Exception )
		{
			throw new Exception( "Starting hosting failed; the editor reported an error and no other change was made." );
		}

		lifetime.ThrowIfInvalid();
		return SnapshotNetwork();
	}

	/// <summary>Disconnect the editor from its current network session without terminating owned child instances.</summary>
	[McpTool( "x_network_disconnect" )]
	public static NetworkState DisconnectNetwork()
	{
		var lifetime = CurrentLifetime();
		using var operation = lifetime.EnterMutation();
		if ( !EditorUtility.Network.Active && !Networking.IsConnecting )
			throw new Exception( "No network session is active." );

		try
		{
			EditorUtility.Network.Disconnect();
		}
		catch ( Exception )
		{
			throw new Exception( "Disconnecting failed; the editor reported an error and owned instances were left untouched." );
		}

		lifetime.ThrowIfInvalid();
		return SnapshotNetwork();
	}

	/// <summary>
	/// Launch a fixed local sbox client contained in a private process job. The argument list is fixed
	/// and deliberately does not copy arbitrary editor preference command-line arguments.
	/// </summary>
	[McpTool( "x_network_spawn_instance" )]
	public static InstanceState SpawnNetworkInstance( bool? windowed = null )
	{
		var lifetime = CurrentLifetime();
		using var operation = lifetime.EnterMutation();
		RequireStableHosting();
		var effectiveWindowed = windowed ?? EditorPreferences.WindowedLocalInstances;

		OwnedInstanceSnapshot snapshot;
		try
		{
			snapshot = lifetime.Registry.LaunchSbox( effectiveWindowed, IsSyntheticIdentityVisible );
		}
		catch ( OwnedInstanceException ex )
		{
			throw TranslateLaunchFailure( ex );
		}
		catch ( Exception )
		{
			throw new Exception( "Local instance launch setup failed; no process was started." );
		}

		lifetime.ThrowIfInvalid();
		return MapInstance( snapshot );
	}

	/// <summary>List only this hotload lifetime's in-memory owned process jobs; never enumerate OS processes.</summary>
	[McpTool.ReadOnly( "x_network_instances" )]
	public static InstanceState[] GetNetworkInstances()
	{
		var lifetime = CurrentLifetime();
		try
		{
			return lifetime.Registry.List( !lifetime.IsBusy ).Select( MapInstance ).ToArray();
		}
		catch ( OwnedInstanceException )
		{
			throw new Exception( "Owned instance inspection failed." );
		}
	}

	/// <summary>
	/// Refuse local-client host migration before launching anything. Installed engine 26.09.08e
	/// chooses successors from Steam lobby membership, which synthetic loopback clients cannot join,
	/// and exposes no supported API that can target or certify a local successor.
	/// </summary>
	[McpTool( "x_network_migrate_to_new_instance" )]
	public static Task<NetworkState> MigrateNetworkToNewInstance( bool? windowed = null, int timeoutSeconds = 120 )
	{
		var lifetime = CurrentLifetime();
		using var operation = lifetime.EnterMutation();
		if ( timeoutSeconds < 1 || timeoutSeconds > 120 )
			throw new Exception( "timeoutSeconds must be between 1 and 120." );
		if ( !IsStableHosting() || GetRemoteConnections().Length != 0 )
			throw new Exception( "Migration requires a hosting session with no remote peers." );

		throw new Exception( "The installed engine cannot migrate hosting to a synthetic local client; no process was launched and the editor remains connected." );
	}

	/// <summary>
	/// Terminate exactly one currently owned process job. Both the PID and the exact creation-time
	/// LaunchedAt token returned by spawn or listing are required. Retained cleanup authority stays
	/// internal until that exact identity is readable. Abrupt skips the two-second graceful close budget.
	/// </summary>
	[McpTool( "x_network_terminate_instance" )]
	public static async Task<InstanceTermination> TerminateNetworkInstance( int processId, string launchTimestamp, bool abrupt = false )
	{
		var lifetime = CurrentLifetime();
		using var operation = lifetime.EnterMutation();
		try
		{
			var outcome = await lifetime.Registry.TerminateAsync( processId, launchTimestamp, abrupt, lifetime.CancellationToken );
			lifetime.ThrowIfInvalid();
			return new InstanceTermination
			{
				ProcessId = outcome.ProcessId,
				Result = outcome.Result,
				Forced = outcome.Forced,
				Message = outcome.Message
			};
		}
		catch ( OperationCanceledException ) when ( !lifetime.IsValid )
		{
			throw new Exception( NetworkToolLifetime.InvalidatedMessage );
		}
		catch ( OwnedInstanceException ex )
		{
			var native = ex.NativeError > 0 ? $"; native error {ex.NativeError}" : string.Empty;
			throw new Exception( $"{ex.Message} PID {processId}{native}. Ownership is retained; the instance stays listed." );
		}
	}

	/// <summary>A privacy-limited snapshot of the active editor network session.</summary>
	public class NetworkState
	{
		public bool IsActive { get; set; }
		public bool IsHost { get; set; }
		public bool IsClient { get; set; }
		public bool IsConnecting { get; set; }
		public Guid? LocalConnectionId { get; set; }
		public Guid? HostConnectionId { get; set; }
		public ConnectionState[] Connections { get; set; }
	}

	/// <summary>A visible connection with account and party identifiers deliberately omitted.</summary>
	public class ConnectionState
	{
		public Guid Id { get; set; }
		public string DisplayName { get; set; }
		public bool IsHost { get; set; }
		public bool IsLocal { get; set; }
		public bool IsActive { get; set; }
		public bool IsConnecting { get; set; }
		public float Ping { get; set; }
	}

	/// <summary>
	/// An in-memory owned local process descriptor. LaunchedAt is the exact UTC process creation time
	/// and its termination token; unidentified cleanup authority is not exposed until this is readable.
	/// </summary>
	public class InstanceState
	{
		public int ProcessId { get; set; }
		public string LaunchedAt { get; set; }
		public bool Windowed { get; set; }
		public string LogDirectory { get; set; }
		public bool Alive { get; set; }
		public bool Exited { get; set; }
		public int ActiveProcessCount { get; set; }
	}

	/// <summary>The bounded result of an ownership-checked termination request.</summary>
	public class InstanceTermination
	{
		public int ProcessId { get; set; }
		public string Result { get; set; }
		public bool Forced { get; set; }
		public string Message { get; set; }
	}

	private static NetworkToolLifetime CurrentLifetime()
	{
		var lifetime = NetworkLifetime;
		lifetime.ThrowIfInvalid();
		return lifetime;
	}

	private static void RequireStableHosting()
	{
		if ( !IsStableHosting() ) throw new Exception( "Editor must be hosting first." );
	}

	private static bool IsStableHosting()
	{
		return EditorUtility.Network.Hosting && Networking.IsActive && !Networking.IsConnecting;
	}

	private static NetworkState SnapshotNetwork()
	{
		var active = Networking.IsActive;
		if ( !active )
		{
			return new NetworkState
			{
				IsActive = false,
				IsHost = false,
				IsClient = false,
				IsConnecting = Networking.IsConnecting,
				LocalConnectionId = null,
				HostConnectionId = null,
				Connections = Array.Empty<ConnectionState>()
			};
		}

		var local = Connection.Local;
		var host = Connection.Host;
		var localId = local?.Id;
		var hostId = host?.Id;
		var connections = Connection.All
			.Where( connection => connection is not null )
			.Select( connection => new ConnectionState
			{
				Id = connection.Id,
				DisplayName = SanitizeDisplayName( connection.DisplayName ),
				IsHost = hostId.HasValue && connection.Id == hostId.Value,
				IsLocal = localId.HasValue && connection.Id == localId.Value,
				IsActive = connection.IsActive,
				IsConnecting = connection.IsConnecting,
				Ping = connection.Ping
			})
			.ToArray();

		return new NetworkState
		{
			IsActive = true,
			IsHost = Networking.IsHost,
			IsClient = Networking.IsClient,
			IsConnecting = Networking.IsConnecting,
			LocalConnectionId = localId,
			HostConnectionId = hostId,
			Connections = connections
		};
	}

	private static Connection[] GetRemoteConnections()
	{
		var localId = Connection.Local?.Id;
		return Connection.All
			.Where( connection => connection is not null && (!localId.HasValue || connection.Id != localId.Value) )
			.ToArray();
	}

	private static bool IsSyntheticIdentityVisible( ulong identity )
	{
		return Connection.All.Any( connection => connection is not null && connection.SteamId.ValueUnsigned == identity );
	}

	private static string SanitizeDisplayName( string value )
	{
		if ( string.IsNullOrEmpty( value ) ) return string.Empty;
		var clean = new StringBuilder( Math.Min( value.Length, 512 ) );
		foreach ( var character in value )
		{
			if ( clean.Length == 512 ) break;
			if ( !char.IsControl( character ) ) clean.Append( character );
		}

		var result = Steam2Pattern.Replace( clean.ToString(), "[redacted]" );
		result = Steam3Pattern.Replace( result, "[redacted]" );
		result = Steam64Pattern.Replace( result, "[redacted]" );
		return result.Length <= 128 ? result : result[..128];
	}

	private static InstanceState MapInstance( OwnedInstanceSnapshot snapshot )
	{
		return new InstanceState
		{
			ProcessId = snapshot.ProcessId,
			LaunchedAt = snapshot.LaunchedAt,
			Windowed = snapshot.Windowed,
			LogDirectory = snapshot.LogDirectory,
			Alive = snapshot.Alive,
			Exited = snapshot.Exited,
			ActiveProcessCount = snapshot.ActiveProcessCount
		};
	}

	private static Exception TranslateLaunchFailure( OwnedInstanceException exception )
	{
		var process = exception.ProcessId > 0 ? $" PID {exception.ProcessId}." : string.Empty;
		var native = exception.NativeError > 0 ? $" Native error {exception.NativeError}." : string.Empty;
		return new Exception( exception.Message + process + native );
	}

}

internal sealed class NetworkToolLifetime : IHotloadManaged
{
	internal const string BusyMessage = "Another network operation is in progress; wait for it to finish.";
	internal const string InvalidatedMessage = "The network tools were invalidated by a library hotload; retry the operation.";

	private OwnedInstanceRegistry _registry = new();
	private CancellationTokenSource _cancellation = new();
	private long _busy;
	private int _valid = 1;

	// Null only after invalidation released the handles; callers report the fixed invalidation error.
	internal OwnedInstanceRegistry Registry
	{
		get
		{
			var registry = Volatile.Read( ref _registry );
			if ( registry is null ) throw new Exception( NetworkToolLifetime.InvalidatedMessage );
			return registry;
		}
	}

	internal CancellationToken CancellationToken => _cancellation.Token;
	internal bool IsBusy => Volatile.Read( ref _busy ) != 0;
	internal bool IsValid => Volatile.Read( ref _valid ) != 0;

	internal IDisposable EnterMutation()
	{
		ThrowIfInvalid();
		if ( Interlocked.CompareExchange( ref _busy, 1, 0 ) != 0 )
			throw new Exception( BusyMessage );
		if ( !IsValid )
		{
			Volatile.Write( ref _busy, 0 );
			throw new Exception( NetworkToolLifetime.InvalidatedMessage );
		}
		return new MutationLease( this );
	}

	internal void ThrowIfInvalid()
	{
		if ( !IsValid ) throw new Exception( NetworkToolLifetime.InvalidatedMessage );
	}

	public void Destroyed( Dictionary<string, object> state ) => Invalidate();

	// Hotload state is deliberately not transferred: surviving children cease to be owned.

	public void Created( IReadOnlyDictionary<string, object> state )
	{
		_registry = new OwnedInstanceRegistry();
		_cancellation = new CancellationTokenSource();
		Volatile.Write( ref _busy, 0 );
		Volatile.Write( ref _valid, 1 );
	}

	public void Persisted() { }
	public void Failed() => Invalidate();

	private void Invalidate()
	{
		Volatile.Write( ref _valid, 0 );
		_cancellation.Cancel();

		// No new mutation can start once invalid, so the only reason to wait is an operation
		// still holding retained handles. Closing them never terminates a process.
		ReleaseHandlesWhenIdle();
	}

	private void ReleaseHandlesWhenIdle()
	{
		if ( Volatile.Read( ref _busy ) != 0 ) return;
		Interlocked.Exchange( ref _registry, null )?.Dispose();
	}

	private sealed class MutationLease : IDisposable
	{
		private NetworkToolLifetime _owner;
		internal MutationLease( NetworkToolLifetime owner ) => _owner = owner;
		public void Dispose()
		{
			var owner = Interlocked.Exchange( ref _owner, null );
			if ( owner is null ) return;
			Volatile.Write( ref owner._busy, 0 );
			if ( !owner.IsValid ) owner.ReleaseHandlesWhenIdle();
		}
	}
}
