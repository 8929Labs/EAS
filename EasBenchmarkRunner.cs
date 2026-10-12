// Assets/Scripts/EAS/Bench/EasBenchmarkRunner.cs
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using UnityEngine;
using EAS;
using EAS.Net;

public sealed class EasBenchmarkRunner : MonoBehaviour
{
    [Header("Chunk shape")]
    public int chunkSize = 32;                // voxels per axis
    public byte initialValue = 0;

    [Header("Benchmark scale")]
    public int warmupIterations = 5;
    public int measureIterations = 30;

    [Tooltip("How many random edits to do before measuring encode/ops/deltas.")]
    public int editsPerIteration = 4096;

    [Header("Encoding/Compression knobs")]
    public bool allowBitplaneForDisk = true;
    public bool useAutoCompression = true;
    public EasCompressionProfile autoProfile = EasCompressionProfile.Fast;
    public EasCompressionType fixedCompression = EasCompressionType.Deflate;

    [Header("Output")]
    public bool writeCsv = true;
    public string csvFileName = "eas_bench.csv";

    // Reuse lists to avoid allocations during tests
    private readonly List<int> _tempTouched = new(8192);

    private enum ChunkPattern
    {
        Uniform,
        DenseMixed,
        Palette4,
        BitplaneFriendly
    }

    private void Start()
    {
        RunAll();
    }

    [ContextMenu("Run EAS Benchmarks")]
    public void RunAll()
    {
        // Configure EAS globals
        EasChunkCodec.AllowBitplaneForDisk = allowBitplaneForDisk;
        EasChunkCodec.UseAutoCompression = useAutoCompression;
        EasChunkCodec.AutoProfile = autoProfile;
        EasChunkCodec.DefaultCompressionForDisk = fixedCompression;

        var cells = chunkSize * chunkSize * chunkSize;

        var rows = new List<Row>(256);
        rows.Add(Row.Header());

        // --- Chunk creation patterns ---
        foreach (ChunkPattern pat in Enum.GetValues(typeof(ChunkPattern)))
        {
            // Baseline and working chunk for ops/deltas
            var baseChunk = CreatePatternChunk(cells, pat);
            baseChunk.ClearTouched();
            baseChunk.ClearDirty();

            var workChunk = CloneChunk(baseChunk);

            // Benchmark: edits only
            rows.Add(Bench($"EditOnly/{pat}",
                setup: () => { ResetTo(workChunk, baseChunk); },
                action: () => { RandomEdits(workChunk, editsPerIteration, seed: 123); },
                extra: () => $"cells={cells},edits={editsPerIteration}"
            ));

            // Benchmark: heuristics choose encoding
            rows.Add(Bench($"HeuristicsChoose/{pat}",
                setup: () => { /* no-op */ },
                action: () => { EasHeuristics.ChooseForDisk(workChunk, out _, allowBitplaneForDisk); },
                extra: () => $"cells={cells}"
            ));

            // Benchmark: EncodeForDisk (includes chosen encoding + compression)
            rows.Add(Bench($"EncodeForDisk/{pat}",
                setup: () => { ResetTo(workChunk, baseChunk); RandomEdits(workChunk, editsPerIteration, seed: 123); },
                action: () => { _ = EasChunkCodec.EncodeForDisk(workChunk); },
                extra: () => $"cells={cells},edits={editsPerIteration},autoComp={useAutoCompression},fixedComp={fixedCompression},autoProfile={autoProfile},allowBitplane={allowBitplaneForDisk}"
            ));

            // Benchmark: Encode+Decode roundtrip
            rows.Add(Bench($"EncodeDecode/{pat}",
                setup: () => { ResetTo(workChunk, baseChunk); RandomEdits(workChunk, editsPerIteration, seed: 123); },
                action: () => {
                    var blob = EasChunkCodec.EncodeForDisk(workChunk);
                    var dec = EasChunkCodec.DecodeFromDisk(blob);
                    // prevent JIT optimizing away
                    if (dec.CellCount != workChunk.CellCount) throw new Exception("CellCount mismatch");
                },
                extra: () => $"cells={cells},edits={editsPerIteration}"
            ));

            // Benchmark: HeaderPeek on encoded blob
            rows.Add(Bench($"HeaderPeek/{pat}",
                setup: () => { ResetTo(workChunk, baseChunk); RandomEdits(workChunk, editsPerIteration, seed: 123); _encodedScratch = EasChunkCodec.EncodeForDisk(workChunk); },
                action: () => { EasHeaderPeek.TryPeek(_encodedScratch, out _); },
                extra: () => $"cells={cells}"
            ));

            // --- Ops (O1): encode touched, then apply to a clone ---
            rows.Add(Bench($"OpsEncode/{pat}",
                setup: () => { ResetTo(workChunk, baseChunk); RandomEdits(workChunk, editsPerIteration, seed: 123); },
                action: () => { _ = EasChunkOpsCodec.EncodeFromTouched(workChunk, _tempTouched, clearTouched: true); },
                extra: () => $"cells={cells},edits={editsPerIteration}"
            ));

            rows.Add(Bench($"OpsEncodeApply/{pat}",
                setup: () => {
                    ResetTo(workChunk, baseChunk);
                    RandomEdits(workChunk, editsPerIteration, seed: 123);
                    _opsScratch = EasChunkOpsCodec.EncodeFromTouched(workChunk, _tempTouched, clearTouched: true);
                    _applyTarget = CloneChunk(baseChunk);
                },
                action: () => { EasChunkOpsCodec.ApplyToChunk(_applyTarget, _opsScratch); },
                extra: () => $"cells={cells},edits={editsPerIteration}"
            ));

            // --- Delta v1: requires base + new ---
            rows.Add(Bench($"Delta1Encode/{pat}",
                setup: () => { ResetTo(workChunk, baseChunk); RandomEdits(workChunk, editsPerIteration, seed: 123); },
                action: () => { _ = EasDeltaCodec.EncodeDelta(baseChunk, workChunk); },
                extra: () => $"cells={cells},edits={editsPerIteration}"
            ));

            rows.Add(Bench($"Delta1EncodeApply/{pat}",
                setup: () => {
                    ResetTo(workChunk, baseChunk);
                    RandomEdits(workChunk, editsPerIteration, seed: 123);
                    _deltaScratch = EasDeltaCodec.EncodeDelta(baseChunk, workChunk);
                },
                action: () => {
                    var outChunk = EasDeltaCodec.ApplyDelta(baseChunk, _deltaScratch);
                    if (outChunk.CellCount != baseChunk.CellCount) throw new Exception("CellCount mismatch");
                },
                extra: () => $"cells={cells},edits={editsPerIteration}"
            ));

            // --- Delta v2: encode using touched indices ---
            rows.Add(Bench($"Delta2Encode/{pat}",
                setup: () => { ResetTo(workChunk, baseChunk); RandomEdits(workChunk, editsPerIteration, seed: 123); },
                action: () => { _ = EasDeltaCodec2.EncodeDeltaFromTouched(baseChunk, workChunk, _tempTouched, clearTouched: true); },
                extra: () => $"cells={cells},edits={editsPerIteration}"
            ));

            rows.Add(Bench($"Delta2EncodeApply/{pat}",
                setup: () => {
                    ResetTo(workChunk, baseChunk);
                    RandomEdits(workChunk, editsPerIteration, seed: 123);
                    _deltaScratch = EasDeltaCodec2.EncodeDeltaFromTouched(baseChunk, workChunk, _tempTouched, clearTouched: true);
                },
                action: () => {
                    var outChunk = EasDeltaCodec2.ApplyDelta(baseChunk, _deltaScratch);
                    if (outChunk.CellCount != baseChunk.CellCount) throw new Exception("CellCount mismatch");
                },
                extra: () => $"cells={cells},edits={editsPerIteration}"
            ));
        }

        // --- Compression microbench (optional but useful) ---
        // Uses a representative payload from DenseMixed to compare codec cost.
        {
            var dense = CreatePatternChunk(cells, ChunkPattern.DenseMixed);
            var blob = EasChunkCodec.EncodeForDisk(dense); // includes header etc; use as raw input source
            var payload = blob;

            rows.Add(Bench("Compression/None",
                setup: () => { },
                action: () => { _ = EasCompression.Compress(payload, EasCompressionType.None); },
                extra: () => $"bytes={payload.Length}"
            ));

            rows.Add(Bench("Compression/Deflate",
                setup: () => { },
                action: () => { _ = EasCompression.Compress(payload, EasCompressionType.Deflate); },
                extra: () => $"bytes={payload.Length}"
            ));

            // LZ4 only if enabled in your build; EasCompression will throw NotSupported otherwise.
            rows.Add(Bench("Compression/LZ4",
                setup: () => { },
                action: () => {
                    try { _ = EasCompression.Compress(payload, EasCompressionType.LZ4); }
                    catch (NotSupportedException) { /* ok */ }
                },
                extra: () => $"bytes={payload.Length} (skips if not supported)"
            ));
        }

        // Print and optionally write CSV
        Print(rows);

        if (writeCsv)
        {
            var path = Path.Combine(Application.persistentDataPath, csvFileName);
            File.WriteAllText(path, ToCsv(rows));
            UnityEngine.Debug.Log($"[EAS-BENCH] Wrote CSV: {path}");
        }
    }

    // ----------------------------
    // Benchmark core
    // ----------------------------

    private sealed class Row
    {
        public string name;
        public int warmups;
        public int iters;

        public double msMean;
        public double msMin;
        public double msMax;

        public double nsPerOp;
        public long bytesAllocatedPerIter;

        public string extra;

        public static Row Header() => new Row
        {
            name = "name",
            warmups = 0,
            iters = 0,
            msMean = double.NaN,
            msMin = double.NaN,
            msMax = double.NaN,
            nsPerOp = double.NaN,
            bytesAllocatedPerIter = long.MinValue,
            extra = "extra"
        };
    }

    private Row Bench(string name, Action setup, Action action, Func<string> extra)
    {
        // Warmup (JIT + caches)
        for (int i = 0; i < warmupIterations; i++)
        {
            setup?.Invoke();
            action();
        }

        // Try to reduce noise (not perfect, but helps)
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        var sw = new Stopwatch();
        double minMs = double.PositiveInfinity;
        double maxMs = 0;
        double sumMs = 0;

        long allocBefore = GC.GetAllocatedBytesForCurrentThread();

        for (int i = 0; i < measureIterations; i++)
        {
            setup?.Invoke();

            sw.Restart();
            action();
            sw.Stop();

            double ms = sw.Elapsed.TotalMilliseconds;
            minMs = Math.Min(minMs, ms);
            maxMs = Math.Max(maxMs, ms);
            sumMs += ms;
        }

        long allocAfter = GC.GetAllocatedBytesForCurrentThread();
        long allocTotal = allocAfter - allocBefore;
        long allocPerIter = measureIterations > 0 ? allocTotal / measureIterations : 0;

        double meanMs = sumMs / Math.Max(1, measureIterations);
        double nsPerOp = meanMs * 1_000_000.0;

        return new Row
        {
            name = name,
            warmups = warmupIterations,
            iters = measureIterations,
            msMean = meanMs,
            msMin = minMs,
            msMax = maxMs,
            nsPerOp = nsPerOp,
            bytesAllocatedPerIter = allocPerIter,
            extra = extra?.Invoke() ?? ""
        };
    }

    private void Print(List<Row> rows)
    {
        UnityEngine.Debug.Log($"[EAS-BENCH] Results ({rows.Count - 1} tests). chunkSize={chunkSize} cells={chunkSize * chunkSize * chunkSize} editsPerIter={editsPerIteration}");

        for (int i = 1; i < rows.Count; i++)
        {
            var r = rows[i];
            UnityEngine.Debug.Log($"[EAS-BENCH] {r.name} | mean={r.msMean:F4}ms min={r.msMin:F4}ms max={r.msMax:F4}ms | alloc/iter={r.bytesAllocatedPerIter}B | {r.extra}");
        }
    }

    private string ToCsv(List<Row> rows)
    {
        static string Esc(string s) => "\"" + (s ?? "").Replace("\"", "\"\"") + "\"";

        var sb = new System.Text.StringBuilder(64 * rows.Count);
        sb.AppendLine("name,warmups,iters,ms_mean,ms_min,ms_max,ns_per_op,alloc_bytes_per_iter,extra");

        for (int i = 1; i < rows.Count; i++)
        {
            var r = rows[i];
            sb.Append(Esc(r.name)).Append(',')
              .Append(r.warmups).Append(',')
              .Append(r.iters).Append(',')
              .Append(r.msMean.ToString("F6")).Append(',')
              .Append(r.msMin.ToString("F6")).Append(',')
              .Append(r.msMax.ToString("F6")).Append(',')
              .Append(r.nsPerOp.ToString("F1")).Append(',')
              .Append(r.bytesAllocatedPerIter).Append(',')
              .Append(Esc(r.extra))
              .AppendLine();
        }

        return sb.ToString();
    }

    // ----------------------------
    // Chunk helpers
    // ----------------------------

    private EasChunk CreatePatternChunk(int cells, ChunkPattern pat)
    {
        switch (pat)
        {
            case ChunkPattern.Uniform:
                {
                    var c = new EasChunk(cells, 3);
                    c.ClearTouched();
                    c.ClearDirty();
                    return c;
                }
            case ChunkPattern.DenseMixed:
                {
                    var c = new EasChunk(cells, 0);
                    for (int i = 0; i < cells; i++) c.Set(i, (byte)(i % 15));
                    c.ClearTouched();
                    c.ClearDirty();
                    return c;
                }
            case ChunkPattern.Palette4:
                {
                    var c = new EasChunk(cells, 1);
                    // 4 values: 1..4 in a structured way
                    for (int i = 0; i < cells; i++) c.Set(i, (byte)(1 + ((i >> 4) & 3)));
                    c.ClearTouched();
                    c.ClearDirty();
                    return c;
                }
            case ChunkPattern.BitplaneFriendly:
                {
                    var c = new EasChunk(cells, 0);
                    // mostly zeros with sparse spikes
                    for (int i = 0; i < cells; i += 97) c.Set(i, 14);
                    c.ClearTouched();
                    c.ClearDirty();
                    return c;
                }
            default:
                throw new ArgumentOutOfRangeException(nameof(pat), pat, null);
        }
    }

    private EasChunk CloneChunk(EasChunk src)
    {
        // Clone by dense bytes (fast and keeps invariants correct)
        var dense = src.DenseRawBytes;
        var copy = new byte[dense.Length];
        Buffer.BlockCopy(dense, 0, copy, 0, dense.Length);
        return EasChunk.FromDenseBytes(src.CellCount, copy);
    }

    private void ResetTo(EasChunk dst, EasChunk src)
    {
        // Overwrite dst with src via dense clone (avoid new allocations? we still allocate here)
        // If you want zero allocations here, we’d need a "LoadDenseInPlace" API on EasChunk.
        var dense = src.DenseRawBytes;
        var copy = new byte[dense.Length];
        Buffer.BlockCopy(dense, 0, copy, 0, dense.Length);
        var rebuilt = EasChunk.FromDenseBytes(src.CellCount, copy);

        // swap-in by reflection-free approach: just assign reference at call site (we can’t here),
        // so instead we copy cell-by-cell into dst:
        int n = src.CellCount;
        // NOTE: This costs time but keeps benchmark correctness without modifying EasChunk internals.
        dst.ClearTouched();
        for (int i = 0; i < n; i++)
            dst.Set(i, rebuilt.Get(i));
        dst.ClearTouched();
        dst.ClearDirty();
    }

    private void RandomEdits(EasChunk chunk, int edits, int seed)
    {
        var rng = new System.Random(seed);
        int n = chunk.CellCount;
        for (int i = 0; i < edits; i++)
        {
            int idx = rng.Next(0, n);
            byte v = (byte)rng.Next(0, 15);
            chunk.Set(idx, v);
        }
    }

    // scratch storage to avoid per-iteration captures
    private byte[] _encodedScratch;
    private byte[] _opsScratch;
    private byte[] _deltaScratch;
    private EasChunk _applyTarget;
}
