# Float32 PCM research

JEMA's file-level results are reported against uncompressed Float32 PCM. [Benchmarks](BENCHMARKS.md)

## Sample structure

A normal Float32 word stores sign, a biased exponent, and 23 explicit significand bits. Separating sign from magnitude limits the discontinuity caused by sign crossings. A block-local power-of-two grid lets a predictor operate on an integer representation of the waveform. The exact remainder stream preserves the discarded significand bits and exceptional words.

The low eight significand bits had nearly one bit of marginal entropy per bit position on three sampled six-track sources and on the calibrated field recording. Lower precision in a source can produce more predictable residual bits. This is a marginal statistic based on sampled windows, and the encoder selects modes by the stored payload size.

## Multitrack coding

Adaptive prediction uses lane-specific coefficients within an indexed block. A common read window can contain tonal and transient signals, so each lane can choose a predictor and update it at section boundaries. The stored stream combines the predictor table and rearranged residual planes from all tracks. Its side data restores any information removed by the fixed-point projection. [Reversible coding](ALGORITHM.md#adaptive-prediction-for-shared-playback-windows)

## Exact block reuse

An exact reference can represent a copied loop when the complete multitrack block has the same alignment and bit pattern. JEMA checks equality before writing a reference. It uses at most one anchor index lookup and decodes only the anchor payload for that block.

A scan of 42,534 track blocks in the six model outputs found no duplicate non-silent track blocks. A deterministic control with one repeated track found 28 non-silent matches among 64 track blocks. Reuse opportunities depend on production workflow, alignment, and sample precision.

```sh
python research/bit_statistics.py stems.f32le 2,2,2,2,2,2 --archive stems.jema
python research/controls.py controls
```

## Related techniques

- Predecessor XOR represents local changes in a floating-point word. [Gorilla paper](https://pages.cs.wisc.edu/~yxy/cs839-s20/papers/gorilla.pdf)
- Bit-plane transposition groups bits at the same significance level. [Bitshuffle paper](https://arxiv.org/abs/1503.00638)
- Lossless floating-point audio research describes splitting a signal into an integer sequence and exact residual. [Harada et al.](https://www.kecl.ntt.co.jp/people/moriya.takehiro/publications.html)
- Linear predictors and cross-channel residual coding motivate alternative representations of sample words. [FLAC specification](https://www.rfc-editor.org/rfc/rfc9639.html) · [MPEG-4 ALS multichannel prediction](https://www.ntt-review.jp/archive/ntttechnical.php?contents=ntr200712sp2.html)
