using System;
using System.Collections.Generic;
using System.Linq;
using OpenUtau.Core.Ustx;

namespace OpenUtau.Core.VoiceActing {
    /// <summary>
    /// A sing-song, game-like way of talking, as in Animal Crossing or Friday Night Funkin': each syllable hops to
    /// a pitch of its own with a quick scoop up into it and is clipped short, and the loudness pulses with the
    /// beat. It goes on top of a character's emotion; <see cref="Rhythm"/> also puts the syllables on the beat.
    /// </summary>
    public class WaveVoice {
        /// <summary>Each syllable jumps to a random whole semitone within plus or minus this many.</summary>
        public float hopSemitones = 4;
        /// <summary>Cents each syllable starts below its pitch, rising into it over <see cref="scoopMs"/>.</summary>
        public float scoop = 120;
        public float scoopMs = 40;
        /// <summary>Cents the voice goes up, and GENC added, for a small, toy-like voice.</summary>
        public float lift = 300;
        public float gender = -30;
        /// <summary>
        /// Loudness pulses per beat of the project's tempo, each starting loud and fading by
        /// <see cref="pulseDepth"/> DYN units.
        /// </summary>
        public float pulsesPerBeat = 2;
        public float pulseDepth = 60;
        /// <summary>The end of each syllable fades by <see cref="chopDepth"/> DYN units over this many ms.</summary>
        public float releaseMs = 50;
        public float chopDepth = 150;
        public int seed = 0;

        public WaveVoice Clone() => (WaveVoice)MemberwiseClone();

        /// <summary>
        /// Where each note goes to be said in a steady rhythm, as (position, duration): one grid step per note, in
        /// order. A pause of at least a grid step between notes starts a new phrase, on the grid step nearest
        /// where it was, with at least a step of rest after the phrase before.
        /// </summary>
        public static List<(int position, int duration)> Rhythm(int partPosition, IReadOnlyList<UNote> notes, int grid) {
            var result = new List<(int, int)>();
            if (grid <= 0) {
                return result;
            }
            var sorted = notes.OrderBy(n => n.position).ToList();
            int next = int.MinValue;
            for (int i = 0; i < sorted.Count; i++) {
                bool startsPhrase = i == 0 || sorted[i].position - sorted[i - 1].End >= grid;
                int position = next;
                if (startsPhrase) {
                    // On the project's grid, not the part's.
                    int snapped = (int)Math.Round((partPosition + sorted[i].position) / (double)grid) * grid - partPosition;
                    if (snapped < 0) {
                        snapped += grid;
                    }
                    position = i == 0 ? snapped : Math.Max(snapped, next + grid);
                }
                result.Add((position, grid));
                next = position + grid;
            }
            return result;
        }
    }
}
