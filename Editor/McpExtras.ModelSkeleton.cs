using Sandbox;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Editor.Mcp;

public static partial class ExtrasTools
{
	/// <summary>
	/// Return read-only model-space bounds, bones, attachment ownership, material groups and animation
	/// names from an installed model resource. Unavailable complete fields are null with a reason;
	/// inspected empty collections remain empty. Skinning presence is unavailable in installed public APIs.
	/// </summary>
	/// <param name="model">Model asset path resolvable by AssetSystem.FindByPath.</param>
	[McpTool.ReadOnly( "x_model_skeleton" )]
	public static ModelSkeletonResult GetModelSkeleton( string model )
	{
		if ( string.IsNullOrWhiteSpace( model ) ) throw new Exception( "Give a model asset path." );
		var asset = AssetSystem.FindByPath( model.Replace( '\\', '/' ) )
			?? throw new Exception( $"Model asset '{Safe( model, 512 )}' was not found." );
		Model resource;
		try { resource = asset.LoadResource<Model>(); }
		catch ( Exception ex ) { throw new Exception( $"Model asset could not be loaded: {Safe( ex.Message, 512 )}" ); }
		if ( resource is null || !resource.IsValid || resource.IsError )
			throw new Exception( $"Model asset '{Safe( asset.Path, 512 )}' did not load as a valid non-error model resource." );

		var result = new ModelSkeletonResult { ModelAsset = asset.Path?.Replace( '\\', '/' ) };
		Capture( "Bounds", () =>
		{
			var bounds = resource.RenderBounds;
			result.Bounds = new ModelBoundsEvidence { Space = "model", Mins = bounds.Mins, Maxs = bounds.Maxs };
		}, result );
		Capture( "Bones", () => result.Bones = resource.Bones.AllBones.Select( x => new ModelBoneEvidence
		{
			Index = x.Index, Name = x.Name, ParentIndex = x.Parent?.Index ?? -1
		} ).ToList(), result );
		Capture( "Attachments", () => result.Attachments = resource.Attachments.All.Select( x => new ModelAttachmentEvidence
		{
			Name = x.Name, BoneIndex = x.Bone?.Index
		} ).ToList(), result );
		Capture( "MaterialGroups", () =>
		{
			var groups = new List<ModelMaterialGroupEvidence>();
			for ( var i = 0; i < resource.MaterialGroupCount; ++i )
			{
				groups.Add( new ModelMaterialGroupEvidence
				{
					Index = i,
					Name = resource.GetMaterialGroupName( i ),
					Materials = resource.GetMaterials( i ).Select( x => x?.ResourcePath?.Replace( '\\', '/' ) ).Where( x => x is not null ).ToList()
				} );
			}
			result.MaterialGroups = groups;
		}, result );
		Capture( "AnimationSequences", () =>
		{
			var animations = new List<string>( resource.AnimationCount );
			for ( var i = 0; i < resource.AnimationCount; ++i ) animations.Add( resource.GetAnimationName( i ) );
			result.AnimationSequences = animations;
		}, result );
		result.HasSkinningData = null;
		result.UnavailableFields["HasSkinningData"] = "Installed public model APIs do not expose skinning-data presence.";
		return result;
	}

	private static void Capture( string field, Action inspect, ModelSkeletonResult result )
	{
		try { inspect(); }
		catch ( Exception ex ) { result.UnavailableFields[field] = Safe( ex.Message, 256 ); }
	}
}

public sealed class ModelSkeletonResult
{
	public string ModelAsset { get; set; }
	public ModelBoundsEvidence Bounds { get; set; }
	public List<ModelBoneEvidence> Bones { get; set; }
	public List<ModelAttachmentEvidence> Attachments { get; set; }
	public List<ModelMaterialGroupEvidence> MaterialGroups { get; set; }
	public List<string> AnimationSequences { get; set; }
	public bool? HasSkinningData { get; set; }
	public Dictionary<string, string> UnavailableFields { get; set; } = new( StringComparer.Ordinal );
}

public sealed class ModelBoundsEvidence
{
	public string Space { get; set; }
	public Vector3 Mins { get; set; }
	public Vector3 Maxs { get; set; }
}

public sealed class ModelBoneEvidence
{
	public int Index { get; set; }
	public string Name { get; set; }
	public int ParentIndex { get; set; }
}

public sealed class ModelAttachmentEvidence
{
	public string Name { get; set; }
	public int? BoneIndex { get; set; }
}

public sealed class ModelMaterialGroupEvidence
{
	public int Index { get; set; }
	public string Name { get; set; }
	public List<string> Materials { get; set; } = [];
}
