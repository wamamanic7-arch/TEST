using System.Collections.Generic;
using OpenUtau.Core.SignalChain.Effects;

namespace OpenUtau.Core.Ustx {
    /// <summary>
    /// A track's voice filter: the PS1 filter, which makes the voice sound as if a PlayStation played it. Saved
    /// with the track; null or disabled, the track sounds as rendered. It comes before the track's volume, pan and
    /// Track Polish.
    /// </summary>
    public class UVoiceFilter {
        public const string Off = "off";
        public const string Room = "room";
        public const string Hall = "hall";
        public const string SpaceEcho = "space_echo";

        public bool Enabled { get; set; }
        /// <summary>The preset last picked, shown in the panel; the values below are what is heard.</summary>
        public string Preset { get; set; } = "classic";
        /// <summary>The game sample rate in Hz: 22050, 11025 or 8000 in the presets.</summary>
        public int SampleRate { get; set; } = 11025;
        /// <summary>Whether the sound goes through the console's 4-bit ADPCM compression.</summary>
        public bool Adpcm { get; set; } = true;
        /// <summary><see cref="Off"/>, <see cref="Room"/>, <see cref="Hall"/> or <see cref="SpaceEcho"/>.</summary>
        public string Reverb { get; set; } = Room;
        public double ReverbAmount { get; set; } = 0.25;
        /// <summary>Whether it plays through an old TV's small, mono speaker.</summary>
        public bool TvSpeaker { get; set; }
        /// <summary>How much of the filtered sound replaces the original, 0 to 1.</summary>
        public double Mix { get; set; } = 1;
        public double VolumeDb { get; set; }

        public UVoiceFilter Clone() => (UVoiceFilter)MemberwiseClone();

        public bool SameSound(UVoiceFilter other) =>
            Enabled == other.Enabled && SampleRate == other.SampleRate && Adpcm == other.Adpcm
            && Reverb == other.Reverb && ReverbAmount == other.ReverbAmount && TvSpeaker == other.TvSpeaker
            && Mix == other.Mix && VolumeDb == other.VolumeDb;

        public Ps1Reverb ReverbType => Reverb switch {
            Room => Ps1Reverb.Room,
            Hall => Ps1Reverb.Hall,
            SpaceEcho => Ps1Reverb.SpaceEcho,
            _ => Ps1Reverb.Off,
        };

        public static readonly string[] PresetNames = { "clean", "classic", "crunchy", "old_tv", "echo" };

        /// <summary>A preset's settings, enabled; the classic sound for an unknown name.</summary>
        public static UVoiceFilter FromPreset(string preset) {
            var filter = new UVoiceFilter { Enabled = true, Preset = preset };
            switch (preset) {
                case "clean":
                    filter.SampleRate = 22050;
                    filter.Reverb = Off;
                    break;
                case "crunchy":
                    filter.SampleRate = 8000;
                    filter.Reverb = Off;
                    filter.VolumeDb = -1;
                    break;
                case "old_tv":
                    filter.TvSpeaker = true;
                    filter.Reverb = Off;
                    break;
                case "echo":
                    filter.SampleRate = 22050;
                    filter.Reverb = SpaceEcho;
                    filter.ReverbAmount = 0.45;
                    break;
                default:
                    filter.Preset = "classic";
                    break;
            }
            return filter;
        }
    }
}
