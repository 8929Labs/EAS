# 📘 EAS – Entropy Adaptive Storage

Unified documentation covering all files in the EAS system.

---

## Assets/Scripts/EAS/EasAutoCompression.cs

### `public static class EasAutoCompression`
Auto-selects the best compression method (None / LZ4 / Deflate) for a given payload, using configurable thresholds.

#### Public fields
- `public static bool AllowLz4`
  - **Visibility:** public
  - **What it does:** Enables/disables considering `EasCompressionType.LZ4` during auto-selection.

- `public static bool AllowDeflate`
  - **Visibility:** public
  - **What it does:** Enables/disables considering `EasCompressionType.Deflate` during auto-selection.

- `public static int MinBytesSavedToCompress`
  - **Visibility:** public
  - **What it does:** Minimum absolute byte savings required before choosing a compressed payload over `None`.

- `public static int MinPercentSavedToCompress`
  - **Visibility:** public
  - **What it does:** Minimum percent savings required before choosing a compressed payload over `None`.

#### Public methods
- `public static (EasCompressionType type, byte[] payload) ChooseBest(byte[] uncompressedPayload, EasCompressionProfile profile)`
  - **Visibility:** public
  - **What it does:** Tries allowed compression candidates (always `None`, optionally `LZ4`, and optionally `Deflate` depending on profile/size) and returns the smallest payload that also clears the configured “worth it” thresholds.
  - **Notes:** If LZ4 fails (missing/disabled), it is silently ignored.

#### Private/local helper
- `Consider(EasCompressionType t, byte[] candidate)` *(local function inside `ChooseBest`)*
  - **Visibility:** local (only inside `ChooseBest`)
  - **What it does:** Compares a candidate payload against the current best and accepts it only if it is smaller and meets `MinBytesSavedToCompress` / `MinPercentSavedToCompress` for non-None types.

---

## Assets/Scripts/EAS/EasBitPacker.cs

### `public sealed class EasBitWriter`
Fast little-endian bit writer that accumulates into a 64-bit register and flushes whole bytes to a growable buffer.

#### Private fields
- `_buf : byte[]` — internal byte buffer
- `_bytePos : int` — current write position in `_buf`
- `_bitAcc : ulong` — bit accumulator register
- `_bitCount : int` — number of valid bits currently in `_bitAcc`

#### Public members
- `public EasBitWriter(int initialBytes = 64)`
  - **Visibility:** public
  - **What it does:** Creates a writer with an initial buffer size (minimum 8 bytes).

- `public void WriteBits(uint value, int bitCount)`
  - **Visibility:** public
  - **What it does:** Writes the lowest `bitCount` bits of `value` into the stream using little-endian bit order (first bit becomes LSB of first byte).
  - **Throws:** `ArgumentOutOfRangeException` if `bitCount > 32`.

- `public byte[] ToArray()`
  - **Visibility:** public
  - **What it does:** Flushes any remaining partial byte, then returns an exact-sized copy of the internal buffer.

- `public int BitLength`
  - **Visibility:** public (property getter)
  - **What it does:** Returns total bits written so far (including bits still in accumulator).

#### Private methods
- `private void Ensure(int addBytes)`
  - **Visibility:** private
  - **What it does:** Grows `_buf` if needed to fit `addBytes` more bytes.

### `public sealed class EasBitReader`
Fast little-endian bit reader that refills a 64-bit register from an input byte array.

#### Private fields
- `_buf : byte[]` — source data
- `_bytePos : int` — current read position
- `_bitAcc : ulong` — bit accumulator register
- `_bitCount : int` — number of valid bits currently in `_bitAcc`

#### Public members
- `public EasBitReader(byte[] buf)`
  - **Visibility:** public
  - **What it does:** Creates a reader over `buf` (must be non-null).

- `public uint ReadBits(int bitCount)`
  - **Visibility:** public
  - **What it does:** Reads `bitCount` bits and returns them as an unsigned integer, matching the writer’s little-endian bit packing.
  - **Throws:** `ArgumentOutOfRangeException` if `bitCount > 32`; `ArgumentException` if reading past end.

---

## Assets/Scripts/EAS/EasChunk.cs

### `public sealed class EasChunk`
Stores a fixed-size array of 4-bit cell values (0..14) with:
- fast dense storage (nibbles)
- per-value histogram counts
- uniform/unique tracking
- dirty flag
- “touched indices” tracking for edits since last clear

#### Public fields/properties
- `public readonly int CellCount`
  - **Visibility:** public
  - **What it does:** Total number of cells in the chunk.

- `public bool Dirty { get; private set; }`
  - **Visibility:** public getter / private setter
  - **What it does:** Indicates whether the chunk has been modified since last `ClearDirty()` or load.

- `public int UniqueCount`
  - **Visibility:** public (property getter)
  - **What it does:** Number of distinct values present in the chunk (based on histogram).

- `public byte UniformValueOr255`
  - **Visibility:** public (property getter)
  - **What it does:** If the chunk is uniform, returns that value; otherwise returns `255`.

- `public byte[] DenseRawBytes`
  - **Visibility:** public (property getter)
  - **What it does:** Exposes the underlying dense nibble byte array (backing storage).

- `public int TouchedCount`
  - **Visibility:** public (property getter)
  - **What it does:** How many unique indices have been touched since last `ClearTouched()`.

#### Constructors / factory
- `public EasChunk(int cellCount, byte initialValue)`
  - **Visibility:** public
  - **What it does:** Allocates a chunk and fills all cells with `initialValue` (0..14). Initializes histogram and touched tracking; marks `Dirty = true`.

- `public static EasChunk FromDenseBytes(int cellCount, byte[] denseNibbleBytes)`
  - **Visibility:** public
  - **What it does:** Creates a chunk from raw dense nibble bytes, rebuilds histogram/unique/uniform tracking, clears `Dirty`, and clears touched state.
  - **Throws:** length mismatch or decoded values outside 0..14.

#### Public methods
- `public byte Get(int index)`
  - **Visibility:** public
  - **What it does:** Returns the value (0..14) stored at `index`.

- `public void Set(int index, byte newValue)`
  - **Visibility:** public
  - **What it does:** Updates a cell to `newValue` (0..14), updates histogram/unique/uniform state, marks `Dirty = true`, and records the index as “touched” (no duplicates).

- `public ushort GetCount(byte value0to14)`
  - **Visibility:** public
  - **What it does:** Returns how many cells currently contain `value0to14`.

- `public void ClearDirty()`
  - **Visibility:** public
  - **What it does:** Sets `Dirty` to false.

- `public void GetTouched(List<int> dst, bool clear = true)`
  - **Visibility:** public
  - **What it does:** Appends all touched indices into `dst`. If `clear` is true, clears touched state afterward.
  - **Notes:** Designed to avoid allocations if caller reuses `dst`.

- `public void ClearTouched()`
  - **Visibility:** public
  - **What it does:** Clears touched tracking without changing `Dirty`.

#### Private methods
- `private void MarkTouched(int index)`
  - **Visibility:** private
  - **What it does:** Marks an index as touched using a bitset and pushes it into a list only once.

- `private byte FindOnlyValue()`
  - **Visibility:** private
  - **What it does:** Scans histogram and returns the sole present value when `UniqueCount == 1`, otherwise returns 255.

---

## Assets/Scripts/EAS/EasChunkCodec.cs

### `public static class EasChunkCodec`
Encodes/decodes `EasChunk` for disk storage with a small header and optional compression.
Supports decoding legacy v1 and current v2 headers.

#### Public configuration fields
- `public static bool AllowBitplaneForDisk`
  - **Visibility:** public
  - **What it does:** Enables/disables `Bitplane` encoding as a disk candidate.

- `public static bool UseAutoCompression`
  - **Visibility:** public
  - **What it does:** If true, selects the best compression using `EasAutoCompression.ChooseBest`.

- `public static EasCompressionType DefaultCompressionForDisk`
  - **Visibility:** public
  - **What it does:** Compression to use when `UseAutoCompression` is false.

- `public static EasCompressionProfile AutoProfile`
  - **Visibility:** public
  - **What it does:** Controls how aggressive auto-compression testing is.

#### Public methods
- `public static byte[] EncodeForDisk(EasChunk chunk)`
  - **Visibility:** public
  - **What it does:** Chooses an encoding strategy (Uniform/Palette/Dense/Bitplane) via `EasHeuristics`, builds an uncompressed payload, then compresses it (auto or fixed). Returns v2 header + payload bytes.

- `public static EasChunk DecodeFromDisk(byte[] encoded)`
  - **Visibility:** public
  - **What it does:** Validates magic, branches based on version:
    - v1: type + cellCount + raw payload (uncompressed)
    - v2: type + compression + uncompressedLen + cellCount + compressed payload
  - Then decodes payload into a new `EasChunk`.

#### Private methods
- `private static EasChunk DecodePayload(...)`
  - **Visibility:** private
  - **What it does:** Copies payload bytes, decompresses using `EasCompression`, then calls the appropriate decode method based on encoding type.

- `private static byte[] EncodeUniform(EasChunk chunk)` / `private static EasChunk DecodeUniform(...)`
  - **Visibility:** private
  - **What they do:** Store/load a single uniform nibble value.

- `private static byte[] EncodeDense(EasChunk chunk)` / `private static EasChunk DecodeDense(...)`
  - **Visibility:** private
  - **What they do:** Store/load raw dense nibble bytes.

- `private static byte[] EncodePalette(EasChunk chunk, int paletteSize)` / `private static EasChunk DecodePalette(...)`
  - **Visibility:** private
  - **What they do:** Store a palette + packed indices (via `EasBitWriter` / `EasBitReader`) and reconstruct dense output.

- `private static byte[] EncodeBitplane(EasChunk chunk)` / `private static EasChunk DecodeBitplane(...)`
  - **Visibility:** private
  - **What they do:** Store 15 bitplanes indicating where each value occurs; reconstruct dense output by scanning planes per cell.

- `private static void SetNibble(byte[] nibbleBytes, int index, byte value0to15)`
  - **Visibility:** private
  - **What it does:** Writes a 4-bit nibble into the dense byte array at `index`.

---

## Assets/Scripts/EAS/EasChunkSerializer.cs

### `public static class EasChunkSerializer`
Convenience helpers to save/load chunk blobs to/from files or streams using `EasChunkCodec`.

#### Public methods
- `public static void SaveToFile(string path, EasChunk chunk)`
  - **Visibility:** public
  - **What it does:** Encodes `chunk` via `EasChunkCodec.EncodeForDisk`, writes bytes to disk, then calls `chunk.ClearDirty()`.

- `public static EasChunk LoadFromFile(string path)`
  - **Visibility:** public
  - **What it does:** Reads bytes from disk and decodes them via `EasChunkCodec.DecodeFromDisk`.

- `public static void SaveToStream(Stream stream, EasChunk chunk)`
  - **Visibility:** public
  - **What it does:** Encodes `chunk`, writes bytes to the provided stream, then clears dirty flag.

- `public static EasChunk LoadFromStream(Stream stream)`
  - **Visibility:** public
  - **What it does:** Copies the entire stream into memory, then decodes via `EasChunkCodec.DecodeFromDisk`.

---

## Assets/Scripts/EAS/EasCompression.cs

### `public static class EasCompression`
Compression/decompression dispatch for EAS blobs. Supports `None`, `Deflate`, and `LZ4` (via `EasLz4Fast`).

#### Public methods
- `public static byte[] Compress(byte[] input, EasCompressionType type)`
  - **Visibility:** public
  - **What it does:** Returns `input` unchanged for `None`, uses Deflate for `Deflate`, and calls `EasLz4Fast.Compress` for `LZ4`.

- `public static byte[] Decompress(byte[] input, EasCompressionType type, int expectedUncompressedBytes)`
  - **Visibility:** public
  - **What it does:** Validates sizes for `None`, deflates into an exact-sized output for `Deflate`, and calls `EasLz4Fast.Decompress` for `LZ4`.

#### Private methods
- `private static byte[] DeflateCompress(byte[] input)`
  - **Visibility:** private
  - **What it does:** Compresses input using `DeflateStream` at `CompressionLevel.Optimal`.

- `private static byte[] DeflateDecompress(byte[] input, int expectedBytes)`
  - **Visibility:** private
  - **What it does:** Decompresses into a fixed buffer of `expectedBytes` and throws if the decompressed size does not match exactly.

---

## Assets/Scripts/EAS/EasCompressionProfile.cs

### `public enum EasCompressionProfile : byte`
Controls how aggressively auto-compression considers multiple codecs.

- `Fast`
  - **Visibility:** public (enum member)
  - **What it does:** Try None first, then optionally LZ4 and Deflate.

- `Smallest`
  - **Visibility:** public (enum member)
  - **What it does:** Try all enabled codecs and select the smallest result.

---

## Assets/Scripts/EAS/EasCompressionType.cs

### `public enum EasCompressionType : byte`
Defines the compression algorithm used for encoded payloads.

- `None` — no compression
- `Deflate` — `System.IO.Compression.DeflateStream`
- `LZ4` — routed through `EasLz4Fast`

---

## Assets/Scripts/EAS/EasDebug.cs

### `public static class EasDebug`
Centralized debug flags for the EAS system. Intended to be safe to reference from any layer.

#### Public fields
- `public static bool Enabled`
  - **Visibility:** public
  - **What it does:** Master switch for EAS debug logging.

- `public static bool LogCodecHeaders`
  - **Visibility:** public
  - **What it does:** Logs encoding and compression choices.

- `public static bool LogNetworking`
  - **Visibility:** public
  - **What it does:** Logs networking events (subscriptions, full sends, ops).

- `public static bool LogOps`
  - **Visibility:** public
  - **What it does:** Logs operation encoding/apply details.

---

## Assets/Scripts/EAS/EasDeltaCodec.cs

### `public static class EasDeltaCodec`
Encodes and applies “delta” updates between two chunks (v1 delta format).
Delta stores runs of edited cells as:
- absolute start index (VarUInt)
- run length (VarUInt)
- packed 4-bit values for the run

#### Public configuration fields
- `public static int MaxGapToMergeRuns`
  - **Visibility:** public
  - **What it does:** If > 0, allows merging edit runs across small gaps of unchanged cells (default 0 = only contiguous edits).

#### Public methods
- `public static byte[] EncodeDelta(EasChunk baseChunk, EasChunk newChunk)`
  - **Visibility:** public
  - **What it does:** Compares `baseChunk` and `newChunk`, finds runs of differing indices, and serializes those runs into the delta format (header + entries). Uses `EasBitWriter` to pack 4-bit values.

- `public static EasChunk ApplyDelta(EasChunk baseChunk, byte[] deltaBytes)`
  - **Visibility:** public
  - **What it does:** Validates delta header, copies base chunk dense bytes, then applies each run (unpacking 4-bit values via `EasBitReader`) to produce a **new** `EasChunk`.

#### Private nested helpers
- `private sealed class ByteWriter`
  - **Visibility:** private
  - **What it does:** Small growable buffer writer supporting U8/U16LE/bytes and VarUInt (LEB128).

- `private sealed class ByteReader`
  - **Visibility:** private
  - **What it does:** Reader for the delta byte format supporting U8/U16LE/VarUInt and byte slices.

#### Private methods
- `private static void SetNibble(byte[] nibbleBytes, int index, byte value0to15)`
  - **Visibility:** private
  - **What it does:** Writes a 4-bit nibble into the dense byte array at `index`.

---

## Assets/Scripts/EAS/EasDeltaCodec2.cs

### `public static class EasDeltaCodec2`
Second-generation delta codec optimized for **O(k)** encoding using only touched indices.

#### Public methods
- `public static byte[] EncodeDeltaFromTouched(EasChunk baseChunk, EasChunk newChunk, List<int> tempTouched, bool clearTouched = true)`
  - **Visibility:** public
  - **What it does:** Encodes a delta using only indices marked as touched in `newChunk`. Any touched indices that resolve to the same value as `baseChunk` are skipped. Produces minimal output when no effective changes exist.
  - **Notes:** Reuses `tempTouched` to avoid allocations; clears touched flags by default.

- `public static EasChunk ApplyDelta(EasChunk baseChunk, byte[] deltaBytes)`
  - **Visibility:** public
  - **What it does:** Applies a D2 delta to `baseChunk` and returns a new editable chunk with the changes applied.

#### Private helpers
- `ByteWriter`
  - **Visibility:** private
  - **What it does:** Growable byte buffer supporting U8, U16LE, VarUInt, and deferred VarUInt patching.

- `ByteReader`
  - **Visibility:** private
  - **What it does:** Sequential reader for the delta format supporting U8, U16LE, VarUInt, and byte slices.

- `SetNibble(byte[] nibbleBytes, int index, byte value0to15)`
  - **Visibility:** private
  - **What it does:** Writes a 4-bit value into a dense nibble array.

---

## Assets/Scripts/EAS/EasEncodingType.cs

### `public enum EasEncodingType : byte`
Defines how chunk payload data is encoded before compression.

- `Uniform` — single repeated value
- `Palette` — small palette + packed indices
- `Dense` — raw nibble array
- `Bitplane` — one bitplane per value (0–14)

---

## Assets/Scripts/EAS/EasHeaderPeek.cs

### `public static class EasHeaderPeek`
Lightweight, allocation-free inspection of encoded EAS chunk blobs.

#### Public types
- `public readonly struct Info`
  - **Visibility:** public
  - **What it does:** Holds parsed header metadata including version, encoding, compression, cell count, payload size, and error state.

#### Public methods
- `public static bool TryPeek(byte[] encoded, out Info info)`
  - **Visibility:** public
  - **What it does:** Validates magic/version and extracts header information without decoding the payload.

- `public static string PeekToString(byte[] encoded)`
  - **Visibility:** public
  - **What it does:** Convenience wrapper that returns a human-readable summary string.

---

## Assets/Scripts/EAS/EasHeuristics.cs

### `public static class EasHeuristics`
Chooses optimal encoding strategies based on chunk statistics.

#### Public methods
- `public static EasEncodingType ChooseForDisk(EasChunk chunk, out int paletteSizeOut, bool allowBitplane = false)`
  - **Visibility:** public
  - **What it does:** Selects Uniform, Palette, Dense, or Bitplane encoding based on unique value count and distribution.

- `public static int BitsForPaletteIndex(int paletteSize)`
  - **Visibility:** public
  - **What it does:** Returns the number of bits required to index a palette of the given size.

---

## Assets/Scripts/EAS/EasLz4Bridge.cs

### `public static class EasLz4Bridge`
Thin LZ4 wrapper compiled only when `EAS_LZ4` is defined.

#### Public methods
- `public static byte[] Compress(byte[] input)`
  - **Visibility:** public
  - **What it does:** Compresses input using K4os LZ4 and prefixes uncompressed length.

- `public static byte[] Decompress(byte[] input, int expectedUncompressedBytes)`
  - **Visibility:** public
  - **What it does:** Decompresses LZ4 data and validates expected output size.

---

## Assets/Scripts/EAS/EasLz4Fast.cs

### `public static class EasLz4Fast`
Allocation-free, pointer-based LZ4 utilities designed to be Burst-friendly.

#### Public methods
- `public static byte[] Compress(byte[] input)`
  - **Visibility:** public
  - **What it does:** Compresses managed byte arrays via pointer-based LZ4.

- `public static byte[] Decompress(byte[] input, int expectedUncompressedLen)`
  - **Visibility:** public
  - **What it does:** Decompresses LZ4 data into a fixed-size output buffer.

- `public static byte[] Compress(NativeArray<byte> input)`
  - **Visibility:** public
  - **What it does:** Compresses a NativeArray without intermediate managed copies.

---

## Assets/Scripts/EAS/EasNibbleArray.cs

### `public sealed class EasNibbleArray`
Compact storage for values 0–15 packed into 4-bit nibbles.

#### Public members
- `public EasNibbleArray(int length)`
  - **Visibility:** public
  - **What it does:** Allocates nibble storage for `length` elements.

- `public byte Get(int index)`
  - **Visibility:** public
  - **What it does:** Reads a value from the nibble array.

- `public void Set(int index, byte value0to15)`
  - **Visibility:** public
  - **What it does:** Writes a value into the nibble array.

- `public byte[] RawBytes`
  - **Visibility:** public (getter)
  - **What it does:** Exposes the underlying packed byte buffer.

---

## Assets/Scripts/EAS/Net/Client/EasE2ETestClient.cs

### `public sealed class EasE2ETestClient : MonoBehaviour`
Client-side validation helper for end-to-end networking tests.

#### Behavior
- Subscribes to chunk change events
- Samples random indices to verify values remain in range (0–14)

---

## Assets/Scripts/EAS/Net/Client/EasNetBootstrap.cs

### `public sealed class EasNetBootstrap : MonoBehaviour`
Client bootstrap component ensuring an `EasNetClient` exists and persists.

#### Behavior
- Creates or finds `EasNetClient`
- Hooks chunk change events
- Optionally logs updates
- Marks itself `DontDestroyOnLoad`

---

## Assets/Scripts/EAS/Net/Client/EasNetClient.cs

### `public sealed class EasNetClient : MonoBehaviour`
Client-side networking endpoint for receiving and applying chunk data.

#### Responsibilities
- Receives full chunk syncs
- Applies delta ops
- Tracks baseline IDs
- Notifies listeners when chunks change

#### Public members
- `public System.Action<ChunkCoord> OnChunkChanged`
  - **Visibility:** public
  - **What it does:** Event invoked whenever a chunk is updated or replaced.

---

## Assets/Scripts/EAS/Net/Server/EasE2ETestServer.cs

### `public sealed class EasE2ETestServer : MonoBehaviour`
Server-side end-to-end test harness for the EAS networking pipeline.

#### Responsibilities
- Creates chunks that intentionally trigger each encoding type (Uniform, Dense, Palette, Bitplane)
- Registers chunks with `EasNetServer`
- Subscribes clients automatically on connect
- Periodically applies random edits to validate ops + resync paths

---

## Assets/Scripts/EAS/Net/Server/EasNetServer.cs

### `public sealed class EasNetServer : NetworkBehaviour`
Authoritative server-side chunk manager and replication system.

#### Responsibilities
- Stores authoritative chunk state
- Tracks per-chunk and per-client baselines
- Sends full chunk snapshots on subscribe or resync
- Builds and broadcasts ops deltas from touched indices
- Flushes dirty chunks at a fixed tick rate

#### Key public methods
- `PutChunk(ChunkCoord key, EasChunk chunk)`
- `MarkChunkDirty(ChunkCoord key)`
- `Subscribe(NetworkConnectionToClient conn, ChunkCoord key)`
- `Unsubscribe(NetworkConnectionToClient conn, ChunkCoord key)`
- `TryGetChunk(...)`

---

## Assets/Scripts/EAS/Net/Server/EasServerChunkInterest.cs

### `public sealed class EasServerChunkInterest : NetworkBehaviour`
Server-side interest management component.

#### Responsibilities
- Converts player world position to chunk coordinates
- Computes desired chunk subscriptions based on render distance
- Subscribes/unsubscribes chunks dynamically as players move
- Supports cube or diamond-shaped interest volumes

---

## Assets/Scripts/EAS/Net/Client/ChunkCoord.cs

### `public readonly struct ChunkCoord`
Immutable integer chunk coordinate used as dictionary keys across server and client.

#### Public members
- `x, y, z` — chunk-space coordinates
- Overridden `GetHashCode`, `Equals`, and `ToString` for efficient hashing and debugging

---

## Assets/Scripts/EAS/Net/Client/EasChunkOpsCodec.cs

### `public static class EasChunkOpsCodec`
Encodes and applies lightweight chunk edit operations (O1 format).

#### Responsibilities
- Encodes touched indices into run-based ops blobs
- Applies ops in-place on existing chunks
- Avoids reallocating full chunks on the client

#### Key methods
- `EncodeFromTouched(EasChunk chunk, List<int> tempTouched, bool clearTouched)`
- `ApplyToChunk(EasChunk chunk, byte[] opsBytes)`

---

## Assets/Scripts/EAS/Net/Client/EasNetMessages.cs

### Network Message Types
Defines all Mirror network messages used by EAS chunk replication.

- `ChunkFullMsg` — full chunk snapshot
- `ChunkOpsMsg` — delta ops update
- `ChunkUnsubscribeMsg` — stop tracking a chunk
- `ChunkResyncRequestMsg` — client resync request

---
