using Sandbox;
using System;
using System.Linq;

namespace Editor.Mcp;

/// <summary>
/// Local extensions to the built in MCP tools, living in a shared library so every project here
/// gets them without waiting on an engine release. Tool names are prefixed 'x_' so they can never
/// collide with the engine's own once the upstream equivalents land.
/// </summary>
[McpToolset( "extras", "Local extensions to the built-in MCP tools" )]
public static class ExtrasTools
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
