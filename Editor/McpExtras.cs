using Sandbox;
using System;
using System.IO;
using System.Linq;

namespace Editor.Mcp;

/// <summary>
/// Local extensions to the built in MCP tools, living in a shared library so every project here
/// gets them without waiting on an engine release. Tool names are prefixed 'x_' so they can never
/// collide with the engine's own once the upstream equivalents land.
/// </summary>
[McpToolset( "extras", "Local extensions to the built-in MCP tools" )]
public static partial class ExtrasTools
{
	/// <summary>
	/// Make a scene the active editor tab, opening it from its asset path when it isn't open yet.
	/// Scene edits always target the active scene, so switch before editing a background scene.
	/// Returns the tab it settled on - name, resource path, type, unsaved changes and root object
	/// count - plus a message saying what happened. list_scenes shows what's already open.
	/// </summary>
	/// <param name="scene">Scene name or resource path as list_scenes reports it, or a .scene/.prefab path from asset_search.</param>
	[McpTool( "x_open_scene" )]
	public static SceneTab OpenSceneTab( string scene )
	{
		if ( string.IsNullOrWhiteSpace( scene ) )
			throw new Exception( "Give a scene name or resource path - list_scenes shows what's open, asset_search type:scene finds scene assets on disk" );

		if ( Game.IsPlaying )
			throw new Exception( "Can't switch scene tabs while playing - play_stop first" );

		var session = FindSession( scene );

		if ( session is GameEditorSession )
			throw new Exception( "That's the running game session, which has no tab to switch to - play_stop first, then open the scene you want to edit" );

		if ( session is not null && session == SceneEditorSession.Active )
			return Row( session, $"'{session.Scene?.Name}' was already the active tab - nothing changed" );

		var opened = session is null;

		session ??= SceneEditorSession.CreateFromPath( scene )
			?? throw new Exception( $"Nothing to open for '{scene}' - list_scenes shows what's already open, asset_search type:scene finds scene assets on disk" );

		session.MakeActive();

		return Row( session, opened
			? $"Opened '{session.Scene?.Name}' from disk and made it the active tab"
			: $"Switched the active tab to the already open '{session.Scene?.Name}'" );
	}

	/// <summary>
	/// Create a new empty scene beneath scenes/diagnostics, save it without prompting, and make
	/// its tab active. Existing tabs, including dirty tabs, are left untouched.
	/// </summary>
	/// <param name="path">Project-relative .scene path beneath scenes/diagnostics.</param>
	/// <param name="name">Optional scene name. Defaults to the destination file name.</param>
	[McpTool( "x_create_scene" )]
	public static SceneTab CreateScene( string path, string name = "" )
	{
		if ( Game.IsPlaying )
			throw new Exception( "Can't create a scene while playing - play_stop first" );

		var destination = ValidateDiagnosticScenePath( path );
		var resourcePath = destination.RelativePath;
		var sceneName = string.IsNullOrWhiteSpace( name )
			? Path.GetFileNameWithoutExtension( resourcePath )
			: name.Trim();

		if ( File.Exists( destination.AbsolutePath ) || AssetSystem.FindByPath( resourcePath ) is not null )
			throw new Exception( $"A scene already exists at '{resourcePath}' - choose a new diagnostic path" );

		var previous = SceneEditorSession.Active;
		SceneEditorSession created = null;
		Asset asset = null;
		var saved = false;

		try
		{
			created = SceneEditorSession.CreateDefault()
				?? throw new Exception( "Couldn't create a new editor scene session" );

			var scene = created.Scene;
			foreach ( var child in scene.Children.ToArray() )
				child.Destroy();
			scene.ProcessDeletes();
			scene.Name = sceneName;

			Directory.CreateDirectory( Path.GetDirectoryName( destination.AbsolutePath ) );

			asset = AssetSystem.CreateResource( "scene", destination.AbsolutePath )
				?? throw new Exception( $"Couldn't create the scene resource at '{resourcePath}'" );

			var sceneFile = new SceneFile
			{
				Id = Guid.NewGuid(),
				GameObjects = [],
				SceneProperties = scene.SerializeProperties()
			};

			saved = asset.SaveToDisk( sceneFile );
			if ( !saved )
				throw new Exception( $"Couldn't save the new scene at '{resourcePath}'" );

			created.Destroy();
			created = null;

			var opened = SceneEditorSession.CreateFromPath( resourcePath )
				?? throw new Exception( $"The new scene was saved but couldn't be opened at '{resourcePath}'" );

			opened.MakeActive();
			return Row( opened, $"Created '{opened.Scene?.Name}' at '{resourcePath}' and made it the active tab" );
		}
		catch
		{
			created?.Destroy();

			if ( asset is not null )
				asset.Delete();

			if ( previous is not null && previous != SceneEditorSession.Active )
				previous.MakeActive();

			throw;
		}
	}


	/// <summary>
	/// What the editor is doing right now - which project is open, which scene tab is active and
	/// whether it has unsaved changes, and whether play mode is running or paused. ActiveScene here
	/// is the editor's active tab, which is what the scene tools edit; the built in editor_status
	/// reports the running game's scene instead and can disagree while playing. Follow up with
	/// scene_tree for the hierarchy, or x_open_scene to switch tabs.
	/// </summary>
	[McpTool.ReadOnly( "x_editor_status" )]
	public static EditorStatusExtras GetEditorStatus()
	{
		var session = SceneEditorSession.Active;
		var scene = session?.Scene ?? Game.ActiveScene;

		return new EditorStatusExtras
		{
			Project = Project.Current?.Config?.Ident,
			ProjectTitle = Project.Current?.Config?.Title,
			ActiveScene = scene?.Name,
			ActiveScenePath = scene?.Source?.ResourcePath,
			SceneHasUnsavedChanges = session?.HasUnsavedChanges ?? false,
			OpenSceneCount = SceneEditorSession.All.Count,
			IsPlaying = Game.IsPlaying,
			IsPaused = Game.IsPaused
		};
	}

	/// <summary>
	/// Render a camera in the scene and return it as an image, with UI text intact. Give any
	/// CameraComponent's id or its game object's id, or nothing for the scene's main camera.
	/// Use this instead of camera_screenshot whenever the shot includes Razor UI: camera_screenshot
	/// renders every text label as a flat gray rectangle at any size other than the live viewport's,
	/// because rendering offscreen relayouts the UI, which throws away each label's text texture
	/// without rebuilding the render descriptors that point at it. In play mode this tool always
	/// renders at the native screen resolution - where that relayout is a no-op, so the descriptors
	/// stay valid - and downscales the result to the size you asked for: the output matches the
	/// requested width and height exactly, but its detail is capped at the viewport's resolution,
	/// so asking for more pixels than the viewport has gets you an upscale, not more detail. In
	/// edit mode there is no game viewport, so no live screen-size UI exists to corrupt and it
	/// renders directly at the requested size, at full detail - making this a drop in replacement
	/// for camera_screenshot in both modes. find_game_objects with component 'Camera' lists the
	/// cameras in a scene.
	/// </summary>
	/// <param name="camera">A CameraComponent id or its game object's id. Empty uses the scene's main camera.</param>
	/// <param name="width">Image width in pixels.</param>
	/// <param name="height">Image height in pixels.</param>
	/// <param name="includeUi">Include any UI the camera renders.</param>
	[McpTool.ReadOnly( "x_camera_screenshot" )]
	public static object CameraScreenshotNative( string camera = "", [Sandbox.Range( 16, 4096 )] int width = 1280,
		[Sandbox.Range( 16, 4096 )] int height = 720, bool includeUi = true )
	{
		var target = ResolveCamera( camera );

		if ( !target.IsValid() )
			throw new Exception( "The scene has no camera - find one with find_game_objects component 'Camera', or add one" );

		// The one size the engine bug can't bite: identical to the screen, so the offscreen
		// relayout changes no panel's size and no text texture gets released underneath its
		// descriptor. Everything else is a downscale we do ourselves.
		var nativeWidth = Screen.Width.CeilToInt();
		var nativeHeight = Screen.Height.CeilToInt();

		// No screen size means no game viewport - edit mode. Nothing live is laid out at the
		// screen's size, so there are no text textures a relayout can destroy, and we can render
		// straight at the size asked for, exactly as the built in camera_screenshot does.
		if ( nativeWidth <= 1 || nativeHeight <= 1 )
		{
			var direct = new Bitmap( width, height );
			target.RenderToBitmap( direct, includeUi );
			return direct;
		}

		var bitmap = new Bitmap( nativeWidth, nativeHeight );
		target.RenderToBitmap( bitmap, includeUi );

		if ( nativeWidth == width && nativeHeight == height )
			return bitmap;

		// Resize hands back a new bitmap, so the native capture is ours to release
		using ( bitmap )
		{
			return bitmap.Resize( width, height );
		}
	}

	/// <summary>
	/// Aim a camera game object at a world-space point using the engine's Rotation.LookAt.
	/// The camera must belong to the active editor scene. The edit is undoable.
	/// </summary>
	/// <param name="camera">A CameraComponent id or its game object's id.</param>
	/// <param name="target">World-space target as 'x,y,z'.</param>
	/// <param name="up">World-space up direction as 'x,y,z'. Defaults to +Z.</param>
	[McpTool( "x_camera_look_at" )]
	public static CameraLookAtResult CameraLookAt( string camera, string target, string up = "0,0,1" )
	{
		if ( string.IsNullOrWhiteSpace( camera ) )
			throw new Exception( "Give a CameraComponent or camera game object id - find_game_objects component 'Camera' lists them" );

		var session = SceneEditorSession.Active
			?? throw new Exception( "No editor scene tab is active" );
		var component = ResolveCamera( camera );
		if ( component.Scene != session.Scene )
			throw new Exception( "The camera isn't in the active editor scene - use x_open_scene or switch_scene first" );

		var targetPosition = Vector3.Parse( target );
		var upDirection = Vector3.Parse( up );
		var direction = targetPosition - component.WorldPosition;
		if ( direction.LengthSquared < 0.000001f )
			throw new Exception( "The camera position and look-at target must differ" );
		if ( upDirection.LengthSquared < 0.000001f )
			throw new Exception( "The look-at up direction must be non-zero" );

		var gameObject = component.GameObject;
		using ( session.UndoScope( "Aim Camera" ).WithGameObjectChanges( gameObject, GameObjectUndoFlags.All ).Push() )
		{
			gameObject.WorldRotation = Rotation.LookAt( direction.Normal, upDirection.Normal );
		}

		return new CameraLookAtResult
		{
			Id = gameObject.Id,
			Name = gameObject.Name,
			Position = gameObject.WorldPosition,
			Target = targetPosition,
			Angles = gameObject.WorldRotation.Angles()
		};
	}

	/// <summary>The resulting camera aim.</summary>
	public class CameraLookAtResult
	{
		public Guid Id { get; set; }
		public string Name { get; set; }
		public Vector3 Position { get; set; }
		public Vector3 Target { get; set; }
		public Angles Angles { get; set; }
	}

	/// <summary>One scene tab open in the editor.</summary>
	public class SceneTab
	{
		/// <summary>What happened - opened, switched, or already active.</summary>
		public string Message { get; set; }

		/// <summary>The scene's name, as list_scenes reports it.</summary>
		public string Name { get; set; }

		/// <summary>The scene asset's resource path. Null for a scene that was never saved.</summary>
		public string ResourcePath { get; set; }

		/// <summary>Scene, Prefab, or Game for the running session.</summary>
		public string Type { get; set; }

		/// <summary>Whether this is the active tab - true unless something else took focus.</summary>
		public bool IsActive { get; set; }

		/// <summary>Whether the scene has edits that save_scene hasn't written yet.</summary>
		public bool HasUnsavedChanges { get; set; }

		/// <summary>How many objects sit at the scene root.</summary>
		public int RootObjectCount { get; set; }
	}

	/// <summary>The editor's current state, from the editor's point of view rather than the game's.</summary>
	public class EditorStatusExtras
	{
		/// <summary>The open project's ident.</summary>
		public string Project { get; set; }

		/// <summary>The open project's title.</summary>
		public string ProjectTitle { get; set; }

		/// <summary>The active scene tab's name. Falls back to the running game's scene when no tab is active.</summary>
		public string ActiveScene { get; set; }

		/// <summary>The active scene's resource path. Null for a scene that was never saved.</summary>
		public string ActiveScenePath { get; set; }

		/// <summary>Whether the active scene has edits that save_scene hasn't written yet.</summary>
		public bool SceneHasUnsavedChanges { get; set; }

		/// <summary>How many scene tabs are open. list_scenes names them.</summary>
		public int OpenSceneCount { get; set; }

		/// <summary>Whether play mode is running - play_stop returns to editing.</summary>
		public bool IsPlaying { get; set; }

		/// <summary>Whether play mode is paused.</summary>
		public bool IsPaused { get; set; }
	}

	/// <summary>
	/// The open session whose scene matches a name or resource path, case insensitive. Null when
	/// nothing open matches - the caller decides whether to open it from disk.
	/// </summary>
	private static SceneEditorSession FindSession( string nameOrPath )
	{
		return SceneEditorSession.All
			.FirstOrDefault( x => string.Equals( x.Scene?.Name, nameOrPath, StringComparison.OrdinalIgnoreCase )
				|| string.Equals( x.Scene?.Source?.ResourcePath, nameOrPath, StringComparison.OrdinalIgnoreCase ) );
	}

	private static (string RelativePath, string AbsolutePath) ValidateDiagnosticScenePath( string path )
	{
		if ( string.IsNullOrWhiteSpace( path ) )
			throw new Exception( "Give a project-relative .scene path beneath scenes/diagnostics/" );

		var normalized = path.Trim().Replace( '\\', '/' );
		if ( Path.IsPathRooted( normalized ) )
			throw new Exception( "Scene path must be project-relative and beneath scenes/diagnostics/" );

		if ( normalized.Split( '/', StringSplitOptions.RemoveEmptyEntries ).Contains( ".." ) )
			throw new Exception( "Scene path can't contain traversal segments" );

		if ( !string.Equals( Path.GetExtension( normalized ), ".scene", StringComparison.OrdinalIgnoreCase ) )
			throw new Exception( "Scene path must use the .scene extension" );

		if ( !normalized.StartsWith( "scenes/diagnostics/", StringComparison.OrdinalIgnoreCase )
			|| normalized.Length == "scenes/diagnostics/".Length )
			throw new Exception( "Scene path must be beneath scenes/diagnostics/" );

		try
		{
			var assetsRoot = Path.GetFullPath( Project.Current.GetAssetsPath() );
			var diagnosticsRoot = Path.GetFullPath( Path.Combine( assetsRoot, "scenes", "diagnostics" ) )
				.TrimEnd( Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar ) + Path.DirectorySeparatorChar;
			var absolute = Path.GetFullPath( Path.Combine( assetsRoot, normalized.Replace( '/', Path.DirectorySeparatorChar ) ) );

			if ( !absolute.StartsWith( diagnosticsRoot, StringComparison.OrdinalIgnoreCase ) )
				throw new Exception( "Scene path must be beneath scenes/diagnostics/" );

			return (normalized, absolute);
		}
		catch ( Exception exception ) when ( exception is ArgumentException or NotSupportedException or PathTooLongException )
		{
			throw new Exception( "Scene path isn't a valid project-relative path" );
		}
	}

	/// <summary>
	/// The camera a tool argument names - a CameraComponent id, or a game object id whose
	/// CameraComponent we take. Empty means the active scene's main camera. The engine's own
	/// resolvers are private to the tools addon, so this repeats them.
	/// </summary>
	private static CameraComponent ResolveCamera( string camera )
	{
		if ( string.IsNullOrWhiteSpace( camera ) )
		{
			var scene = SceneEditorSession.Active?.Scene ?? Game.ActiveScene
				?? throw new Exception( "No scene is open in the editor" );

			return scene.Camera;
		}

		if ( !Guid.TryParse( camera, out var guid ) )
			throw new Exception( $"'{camera}' isn't a guid - find_game_objects and scene_tree show object ids, get_game_object shows component ids" );

		foreach ( var session in SceneEditorSession.All )
		{
			if ( session.Scene?.Directory?.FindComponentByGuid( guid ) is Component component )
			{
				return component as CameraComponent
					?? throw new Exception( "That component isn't a camera - give a CameraComponent or its game object" );
			}

			if ( session.Scene?.Directory?.FindByGuid( guid ) is GameObject go )
			{
				// includeDisabled, matching the built-in resolver's view of a game object's components
				return go.Components.Get<CameraComponent>( true )
					?? throw new Exception( $"'{go.Name}' has no camera component - find one with find_game_objects component 'Camera'" );
			}
		}

		throw new Exception( $"Nothing in any open scene has id {guid} - find_game_objects and scene_tree show what's there" );
	}

	private static SceneTab Row( SceneEditorSession session, string message )
	{
		return new SceneTab
		{
			Message = message,
			Name = session.Scene?.Name,
			ResourcePath = session.Scene?.Source?.ResourcePath,
			Type = session is GameEditorSession ? "Game" : session.Scene is PrefabScene ? "Prefab" : "Scene",
			IsActive = session == SceneEditorSession.Active,
			HasUnsavedChanges = session.HasUnsavedChanges,
			RootObjectCount = session.Scene?.Children.Count ?? 0
		};
	}
}
