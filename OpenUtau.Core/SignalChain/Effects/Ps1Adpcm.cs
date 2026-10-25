using System;

namespace OpenUtau.Core.SignalChain.Effects {
    /// <summary>
    /// The PlayStation sound chip's (SPU) sample format: blocks of 28 samples, each stored as a 4-bit step from a
    /// prediction by one of five filters, scaled by a shift. Encoding and decoding it gives the console's own
    /// compression artifacts.
    /// </summary>
    public static class Ps1Adpcm {
        public const int BlockSize = 28;

        // The predictor filters' coefficients, in 64ths: prediction = (p1 * Pos + p2 * Neg + 32) >> 6.
        static readonly int[] Pos = { 0, 60, 115, 98, 122 };
        static readonly int[] Neg = { 0, 0, -52, -55, -60 };

        /// <summary>
        /// Decodes one block as the SPU does, from <paramref name="prev1"/> and <paramref name="prev2"/>, the last
        /// two samples of the block before, which it updates.
        /// </summary>
        public static void DecodeBlock(ReadOnlySpan<sbyte> nibbles, int filter, int shift,
                ref int prev1, ref int prev2, Span<short> output) {
            for (int i = 0; i < BlockSize; i++) {
                int sample = Clamp16((nibbles[i] << 12 >> shift) + ((prev1 * Pos[filter] + prev2 * Neg[filter] + 32) >> 6));
                output[i] = (short)sample;
                prev2 = prev1;
                prev1 = sample;
            }
        }

        /// <summary>
        /// Encodes one block, choosing the filter and shift that decode closest to the input, and gives what the
        /// SPU would decode from it. Updates <paramref name="prev1"/> and <paramref name="prev2"/> as decoding does.
        /// </summary>
        public static (int filter, int shift) EncodeBlock(ReadOnlySpan<short> input, ref int prev1, ref int prev2,
                Span<sbyte> nibbles, Span<short> decoded) {
            Span<sbyte> candidate = stackalloc sbyte[BlockSize];
            Span<short> candidateDecoded = stackalloc short[BlockSize];
            long bestError = long.MaxValue;
            int bestFilter = 0, bestShift = 12, bestPrev1 = prev1, bestPrev2 = prev2;
            for (int filter = 0; filter < Pos.Length; filter++) {
                // The smallest step that holds the largest residual of an open-loop prediction.
                int largest = 0;
                int p1 = prev1, p2 = prev2;
                for (int i = 0; i < BlockSize; i++) {
                    int residual = input[i] - ((p1 * Pos[filter] + p2 * Neg[filter] + 32) >> 6);
                    largest = Math.Max(largest, Math.Abs(residual));
                    p2 = p1;
                    p1 = input[i];
                }
                int scale = 0;
                while (scale < 12 && largest > 7 << scale) {
                    scale++;
                }
                // Decoding drifts from the open-loop prediction, so the next larger step may do better.
                for (int s = scale; s <= Math.Min(12, scale + 1); s++) {
                    p1 = prev1;
                    p2 = prev2;
                    long error = 0;
                    for (int i = 0; i < BlockSize; i++) {
                        int prediction = (p1 * Pos[filter] + p2 * Neg[filter] + 32) >> 6;
                        int step = (int)Math.Clamp(Math.Round((input[i] - prediction) / (double)(1 << s),
                            MidpointRounding.AwayFromZero), -8, 7);
                        int sample = Clamp16((step << s) + prediction);
                        candidate[i] = (sbyte)step;
                        candidateDecoded[i] = (short)sample;
                        long difference = input[i] - sample;
                        error += difference * difference;
                        p2 = p1;
                        p1 = sample;
                    }
                    if (error < bestError) {
                        bestError = error;
                        (bestFilter, bestShift, bestPrev1, bestPrev2) = (filter, 12 - s, p1, p2);
                        candidate.CopyTo(nibbles);
                        candidateDecoded.CopyTo(decoded);
                    }
                }
            }
            prev1 = bestPrev1;
            prev2 = bestPrev2;
            return (bestFilter, bestShift);
        }

        static int Clamp16(int x) => Math.Clamp(x, short.MinValue, short.MaxValue);
    }
}
