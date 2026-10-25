using System;
using System.Linq;
using OpenUtau.Core.SignalChain.Effects;
using OpenUtau.Core.Ustx;
using Xunit;

namespace OpenUtau.Core.SignalChain {
    public class Ps1FilterTest {
        const int Rate = 44100;

        static float[] Stereo(double seconds, Func<double, double> signal) {
            var buffer = new float[(int)(seconds * Rate) * 2];
            for (int i = 0; i < buffer.Length / 2; i++) {
                buffer[2 * i] = buffer[2 * i + 1] = (float)signal((double)i / Rate);
            }
            return buffer;
        }

        static Func<double, double> Sine(double hz, double amplitude = 0.5) => t => amplitude * Math.Sin(2 * Math.PI * hz * t);

        static double Rms(float[] buffer, int fromFrame = 0) {
            double sum = 0;
            int n = 0;
            for (int i = fromFrame * 2; i < buffer.Length; i++, n++) {
                sum += buffer[i] * (double)buffer[i];
            }
            return Math.Sqrt(sum / n);
        }

        static Ps1Filter Filter(UVoiceFilter settings) {
            var filter = new Ps1Filter();
            filter.Configure(settings.SampleRate, settings.Adpcm, settings.ReverbType, settings.ReverbAmount,
                settings.TvSpeaker, settings.Mix, settings.VolumeDb);
            return filter;
        }

        static UVoiceFilter Dry(string preset) {
            var settings = UVoiceFilter.FromPreset(preset);
            settings.Reverb = UVoiceFilter.Off;
            return settings;
        }

        [Fact]
        public void AdpcmDecodesAsTheSoundChipWould() {
            var input = Enumerable.Range(0, Ps1Adpcm.BlockSize * 20)
                .Select(i => (short)(12000 * Math.Sin(2 * Math.PI * 300 * i / 11025.0) + 3000 * Math.Sin(i * 0.9))).ToArray();
            int p1 = 0, p2 = 0, d1 = 0, d2 = 0;
            var nibbles = new sbyte[Ps1Adpcm.BlockSize];
            var decoded = new short[Ps1Adpcm.BlockSize];
            var again = new short[Ps1Adpcm.BlockSize];
            double signal = 0, noise = 0;
            var filters = new int[5];
            for (int b = 0; b < 20; b++) {
                var block = input.AsSpan(b * Ps1Adpcm.BlockSize, Ps1Adpcm.BlockSize);
                var (filter, shift) = Ps1Adpcm.EncodeBlock(block, ref p1, ref p2, nibbles, decoded);
                filters[filter]++;
                Assert.InRange(shift, 0, 12);
                Assert.All(nibbles, n => Assert.InRange(n, -8, 7));
                // The chip's decoder, from its own state, gets exactly what the encoder said it would.
                Ps1Adpcm.DecodeBlock(nibbles, filter, shift, ref d1, ref d2, again);
                Assert.Equal(decoded, again);
                for (int i = 0; i < Ps1Adpcm.BlockSize; i++) {
                    signal += block[i] * (double)block[i];
                    noise += (block[i] - decoded[i]) * (double)(block[i] - decoded[i]);
                }
            }
            Assert.Equal((p1, p2), (d1, d2));
            // Close, but not lossless: 4 bits a sample.
            double snr = 10 * Math.Log10(signal / noise);
            Assert.InRange(snr, 15, 60);
            // A smooth tone is predicted, not stored raw.
            Assert.True(filters[0] < 20);
        }

        [Fact]
        public void SilenceStaysSilent() {
            var buffer = new float[4410 * 2];
            Filter(UVoiceFilter.FromPreset("echo")).Process(buffer, 0, buffer.Length);
            Assert.All(buffer, x => Assert.Equal(0, x));
        }

        [Theory]
        [InlineData("clean")]
        [InlineData("classic")]
        [InlineData("crunchy")]
        public void KeepsAVoicesBodyAndLosesWhatTheRateCannotHold(string preset) {
            var settings = Dry(preset);
            var low = Stereo(1, Sine(300));
            Filter(settings).Process(low, 0, low.Length);
            Assert.All(low, x => Assert.True(float.IsFinite(x)));
            Assert.InRange(Rms(low, 4410) / (0.5 / Math.Sqrt(2)), 0.7, 1.15);
            // Well above the game rate's Nyquist: gone.
            var high = Stereo(1, Sine(settings.SampleRate * 0.75));
            Filter(settings).Process(high, 0, high.Length);
            Assert.True(Rms(high, 4410) < 0.1 * (0.5 / Math.Sqrt(2)));
        }

        [Fact]
        public void IsTheSameWhateverTheBufferSize() {
            var settings = UVoiceFilter.FromPreset("classic");
            var whole = Stereo(0.5, t => Sine(220)(t) + Sine(1300, 0.2)(t));
            var pieces = (float[])whole.Clone();
            Filter(settings).Process(whole, 0, whole.Length);
            var filter = Filter(settings);
            for (int offset = 0, size = 2; offset < pieces.Length; offset += size, size = size * 3 % 998 + 2) {
                filter.Process(pieces, offset, Math.Min(size, pieces.Length - offset));
            }
            Assert.Equal(whole, pieces);
        }

        [Fact]
        public void TheOldTvIsMonoAndThin() {
            var settings = Dry("old_tv");
            var buffer = new float[Rate * 2];
            for (int i = 0; i < Rate; i++) {
                buffer[2 * i] = (float)Sine(400)((double)i / Rate);
                buffer[2 * i + 1] = (float)Sine(90)((double)i / Rate);
            }
            Filter(settings).Process(buffer, 0, buffer.Length);
            for (int i = 0; i < buffer.Length; i += 2) {
                Assert.Equal(buffer[i], buffer[i + 1]);
            }
            var bass = Stereo(1, Sine(60));
            Filter(settings).Process(bass, 0, bass.Length);
            Assert.True(Rms(bass, 4410) < 0.25 * (0.5 / Math.Sqrt(2)));
        }

        [Theory]
        [InlineData(UVoiceFilter.Room)]
        [InlineData(UVoiceFilter.Hall)]
        [InlineData(UVoiceFilter.SpaceEcho)]
        public void ReverbRingsOnAfterTheVoiceStops(string reverb) {
            double Tail(string type) {
                var settings = UVoiceFilter.FromPreset("classic");
                settings.Reverb = type;
                settings.ReverbAmount = 0.5;
                var buffer = Stereo(1.5, t => t < 0.3 ? Sine(300)(t) : 0);
                Filter(settings).Process(buffer, 0, buffer.Length);
                return Rms(buffer, (int)(0.4 * Rate));
            }
            Assert.True(Tail(reverb) > 0.003);
            Assert.True(Tail(UVoiceFilter.Off) < 0.0005);
        }

        [Fact]
        public void MixAndVolumeBlendWithTheDryVoice() {
            var settings = Dry("classic");
            settings.Mix = 0;
            settings.VolumeDb = 6;
            var buffer = Stereo(0.5, Sine(300));
            Filter(settings).Process(buffer, 0, buffer.Length);
            Assert.InRange(Rms(buffer, 4410) / (0.5 / Math.Sqrt(2)), 1.95, 2.05);
        }

        [Fact]
        public void ATrackPlaysThroughItsFilterOnlyWhenItIsOn() {
            var track = new UTrack("t");
            var source = new ToneSource();
            var live = VoiceFilterSource.WrapLive(source, track);
            float[] Block(int position) {
                var buffer = new float[2048];
                live.Mix(position, buffer, 0, buffer.Length);
                return buffer;
            }
            // No filter: the voice as rendered.
            Assert.Equal(source.Expected(0, 2048), Block(0));
            track.VoiceFilter = Dry("crunchy");
            var changed = Block(2048);
            Assert.NotEqual(source.Expected(2048, 2048), changed);
            Assert.All(changed, x => Assert.True(float.IsFinite(x)));
            // Off again, it fades back to the voice within a block or two.
            track.VoiceFilter!.Enabled = false;
            Block(4096);
            Assert.Equal(source.Expected(6144, 2048), Block(6144));

            Assert.Same(source, VoiceFilterSource.WrapWith(source, null));
            Assert.Same(source, VoiceFilterSource.WrapWith(source, new UVoiceFilter()));
            Assert.NotSame(source, VoiceFilterSource.WrapWith(source, Dry("classic")));
        }

        sealed class ToneSource : ISignalSource {
            public bool IsReady(int position, int count) => true;
            public float[] Expected(int position, int count) {
                var buffer = new float[count];
                Mix(position, buffer, 0, count);
                return buffer;
            }
            public int Mix(int position, float[] buffer, int index, int count) {
                for (int i = 0; i < count; i++) {
                    buffer[index + i] += (float)(0.3 * Math.Sin(2 * Math.PI * 330 * ((position + i) / 2) / Rate));
                }
                return position + count;
            }
        }
    }
}
