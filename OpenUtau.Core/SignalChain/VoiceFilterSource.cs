using System;
using OpenUtau.Core.SignalChain.Effects;
using OpenUtau.Core.Ustx;

namespace OpenUtau.Core.SignalChain {
    /// <summary>
    /// Applies a track's voice filter (<see cref="UTrack.VoiceFilter"/>) to the track's rendered voice, before its
    /// fader. Like <see cref="MixFxSource"/>, it reads the settings every block, so edits are heard while playing,
    /// and switching the filter on or off crossfades instead of clicking. Stateful: one per playback session.
    /// </summary>
    public class VoiceFilterSource : ISignalSource {
        const int Channels = 2;
        // ~15 ms at 44.1 kHz.
        const int FadeFrames = 661;
        const float FadeStep = 1f / FadeFrames;

        readonly ISignalSource source;
        readonly Func<UVoiceFilter?> getFilter;
        readonly Ps1Filter filter = new Ps1Filter();
        UVoiceFilter? applied;
        float gain;
        float[] scratch = Array.Empty<float>();
        float[] dry = Array.Empty<float>();

        VoiceFilterSource(ISignalSource source, Func<UVoiceFilter?> getFilter) {
            this.source = source;
            this.getFilter = getFilter;
            var current = getFilter();
            if (current != null) {
                Sync(current);
                gain = current.Enabled ? 1f : 0f;
            }
        }

        public bool IsReady(int position, int count) => source.IsReady(position, count);

        public int Mix(int position, float[] buffer, int index, int count) {
            if (scratch.Length < count) {
                scratch = new float[count];
                dry = new float[count];
            }
            Array.Clear(scratch, 0, count);
            int end = source.Mix(position, scratch, 0, count);
            var current = getFilter();
            if (current != null) {
                Sync(current);
            }
            float target = current?.Enabled == true ? 1f : 0f;
            if (gain > 0f || target > 0f) {
                bool steady = gain == 1f && target == 1f;
                if (!steady) {
                    Array.Copy(scratch, dry, count);
                }
                filter.Process(scratch, 0, count);
                if (!steady) {
                    float g = gain;
                    for (int i = 0; i + 1 < count; i += Channels) {
                        g = g < target ? Math.Min(target, g + FadeStep) : Math.Max(target, g - FadeStep);
                        for (int c = 0; c < Channels; c++) {
                            scratch[i + c] = dry[i + c] + (scratch[i + c] - dry[i + c]) * g;
                        }
                    }
                    gain = g;
                    if (gain == 0f) {
                        filter.Reset();
                    }
                }
            }
            for (int i = 0; i < count; i++) {
                buffer[index + i] += scratch[i];
            }
            return end;
        }

        void Sync(UVoiceFilter current) {
            if (applied != null && applied.SameSound(current)) {
                return;
            }
            applied = current.Clone();
            filter.Configure(applied.SampleRate, applied.Adpcm, applied.ReverbType, applied.ReverbAmount,
                applied.TvSpeaker, applied.Mix, applied.VolumeDb);
        }

        /// <summary>For export: the filter as set now, or the source itself when there is none.</summary>
        public static ISignalSource WrapWith(ISignalSource inner, UVoiceFilter? filter) {
            if (filter == null || !filter.Enabled) {
                return inner;
            }
            var snapshot = filter.Clone();
            return new VoiceFilterSource(inner, () => snapshot);
        }

        /// <summary>For playback: follows the track's filter as it is edited.</summary>
        public static ISignalSource WrapLive(ISignalSource inner, UTrack track) =>
            new VoiceFilterSource(inner, () => track.VoiceFilter);
    }
}
