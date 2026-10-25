using System;
using System.Collections.Generic;
using System.Linq;

namespace OpenUtau.Core.VoiceActing {
    /// <summary>
    /// A recording of a voice measured every <see cref="HopMs"/>: pitch, loudness and how noisy it is.
    /// Frame i describes the audio around i * HopMs.
    /// </summary>
    public sealed class VoiceAnalysis {
        public const double HopMs = 10;

        /// <summary>MIDI pitch, NaN where unvoiced.</summary>
        public readonly float[] Pitch;
        /// <summary>
        /// Pitch with short unvoiced gaps between voiced frames (consonants) filled in linearly, NaN elsewhere.
        /// </summary>
        public readonly float[] BridgedPitch;
        /// <summary>Loudness in dB relative to full scale.</summary>
        public readonly float[] LoudnessDb;
        /// <summary>0 for a perfectly periodic frame, towards 1 for noise.</summary>
        public readonly float[] Aperiodicity;
        /// <summary>Frames loud enough to be speech rather than silence.</summary>
        public readonly bool[] Active;

        public VoiceAnalysis(float[] pitch, float[] bridgedPitch, float[] loudnessDb, float[] aperiodicity, bool[] active) {
            Pitch = pitch;
            BridgedPitch = bridgedPitch;
            LoudnessDb = loudnessDb;
            Aperiodicity = aperiodicity;
            Active = active;
        }

        public int FrameCount => Pitch.Length;
        public double DurationMs => FrameCount * HopMs;
        public bool IsVoiced(int frame) => float.IsFinite(Pitch[frame]);

        /// <summary>
        /// One of the frame arrays at a time in ms: linear between two frames with values, else the nearer
        /// frame's value, which may be NaN.
        /// </summary>
        public float Sample(float[] values, double ms) {
            if (values.Length == 0) {
                return float.NaN;
            }
            double position = Math.Clamp(ms / HopMs, 0, values.Length - 1);
            int i = Math.Min((int)position, values.Length - 1);
            int j = Math.Min(i + 1, values.Length - 1);
            double t = position - i;
            float a = values[i], b = values[j];
            if (float.IsFinite(a) && float.IsFinite(b)) {
                return (float)(a + (b - a) * t);
            }
            return t < 0.5 ? a : b;
        }

        /// <summary>Whether the frame nearest a time in ms is active.</summary>
        public bool IsActiveAt(double ms) {
            if (Active.Length == 0) {
                return false;
            }
            return Active[(int)Math.Clamp(Math.Round(ms / HopMs), 0, Active.Length - 1)];
        }

        /// <summary>The runs of consecutive frames where a predicate holds, as [start, end) frame ranges.</summary>
        public static List<(int start, int end)> Runs(int count, Func<int, bool> predicate) {
            var runs = new List<(int, int)>();
            int start = -1;
            for (int i = 0; i <= count; i++) {
                bool inside = i < count && predicate(i);
                if (inside && start < 0) {
                    start = i;
                } else if (!inside && start >= 0) {
                    runs.Add((start, i));
                    start = -1;
                }
            }
            return runs;
        }
    }

    /// <summary>
    /// Measures a recorded voice without any model: pitch by YIN (de Cheveigné and Kawahara, 2002) and loudness
    /// by RMS, on the audio decimated to about 16 to 24 kHz.
    /// </summary>
    public static class VoiceAnalyzer {
        public const double MinF0 = 60;
        public const double MaxF0 = 1000;
        const double WindowMs = 25;
        /// <summary>YIN's threshold for picking the first dip of the normalized difference.</summary>
        const double DipThreshold = 0.15;
        /// <summary>Frames whose best dip is above this are unvoiced.</summary>
        const double VoicingThreshold = 0.3;
        /// <summary>Frames this far below the loud frames are silence.</summary>
        const double SilenceRangeDb = 35;
        const double SilenceFloorDb = -70;
        const int MinVoicedFrames = 3;
        /// <summary>The longest unvoiced gap <see cref="VoiceAnalysis.BridgedPitch"/> fills, in frames.</summary>
        public const int MaxBridgeFrames = 15;

        /// <summary>Analyzes interleaved samples of one or more channels.</summary>
        public static VoiceAnalysis Analyze(float[] samples, int sampleRate, int channels = 1) {
            if (sampleRate <= 0) {
                throw new ArgumentOutOfRangeException(nameof(sampleRate));
            }
            channels = Math.Max(1, channels);
            var mono = ToMono(samples, channels);
            int factor = Math.Max(1, sampleRate / 16000);
            var audio = Decimate(mono, factor);
            int rate = sampleRate / factor;

            // Not rounded to whole samples, so frames keep time with the project over long takes.
            double hop = rate * VoiceAnalysis.HopMs / 1000;
            int window = (int)Math.Round(rate * WindowMs / 1000);
            int maxLag = (int)Math.Ceiling(rate / MinF0);
            int minLag = Math.Max(2, (int)Math.Floor(rate / MaxF0));
            int frames = audio.Length == 0 ? 0 : (int)Math.Floor(audio.Length / hop) + 1;

            var pitch = new float[frames];
            var loudness = new float[frames];
            var aperiodicity = new float[frames];
            var difference = new double[maxLag + 2];
            var normalized = new double[maxLag + 2];
            for (int f = 0; f < frames; f++) {
                int center = (int)Math.Round(f * hop);
                double energy = 0;
                for (int j = center - window / 2; j < center + window / 2; j++) {
                    double x = At(audio, j);
                    energy += x * x;
                }
                loudness[f] = (float)(10 * Math.Log10(energy / window + 1e-12));

                // The difference of the window with itself delayed by each lag, centred on the frame.
                int start = center - (window + maxLag) / 2;
                for (int lag = 1; lag <= maxLag + 1; lag++) {
                    double sum = 0;
                    for (int j = 0; j < window; j++) {
                        double d = At(audio, start + j) - At(audio, start + j + lag);
                        sum += d * d;
                    }
                    difference[lag] = sum;
                }
                double running = 0;
                normalized[0] = 1;
                for (int lag = 1; lag <= maxLag + 1; lag++) {
                    running += difference[lag];
                    normalized[lag] = running > 0 ? difference[lag] * lag / running : 1;
                }
                int best = -1;
                for (int lag = minLag; lag <= maxLag; lag++) {
                    if (normalized[lag] < DipThreshold) {
                        while (lag + 1 <= maxLag && normalized[lag + 1] < normalized[lag]) {
                            lag++;
                        }
                        best = lag;
                        break;
                    }
                }
                if (best < 0) {
                    best = minLag;
                    for (int lag = minLag + 1; lag <= maxLag; lag++) {
                        if (normalized[lag] < normalized[best]) {
                            best = lag;
                        }
                    }
                }
                aperiodicity[f] = (float)Math.Clamp(normalized[best], 0, 1);
                // Parabolic interpolation of the dip.
                double a = normalized[best - 1], b = normalized[best], c = normalized[best + 1];
                double denominator = a - 2 * b + c;
                double shift = denominator > 0 ? Math.Clamp(0.5 * (a - c) / denominator, -0.5, 0.5) : 0;
                double f0 = rate / (best + shift);
                pitch[f] = normalized[best] < VoicingThreshold ? (float)(69 + 12 * Math.Log2(f0 / 440)) : float.NaN;
            }

            var active = ActiveFrames(loudness);
            for (int f = 0; f < frames; f++) {
                if (!active[f]) {
                    pitch[f] = float.NaN;
                }
            }
            RemoveShortRuns(pitch);
            FixOctaveJumps(pitch);
            Median3(pitch);
            return new VoiceAnalysis(pitch, Bridge(pitch), loudness, aperiodicity, active);
        }

        static double At(float[] audio, int i) => i >= 0 && i < audio.Length ? audio[i] : 0;

        static float[] ToMono(float[] samples, int channels) {
            if (channels == 1) {
                return samples;
            }
            var mono = new float[samples.Length / channels];
            for (int i = 0; i < mono.Length; i++) {
                float sum = 0;
                for (int c = 0; c < channels; c++) {
                    sum += samples[i * channels + c];
                }
                mono[i] = sum / channels;
            }
            return mono;
        }

        /// <summary>Keeps every factor-th sample after a windowed-sinc low-pass below the new Nyquist.</summary>
        internal static float[] Decimate(float[] samples, int factor) {
            if (factor <= 1) {
                return samples;
            }
            const int half = 31;
            double cutoff = 0.45 / factor;
            var taps = new double[2 * half + 1];
            double total = 0;
            for (int k = -half; k <= half; k++) {
                double sinc = k == 0 ? 2 * cutoff : Math.Sin(2 * Math.PI * cutoff * k) / (Math.PI * k);
                double hamming = 0.54 + 0.46 * Math.Cos(Math.PI * k / half);
                taps[k + half] = sinc * hamming;
                total += taps[k + half];
            }
            var result = new float[(samples.Length + factor - 1) / factor];
            for (int i = 0; i < result.Length; i++) {
                int center = i * factor;
                double sum = 0;
                for (int k = -half; k <= half; k++) {
                    int j = center + k;
                    if (j >= 0 && j < samples.Length) {
                        sum += samples[j] * taps[k + half];
                    }
                }
                result[i] = (float)(sum / total);
            }
            return result;
        }

        static bool[] ActiveFrames(float[] loudness) {
            var active = new bool[loudness.Length];
            if (loudness.Length == 0) {
                return active;
            }
            var sorted = loudness.OrderBy(x => x).ToArray();
            double loud = sorted[(int)Math.Floor((sorted.Length - 1) * 0.95)];
            double gate = Math.Max(loud - SilenceRangeDb, SilenceFloorDb);
            for (int i = 0; i < active.Length; i++) {
                active[i] = loudness[i] > gate;
            }
            return active;
        }

        static void RemoveShortRuns(float[] pitch) {
            foreach (var (start, end) in VoiceAnalysis.Runs(pitch.Length, i => float.IsFinite(pitch[i]))) {
                if (end - start < MinVoicedFrames) {
                    for (int i = start; i < end; i++) {
                        pitch[i] = float.NaN;
                    }
                }
            }
        }

        /// <summary>Folds frames an octave away from the voiced frames around them back to them.</summary>
        static void FixOctaveJumps(float[] pitch) {
            var original = (float[])pitch.Clone();
            var window = new List<float>();
            for (int i = 0; i < pitch.Length; i++) {
                if (!float.IsFinite(original[i])) {
                    continue;
                }
                window.Clear();
                for (int j = Math.Max(0, i - 10); j <= Math.Min(pitch.Length - 1, i + 10); j++) {
                    if (float.IsFinite(original[j])) {
                        window.Add(original[j]);
                    }
                }
                window.Sort();
                float median = window[window.Count / 2];
                float difference = original[i] - median;
                if (Math.Abs(difference) > 7 && Math.Abs(Math.Abs(difference) - 12) < 3) {
                    pitch[i] -= 12 * Math.Sign(difference);
                }
            }
        }

        static void Median3(float[] pitch) {
            var original = (float[])pitch.Clone();
            for (int i = 1; i + 1 < pitch.Length; i++) {
                float a = original[i - 1], b = original[i], c = original[i + 1];
                if (float.IsFinite(a) && float.IsFinite(b) && float.IsFinite(c)) {
                    pitch[i] = Math.Max(Math.Min(a, b), Math.Min(Math.Max(a, b), c));
                }
            }
        }

        static float[] Bridge(float[] pitch) {
            var bridged = (float[])pitch.Clone();
            var gaps = VoiceAnalysis.Runs(pitch.Length, i => !float.IsFinite(pitch[i]));
            foreach (var (start, end) in gaps) {
                if (start == 0 || end == pitch.Length || end - start > MaxBridgeFrames) {
                    continue;
                }
                float a = pitch[start - 1], b = pitch[end];
                for (int i = start; i < end; i++) {
                    bridged[i] = a + (b - a) * (i - start + 1) / (end - start + 1);
                }
            }
            return bridged;
        }
    }

    /// <summary>
    /// What characterizes a recorded voice, for fitting a <see cref="VoiceCharacter"/> to it. Jitter is the pitch
    /// movement faster than about 50 ms, within voiced stretches. Drift is the movement between about a quarter
    /// second and a second, across consonants: slower than syllables, faster than the phrase's declination.
    /// </summary>
    public sealed class VoiceProfile {
        /// <summary>The median, 5th and 95th percentile pitches, as MIDI note numbers.</summary>
        public float MedianPitch;
        public float LowPitch;
        public float HighPitch;
        /// <summary>RMS of the pitch's drift and jitter, in cents.</summary>
        public float DriftCents;
        public float JitterCents;
        /// <summary>RMS of the fast loudness changes in voiced stretches, in dB.</summary>
        public float ShimmerDb;
        /// <summary>The median aperiodicity of voiced frames.</summary>
        public float Aperiodicity;
        /// <summary>How far the pitch typically drifts down over a phrase, in cents.</summary>
        public float DeclinationCents;
        /// <summary>How the pitch typically moves over the end of a phrase, in cents: negative falls.</summary>
        public float FinalCents;
        public float SyllablesPerSecond;

        const int JitterRadius = 2;
        const int SyllableRadius = 12;
        const int PhraseRadius = 50;
        /// <summary>Silence this long, in frames, separates phrases.</summary>
        const int PhraseGapFrames = 25;
        const int EndingFrames = 20;

        /// <summary>The profile of an analysis, or null when it has less than a fifth of a second of voice.</summary>
        public static VoiceProfile? Of(VoiceAnalysis analysis) {
            var voiced = Enumerable.Range(0, analysis.FrameCount).Where(analysis.IsVoiced).ToArray();
            if (voiced.Length < 20) {
                return null;
            }
            var pitches = voiced.Select(i => analysis.Pitch[i]).OrderBy(p => p).ToArray();
            var profile = new VoiceProfile {
                MedianPitch = Percentile(pitches, 0.5),
                LowPitch = Percentile(pitches, 0.05),
                HighPitch = Percentile(pitches, 0.95),
                Aperiodicity = Percentile(voiced.Select(i => analysis.Aperiodicity[i]).OrderBy(a => a).ToArray(), 0.5),
            };

            double jitter = 0, drift = 0, shimmer = 0;
            int jitterCount = 0, driftCount = 0;
            foreach (var (start, end) in VoiceAnalysis.Runs(analysis.FrameCount, analysis.IsVoiced)) {
                var cents = Enumerable.Range(start, end - start).Select(i => analysis.Pitch[i] * 100.0).ToArray();
                var db = Enumerable.Range(start, end - start).Select(i => (double)analysis.LoudnessDb[i]).ToArray();
                for (int i = JitterRadius; i + JitterRadius < cents.Length; i++) {
                    jitter += Square(cents[i] - Mean(cents, i, JitterRadius));
                    shimmer += Square(db[i] - Mean(db, i, JitterRadius));
                    jitterCount++;
                }
            }
            foreach (var (start, end) in VoiceAnalysis.Runs(analysis.FrameCount, i => float.IsFinite(analysis.BridgedPitch[i]))) {
                var cents = Enumerable.Range(start, end - start).Select(i => analysis.BridgedPitch[i] * 100.0).ToArray();
                for (int i = PhraseRadius; i + PhraseRadius < cents.Length; i++) {
                    drift += Square(Mean(cents, i, SyllableRadius) - Mean(cents, i, PhraseRadius));
                    driftCount++;
                }
            }
            profile.JitterCents = jitterCount > 0 ? (float)Math.Sqrt(jitter / jitterCount) : 0;
            profile.ShimmerDb = jitterCount > 0 ? (float)Math.Sqrt(shimmer / jitterCount) : 0;
            profile.DriftCents = driftCount > 0 ? (float)Math.Sqrt(drift / driftCount) : 0;

            var declinations = new List<float>();
            var endings = new List<float>();
            foreach (var (start, end) in Phrases(analysis)) {
                var frames = Enumerable.Range(start, end - start).Where(analysis.IsVoiced).ToArray();
                if (frames.Length < 30) {
                    continue;
                }
                // The trend without the ending, which falls or rises on its own.
                var body = frames.Length > 3 * EndingFrames ? frames[..^EndingFrames] : frames;
                double slope = Slope(body, i => analysis.Pitch[i] * 100.0);
                declinations.Add((float)(-slope * (frames[^1] - frames[0])));
                var last = frames.Where(i => i > frames[^1] - EndingFrames).ToArray();
                var before = frames.Where(i => i <= frames[^1] - EndingFrames && i > frames[^1] - 3 * EndingFrames).ToArray();
                if (last.Length > 3 && before.Length > 3) {
                    endings.Add((float)(last.Average(i => analysis.Pitch[i]) - before.Average(i => analysis.Pitch[i])) * 100);
                }
            }
            profile.DeclinationCents = declinations.Count > 0 ? Percentile(declinations.OrderBy(x => x).ToArray(), 0.5) : 0;
            profile.FinalCents = endings.Count > 0 ? Percentile(endings.OrderBy(x => x).ToArray(), 0.5) : 0;

            var segments = SpeechSegmenter.Segment(analysis);
            double activeSeconds = segments.Sum(s => s.EndMs - s.StartMs) / 1000;
            profile.SyllablesPerSecond = activeSeconds > 0 ? (float)(segments.Count / activeSeconds) : 0;
            return profile;
        }

        /// <summary>Stretches of activity separated by at least <see cref="PhraseGapFrames"/> of silence.</summary>
        static List<(int start, int end)> Phrases(VoiceAnalysis analysis) {
            var phrases = new List<(int start, int end)>();
            foreach (var run in VoiceAnalysis.Runs(analysis.FrameCount, i => analysis.Active[i])) {
                if (phrases.Count > 0 && run.start - phrases[^1].end < PhraseGapFrames) {
                    phrases[^1] = (phrases[^1].start, run.end);
                } else {
                    phrases.Add(run);
                }
            }
            return phrases;
        }

        static double Square(double x) => x * x;

        static double Mean(double[] values, int center, int radius) {
            double sum = 0;
            for (int i = center - radius; i <= center + radius; i++) {
                sum += values[i];
            }
            return sum / (2 * radius + 1);
        }

        /// <summary>The least-squares slope of values per frame.</summary>
        static double Slope(int[] frames, Func<int, double> value) {
            double meanX = frames.Average(), meanY = frames.Average(value);
            double num = 0, den = 0;
            foreach (int f in frames) {
                num += (f - meanX) * (value(f) - meanY);
                den += (f - meanX) * (f - meanX);
            }
            return den > 0 ? num / den : 0;
        }

        static float Percentile(float[] sorted, double p) =>
            sorted.Length == 0 ? 0 : sorted[(int)Math.Round((sorted.Length - 1) * p)];
    }
}
