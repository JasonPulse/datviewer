namespace Vellichor.Dat;

/// <summary>
/// Engine-agnostic decoded mesh: flat float arrays so the Dat library stays free of any
/// Godot dependency (the Render layer converts this to an ArrayMesh). Indices are a plain
/// triangle list — the MMB decoder is responsible for converting the source triangle-strip
/// topology into a list here.
/// </summary>
public sealed class MeshData
{
    /// <summary>Vertex positions, 3 floats (x,y,z) per vertex.</summary>
    public required float[] Positions { get; init; }

    /// <summary>Vertex normals, 3 floats per vertex. Null if not decoded.</summary>
    public float[]? Normals { get; init; }

    /// <summary>Optional UVs, 2 floats (u,v) per vertex. Null if untextured.</summary>
    public float[]? Uvs { get; init; }

    /// <summary>Triangle-list indices into the vertex arrays.</summary>
    public required int[] Indices { get; init; }

    /// <summary>Hex of the 16-byte texture id this mesh binds (matched against IMG chunks), if any.</summary>
    public string? TextureId { get; init; }

    /// <summary>Skinning: 4 skeleton bone indices per vertex (null for unskinned zone meshes).</summary>
    public int[]? BoneIndices { get; init; }

    /// <summary>Skinning: 4 bone weights per vertex, summing to 1 (parallel to BoneIndices).</summary>
    public float[]? BoneWeights { get; init; }

    public int VertexCount => Positions.Length / 3;
    public int TriangleCount => Indices.Length / 3;
}
