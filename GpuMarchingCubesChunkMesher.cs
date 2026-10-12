using System;
using System.Runtime.InteropServices;
using UnityEngine;
using UnityEngine.Rendering;

public sealed class GpuMarchingCubesChunkMesher : IDisposable
{
    readonly ComputeShader _cs;
    readonly int _kernel;

    GraphicsBuffer _densities;   // float
    GraphicsBuffer _outVerts;    // float3
    GraphicsBuffer _outIndices;  // uint
    GraphicsBuffer _counter;     // uint[1]

    int _cellsPerAxis;
    int _samplesPerAxis;
    int _maxVerts;

    public GpuMarchingCubesChunkMesher(ComputeShader marchingCubesCompute, int cellsPerAxis, int maxVertsPerChunk)
    {
        _cs = marchingCubesCompute;
        _kernel = _cs.FindKernel("March");

        _cellsPerAxis = cellsPerAxis;
        _samplesPerAxis = cellsPerAxis + 1;
        _maxVerts = maxVertsPerChunk;

        AllocateBuffers();
    }

    void AllocateBuffers()
    {
        int densityCount = _samplesPerAxis * _samplesPerAxis * _samplesPerAxis;

        _densities = new GraphicsBuffer(GraphicsBuffer.Target.Structured, densityCount, sizeof(float));
        _outVerts = new GraphicsBuffer(GraphicsBuffer.Target.Structured, _maxVerts, 12); // float3
        _outIndices = new GraphicsBuffer(GraphicsBuffer.Target.Structured, _maxVerts, 4);  // uint
        _counter = new GraphicsBuffer(GraphicsBuffer.Target.Structured, 1, 4);
    }

    public void Dispose()
    {
        _densities?.Dispose();
        _outVerts?.Dispose();
        _outIndices?.Dispose();
        _counter?.Dispose();
    }

    /// <summary>
    /// densities length must be (cellsPerAxis+1)^3
    /// </summary>
    public void BuildMeshFromDensities(
        float[] densities,
        Vector3 chunkOrigin,
        float voxelSize,
        float isoLevel,
        Mesh targetMesh)
    {
        if (densities == null) throw new ArgumentNullException(nameof(densities));
        int expected = _samplesPerAxis * _samplesPerAxis * _samplesPerAxis;
        if (densities.Length != expected) throw new ArgumentException($"densities must be length {expected}");

        // Upload densities
        _densities.SetData(densities);

        // Reset counter to 0
        _counter.SetData(new uint[] { 0 });

        // Bind
        _cs.SetInt("_CellsPerAxis", _cellsPerAxis);
        _cs.SetInt("_SamplesPerAxis", _samplesPerAxis);
        _cs.SetFloat("_IsoLevel", isoLevel);
        _cs.SetVector("_ChunkOrigin", chunkOrigin);
        _cs.SetFloat("_VoxelSize", voxelSize);

        _cs.SetBuffer(_kernel, "_Densities", _densities);
        _cs.SetBuffer(_kernel, "_OutVertices", _outVerts);
        _cs.SetBuffer(_kernel, "_OutIndices", _outIndices);
        _cs.SetBuffer(_kernel, "_Counters", _counter);

        // Dispatch
        int groups = Mathf.CeilToInt(_cellsPerAxis / 8.0f);
        _cs.Dispatch(_kernel, groups, groups, groups);

        // Read back counter (sync). For smoother frame times, convert this to AsyncGPUReadback.
        var counterArr = new uint[1];
        _counter.GetData(counterArr);
        int vertCount = (int)counterArr[0];

        if (vertCount <= 0)
        {
            targetMesh.Clear();
            return;
        }

        if (vertCount > _maxVerts)
        {
            // If this happens, just increase maxVertsPerChunk.
            // Worst-case is 5 triangles per cell => 15 verts per cell.
            // So maxVerts = cells^3 * 15 (very high). Most terrain far less.
            Debug.LogWarning($"Marching Cubes overflow: {vertCount} > {_maxVerts}. Increase maxVertsPerChunk.");
            vertCount = _maxVerts;
        }

        // Read back verts + indices
        var verts = new Vector3[vertCount];
        var inds = new int[vertCount];

        // float3 -> Vector3
        var vtemp = new Vector3[vertCount];
        _outVerts.GetData(vtemp, 0, 0, vertCount);
        Array.Copy(vtemp, verts, vertCount);

        // uint -> int
        var utemp = new uint[vertCount];
        _outIndices.GetData(utemp, 0, 0, vertCount);
        for (int i = 0; i < vertCount; i++) inds[i] = (int)utemp[i];

        // Build mesh
        targetMesh.Clear();
        targetMesh.indexFormat = (vertCount > 65535) ? IndexFormat.UInt32 : IndexFormat.UInt16;
        targetMesh.vertices = verts;
        targetMesh.triangles = inds;
        targetMesh.RecalculateNormals(); // replace later with GPU normals or analytic gradient
        targetMesh.RecalculateBounds();
    }
}
