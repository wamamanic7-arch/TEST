using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using NAudio.Wave;
using OpenUtau.Core.ExpressionGraph;
using OpenUtau.Core.Ustx;
using Xunit;

namespace OpenUtau.Core.VoiceActing {
    public class VoiceAnalysisTest {
        const int Rate = 44100;

        /// <summary>A buzzy tone of five harmonics, its pitch and loudness given over time in seconds.</summary>
        static float[] Tone(double seconds, Func<double, double> midi, Func<double, double> amplitude,
                double noise = 0, int seed = 1) {
            var random = new Random(seed);
            var samples = new float[(int)(seconds * Rate)];
            double phase = 0;
            for (int i = 0; i < samples.Length; i++) {
                double t = (double)i / Rate;
                phase += 440 * Math.Pow(2, (midi(t) - 69) / 12) / Rate;
                double y = 0;
                for (int k = 1; k <= 5; k++) {
                    y += Math.Sin(2 * Math.PI * k * phase) / k;
                }
                y = y * 0.3 + noise * (random.NextDouble() * 2 - 1);
                samples[i] = (float)(y * amplitude(t));
            }
            return samples;
        }

        static double On(double t, double from, double to, double fade = 0.02) =>
            t < from || t > to ? 0 : Math.Min(1, Math.Min(t - from, to - t) / fade);

        [Fact]
        public void FindsThePitchOfAGlideAndTheSilenceAroundIt() {
            Func<double, double> midi = t => 55 + 4 * (t - 0.2);
            var analysis = VoiceAnalyzer.Analyze(Tone(1.4, midi, t => On(t, 0.2, 1.2)), Rate);

            Assert.Equal(141, analysis.FrameCount);
            for (int f = 25; f <= 115; f++) {
                Assert.True(analysis.IsVoiced(f), $"frame {f}");
                Assert.InRange(analysis.Pitch[f] - midi(f / 100.0), -0.15, 0.15);
            }
            Assert.All(Enumerable.Range(0, 15).Concat(Enumerable.Range(126, 15)), f => {
                Assert.False(analysis.IsVoiced(f));
                Assert.False(analysis.Active[f]);
            });
        }

        [Theory]
        [InlineData(44100)]
        [InlineData(48000)]
        [InlineData(16000)]
        public void WorksAtCommonSampleRates(int rate) {
            var samples = new float[rate];
            for (int i = 0; i < samples.Length; i++) {
                samples[i] = (float)(0.3 * Math.Sin(2 * Math.PI * 300 * i / rate) + 0.1 * Math.Sin(2 * Math.PI * 600 * i / rate));
            }
            var analysis = VoiceAnalyzer.Analyze(samples, rate);
            double expected = 69 + 12 * Math.Log2(300 / 440.0);
            Assert.All(Enumerable.Range(10, 80), f => Assert.InRange(analysis.Pitch[f], expected - 0.1, expected + 0.1));
        }

        [Fact]
        public void StereoIsAnalyzedAsItsMix() {
            var mono = Tone(0.5, _ => 60, _ => 1);
            var stereo = mono.SelectMany(x => new[] { x, x }).ToArray();
            Assert.Equal(VoiceAnalyzer.Analyze(mono, Rate).Pitch, VoiceAnalyzer.Analyze(stereo, Rate, 2).Pitch);
        }

        [Fact]
        public void DecimationRemovesWhatTheLowerRateCannotHold() {
            float[] Sine(double hz) => Enumerable.Range(0, Rate / 2).Select(i => (float)Math.Sin(2 * Math.PI * hz * i / Rate)).ToArray();
            double Rms(float[] x) => Math.Sqrt(x.Skip(100).Take(x.Length - 200).Average(v => v * v));
            Assert.InRange(Rms(VoiceAnalyzer.Decimate(Sine(1000), 2)), 0.69, 0.72);
            Assert.InRange(Rms(VoiceAnalyzer.Decimate(Sine(14000), 2)), 0, 0.02);
        }

        [Fact]
        public void BridgesAConsonantBetweenVowelsButNotASilence() {
            var vowels = Tone(0.25, _ => 60, _ => 1).Concat(Noise(0.06)).Concat(Tone(0.25, _ => 64, _ => 1))
                .Concat(new float[Rate / 2]).Concat(Tone(0.25, _ => 64, _ => 1)).ToArray();
            var analysis = VoiceAnalyzer.Analyze(vowels, Rate);
            var consonant = Enumerable.Range(20, 15).Where(f => !analysis.IsVoiced(f)).ToArray();
            Assert.NotEmpty(consonant);
            Assert.All(consonant, f => Assert.InRange(analysis.BridgedPitch[f], 59.5, 64.5));
            Assert.All(Enumerable.Range(65, 40), f => Assert.True(float.IsNaN(analysis.BridgedPitch[f])));
        }

        static float[] Noise(double seconds, double level = 0.3, int seed = 3) {
            var random = new Random(seed);
            return Enumerable.Range(0, (int)(seconds * Rate)).Select(_ => (float)(level * (random.NextDouble() * 2 - 1))).ToArray();
        }

        /// <summary>Two phrases, each falling from 57 to 53 over 1.5 s, separated by half a second of silence.</summary>
        static float[] TwoFallingPhrases(double noise = 0) {
            Func<double, double> midi = t => {
                double p = t < 1.6 ? t - 0.1 : t - 2.1;
                return 57 - 4 * p / 1.5;
            };
            return Tone(3.7, midi, t => On(t, 0.1, 1.6) + On(t, 2.1, 3.6), noise);
        }

        [Fact]
        public void ProfilesTheMelodyOfPhrases() {
            var profile = VoiceProfile.Of(VoiceAnalyzer.Analyze(TwoFallingPhrases(), Rate))!;
            Assert.NotNull(profile);
            Assert.InRange(profile.MedianPitch, 54.5, 55.5);
            Assert.InRange(profile.LowPitch, 53, 53.6);
            Assert.InRange(profile.HighPitch, 56.4, 57);
            Assert.InRange(profile.DeclinationCents, 340, 420);
            // The last 0.2 s against the 0.4 s before: 0.3 s apart on a fall of 267 cents a second.
            Assert.InRange(profile.FinalCents, -100, -60);
            Assert.InRange(profile.JitterCents, 0, 3);
            Assert.InRange(profile.DriftCents, 0, 3);
        }

        [Fact]
        public void NoiseMakesTheVoiceBreathier() {
            var clean = VoiceProfile.Of(VoiceAnalyzer.Analyze(TwoFallingPhrases(), Rate))!;
            var breathy = VoiceProfile.Of(VoiceAnalyzer.Analyze(TwoFallingPhrases(noise: 0.2), Rate))!;
            Assert.True(breathy.Aperiodicity > clean.Aperiodicity + 0.05);
            Assert.True(VoiceCharacter.Preset("neutral").FittedTo(breathy).breathiness
                > VoiceCharacter.Preset("neutral").FittedTo(clean).breathiness + 15);
        }

        /// <summary>A voice whose pitch wanders as a character's fluctuation nodes make it wander.</summary>
        static VoiceAnalysis Wandering(float driftDepth, float jitterDepth, float shimmerDepth, double seconds = 30) {
            var character = new VoiceCharacter();
            int frames = (int)(seconds * 100);
            var pitch = new float[frames];
            var loudness = new float[frames];
            for (int f = 0; f < frames; f++) {
                double t = f / 100.0;
                pitch[f] = (float)(60 + (driftDepth * GraphNodeTypes.FractalNoise(t * character.driftRate, 2, 0)
                    + jitterDepth * GraphNodeTypes.FractalNoise(t * character.jitterRate, 2, 1)) / 100);
                // DYN is in tenths of a dB; the shimmer node runs at 12 Hz.
                loudness[f] = (float)(-20 + shimmerDepth / 10 * GraphNodeTypes.FractalNoise(t * 12, 2, 2));
            }
            return new VoiceAnalysis(pitch, pitch, loudness, new float[frames], Enumerable.Repeat(true, frames).ToArray());
        }

        [Theory]
        [InlineData(25, 6, 15)]
        [InlineData(60, 15, 30)]
        [InlineData(10, 2, 5)]
        public void FittingRecoversTheUnsteadinessACharacterMakes(float drift, float jitter, float shimmer) {
            var fitted = new VoiceCharacter().FittedTo(VoiceProfile.Of(Wandering(drift, jitter, shimmer))!);
            Assert.InRange(fitted.driftDepth, drift * 0.75, drift * 1.25);
            Assert.InRange(fitted.jitterDepth, jitter * 0.75, jitter * 1.25);
            Assert.InRange(fitted.shimmerDepth, shimmer * 0.75, shimmer * 1.25);
        }

        /// <summary>Loudness of syllables in dB, voiced at the given pitches, with dips between them.</summary>
        static VoiceAnalysis Syllables(params (int frames, float pitch, float db)[] parts) {
            var pitch = new List<float>();
            var loudness = new List<float>();
            foreach (var (frames, p, db) in parts) {
                for (int i = 0; i < frames; i++) {
                    // A rounded peak in the middle of each syllable.
                    double shape = Math.Sin(Math.PI * (i + 0.5) / frames);
                    loudness.Add(db < -50 ? db : (float)(db - 12 * (1 - shape)));
                    pitch.Add(db < -50 ? float.NaN : p);
                }
            }
            int count = pitch.Count;
            var active = loudness.Select(d => d > -45).ToArray();
            return new VoiceAnalysis(pitch.ToArray(), pitch.ToArray(), loudness.ToArray(), new float[count], active);
        }

        [Fact]
        public void SplitsSpeechIntoSyllablesAtLoudnessDips() {
            var analysis = Syllables((20, float.NaN, -80), (20, 60, -10), (25, 62, -10), (20, 64, -12),
                (40, float.NaN, -80), (30, 65, -10), (20, float.NaN, -80));
            var segments = SpeechSegmenter.Segment(analysis);
            Assert.Equal(new float[] { 60, 62, 64, 65 }, segments.Select(s => s.Pitch));
            Assert.InRange(segments[0].StartMs, 190, 210);
            Assert.InRange(segments[1].StartMs, 380, 420);
            Assert.InRange(segments[3].StartMs, 1240, 1260);
            Assert.InRange(segments[3].EndMs, 1540, 1560);
        }

        [Fact]
        public void FitsSegmentsToANoteCount() {
            var segments = new List<SpeechSegment> {
                new SpeechSegment(0, 100, 60), new SpeechSegment(110, 300, 62),
                new SpeechSegment(500, 600, 64), new SpeechSegment(605, 700, 66),
            };
            var three = SpeechSegmenter.Fit(segments, 3);
            Assert.Equal(new[] { (0.0, 100.0), (110.0, 300.0), (500.0, 700.0) }, three.Select(s => (s.StartMs, s.EndMs)));
            // Weighted by length: 100 ms of 64 and 95 ms of 66.
            Assert.InRange(three[2].Pitch, (64 * 100 + 66 * 95) / 195f - 0.001, (64 * 100 + 66 * 95) / 195f + 0.001);
            var five = SpeechSegmenter.Fit(segments, 5);
            Assert.Equal(new[] { 0.0, 110, 205, 500, 605 }, five.Select(s => s.StartMs));
            Assert.Empty(SpeechSegmenter.Fit(segments, 0));
        }

        /// <summary>A project with an empty voice part on track 0 and a take of three syllables on track 1.</summary>
        static async Task<(UProject project, UVoicePart part, string path)> TakeFixture() {
            var project = new UProject();
            Format.Ustx.AddDefaultExpressions(project);
            project.tracks.Add(new UTrack(project));
            var part = new UVoicePart { trackNo = 0, position = 480, Duration = 480 * 8 };
            project.parts.Add(part);

            // From 0.4 s into the take (0.9 s into the project): 57, 60 and 62, 0.25 s each with short dips.
            double[] starts = { 0.4, 0.7, 1.0 };
            double[] pitches = { 57, 60, 62 };
            Func<double, double> midi = t => pitches[Math.Clamp((int)((t - 0.4) / 0.3), 0, 2)];
            Func<double, double> amplitude = t => starts.Sum(s => On(t, s, s + 0.25, 0.04));
            var path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.wav");
            using (var writer = new WaveFileWriter(path, WaveFormat.CreateIeeeFloatWaveFormat(Rate, 1))) {
                var samples = Tone(1.6, midi, amplitude);
                writer.WriteSamples(samples, 0, samples.Length);
            }
            // The take and the part both start 0.5 s into the project.
            var wave = new UWavePart { trackNo = 1, position = 480, FilePath = path };
            project.parts.Add(wave);
            wave.Load(project);
            await wave.Peaks;
            return (project, part, path);
        }

        [Fact]
        public async Task TurnsATakeIntoNotesAndCurves() {
            var (project, part, path) = await TakeFixture();
            try {
                var wave = ReferenceTake.Find(project, part);
                Assert.NotNull(wave);
                var take = ReferenceTake.Analyze(project, part, wave!, toRecordingEnd: true)!;
                Assert.Equal(500, take.StartMs, 3);

                var notes = PerformanceTransfer.CreateNotes(project, part, take);
                Assert.Equal(new[] { 57, 60, 62 }, notes.Select(n => n.tone));
                // 0.9, 1.2 and 1.5 s into the project, 0.5 s after the part starts: 384, 672 and 960 ticks in.
                var expected = new[] { 384, 672, 960 };
                for (int i = 0; i < notes.Count; i++) {
                    Assert.InRange(notes[i].position, expected[i] - 30, expected[i] + 30);
                    Assert.InRange(notes[i].duration, 200, 290);
                }
                foreach (var note in notes) {
                    part.notes.Add(note);
                }

                var commands = PerformanceTransfer.FollowPitchAndDynamics(project, part, take, pitchOverride: true);
                Assert.Equal(2, commands.Count);
                commands.ForEach(c => c.Execute());
                var pito = part.maskedCurves.Single(c => c.abbr == Format.Ustx.PITO);
                for (int i = 0; i < notes.Count; i++) {
                    int middle = notes[i].position + notes[i].duration / 2;
                    Assert.True(pito.TrySample(middle, out float cents));
                    Assert.InRange(cents, notes[i].tone * 100 - 20, notes[i].tone * 100 + 20);
                }
                var dyn = part.curves.Single(c => c.abbr == Format.Ustx.DYN);
                Assert.True(dyn.Sample(notes[0].position + notes[0].duration / 2) > dyn.Sample(notes[0].End + 20));

                // Without a pitch override, PITD carries what the notes don't.
                PerformanceTransfer.FollowPitchAndDynamics(project, part, take, pitchOverride: false,
                    new TransferOptions { Dynamics = false }).ForEach(c => c.Execute());
                var pitd = part.curves.Single(c => c.abbr == Format.Ustx.PITD);
                Assert.All(notes, n => Assert.InRange(pitd.Sample(n.position + n.duration / 2), -20, 20));
            } finally {
                File.Delete(path);
            }
        }

        [Fact]
        public async Task MovesTheCharactersPitchToItsNotes() {
            var (project, part, path) = await TakeFixture();
            try {
                var take = ReferenceTake.Analyze(project, part, ReferenceTake.Find(project, part)!, toRecordingEnd: true)!;
                // The same line written an octave up, as for a younger character.
                foreach (var note in PerformanceTransfer.CreateNotes(project, part, take)) {
                    note.tone += 12;
                    part.notes.Add(note);
                }
                PerformanceTransfer.FollowPitchAndDynamics(project, part, take, pitchOverride: true)
                    .ForEach(c => c.Execute());
                var pito = part.maskedCurves.Single(c => c.abbr == Format.Ustx.PITO);
                var first = part.notes.First();
                Assert.True(pito.TrySample(first.position + first.duration / 2, out float cents));
                Assert.InRange(cents, 6900 - 20, 6900 + 20);
            } finally {
                File.Delete(path);
            }
        }

        [Fact]
        public async Task AlignsWrittenNotesToTheTake() {
            var (project, part, path) = await TakeFixture();
            try {
                var take = ReferenceTake.Analyze(project, part, ReferenceTake.Find(project, part)!)!;
                var written = new[] { 0, 240, 480, 720 }.Select(p => project.CreateNote(60, p, 240)).ToList();
                var targets = PerformanceTransfer.AlignNotes(project, part, written, take);
                Assert.Equal(4, targets.Count);
                Assert.InRange(targets[0].position, 384 - 30, 384 + 30);
                Assert.All(targets.Zip(targets.Skip(1)), pair =>
                    Assert.True(pair.First.position + pair.First.duration <= pair.Second.position));
            } finally {
                File.Delete(path);
            }
        }
    }
}
