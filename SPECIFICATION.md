# JEMA experimental format, revision 1

The key words **must**, **should**, and **may** describe requirements, recommendations, and permissions. File offsets are measured from byte zero. All multibyte integers are unsigned little-endian unless stated otherwise. Every file offset and the frame count must fit in a signed 64-bit integer. These rules define revision 1. Future experimental revisions may be incompatible.

## PCM representation

A sample is one IEEE 754 binary32 bit pattern. For each frame, the file describes the complete set of tracks in their declared order and the channels of each track in their declared order. The canonical PCM byte sequence places the 32 bits of each sample little-endian and interleaves the resulting words by frame. Every 32-bit pattern is allowed, including both zeros, subnormals, infinities, and every NaN representation. Sample words are never evaluated as floating-point numbers.

All tracks have the same sample rate and frame count. The sample rate is in hertz and must be in 1–2,147,483,647. A file has 1–256 tracks, each with at least one channel, with at most 256 channels in total. Channel positions are ordinal within a track. Speaker identities and track names can be stored in an application payload.

## File structure

The header and track layout are followed by stored audio block payloads, the block index, and an optional metadata trailer. Blocks partition the timeline into consecutive groups of `block_frames` frames. The last block may be shorter. Block numbers and frame numbers start at zero.

| Header offset | Bytes | Value |
| ---: | ---: | --- |
| 0 | 8 | Magic `4a 45 4d 41 0d 0a 1a 0a` |
| 8 | 2 | Format revision, 1 |
| 10 | 2 | Reserved, zero |
| 12 | 4 | Header CRC |
| 16 | 4 | Sample rate in hertz |
| 20 | 4 | `block_frames`, strictly positive |
| 24 | 8 | Total frame count, possibly zero |
| 32 | 8 | Offset of the block index |
| 40 | 2 | Track count, `T` |
| 42 | 6 | Reserved, zero |
| 48 | `2*T` | Channel count of each track in order |

All CRC fields use CRC-32/ISO-HDLC. Its reflected polynomial is `0xedb88320`, its initial value and final XOR are `0xffffffff`, and the check value for ASCII `123456789` is `0xcbf43926`. The header CRC covers bytes 0 through `48+2*T-1` with its own four bytes set to zero.

Let `W = 4 * total_channels` and `B = block_frames`. Both `B*W` and the PCM size implied by the header must be representable. `B*W` must be at most 4,194,304 and `total_frames*W` must fit in a signed 64-bit integer. The index has exactly `ceil(total_frames/B)` records. A file with zero frames has no block payloads or records. Encoders place stored block payloads in block order, directly after the track layout, and the index immediately after the last payload.

## Block index and access

The record for block `j` starts at `index_offset + 24*j`. Index offsets, sizes, and range checks must be evaluated without arithmetic overflow.

| Record offset | Bytes | Value |
| ---: | ---: | --- |
| 0 | 8 | Stored payload offset, or anchor block number in mode 255 |
| 8 | 4 | Stored payload size, or zero in mode 255 |
| 12 | 4 | CRC of the block's canonical PCM byte sequence |
| 16 | 1 | Block mode |
| 17 | 3 | Reserved, zero |
| 20 | 4 | Index record CRC |

The record CRC covers the 24-byte record with its CRC field zeroed, followed by the eight-byte little-endian value `j`. The index must start at or after the end of the track layout and fit within the file. A stored block payload must start at or after the end of the track layout and end at or before the index offset. Its size must be positive and is bounded by the canonical byte size of the block. Mode 0 has exactly that size. Compressed modes have strictly smaller payloads. A range reader validates the header, then validates the records and payloads used by the requested range.

Mode 255 represents the same canonical PCM as an earlier stored block of the same decoded size. The anchor must have a stored mode and matching PCM CRC. Reference chains are invalid. A decoder reads the anchor directly by its index record and uses its samples at the referenced block's timeline position. The block number distance does not change the amount of decoding.

For a request `[first, first+count)`, reject ranges outside `[0,total_frames]`. For each intersecting block, decode its complete canonical PCM and copy the intersection into the result. A zero-frame request returns empty PCM. A failed structural, decompression, or PCM CRC check makes that read fail. Callers should discard a range buffer if the read fails. Invalid modes, truncation, and unsupported header revisions are errors. Integrity checks detect accidental corruption. CRC fields have no authentication semantics.

## Stored modes

| Mode | Payload | Predictor | Rearrangement |
| ---: | --- | --- | --- |
| 0 | Canonical PCM | — | — |
| 1 | Zstandard | — | — |
| 2–3 | Zstandard | Original words | Byte / bit |
| 4–5 | Zstandard | Previous-word XOR | Byte / bit |
| 6–7 | Zstandard | First-order magnitude | Byte / bit |
| 8–9 | Zstandard | Second-order magnitude | Byte / bit |
| 10–11 | Zstandard | First-order magnitude with within-track sign prediction | Byte / bit |
| 12–13 | Zstandard | First-order magnitude with cross-track sign prediction | Byte / bit |
| 14–15 | Fixed-point core and side stream | Second-order signed integer | Byte / bit on the core |
| 16–17 | Fixed-point core and side stream | Section-adaptive linear prediction and within-track residual coupling | Byte / bit on the core |

Modes 18–254 are reserved. Every Zstandard stream uses the [Zstandard format](https://www.rfc-editor.org/rfc/rfc8878.html). Writers emit one normal Zstandard frame per stream without a dictionary or prefix. A reader must support that representation and may accept equivalent dictionary-free framing. Modes 1–13 expand to exactly the canonical PCM size of the block. Mode 1 yields interleaved canonical PCM directly.

### Prediction

The following rules operate on word values in each channel lane. Predictor state is reset at the beginning of every block. The previous word, previous relative sign, and previous magnitude difference are initially zero.

- Original words produce the sample word unchanged.
- Previous-word XOR produces the word XOR its predecessor. Apply XOR with the previous reconstructed word to invert it.
- Magnitude predictors split a word into its sign bit `s` and lower 31-bit magnitude `m`. Arithmetic on magnitudes and differences is in the ring modulo `2^31`. First-order prediction forms `d = m - previous_m`. Second-order prediction forms `r = d - previous_d`. First-order uses `r = d`.
- The low 31 bits of a magnitude predictor's output are `((r << 1) XOR -(r >> 30)) AND 0x7fffffff`, with `r` in 0 through `2^31-1`. Invert with `((z >> 1) XOR -(z AND 1)) AND 0x7fffffff`. For second-order, add the previous difference to recover `d`. Add `d` to the previous magnitude to recover `m`.
- For all magnitude modes, the high bit is the current relative sign XOR the previous relative sign. The relative sign is `s` XOR the parent lane's sign at the same frame. A lane without a parent uses parent sign zero. Decode the relative sign by prefix XOR, then XOR the already reconstructed parent sign to obtain `s`.

Modes 6–9 have no parents. Modes 10–11 use the first channel of the same track as the parent of every other channel of that track. For modes 12–13, channel `k` uses channel `k` of the immediately preceding track if that channel exists. Otherwise it uses the first channel of the current track if `k>0`. Channel 0 of the first track has no parent. These relations require inverse prediction in declared lane order.

### Rearrangement

For byte rearrangement, iterate byte position 0 through 3 from least to most significant. For each byte position, iterate channel lanes in declared order and frames in chronological order, emitting the indicated byte of each predicted word.

For bit rearrangement, iterate bit position 0 through 31 from least to most significant. For each bit position, iterate lanes in declared order and complete chronological groups of eight frames. In each group emit one byte with frame `k` in bit `k` for `k=0...7`. After all 32 planes, append the leftover words as little-endian 32-bit integers in lane order, with each lane's remaining frames in chronological order.

### Fixed-point modes

Let `C` be the total channel count. The fixed-point payload starts with a 4-byte Zstandard core length, followed by `C` one-byte scale exponents in lane order, then the compressed core and side stream. The core length and remaining side stream length must both be positive. Each scale exponent `E` is in 0–254. Writers use the greatest biased exponent of any normal sample in its lane for the block, or zero if none occur.

For each normal word with biased exponent `e`, form the 24-bit significand `M = 0x800000 OR fraction` and let `q = E - e + 4`. If `q < 24`, the signed core integer `a` has magnitude `M >> q` and the sample's original sign. Store the low `q` bits of `M` in the side stream. For words with `q >= 24` and for all subnormal, zero, or nonfinite words, use core integer zero and store the original word as an exception.

For modes 14–15, adopt the 32-bit two's-complement bit patterns of the signed core integers. Take two successive modular differences in the ring modulo `2^32`, initializing both previous states to zero. Fold each second difference `r` with `(r << 1) XOR -(r >> 31)`, where the shift right is logical and the result is reduced modulo `2^32`. Rearrange these words as above and Zstandard-compress them. The decompressed rearranged core is exactly the canonical PCM size.

In modes 14–15, unfold with `(z >> 1) XOR -(z AND 1)` and integrate twice modulo `2^32`. Modes 16–17 recover the same fixed-point integers using the predictor table below. After interpreting the reconstructed integer as signed, handle zero as an exception. For each nonzero value, let `h = floor(log2(abs(a)))`. Require `h <= 19` and `e = E - 19 + h` in 1–254. Read `23-h` low significand bits from the side stream and append them to `abs(a)`. Restore the binary32 exponent `e`, the fraction below the implicit leading bit, and the sign of `a`.

The uncompressed side stream begins with all low-significand fields in lane order, with frames in chronological order within each lane. Each field is written least significant bit first into the next available bits of the stream, starting at bit 0 of the first byte. Pad the final byte with zero bits. Then append all exception words in lane order as little-endian words, with frames in chronological order. The uncompressed side length is `ceil(sum(23-h)/8) + 4*exception_count`. Derive it from the decoded core. A stored side stream exactly that size is raw. A shorter one is a Zstandard stream and must inflate to that size. A larger stream or nonzero pad bits are invalid.

### Adaptive fixed-point modes

For modes 16–17, the Zstandard core expands to the predictor table followed by the rearranged residual words. The final `4*C*F` bytes are the residual words, where `F` is the actual frame count of the block. Bound the preceding table to `1+C*(2+65*ceil(F/2048))` bytes. The side stream and outer payload prefix follow the fixed-point rules above.

The first table byte is 0 for one section of `F` frames or 1 for consecutive sections of up to 2048 frames. For each lane in declared order, read its signed 16-bit gain and then one predictor record per section. Each record contains an order byte and that many signed 16-bit coefficients. An order is 0, 2, 4, 8, 12, 16, 24, or 32. The first lane in each track has gain zero. Other gains are in -512 through 512. Reject missing or extra table bytes and unsupported selectors, gains, and orders.

Within a lane, use signed 32-bit interpretations of values from the ring modulo `2^32`. For order zero use the prediction `2*a[t-1]-a[t-2]`. For nonzero order `p`, the prediction is `floor(sum(c[j]*a[t-1-j], j=0...p-1)/4096)`, using an exact signed accumulator. Samples before frame zero of the block are zero. Sections share the preceding samples of the same block. The temporal residual is the core integer minus the prediction modulo `2^32`.

For a lane with gain `g`, subtract `floor(g*r_first[t]/256)` from its temporal residual modulo `2^32`. Here `r_first[t]` is the signed temporal residual of the first lane of the same track. Fold the resulting residuals by the same zigzag rule as modes 14–15, then rearrange by byte (mode 16) or bit (mode 17). To invert, unfold first, restore within-track coupled residuals, then add each temporal prediction to its residual in chronological order. Treat every floor as rounding toward negative infinity.

## Optional metadata

The audio extent ends after the last index record. If the file ends there, both payloads are absent. Otherwise the trailing bytes contain a complete ID3 blob if present, then application bytes if present, then the 24-byte footer. Each payload is limited to 33,554,432 bytes. At least one payload has nonzero length when a footer is present.

| Footer offset | Bytes | Value |
| ---: | ---: | --- |
| 0 | 8 | ASCII `JEMAMETA` |
| 8 | 4 | ID3 byte count |
| 12 | 4 | Application byte count |
| 16 | 4 | CRC of ID3 bytes, zero if absent |
| 20 | 4 | CRC of application bytes, zero if absent |

The two byte counts plus 24 must match the number of bytes after the audio extent. ID3 is a [v2.3 or v2.4 tag](https://id3.org/Developer%20Information) with its original header, body, and footer if flagged in v2.4. Validate the major version, header marker, non-reserved revision byte, synchsafe size, exact outer length, and matching footer if present. Internal frame payloads are opaque. The application payload is opaque. Metadata readers verify sizes and CRCs independently of PCM. Audio readers do not parse or validate optional bytes. Replacing a trailer can copy the audio extent unchanged.
