using System;
using System.Collections.Generic;
using System.Linq;
using OpenUtau.Core.Ustx;

namespace OpenUtau.Core.VoiceActing {
    /// <summary>
    /// A take of a line, recorded or imported into a wave part, analyzed where it lies under a voice part.
    /// </summary>
    public sealed class ReferenceTake {
        public readonly UWavePart Wave;
        public readonly VoiceAnalysis Analysis;
        /// <summary>Project time, in ms, of the analysis's first frame.</summary>
        public readonly double StartMs;

        public ReferenceTake(UWavePart wave, VoiceAnalysis analysis, double startMs) {
            Wave = wave;
            Analysis = analysis;
            StartMs = startMs;
        }

        public double EndMs => StartMs + Analysis.DurationMs;

        /// <summary>
        /// The recording for a voice part: the loaded wave part on another track that overlaps it the longest.
        /// </summary>
        public static UWavePart? Find(UProject project, UVoicePart part) {
            double partStart = project.timeAxis.TickPosToMsPos(part.position);
            double partEnd = project.timeAxis.TickPosToMsPos(part.End);
            UWavePart? best = null;
            double bestOverlap = 0;
            foreach (var wave in project.parts.OfType<UWavePart>()) {
                if (wave.trackNo == part.trackNo || wave.Samples == null || wave.sampleRate <= 0) {
                    continue;
                }
                double overlap = Math.Min(partEnd, project.timeAxis.TickPosToMsPos(wave.End))
                    - Math.Max(partStart, project.timeAxis.TickPosToMsPos(wave.position));
                if (overlap > bestOverlap) {
                    best = wave;
                    bestOverlap = overlap;
                }
            }
            return best;
        }

        /// <summary>
        /// Analyzes the audible part of a wave part from the voice part's start: up to the voice part's end, or to
        /// the recording's end when <paramref name="toRecordingEnd"/>. Null if nothing is left.
        /// </summary>
        public static ReferenceTake? Analyze(UProject project, UVoicePart part, UWavePart wave, bool toRecordingEnd = false) {
            var samples = wave.Samples;
            if (samples == null || wave.sampleRate <= 0) {
                return null;
            }
            var axis = project.timeAxis;
            int channels = Math.Clamp(wave.channels, 1, 2);
            double fileStartMs = axis.TickPosToMsPos(wave.position) - wave.GetSkipMs(project);
            double startMs = Math.Max(axis.TickPosToMsPos(wave.position), axis.TickPosToMsPos(part.position));
            double endMs = axis.TickPosToMsPos(wave.End);
            if (!toRecordingEnd) {
                endMs = Math.Min(endMs, axis.TickPosToMsPos(part.End));
            }
            long first = Math.Max(0, (long)Math.Round((startMs - fileStartMs) * wave.sampleRate / 1000));
            long last = Math.Min(samples.Length / channels, (long)Math.Round((endMs - fileStartMs) * wave.sampleRate / 1000));
            if (last - first < wave.sampleRate / 10) {
                return null;
            }
            var slice = new float[(last - first) * channels];
            Array.Copy(samples, first * channels, slice, 0, slice.Length);
            var analysis = VoiceAnalyzer.Analyze(slice, wave.sampleRate, channels);
            return new ReferenceTake(wave, analysis, fileStartMs + first * 1000.0 / wave.sampleRate);
        }
    }

    public sealed class TransferOptions {
        public bool Pitch = true;
        public bool Dynamics = true;
        /// <summary>How much of the recording's loudness changes reach DYN.</summary>
        public float DynamicsAmount = 0.6f;
        /// <summary>
        /// Moves the recording's pitch to where the notes are, so a line acted in one register is spoken by the
        /// character in its own. Otherwise the pitch is kept as recorded.
        /// </summary>
        public bool FollowNotes = true;
    }

    /// <summary>Turns a take into notes and curves: the actor's timing, melody and loudness for the character.</summary>
    public static class PerformanceTransfer {
        const int MinNoteTicks = 15;

        static int TickAt(UProject project, UVoicePart part, double ms) =>
            project.timeAxis.MsPosToTickPos(ms) - part.position;

        static double MsAt(UProject project, UVoicePart part, int tick) =>
            project.timeAxis.TickPosToMsPos(part.position + tick);

        /// <summary>One note per syllable of the take, at its median pitch, with the default lyric.</summary>
        public static List<UNote> CreateNotes(UProject project, UVoicePart part, ReferenceTake take) {
            var segments = SpeechSegmenter.Segment(take.Analysis);
            var voiced = segments.Where(s => float.IsFinite(s.Pitch)).Select(s => s.Pitch).OrderBy(p => p).ToArray();
            float fallback = voiced.Length > 0 ? voiced[voiced.Length / 2] : 60;
            var notes = new List<UNote>();
            int previousEnd = int.MinValue;
            foreach (var segment in segments) {
                int position = Math.Max(TickAt(project, part, take.StartMs + segment.StartMs), previousEnd);
                int end = TickAt(project, part, take.StartMs + segment.EndMs);
                if (end - position < MinNoteTicks) {
                    continue;
                }
                float pitch = float.IsFinite(segment.Pitch) ? segment.Pitch : fallback;
                fallback = pitch;
                notes.Add(project.CreateNote((int)Math.Round(pitch), position, end - position));
                previousEnd = end;
            }
            return notes;
        }

        /// <summary>
        /// Where each of the notes, in order, goes to line up with the take's syllables: (position, duration).
        /// Syllables are merged or split until there is one per note.
        /// </summary>
        public static List<(int position, int duration)> AlignNotes(UProject project, UVoicePart part,
                IReadOnlyList<UNote> notes, ReferenceTake take) {
            var segments = SpeechSegmenter.Fit(SpeechSegmenter.Segment(take.Analysis), notes.Count);
            var result = new List<(int, int)>();
            int previousEnd = int.MinValue;
            foreach (var segment in segments) {
                int position = Math.Max(TickAt(project, part, take.StartMs + segment.StartMs), previousEnd);
                int end = Math.Max(position + MinNoteTicks, TickAt(project, part, take.StartMs + segment.EndMs));
                result.Add((position, end - position));
                previousEnd = end;
            }
            return result;
        }

        /// <summary>
        /// Commands writing the take's pitch and loudness over the span it covers: pitch into the pitch override
        /// (PITO) when <paramref name="pitchOverride"/>, else into PITD against the pitch the notes have; loudness
        /// into DYN relative to the take's typical voiced loudness.
        /// </summary>
        public static List<UCommand> FollowPitchAndDynamics(UProject project, UVoicePart part, ReferenceTake take,
                bool pitchOverride, TransferOptions? options = null) {
            options ??= new TransferOptions();
            var analysis = take.Analysis;
            var commands = new List<UCommand>();
            int from = Math.Max(0, UCurve.interval * (int)Math.Ceiling(TickAt(project, part, take.StartMs) / (double)UCurve.interval));
            int to = Math.Min(part.Duration, TickAt(project, part, take.EndMs));
            if (to <= from) {
                return commands;
            }
            var xs = new List<int>();
            for (int x = from; x <= to; x += UCurve.interval) {
                xs.Add(x);
            }
            var pitches = xs.Select(x => analysis.Sample(analysis.BridgedPitch, MsAt(project, part, x) - take.StartMs)).ToArray();

            if (options.Pitch && pitches.Any(float.IsFinite)) {
                double shift = options.FollowNotes ? NoteShift(part, xs, pitches) : 0;
                if (pitchOverride) {
                    var curve = part.maskedCurves.FirstOrDefault(c => c.abbr == Format.Ustx.PITO)?.Clone()
                        ?? new UMaskedCurve(Format.Ustx.PITO);
                    curve.Clear(from, to);
                    curve.SetValues(xs.Select((x, i) => (x, y: (float)(pitches[i] * 100 + shift)))
                        .Where(v => float.IsFinite(v.y)));
                    commands.Add(new ReplaceMaskedCurveCommand(part, Format.Ustx.PITO, curve));
                } else {
                    var ys = xs.Select((x, i) => float.IsFinite(pitches[i])
                        ? (int)Math.Round(pitches[i] * 100 + shift - BasePitch(part, x)) : 0);
                    commands.Add(new PasteCurveCommand(project, part, Format.Ustx.PITD, xs, ys));
                }
            }

            if (options.Dynamics) {
                var voicedDb = Enumerable.Range(0, analysis.FrameCount).Where(analysis.IsVoiced)
                    .Select(i => analysis.LoudnessDb[i]).OrderBy(d => d).ToArray();
                if (voicedDb.Length > 0) {
                    float reference = voicedDb[voicedDb.Length / 2];
                    var ys = xs.Select(x => {
                        float db = analysis.Sample(analysis.LoudnessDb, MsAt(project, part, x) - take.StartMs);
                        // DYN is in tenths of a dB.
                        return (int)Math.Round(Math.Clamp((db - reference) * 10 * options.DynamicsAmount, -200, 100));
                    });
                    commands.Add(new PasteCurveCommand(project, part, Format.Ustx.DYN, xs, ys));
                }
            }
            return commands;
        }

        /// <summary>
        /// Cents from the take's pitch to the notes' pitch, by their medians where the take is voiced over a note.
        /// </summary>
        static double NoteShift(UVoicePart part, List<int> xs, float[] pitches) {
            var notes = new List<double>();
            var take = new List<double>();
            for (int i = 0; i < xs.Count; i++) {
                if (!float.IsFinite(pitches[i])) {
                    continue;
                }
                var note = part.notes.FirstOrDefault(n => n.position <= xs[i] && xs[i] < n.End);
                if (note != null) {
                    notes.Add(note.tone * 100);
                    take.Add(pitches[i] * 100);
                }
            }
            if (notes.Count == 0) {
                return 0;
            }
            notes.Sort();
            take.Sort();
            return notes[notes.Count / 2] - take[take.Count / 2];
        }

        /// <summary>The pitch before PITD at a part tick: from the rendered phrase if there is one, else the note's.</summary>
        static float BasePitch(UVoicePart part, int x) {
            foreach (var phrase in part.renderPhrases) {
                int start = phrase.position - part.position - phrase.leading;
                if (x >= start && x < phrase.position - part.position + phrase.duration
                        && phrase.pitchesBeforeDeviation.Length > 0) {
                    int index = Math.Clamp((x - start) / UCurve.interval, 0, phrase.pitchesBeforeDeviation.Length - 1);
                    return phrase.pitchesBeforeDeviation[index];
                }
            }
            var note = part.notes.FirstOrDefault(n => n.position <= x && x < n.End)
                ?? part.notes.OrderBy(n => Math.Min(Math.Abs(n.position - x), Math.Abs(n.End - x))).FirstOrDefault();
            return (note?.tone ?? 60) * 100;
        }
    }
}
