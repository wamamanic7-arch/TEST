using System;
using System.Collections.Generic;
using System.Linq;

namespace OpenUtau.Core.VoiceActing {
    /// <summary>A syllable-sized stretch of a recording; its pitch is a MIDI note number, NaN if it has no voice.</summary>
    public readonly struct SpeechSegment {
        public readonly double StartMs;
        public readonly double EndMs;
        public readonly float Pitch;

        public SpeechSegment(double startMs, double endMs, float pitch) {
            StartMs = startMs;
            EndMs = endMs;
            Pitch = pitch;
        }

        public double DurationMs => EndMs - StartMs;
    }

    /// <summary>
    /// Splits speech into syllables: stretches of sound, cut where the loudness dips between two voiced peaks,
    /// as syllable nuclei are found in de Jong and Wempe (2009).
    /// </summary>
    public static class SpeechSegmenter {
        /// <summary>Silences shorter than this, in frames, such as stop closures, stay inside a stretch.</summary>
        const int MaxClosureFrames = 8;
        const int MinStretchFrames = 6;
        const int MinSyllableFrames = 8;
        /// <summary>Peaks quieter than the loudest of their stretch by this much are ignored.</summary>
        const double PeakRangeDb = 25;

        public static List<SpeechSegment> Segment(VoiceAnalysis analysis, double dipDb = 3) {
            var segments = new List<SpeechSegment>();
            int count = analysis.FrameCount;
            var smooth = new double[count];
            for (int i = 0; i < count; i++) {
                int lo = Math.Max(0, i - 2), hi = Math.Min(count - 1, i + 2);
                double sum = 0;
                for (int j = lo; j <= hi; j++) {
                    sum += analysis.LoudnessDb[j];
                }
                smooth[i] = sum / (hi - lo + 1);
            }

            var stretches = new List<(int start, int end)>();
            foreach (var run in VoiceAnalysis.Runs(count, i => analysis.Active[i])) {
                if (stretches.Count > 0 && run.start - stretches[^1].end <= MaxClosureFrames) {
                    stretches[^1] = (stretches[^1].start, run.end);
                } else {
                    stretches.Add(run);
                }
            }

            foreach (var (start, end) in stretches.Where(s => s.end - s.start >= MinStretchFrames)) {
                double loudest = Enumerable.Range(start, end - start).Max(i => smooth[i]);
                var peaks = new List<int>();
                for (int i = start; i < end; i++) {
                    bool rising = i == start || smooth[i] >= smooth[i - 1];
                    bool falling = i == end - 1 || smooth[i] > smooth[i + 1];
                    if (rising && falling && analysis.IsVoiced(i) && smooth[i] >= loudest - PeakRangeDb) {
                        peaks.Add(i);
                    }
                }
                var cuts = new List<int>();
                int last = start;
                for (int k = 1, current = peaks.Count > 0 ? peaks[0] : -1; k < peaks.Count; k++) {
                    int next = peaks[k];
                    int valley = current + 1;
                    for (int i = current + 1; i < next; i++) {
                        if (smooth[i] < smooth[valley]) {
                            valley = i;
                        }
                    }
                    if (Math.Min(smooth[current], smooth[next]) - smooth[valley] >= dipDb
                            && valley - last >= MinSyllableFrames && end - valley >= MinSyllableFrames) {
                        cuts.Add(valley);
                        last = valley;
                        current = next;
                    } else if (smooth[next] > smooth[current]) {
                        current = next;
                    }
                }
                int from = start;
                foreach (int to in cuts.Append(end)) {
                    var voiced = Enumerable.Range(from, to - from).Where(analysis.IsVoiced)
                        .Select(i => analysis.Pitch[i]).OrderBy(p => p).ToArray();
                    float pitch = voiced.Length > 0 ? voiced[voiced.Length / 2] : float.NaN;
                    segments.Add(new SpeechSegment(from * VoiceAnalysis.HopMs, to * VoiceAnalysis.HopMs, pitch));
                    from = to;
                }
            }
            return segments;
        }

        /// <summary>
        /// Exactly <paramref name="count"/> segments from these: the closest neighbours merged, or the longest
        /// split in half, until the count matches. For lining up notes already written with a recording.
        /// </summary>
        public static List<SpeechSegment> Fit(IReadOnlyList<SpeechSegment> segments, int count) {
            var result = segments.ToList();
            if (count <= 0 || result.Count == 0) {
                return new List<SpeechSegment>();
            }
            while (result.Count > count) {
                int best = 0;
                double bestGap = double.MaxValue, bestLength = double.MaxValue;
                for (int i = 0; i + 1 < result.Count; i++) {
                    double gap = result[i + 1].StartMs - result[i].EndMs;
                    double length = result[i + 1].EndMs - result[i].StartMs;
                    if (gap < bestGap || gap == bestGap && length < bestLength) {
                        (best, bestGap, bestLength) = (i, gap, length);
                    }
                }
                var a = result[best];
                var b = result[best + 1];
                float pitch = float.IsFinite(a.Pitch) && float.IsFinite(b.Pitch)
                    ? (float)((a.Pitch * a.DurationMs + b.Pitch * b.DurationMs) / (a.DurationMs + b.DurationMs))
                    : float.IsFinite(a.Pitch) ? a.Pitch : b.Pitch;
                result[best] = new SpeechSegment(a.StartMs, b.EndMs, pitch);
                result.RemoveAt(best + 1);
            }
            while (result.Count < count) {
                int longest = 0;
                for (int i = 1; i < result.Count; i++) {
                    if (result[i].DurationMs > result[longest].DurationMs) {
                        longest = i;
                    }
                }
                var s = result[longest];
                double middle = (s.StartMs + s.EndMs) / 2;
                result[longest] = new SpeechSegment(s.StartMs, middle, s.Pitch);
                result.Insert(longest + 1, new SpeechSegment(middle, s.EndMs, s.Pitch));
            }
            return result;
        }
    }
}
