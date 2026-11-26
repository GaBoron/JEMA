# Float32 multitrack compression research

The corpus and complete-size methodology are described in [BENCHMARKS.md](BENCHMARKS.md). A ratio below 100% expresses a reduction from canonical Float32 PCM. Each coding experiment preserves the original 32-bit words.

## Float32 structure

A normal Float32 word has a sign, an eight-bit biased exponent, and 23 explicit significand bits. Mantissa bits can carry conversion and DSP rounding even in nearly silent passages. Their compressibility depends on provenance. PCM derived directly from a narrower integer representation may have deterministic trailing zeros. Model output and processed recordings can occupy substantially more of the significand space. Per-bit entropy and sign-transition statistics can be reproduced with `research/bit_statistics.py`.

| Source | Mean marginal entropy of low eight bits | Temporal sign residual | Within-track sign residual | Cross-track sign residual |
| --- | ---: | ---: | ---: | ---: |
| stem-01 | 1.00 | 0.80 | 0.72 | 0.87 |
| stem-03 | 1.00 | 0.78 | 0.71 | 0.87 |
| stem-06 | 1.00 | 0.64 | 0.60 | 0.74 |
| uvr-two | 0.89 | 0.28 | 0.36 | 0.47 |
| album-dsp | 0.85 | 0.13 | 0.13 | 0.19 |
| recorded-float | 1.00 | 0.21 | 0.28 | 0.28 |

Each row uses 24 sampled windows across the source. Low-bit entropy is measured separately for each bit position, then averaged. Sign entropy is measured separately for each channel, then averaged. These estimates are in bits per binary symbol and leave higher-order dependencies unmeasured. Stereo coupling reduces this particular sign entropy on the three six-track samples and raises it on the other stereo material.

The short-term experiments evaluated original words, previous-word XOR, modular differences in float-word order, and differences in the magnitude field after removing the sign. Separating sign from magnitude avoids a large coded discontinuity at sign crossings. Remapping the word alone still scales the numerical difference by the local binade. A power-of-two grid held constant across a block lets second-order prediction model numerical curvature. Recovering the exponent from the core integer and writing the remaining bits separately reduces the cost of that representation on real Float32 material.

On the full `stem-01` sample, the best adaptive word-transform implementation occupied 242,929,452 bytes before the fixed-point candidate was added. The same geometry and compression level with the candidate enabled occupied 226,174,323 bytes, a 6.90% reduction from that implementation. The fixed-point candidate was selected in 794 of 835 blocks. It reduced file size by 5.66–11.54% across the six six-track files relative to the word-transform implementation. The source PCM and raw fallback were identical. The effect includes every byte of the exact side stream.

The uniform random-word control stored all 32 blocks raw. Its file was 820 bytes larger than its 3,145,728-byte PCM input, exactly the header, track layout, and index overhead.

## Joint modeling

The direct cross-track hypothesis is that corresponding channels sometimes share polarity transitions. A previous-track sign predictor can cancel those transitions. Independent predictions retain an alternative when leakage, stereo width, and instrument changes make the cross-track model unhelpful. On `stem-01`, removing the dedicated cross-track sign candidate increased the final file by 470 bytes. The measured joint-file benefit is much greater than that explicit predictor's contribution, consistent with the joint entropy stream and shared framing doing most of the work on this sample.

The same-track predictor uses the first lane as the sign parent. Its transform affects one bit per word. Cross-channel magnitude similarity and sign similarity are distinct. In the exploratory 24-block scan of `stem-01`, a whole-word XOR between left and right reconstructed exactly but increased the best byte-plane compressed size compared with temporal magnitude coding. That negative result is specific to the tested transform and sample.

The independent-track comparator gives each track its own mode selection and equal reset spacing. Shared framing alone saves `24*(tracks-1)` bytes per block plus the separate headers. The remaining file-size difference captures the net effect of shared coding and the cost of using a common recipe. That net measurement includes model overhead and can be negative when independent adaptation wins.

## Long-range structure

The approximate-reference probe searches each selected lane independently for a similar prior waveform at least one second away. It pools a 2048-frame query into 64 mean values, compares unit-normalized feature vectors on a 512-frame grid, checks the four strongest absolute coarse correlations, and searches each location over ±32 frames. A predictive payload is the better of bit-plane Zstandard on a word XOR and on a temporal-residual XOR. It includes an idealized nine-byte pointer and recipe. The comparator selects the smaller of direct bit-plane word coding and direct temporal-residual coding.

| Source | Windows queried | Windows with a smaller reference payload | Best-case byte reduction | Exact shortlisted matches |
| --- | ---: | ---: | ---: | ---: |
| stem-01 | 192 | 6 | 0.095% | 0 |
| stem-03 | 192 | 10 | 0.028% | 0 |
| stem-05 | 192 | 9 | 0.022% | 0 |
| stem-06 | 192 | 10 | 0.017% | 0 |

The bass lane of `stem-06` had median best absolute normalized waveform correlation of about 0.99. Its candidate still rarely beat local prediction. Strong similarity in the signal domain can leave different significand bits in the same time interval. The table gives a favorable bound for this particular screened candidate set. It charges no extra reads or anchor materialization and uses a local comparator weaker than the final encoder. It provides no bound on other reference searches, predictors, or datasets.

Exact whole-block references work when the PCM itself repeats at matching boundaries. In the seeded repeated-loop control, 28 of 32 blocks were references. The file shrank from 1,412,764 to 177,313 bytes, an 87.45% reduction. The companion control repeats one track while the second changes. It produced no whole-block references, even though an independent encoder could reuse the repeated stem. Track-granular references would add dependency and index overhead. Whole-block exact references leave the number of decoded payloads per request unchanged.

```sh
python research/controls.py controls
python research/repetition.py stems.f32le 44100 12 --channel 0 --channel 2 --channel 4 --channel 6 --channel 8 --channel 10
python research/bit_statistics.py stems.f32le 2,2,2,2,2,2 --archive stems.jema
```

## Prior work and implementation boundary

- Gorilla uses predecessor XOR and codes the shape of its nonzero span for time-series values. The previous-word XOR mode here is evaluated with PCM-specific layout and an independent block boundary. [Gorilla paper](https://pages.cs.wisc.edu/~yxy/cs839-s20/papers/gorilla.pdf)
- Bitshuffle transposes bits from adjacent values to expose similar lanes to a general compressor. JEMA applies the transform to lane-major predictor outputs and defines its precise layout in the format. [Masui et al.](https://arxiv.org/abs/1503.00638)
- MPEG-4 ALS literature describes floating audio decomposed into an integer signal and exact residual under an approximate common factor. The fixed grid described here uses a block-local power of two and reconstructs exponent and residual length from the signed core. Publication of the related method limits any broad claim of novelty for exact integer-core decomposition. [Harada et al.](https://www.kecl.ntt.co.jp/people/moriya.takehiro/publications.html)
- WavPack and OptimFROG publicly document Float32 lossless modes. WavPack is measured as an executable black box. FLAC's integer sample model makes it a reference for framing and random access. [WavPack documentation](https://www.wavpack.com/wavpack_doc.html) · [OptimFROG IEEE Float](https://losslessaudio.org/IEEE_Float.php) · [FLAC specification](https://www.rfc-editor.org/rfc/rfc9639.html)
- Pcodec provides an independent baseline for typed float sequences. OpenZL illustrates how structural splits and homogeneous streams can improve general-purpose compression. JEMA uses Zstandard for its interchangeable entropy layer. [Pcodec paper](https://arxiv.org/abs/2502.06112) · [OpenZL technical overview](https://engineering.fb.com/2025/10/06/developer-tools/openzl-open-source-format-aware-compression-framework/) · [Zstandard specification](https://www.rfc-editor.org/rfc/rfc8878.html)
