using System;

namespace OpenUtau.Core.SignalChain.Effects {
    public enum Ps1Reverb { Off, Room, Hall, SpaceEcho }

    /// <summary>
    /// Makes a voice sound as if a PlayStation played it. The audio is filtered and resampled to a game sample
    /// rate, squeezed through the sound chip's 4-bit ADPCM, optionally given an SPU-style reverb at that rate, and
    /// played back with the chip's 4-point Gaussian interpolation, which muffles it. Optionally an old TV's small
    /// speaker comes last. Works on interleaved stereo at 44.1 kHz.
    /// </summary>
    public class Ps1Filter : IEffect {
        public const int SampleRate = 44100;
        const int Channels = 2;
        /// <summary>
        /// Low-rate samples from taking a sample to playing it: a whole ADPCM block must be decoded, plus the
        /// interpolation's reach.
        /// </summary>
        const int Latency = Ps1Adpcm.BlockSize + 4;
        const int RingSize = 64;
        // The SPU's Gaussian interpolation weighs the samples next to the played one at about 0.15 each, the
        // played one at 0.7: a Gaussian of this width, in samples.
        const double GaussSigma = 0.57;

        int lowRate;
        double ratio;
        bool adpcm = true;
        bool tvSpeaker;
        double mix = 1;
        double gain = 1;

        // A sixth-order Butterworth low-pass, as three sections.
        readonly Biquad[] antiAlias1 = new Biquad[Channels];
        readonly Biquad[] antiAlias2 = new Biquad[Channels];
        readonly Biquad[] antiAlias3 = new Biquad[Channels];
        Biquad tvLow, tvHigh;
        readonly SpuReverb[] reverbs = { new SpuReverb(0), new SpuReverb(1) };

        double phase;
        long produced;
        int filled;
        readonly short[][] block = { new short[Ps1Adpcm.BlockSize], new short[Ps1Adpcm.BlockSize] };
        readonly short[] decoded = new short[Ps1Adpcm.BlockSize];
        readonly sbyte[] nibbles = new sbyte[Ps1Adpcm.BlockSize];
        readonly int[] prev1 = new int[Channels];
        readonly int[] prev2 = new int[Channels];
        readonly float[][] ring = { new float[RingSize], new float[RingSize] };
        // The dry signal, delayed to line up with the processed one when they are mixed.
        readonly float[][] dryLine = { new float[512], new float[512] };
        int dryDelay;
        int dryIndex;

        public Ps1Filter() {
            Configure(11025, true, Ps1Reverb.Off, 0, false, 1, 0);
        }

        /// <param name="lowRate">The game sample rate, from 4 to 44.1 kHz.</param>
        /// <param name="reverbAmount">How much reverb is added, 0 to 1.</param>
        /// <param name="mix">How much of the result replaces the input, 0 to 1.</param>
        public void Configure(int lowRate, bool adpcm, Ps1Reverb reverb, double reverbAmount, bool tvSpeaker,
                double mix, double volumeDb) {
            lowRate = Math.Clamp(lowRate, 4000, SampleRate);
            if (lowRate != this.lowRate) {
                this.lowRate = lowRate;
                ratio = (double)lowRate / SampleRate;
                for (int c = 0; c < Channels; c++) {
                    antiAlias1[c] = Biquad.LowPass(0.45 * lowRate, 0.5176);
                    antiAlias2[c] = Biquad.LowPass(0.45 * lowRate, 0.7071);
                    antiAlias3[c] = Biquad.LowPass(0.45 * lowRate, 1.9319);
                }
                dryDelay = Math.Min(dryLine[0].Length - 1, (int)Math.Round(Latency / ratio));
                Reset();
            }
            this.adpcm = adpcm;
            this.tvSpeaker = tvSpeaker;
            this.mix = Math.Clamp(mix, 0, 1);
            gain = Math.Pow(10, Math.Clamp(volumeDb, -24, 24) / 20);
            foreach (var r in reverbs) {
                r.Configure(reverb, lowRate, Math.Clamp(reverbAmount, 0, 1));
            }
        }

        public bool IsBypassed => mix <= 0 && gain == 1;

        public void Reset() {
            for (int c = 0; c < Channels; c++) {
                antiAlias1[c].Reset();
                antiAlias2[c].Reset();
                antiAlias3[c].Reset();
                prev1[c] = prev2[c] = 0;
                Array.Clear(ring[c]);
                Array.Clear(dryLine[c]);
                reverbs[c].Reset();
            }
            tvLow = Biquad.LowPass(4200, 0.707);
            tvHigh = Biquad.HighPass(280, 0.707);
            phase = 0;
            produced = 0;
            filled = 0;
            dryIndex = 0;
        }

        public void Process(float[] buffer, int offset, int count) {
            Span<double> filtered = stackalloc double[Channels];
            Span<double> wet = stackalloc double[Channels];
            for (int i = offset; i + 1 < offset + count; i += Channels) {
                for (int c = 0; c < Channels; c++) {
                    filtered[c] = antiAlias3[c].Run(antiAlias2[c].Run(antiAlias1[c].Run(buffer[i + c])));
                }
                // Take a game-rate sample whenever its time comes; decode each full block.
                phase += ratio;
                if (phase >= 1) {
                    phase -= 1;
                    for (int c = 0; c < Channels; c++) {
                        block[c][filled] = (short)Math.Clamp(Math.Round(filtered[c] * 32767), short.MinValue, short.MaxValue);
                    }
                    produced++;
                    if (++filled == Ps1Adpcm.BlockSize) {
                        filled = 0;
                        for (int c = 0; c < Channels; c++) {
                            DecodeBlock(c);
                        }
                    }
                }
                // Play back the game-rate samples a fixed latency behind, at this frame's point between them.
                double position = produced - Latency + phase;
                for (int c = 0; c < Channels; c++) {
                    wet[c] = Gaussian(ring[c], position);
                }
                if (tvSpeaker) {
                    // One small speaker: mono, without lows or highs, a little overdriven.
                    double mono = tvLow.Run(tvHigh.Run((wet[0] + wet[1]) * 0.5));
                    wet[0] = wet[1] = Math.Tanh(1.8 * mono) / Math.Tanh(1.8) * 1.2;
                }
                for (int c = 0; c < Channels; c++) {
                    var line = dryLine[c];
                    line[dryIndex] = buffer[i + c];
                    float dry = line[(dryIndex - dryDelay + line.Length) % line.Length];
                    double y = mix >= 1 ? wet[c] : dry + (wet[c] - dry) * mix;
                    buffer[i + c] = (float)(y * gain);
                }
                dryIndex = (dryIndex + 1) % dryLine[0].Length;
            }
        }

        void DecodeBlock(int channel) {
            if (adpcm) {
                Ps1Adpcm.EncodeBlock(block[channel], ref prev1[channel], ref prev2[channel], nibbles, decoded);
            } else {
                block[channel].CopyTo(decoded, 0);
            }
            long first = produced - Ps1Adpcm.BlockSize;
            for (int j = 0; j < Ps1Adpcm.BlockSize; j++) {
                ring[channel][(first + j) & (RingSize - 1)] = reverbs[channel].Run(decoded[j] / 32768f);
            }
        }

        static float Gaussian(float[] samples, double position) {
            double floor = Math.Floor(position);
            long n = (long)floor;
            double t = position - floor;
            double sum = 0, weights = 0;
            for (int k = -1; k <= 2; k++) {
                double d = k - t;
                double w = Math.Exp(-d * d / (2 * GaussSigma * GaussSigma));
                sum += w * samples[(n + k) & (RingSize - 1)];
                weights += w;
            }
            return (float)(sum / weights);
        }

        /// <summary>A second-order filter in direct form I, from the RBJ cookbook.</summary>
        struct Biquad {
            double b0, b1, b2, a1, a2, x1, x2, y1, y2;

            public static Biquad LowPass(double hz, double q) => Make(hz, q, false);
            public static Biquad HighPass(double hz, double q) => Make(hz, q, true);

            static Biquad Make(double hz, double q, bool high) {
                double w = 2 * Math.PI * Math.Min(hz, SampleRate * 0.49) / SampleRate;
                double alpha = Math.Sin(w) / (2 * q);
                double cos = Math.Cos(w);
                double a0 = 1 + alpha;
                double b = high ? (1 + cos) / 2 : (1 - cos) / 2;
                return new Biquad {
                    b0 = b / a0,
                    b1 = (high ? -(1 + cos) : 1 - cos) / a0,
                    b2 = b / a0,
                    a1 = -2 * cos / a0,
                    a2 = (1 - alpha) / a0,
                };
            }

            public double Run(double x) {
                double y = b0 * x + b1 * x1 + b2 * x2 - a1 * y1 - a2 * y2;
                x2 = x1;
                x1 = x;
                y2 = y1;
                y1 = y;
                return y;
            }

            public void Reset() => x1 = x2 = y1 = y2 = 0;
        }

        /// <summary>
        /// A small reverb in the style of the SPU's, which ran at a low rate from its own sound memory: four
        /// damped feedback delays into two all-pass diffusers. The right channel's delays are a little longer.
        /// </summary>
        sealed class SpuReverb {
            readonly int channel;
            float[][] combs = Array.Empty<float[]>();
            int[] combIndex = Array.Empty<int>();
            float[] combLow = Array.Empty<float>();
            float[][] allpasses = Array.Empty<float[]>();
            int[] allpassIndex = Array.Empty<int>();
            float feedback, damp, amount;
            Ps1Reverb type = Ps1Reverb.Off;
            int rate;

            public SpuReverb(int channel) {
                this.channel = channel;
            }

            public void Configure(Ps1Reverb type, int rate, double amount) {
                this.amount = (float)amount;
                if (type == this.type && rate == this.rate) {
                    return;
                }
                this.type = type;
                this.rate = rate;
                (double[] combMs, float fb, float dampening) = type switch {
                    Ps1Reverb.Room => (new[] { 29.7, 37.1, 41.1, 43.7 }, 0.72f, 0.35f),
                    Ps1Reverb.Hall => (new[] { 50.3, 56.7, 61.1, 68.9 }, 0.84f, 0.25f),
                    Ps1Reverb.SpaceEcho => (new[] { 187.0, 263.0 }, 0.6f, 0.3f),
                    _ => (Array.Empty<double>(), 0f, 0f),
                };
                double spread = channel * 1.13;
                combs = Array.ConvertAll(combMs, ms => new float[Samples(ms + spread)]);
                combIndex = new int[combs.Length];
                combLow = new float[combs.Length];
                allpasses = combs.Length == 0 ? Array.Empty<float[]>()
                    : new[] { new float[Samples(5.0 + spread)], new float[Samples(1.7 + spread)] };
                allpassIndex = new int[allpasses.Length];
                feedback = fb;
                damp = dampening;
            }

            int Samples(double ms) => Math.Max(1, (int)Math.Round(ms * rate / 1000));

            public void Reset() {
                foreach (var line in combs) Array.Clear(line);
                foreach (var line in allpasses) Array.Clear(line);
                Array.Clear(combLow);
            }

            public float Run(float x) {
                if (combs.Length == 0 || amount <= 0) {
                    return x;
                }
                float input = x * 0.5f;
                float sum = 0;
                for (int k = 0; k < combs.Length; k++) {
                    var line = combs[k];
                    float delayed = line[combIndex[k]];
                    combLow[k] = delayed * (1 - damp) + combLow[k] * damp;
                    line[combIndex[k]] = input + combLow[k] * feedback;
                    combIndex[k] = (combIndex[k] + 1) % line.Length;
                    sum += delayed;
                }
                float y = sum / combs.Length;
                for (int k = 0; k < allpasses.Length; k++) {
                    var line = allpasses[k];
                    float delayed = line[allpassIndex[k]];
                    float v = y + delayed * 0.5f;
                    line[allpassIndex[k]] = v;
                    allpassIndex[k] = (allpassIndex[k] + 1) % line.Length;
                    y = delayed - v * 0.5f;
                }
                return x + y * amount * 2;
            }
        }
    }
}
