using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

public static class DiandianMeshReduction
{
    public static Mesh Reduce(Mesh source, int targetTriangles = 240000)
    {
        if (!source) throw new ArgumentNullException(nameof(source));
        if (targetTriangles <= 0) throw new ArgumentOutOfRangeException(nameof(targetTriangles));
        if (source.blendShapeCount > 0 || source.bindposes.Length > 0 ||
            source.HasVertexAttribute(VertexAttribute.BlendWeight) ||
            source.HasVertexAttribute(VertexAttribute.BlendIndices))
            throw new InvalidOperationException("Reduce expects an unrigged mesh without blend shapes.");
        if (source.subMeshCount != 1 || source.GetTopology(0) != MeshTopology.Triangles)
            throw new InvalidOperationException("Reduce expects the Tripo model's single triangle submesh.");

        var working = UnityEngine.Object.Instantiate(source);
        try
        {
            MeshLodUtility.GenerateMeshLods(working, 4);
            int selected = 0;
            double bestDifference = double.PositiveInfinity;
            for (int lod = 0; lod < working.lodCount; lod++)
            {
                long triangleCount = working.GetIndexCount(0, lod) / 3;
                if (triangleCount == 0) continue;
                double difference = Math.Abs(Math.Log(triangleCount / (double)targetTriangles));
                if (difference < bestDifference)
                {
                    selected = lod;
                    bestDifference = difference;
                }
            }

            int[] indices = working.GetIndices(0, selected, true);
            Vector3[] positions = working.vertices;
            Vector3[] normals = working.normals;
            Vector2[] uv = working.uv;
            Vector4[] tangents = working.tangents;
            if (indices.Length == 0 || normals.Length != positions.Length || uv.Length != positions.Length)
                throw new InvalidOperationException("Mesh indices, authored normals or UVs are missing.");

            // Remap by vertex identity, never position: keep UV and normal seams split.
            var remap = new int[positions.Length];
            Array.Fill(remap, -1);
            var used = new List<int>();
            for (int i = 0; i < indices.Length; i++)
            {
                int oldIndex = indices[i];
                if (remap[oldIndex] < 0)
                {
                    remap[oldIndex] = used.Count;
                    used.Add(oldIndex);
                }
                indices[i] = remap[oldIndex];
            }

            var newPositions = new Vector3[used.Count];
            var newNormals = new Vector3[used.Count];
            var newUv = new Vector2[used.Count];
            Vector4[] newTangents = tangents.Length == positions.Length ? new Vector4[used.Count] : null;
            for (int i = 0; i < used.Count; i++)
            {
                int oldIndex = used[i];
                newPositions[i] = positions[oldIndex];
                newNormals[i] = normals[oldIndex];
                newUv[i] = uv[oldIndex];
                if (newTangents != null) newTangents[i] = tangents[oldIndex];
            }

            var result = new Mesh
            {
                name = source.name + "_" + indices.Length / 3 + "tri",
                indexFormat = used.Count > 65535 ? IndexFormat.UInt32 : IndexFormat.UInt16,
                vertices = newPositions,
                normals = newNormals,
                uv = newUv
            };
            if (newTangents != null) result.tangents = newTangents;
            result.SetTriangles(indices, 0); // Recalculate bounds only; retain authored normals.
            Debug.Log("DIANDIAN_REDUCE_OK lod=" + selected +
                " vertices=" + source.vertexCount + "->" + result.vertexCount +
                " triangles=" + source.GetIndexCount(0) / 3 + "->" + indices.Length / 3);
            return result;
        }
        finally
        {
            UnityEngine.Object.DestroyImmediate(working);
        }
    }
}
