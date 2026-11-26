# Reversible coding

A JEMA block spans a time interval across all tracks. The encoder evaluates several representations of its words and writes the shortest complete payload, including the Zstandard frame overhead. A fixed-size index record carries the coding mode. The raw candidate puts a ceiling on payload size for incompressible data. Separate blocks can choose different predictors and layouts.

## Magnitude differences

For a finite nonnegative binary32 number, the exponent and fraction interpreted together as a 31-bit integer are monotonic in numerical magnitude. Within a normal binade with unbiased exponent `e`, changing the sample by one representable step changes its magnitude word by one. A difference of `d` word units within that binade corresponds to `d * 2^(e-23)` in the sample domain. The slope changes across binades. The modular word difference remains defined for crossings, subnormals, and every nonfinite representation.

Subtracting successive magnitudes sharpens the distribution where short waveforms are locally smooth. A second difference can sharpen it further where the first difference changes slowly. The centered zigzag mapping sends small positive and negative differences to small unsigned values, so many upper residual planes become sparse. The sign is predicted separately so sign crossings avoid introducing a high-bit discontinuity into the magnitude residual.

Inverse zigzag recovers the modular residual. One or two cumulative sums recover the original magnitude, depending on the order. XOR recovers the sign. No arithmetic operates on the samples as floating-point values. The word predictors and the rearrangements are bijections.

## Coupling across channels and tracks

The sign model can exploit shared polarity between nearby channels or leakage between tracks. Write `s_i(t)` for a lane's sign and `s_p(t)` for its parent's sign. The transient-coded sign is

```
g_i(t) = (s_i(t) XOR s_p(t)) XOR (s_i(t-1) XOR s_p(t-1))
```

When both lanes cross zero together, the residual cancels. A constant polarity inversion also produces a stable relative sign. Parent relationships are implicit from the track layout and only point to earlier lanes. Magnitudes retain their own predictors. Separate sign prediction is useful when polarity is shared but amplitude or floating-point scale differs.

The single entropy stream sees the rearranged planes of all lanes. Homogeneous planes can share statistical coding and repeated byte sequences. For example, high residual planes may be similarly sparse across tracks of different nominal loudness. This sharing competes with per-track adaptation. Selecting one recipe for a block can cost more than selecting the best recipe separately for each track.

An invertible map preserves the block's joint entropy for a fixed boundary state. Its benefit comes from making distributions and repeated patterns easier for a finite compressor to model. The experiments compare complete sizes to attribute gains to the transforms and joint representation.

## Exact fixed-point decomposition

The magnitude-word predictor uses a different numerical step in each binade. A second candidate projects each lane onto a power-of-two grid that is fixed across the block. The scale is set by the lane's greatest normal exponent. For a nonzero core integer, its leading-bit position and the stored scale recover the sample exponent directly. The core sign supplies the sample sign. Remaining significand bits are written to the side stream, and words below the grid or outside the normal range are stored as exact exceptions.

The fixed grid lets second-order prediction follow the actual waveform through sign and binade crossings. The high planes of the signed integer residual become sparse and are coded jointly across tracks. The paired side stream retains all original bits and can be stored raw when its entropy is high. Splitting the core and side also prevents low-significance bits from diluting compression of the predicted waveform. The measured cost includes scales, framing, side bits, exception words, and the index.

Variable bit counts depend on the restored integer core, so the decoder reads and reconstructs that core first. All reconstruction uses bounded integer arithmetic. The core and side occupy at most one decoded block each in temporary buffers.

## Long-range exact reuse

The encoder hashes original block PCM to nominate earlier anchors. It verifies equality against reconstructed anchor bytes before emitting a reference. Equal-size repeats require only their index record. Anchors are stored payloads. A reference can span the length of the file and still needs at most two index records and a single payload decode.

Exact reuse benefits duplicated, block-aligned material. Its detection granularity is the complete multitrack block. The waveform-similarity experiment in [RESEARCH.md](RESEARCH.md) measures approximate single-lane matches under exact XOR residual coding. The reference library uses exact anchors after accounting for the measured gain and the extra decoding work an approximate reference would require.

## Access and resource bound

A request touches only the blocks intersecting its frame range. A single-frame request reconstructs at most one decoded block, bounded at 4 MiB. A request of `n > 0` frames and block size `B` intersects at most `ceil((n+B-1)/B)` blocks. Reference distance adds one record read per reference and zero extra payload decodes. The library reads each record by arithmetic offset and retains block-sized scratch and output storage, independent of duration. The encoder retains an index and optional anchor map proportional to the number of blocks.

The format reuses RFC 8878 Zstandard, byte reordering, bit-plane transposition, modular differences, and XOR. The JEMA design combines these building blocks with a track-aware sign model, exact block-adaptive fixed-point decomposition, joint coding of residual planes, and a bounded-reference index. [Research context and evidence](RESEARCH.md)
