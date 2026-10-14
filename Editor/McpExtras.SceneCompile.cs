using Sandbox;
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Editor.Mcp;

public static partial class ExtrasTools
{
	/// <summary>
	/// Compile the active scene exactly like Scene > Compile Scene (F9): the editor's own
	/// SceneCompileSession bakes meshes and props into a new generation under the scene's
	/// _scene_data/compiled folder and refreshes the compiled scene. Requires edit mode and a saved,
	/// non-prefab active tab - save_scene first, x_open_scene to pick the scene. Waits for the
	/// compile to finish and returns its status, summary, warnings and log lines, plus every
	/// generation folder left on disk and whether the compiled scene references it. Folders the
	/// compiled scene doesn't reference are stale, and publishing still collects them and their
	/// dependencies. The engine means to delete them after each compile but its cleanup goes
	/// through the read-only mounted filesystem and fails, so by default this tool finishes that
	/// cleanup: after a successful compile it deletes generation folders the compiled scene doesn't
	/// reference, and only those.
	/// </summary>
	/// <param name="timeoutSeconds">How long to wait before requesting cancellation.</param>
	/// <param name="removeStale">Delete unreferenced generation folders after a successful compile.</param>
	[McpTool( "x_compile_scene" )]
	public static async Task<SceneCompileResult> CompileScene( [Sandbox.Range( 10, 3600 )] int timeoutSeconds = 600, bool removeStale = true )
	{
		if ( Game.IsPlaying )
			throw new Exception( "Can't compile a scene while playing - play_stop first" );

		var editor = SceneEditorSession.Active
			?? throw new Exception( "No scene tab is active - x_open_scene first" );

		if ( editor.IsPrefabSession )
			throw new Exception( "The active tab is a prefab, which has nothing to compile - x_open_scene a scene first" );

		if ( editor.HasUnsavedChanges )
			throw new Exception( "The active scene has unsaved changes - save_scene first, then compile" );

		var session = SceneCompileSession.Current;
		if ( session.Running )
			throw new Exception( $"A scene compile is already running ({session.Status}) - wait for it to finish" );

		session.Refresh();
		if ( !session.CanCompile )
			throw new Exception( $"The editor can't compile this scene: {(string.IsNullOrEmpty( session.Error ) ? "it reports CanCompile=false" : session.Error)}" );

		var timer = Stopwatch.StartNew();
		var compile = session.StartAsync();

		if ( await Task.WhenAny( compile, Task.Delay( TimeSpan.FromSeconds( timeoutSeconds ) ) ) != compile )
		{
			session.RequestCancel();
			await Task.WhenAny( compile, Task.Delay( TimeSpan.FromSeconds( 30 ) ) );
			throw new Exception( $"Compile didn't finish within {timeoutSeconds}s; cancellation was requested (status: {session.Status}). Check x_editor_status and the scene's compiled folder before retrying" );
		}

		await compile;
		return SnapshotCompile( session, editor, timer.Elapsed.TotalSeconds, removeStale );
	}

	private static SceneCompileResult SnapshotCompile( SceneCompileSession session, SceneEditorSession editor, double seconds, bool removeStale )
	{
		var status = session.Status;
		var scenePath = editor.Scene?.Source?.ResourcePath;
		var generations = ReadGenerations( scenePath, out var compiledFolder );
		var removed = new System.Collections.Generic.List<string>();
		var notes = new System.Collections.Generic.List<string>();

		// Only after a real compile, and never when no generation is referenced - that would mean
		// the reference check itself failed, not that everything is stale.
		if ( removeStale && status == "Done" && generations.Any( x => x.ReferencedByCompiledScene ) )
		{
			foreach ( var generation in generations.Where( x => !x.ReferencedByCompiledScene ) )
			{
				if ( !IsGenerationName( generation.Name ) )
				{
					notes.Add( $"Left '{generation.Name}': not a generation folder name" );
					continue;
				}

				try
				{
					Directory.Delete( Path.Combine( compiledFolder, generation.Name ), true );
					removed.Add( generation.Name );
				}
				catch ( Exception e ) when ( e is IOException or UnauthorizedAccessException )
				{
					notes.Add( $"Couldn't delete '{generation.Name}': {e.Message}" );
				}
			}

			if ( removed.Count > 0 )
				generations = ReadGenerations( scenePath, out _ );
		}

		var stale = generations.Count( x => !x.ReferencedByCompiledScene );

		return new SceneCompileResult
		{
			Scene = session.Name,
			ScenePath = scenePath,
			Status = status,
			Succeeded = status is "Done" or "Nothing to compile",
			Error = string.IsNullOrEmpty( session.Error ) ? null : session.Error,
			DurationSeconds = Math.Round( seconds, 2 ),
			MeshCount = session.Report?.MeshCount ?? 0,
			PropCount = session.Report?.PropCount ?? 0,
			SkippedCount = session.Report?.Skipped?.Count ?? 0,
			Summary = session.Summary ?? [],
			Warnings = session.Warnings
				.Select( x => x.Component.IsValid() ? $"{x.Message} ({x.Component.GameObject?.Name})" : x.Message )
				.ToArray(),
			Lines = session.Lines.ToArray(),
			Generations = generations,
			StaleGenerations = stale,
			RemovedGenerations = removed.ToArray(),
			Notes = notes.ToArray(),
			Message = stale == 0
				? $"Compile {status.ToLowerInvariant()}; {generations.Length} generation folder(s) on disk, all referenced"
					+ (removed.Count > 0 ? $"; removed {removed.Count} stale folder(s)" : "")
				: $"Compile {status.ToLowerInvariant()}; {stale} stale generation folder(s) remain and would still be published"
		};
	}

	private static bool IsGenerationName( string name ) =>
		name.Length == 32 && name.All( Uri.IsHexDigit );

	private static SceneCompileGeneration[] ReadGenerations( string scenePath, out string compiledFolder )
	{
		compiledFolder = null;
		var asset = string.IsNullOrEmpty( scenePath ) ? null : AssetSystem.FindByPath( scenePath );
		var source = asset?.AbsolutePath;
		if ( string.IsNullOrEmpty( source ) )
			return [];

		var extension = Path.GetExtension( source ).Replace( ".", "_" );
		compiledFolder = Path.Combine( $"{Path.ChangeExtension( source, null )}{extension}_data", "compiled" );
		if ( !Directory.Exists( compiledFolder ) )
			return [];

		var compiledScene = asset.GetCompiledFile( true );
		var compiledText = !string.IsNullOrEmpty( compiledScene ) && File.Exists( compiledScene )
			? Encoding.Latin1.GetString( File.ReadAllBytes( compiledScene ) )
			: "";

		return Directory.GetDirectories( compiledFolder )
			.Select( x => new DirectoryInfo( x ) )
			.OrderByDescending( x => x.LastWriteTimeUtc )
			.Select( x => new SceneCompileGeneration
			{
				Name = x.Name,
				ReferencedByCompiledScene = compiledText.Contains( x.Name, StringComparison.OrdinalIgnoreCase ),
				LastWriteUtc = x.LastWriteTimeUtc.ToString( "O" ),
				FileCount = x.GetFiles( "*", SearchOption.AllDirectories ).Length
			} )
			.ToArray();
	}

	public class SceneCompileResult
	{
		/// <summary>The compiled scene's name.</summary>
		public string Scene { get; set; }

		/// <summary>The compiled scene's resource path.</summary>
		public string ScenePath { get; set; }

		/// <summary>The editor's final status: Done, Nothing to compile, Failed or Cancelled.</summary>
		public string Status { get; set; }

		/// <summary>True for Done or Nothing to compile.</summary>
		public bool Succeeded { get; set; }

		/// <summary>The editor's error, when it reported one.</summary>
		public string Error { get; set; }

		/// <summary>Wall-clock time the tool waited for the compile.</summary>
		public double DurationSeconds { get; set; }

		/// <summary>Mesh components the editor's scan reported.</summary>
		public int MeshCount { get; set; }

		/// <summary>Model renderers the editor's scan reported.</summary>
		public int PropCount { get; set; }

		/// <summary>Active components the compiler left alone, with reasons in the editor's compile report.</summary>
		public int SkippedCount { get; set; }

		/// <summary>The editor's completion summary.</summary>
		public string[] Summary { get; set; }

		/// <summary>Compiler warnings, with the owning game object's name when known.</summary>
		public string[] Warnings { get; set; }

		/// <summary>The compile log the Scene Compiler window shows.</summary>
		public string[] Lines { get; set; }

		/// <summary>Every generation folder under the scene's compiled folder, newest first.</summary>
		public SceneCompileGeneration[] Generations { get; set; }

		/// <summary>Generation folders the compiled scene doesn't reference, after any removal.</summary>
		public int StaleGenerations { get; set; }

		/// <summary>Stale generation folders this call deleted.</summary>
		public string[] RemovedGenerations { get; set; }

		/// <summary>Folders the cleanup left alone, and why.</summary>
		public string[] Notes { get; set; }

		/// <summary>What happened, in one line.</summary>
		public string Message { get; set; }
	}

	public class SceneCompileGeneration
	{
		/// <summary>The generation folder name.</summary>
		public string Name { get; set; }

		/// <summary>Whether the compiled scene file mentions this generation.</summary>
		public bool ReferencedByCompiledScene { get; set; }

		/// <summary>The folder's last write time, UTC, ISO 8601.</summary>
		public string LastWriteUtc { get; set; }

		/// <summary>Files inside the folder.</summary>
		public int FileCount { get; set; }
	}
}
