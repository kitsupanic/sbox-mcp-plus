using System;
using Sandbox;
using Mouse = Sandbox.Mouse;

namespace Editor.Mcp;

public static partial class ExtrasTools
{
	private static readonly InputTraceBuffer InputTrace = new();
	private static Scene _inputTraceScene;
    private static Guid _inputTraceSceneId;
	private static Project _inputTraceProject;
	private static string _inputTraceAction = "Attack1";

	/// <summary>Reads engine input and camera state without supplying input or changing Play.</summary>
	[McpTool.ReadOnly( "x_input_observe" )]
	public static object ObserveInput( string action = "Attack1" )
	{
		ValidateInputAction( action );
		if ( !Sandbox.Application.IsEditor ) throw new InvalidOperationException( "Editor observation only." );
		var scene = Game.ActiveScene;
		var look = global::Sandbox.Input.AnalogLook;
		var camera = scene?.Camera;
		return new
		{
			Layer = "engine-input-observation; input-source-undetermined; not-physical-device-coverage",
			Playing = Game.IsPlaying, Paused = Game.IsPaused, Focused = Sandbox.Application.IsFocused,
			Suppressed = global::Sandbox.Input.Suppressed, MouseVisibility = global::Sandbox.Mouse.Visibility.ToString(),
			SceneId = scene?.Id, ScenePath = scene?.Source?.ResourcePath,
			Project = Project.Current?.Config?.Ident, Action = action,
			Down = global::Sandbox.Input.Down( action ), Pressed = global::Sandbox.Input.Pressed( action ), Released = global::Sandbox.Input.Released( action ),
			AnalogLook = new { Pitch = look.pitch, Yaw = look.yaw, Roll = look.roll },
			CameraId = camera?.Id,
			CameraRotation = camera is null ? null : new float[] { camera.WorldRotation.x, camera.WorldRotation.y, camera.WorldRotation.z, camera.WorldRotation.w },
			SceneSeconds = Time.NowDouble, RealSeconds = RealTime.NowDouble, Utc = DateTime.UtcNow
		};
	}

	/// <summary>Starts a bounded raw-engine trace in an explicitly identified existing editor Play scene.</summary>
	[McpTool( "x_input_trace_start" )]
	public static object StartInputTrace( string sceneId, int frameLimit = 300, double seconds = 15,
		string label = "look-click", string action = "Attack1" )
	{
		ValidateInputAction( action );
		CheckInputTraceLifetime();
		if ( !Sandbox.Application.IsEditor || !Game.IsPlaying || Game.ActiveScene is null
			|| !Guid.TryParse( sceneId, out var expected ) || Game.ActiveScene.Id != expected )
			throw new InvalidOperationException( "Exact existing editor Play scene ID required; no Play is started or replaced." );
		InputTrace.Start( frameLimit, seconds, label, RealTime.NowDouble );
		_inputTraceScene = Game.ActiveScene; _inputTraceSceneId = Game.ActiveScene.Id;
        _inputTraceProject = Project.Current; _inputTraceAction = action;
		return InputTraceStatus();
	}

	[McpTool( "x_input_trace_stop" )]
	public static object StopInputTrace( string token )
	{
		CheckInputTraceLifetime(); InputTrace.Stop( token ); return InputTraceStatus();
	}

	[McpTool.ReadOnly( "x_input_trace_status" )]
	public static object InputTraceStatus()
	{
		CheckInputTraceLifetime();
		return new
		{
			InputTrace.Token, Status = InputTrace.State, InputTrace.Label, Frames = InputTrace.Count,
			InputTrace.FrameLimit, InputTrace.SecondsLimit, StartedRealSeconds = InputTrace.Started,
			SceneId = _inputTraceSceneId, Action = _inputTraceAction,
			Layer = "engine-input-observation; input-source-undetermined; not-physical-device-coverage",
			Sampling = "editor-frame; not HUD/consumer ordered; screenshots are separate"
		};
	}

	[McpTool.ReadOnly( "x_input_trace_read" )]
	public static object ReadInputTrace( string token, int offset = 0, int count = 60 )
	{
		CheckInputTraceLifetime();
		var samples = InputTrace.Read( token, offset, count );
		return new { Metadata = InputTraceStatus(), Offset = offset, Samples = samples, NextOffset = Math.Min( InputTrace.Count, (long)offset + count ) };
	}

	[EditorEvent.Frame]
	public static void SampleInputTrace()
	{
		CheckInputTraceLifetime();
		if ( InputTrace.Recording )
			InputTrace.Add( new { Sequence = InputTrace.Count, Data = ObserveInput( _inputTraceAction ) } );
	}

	private static void CheckInputTraceLifetime()
	{
		InputTrace.CheckLifetime( Sandbox.Application.IsEditor && Game.IsPlaying && Game.ActiveScene == _inputTraceScene
			&& Project.Current == _inputTraceProject, RealTime.NowDouble );
		if ( !InputTrace.Recording ) { _inputTraceScene = null; _inputTraceProject = null; }
	}

	private static void ValidateInputAction( string action )
	{
		if ( string.IsNullOrWhiteSpace( action ) || action.Length > 64 || action != action.Trim() )
			throw new ArgumentException( "Supply an input action name of 1..64 characters from the project's Input settings." );
	}
}
