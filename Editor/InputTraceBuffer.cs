using System;
using System.Collections.Generic;
using System.Linq;

namespace Editor.Mcp;

/// <summary>Bounded in-memory observation; owns no Play session and never supplies input.</summary>
internal sealed class InputTraceBuffer
{
	private readonly List<object> _samples = [];
	internal string Token { get; private set; } = "";
	internal string State { get; private set; } = "idle";
	internal string Label { get; private set; } = "";
	internal int FrameLimit { get; private set; }
	internal double SecondsLimit { get; private set; }
	internal double Started { get; private set; }
	internal int Count => _samples.Count;
	internal bool Recording => State == "recording";

	internal void Start( int frameLimit, double seconds, string label, double now )
	{
		if ( Recording ) throw new InvalidOperationException( "Stop the current trace with its exact token first." );
		if ( frameLimit < 2 || frameLimit > 600 || !double.IsFinite( seconds ) || seconds < 1 || seconds > 30
			|| label is null || label.Length > 64 || !double.IsFinite( now ) )
			throw new ArgumentException( "Limits: 2..600 editor frames, 1..30 seconds, label <=64 characters." );
		Token = Guid.NewGuid().ToString( "N" );
		FrameLimit = frameLimit; SecondsLimit = seconds; Label = label; Started = now;
		_samples.Clear(); State = "recording";
	}

	internal void CheckLifetime( bool sameContext, double now )
	{
		if ( !Recording ) return;
		if ( !sameContext ) State = "context-lost";
		else if ( !double.IsFinite( now ) || now < Started || now - Started >= SecondsLimit ) State = "time-limit";
	}

	internal void Add( object sample )
	{
		if ( !Recording ) return;
		_samples.Add( sample );
		if ( _samples.Count >= FrameLimit ) State = "frame-limit";
	}

	internal void Stop( string token )
	{
		RequireToken( token );
		if ( Recording ) State = "stopped";
	}

	internal object[] Read( string token, int offset, int count )
	{
		RequireToken( token );
		if ( offset < 0 || count < 1 || count > 90 ) throw new ArgumentException( "Offset >=0 and count 1..90 required." );
		return _samples.Skip( offset ).Take( count ).ToArray();
	}

	private void RequireToken( string token )
	{
		if ( Token.Length == 0 || token != Token ) throw new InvalidOperationException( "Exact current trace token required." );
	}
}
