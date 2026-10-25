using System;
using System.Collections.Generic;
using System.Linq;
using OpenUtau.Core;
using OpenUtau.Core.Editing;
using OpenUtau.Core.ExpressionGraph;
using OpenUtau.Core.Ustx;
using OpenUtau.Core.VoiceActing;
using ReactiveUI;
using ReactiveUI.Primitives;
using ReactiveUI.SourceGenerators;

namespace OpenUtau.App.ViewModels {
    /// <summary>One of a set of choices, shown as a tile or a list entry with a short explanation.</summary>
    public class VoiceActingChoice {
        public string Key { get; }
        public string Title { get; }
        public string Description { get; }
        public VoiceActingChoice(string key, string title, string description = "") {
            Key = key;
            Title = title;
            Description = description;
        }
        public override string ToString() => Title;
    }

    /// <summary>
    /// A track's Voice Acting panel: its character (emotion), Wave Voice and PS1 filter. Edits are heard right
    /// away: the filter as it plays, the character once <see cref="PreviewCharacter"/> rebuilds its graph, which
    /// the window does shortly after the last change. <see cref="Apply"/> keeps everything as one undoable step;
    /// <see cref="Revert"/> puts the track back as it was.
    /// </summary>
    public partial class VoiceActingViewModel : ViewModelBase {
        public const int CharacterPage = 0;
        public const int WaveVoicePage = 1;
        public const int FilterPage = 2;

        [Reactive] public partial string TrackName { get; set; }
        [Reactive] public partial int Page { get; set; }
        /// <summary>Whether the track has a renderer, which a character needs. The filter works on any track.</summary>
        public bool CanSpeak { get; }

        // Character
        [Reactive] public partial bool SpeechOn { get; set; }
        public List<VoiceActingChoice> Emotions { get; }
        [Reactive] public partial VoiceActingChoice? SelectedEmotion { get; set; }
        /// <summary>How strongly the speech melody moves, in percent.</summary>
        [Reactive] public partial double Expressiveness { get; set; }
        /// <summary>The character's pitch, in semitones up or down.</summary>
        [Reactive] public partial double Pitch { get; set; }
        [Reactive] public partial double Breathiness { get; set; }

        // Wave Voice
        [Reactive] public partial bool WaveOn { get; set; }
        /// <summary>How far each syllable may jump, in semitones.</summary>
        [Reactive] public partial double Jumps { get; set; }
        /// <summary>How far each syllable slides up into its note, in semitones.</summary>
        [Reactive] public partial double Bounce { get; set; }
        /// <summary>How much higher the whole voice is, in semitones.</summary>
        [Reactive] public partial double Height { get; set; }
        /// <summary>From tiny (-100) to big (100): the voice's gender.</summary>
        [Reactive] public partial double Size { get; set; }
        /// <summary>The beat pulse's strength, in percent.</summary>
        [Reactive] public partial double Pulse { get; set; }
        public List<VoiceActingChoice> PulseRates { get; }
        [Reactive] public partial VoiceActingChoice? SelectedPulseRate { get; set; }
        /// <summary>How short each syllable is clipped, in percent.</summary>
        [Reactive] public partial double Choppiness { get; set; }

        // Filter
        [Reactive] public partial bool FilterOn { get; set; }
        public List<VoiceActingChoice> FilterPresets { get; }
        [Reactive] public partial VoiceActingChoice? SelectedFilterPreset { get; set; }
        public List<VoiceActingChoice> SampleRates { get; }
        [Reactive] public partial VoiceActingChoice? SelectedSampleRate { get; set; }
        [Reactive] public partial bool Adpcm { get; set; }
        [Reactive] public partial bool TvSpeaker { get; set; }
        public List<VoiceActingChoice> Reverbs { get; }
        [Reactive] public partial VoiceActingChoice? SelectedReverb { get; set; }
        public bool HasReverb => SelectedReverb != null && SelectedReverb.Key != UVoiceFilter.Off;
        /// <summary>In percent.</summary>
        [Reactive] public partial double ReverbAmount { get; set; }
        /// <summary>How much of the filtered sound is heard, in percent.</summary>
        [Reactive] public partial double FilterMix { get; set; }
        [Reactive] public partial double FilterVolume { get; set; }

        /// <summary>Raised when the character changes, for the window to preview it once the edits settle.</summary>
        public event Action? CharacterChanged;

        readonly UProject project;
        readonly UTrack track;
        readonly DocManager docManager;
        readonly ExpressionGraphState originalGraphs;
        readonly UVoiceFilter? originalFilter;
        // The character the tiles and sliders edit; it keeps what they don't show, such as values fitted to a take.
        VoiceCharacter character;
        WaveVoice wave;
        bool previewed;
        bool loading;
        // Whether the character was edited, so OK leaves a character it wasn't asked to change alone.
        bool characterEdited;

        public VoiceActingViewModel() : this(new UProject(), new UTrack("Preview"), DocManager.Inst) { }

        public VoiceActingViewModel(UProject project, UTrack track, DocManager docManager) {
            this.project = project;
            this.track = track;
            this.docManager = docManager;
            TrackName = track.TrackName;
            CanSpeak = !string.IsNullOrEmpty(track.RendererSettings?.renderer);
            originalGraphs = ExpressionGraphState.Capture(project);
            originalFilter = track.VoiceFilter?.Clone();

            Emotions = VoiceCharacter.PresetNames.Select(key => new VoiceActingChoice(key,
                ThemeManager.GetString($"voiceacting.preset.{key}"),
                ThemeManager.GetString($"voiceacting.preset.{key}.desc"))).ToList();
            PulseRates = new[] { 1, 2, 4 }.Select(n => new VoiceActingChoice(
                n.ToString(), ThemeManager.GetString($"voiceacting.wave.pulserate.{n}"))).ToList();
            FilterPresets = UVoiceFilter.PresetNames.Select(key => new VoiceActingChoice(key,
                ThemeManager.GetString($"voiceacting.filter.preset.{key}"),
                ThemeManager.GetString($"voiceacting.filter.preset.{key}.desc"))).ToList();
            SampleRates = new[] { 22050, 11025, 8000 }.Select(rate => new VoiceActingChoice(
                rate.ToString(), ThemeManager.GetString($"voiceacting.filter.rate.{rate}"))).ToList();
            Reverbs = new[] { UVoiceFilter.Off, UVoiceFilter.Room, UVoiceFilter.Hall, UVoiceFilter.SpaceEcho }
                .Select(key => new VoiceActingChoice(key, ThemeManager.GetString($"voiceacting.filter.reverb.{key}")))
                .ToList();

            var current = TrackVoice.CharacterOf(project, track);
            character = current == null || current.emotion == VoiceCharacter.NoEmotion
                ? VoiceCharacter.Preset("neutral") : current;
            wave = current?.wave?.Clone() ?? new WaveVoice();
            loading = true;
            try {
                SpeechOn = current != null && current.emotion != VoiceCharacter.NoEmotion;
                WaveOn = current?.wave != null;
                LoadCharacter();
                LoadWave();
                var filter = track.VoiceFilter?.Clone() ?? UVoiceFilter.FromPreset("classic");
                LoadFilter(filter);
                FilterOn = track.VoiceFilter?.Enabled == true;
                if (!FilterOn) {
                    SelectedFilterPreset = null;
                }
            } finally {
                loading = false;
            }
            Page = !CanSpeak ? FilterPage : current?.wave != null && current.emotion == VoiceCharacter.NoEmotion
                ? WaveVoicePage : CharacterPage;

            // WhenAnyValue reports the current values as it subscribes; those aren't edits.
            Load(() => {
                // Picking an emotion or a filter preset loads it and switches its part on.
                this.WhenAnyValue(x => x.SelectedEmotion).Subscribe(choice => {
                    if (loading || choice == null) return;
                    character = VoiceCharacter.Preset(choice.Key);
                    Load(() => {
                        SpeechOn = true;
                        LoadCharacter();
                    });
                    OnCharacterEdited();
                });
                this.WhenAnyValue(x => x.SelectedFilterPreset).Subscribe(choice => {
                    if (loading || choice == null) return;
                    Load(() => {
                        LoadFilter(UVoiceFilter.FromPreset(choice.Key));
                        FilterOn = true;
                    });
                    PushFilter();
                });
                // Switched on, a part shows which tile it is.
                this.WhenAnyValue(x => x.SpeechOn).Subscribe(on => {
                    if (!loading && on && SelectedEmotion == null) {
                        Load(LoadCharacter);
                    }
                });
                this.WhenAnyValue(x => x.FilterOn).Subscribe(on => {
                    if (!loading && on && SelectedFilterPreset == null) {
                        var filter = BuildFilter();
                        Load(() => SelectedFilterPreset = FilterPresets.FirstOrDefault(
                            p => UVoiceFilter.FromPreset(p.Key).SameSound(filter)));
                    }
                });
                this.WhenAnyValue(x => x.SpeechOn, x => x.Expressiveness, x => x.Pitch, x => x.Breathiness)
                    .Subscribe(_ => { if (!loading) OnCharacterEdited(); });
                this.WhenAnyValue(x => x.WaveOn, x => x.Jumps, x => x.Bounce, x => x.Height, x => x.Size)
                    .Subscribe(_ => { if (!loading) OnCharacterEdited(); });
                this.WhenAnyValue(x => x.Pulse, x => x.SelectedPulseRate, x => x.Choppiness)
                    .Subscribe(_ => { if (!loading) OnCharacterEdited(); });
                this.WhenAnyValue(x => x.FilterOn, x => x.SelectedSampleRate, x => x.Adpcm, x => x.TvSpeaker)
                    .Subscribe(_ => PushFilter());
                this.WhenAnyValue(x => x.SelectedReverb, x => x.ReverbAmount, x => x.FilterMix, x => x.FilterVolume)
                    .Subscribe(_ => PushFilter());
                this.WhenAnyValue(x => x.SelectedReverb).Subscribe(_ => this.RaisePropertyChanged(nameof(HasReverb)));
            });
        }

        void OnCharacterEdited() {
            characterEdited = true;
            CharacterChanged?.Invoke();
        }

        void Load(Action load) {
            loading = true;
            try {
                load();
            } finally {
                loading = false;
            }
        }

        void LoadCharacter() {
            SelectedEmotion = SpeechOn ? Emotions.FirstOrDefault(e => e.Key == character.emotion) : null;
            Expressiveness = Math.Round(character.expressiveness * 100);
            Pitch = character.pitchOffset / 100;
            Breathiness = character.breathiness;
        }

        void LoadWave() {
            Jumps = wave.hopSemitones;
            Bounce = wave.scoop / 100;
            Height = wave.lift / 100;
            Size = -wave.gender;
            Pulse = wave.pulseDepth / 2;
            SelectedPulseRate = PulseRates.FirstOrDefault(r => r.Key == ((int)wave.pulsesPerBeat).ToString()) ?? PulseRates[1];
            Choppiness = Math.Round(wave.chopDepth / 2.4);
        }

        void LoadFilter(UVoiceFilter filter) {
            SelectedFilterPreset = FilterPresets.FirstOrDefault(p => p.Key == filter.Preset);
            SelectedSampleRate = SampleRates.FirstOrDefault(r => r.Key == filter.SampleRate.ToString()) ?? SampleRates[1];
            Adpcm = filter.Adpcm;
            TvSpeaker = filter.TvSpeaker;
            SelectedReverb = Reverbs.FirstOrDefault(r => r.Key == filter.Reverb) ?? Reverbs[0];
            ReverbAmount = Math.Round(filter.ReverbAmount * 100);
            FilterMix = Math.Round(filter.Mix * 100);
            FilterVolume = filter.VolumeDb;
        }

        /// <summary>A fresh melody for Wave Voice's note jumps.</summary>
        public void NewMelody() {
            wave.seed = Random.Shared.Next(1, 1000000);
            OnCharacterEdited();
        }

        /// <summary>Puts every syllable on the track on the beat, now, as an undoable edit of the notes.</summary>
        public void RhythmEighths() => WaveVoiceRhythm.RunOnTrack(project, track.TrackNo, 8, docManager);
        public void RhythmSixteenths() => WaveVoiceRhythm.RunOnTrack(project, track.TrackNo, 16, docManager);

        /// <summary>The character as set, or null when neither the emotion nor Wave Voice is on.</summary>
        public VoiceCharacter? BuildCharacter() {
            if (!SpeechOn && !WaveOn) {
                return null;
            }
            VoiceCharacter result;
            if (SpeechOn) {
                result = character.Clone();
                result.expressiveness = (float)(Expressiveness / 100);
                result.pitchOffset = (float)(Pitch * 100);
                result.breathiness = (float)Breathiness;
            } else {
                result = VoiceCharacter.Plain();
            }
            result.name = $"{TrackName} · {(SpeechOn ? SelectedEmotion?.Title ?? result.emotion : ThemeManager.GetString("voiceacting.wave"))}";
            if (WaveOn) {
                result.wave = wave.Clone();
                result.wave.hopSemitones = (float)Jumps;
                result.wave.scoop = (float)(Bounce * 100);
                result.wave.lift = (float)(Height * 100);
                result.wave.gender = (float)-Size;
                result.wave.pulseDepth = (float)(Pulse * 2);
                result.wave.pulsesPerBeat = int.TryParse(SelectedPulseRate?.Key, out int rate) ? rate : 2;
                result.wave.chopDepth = (float)(Choppiness * 2.4);
            } else {
                result.wave = null;
            }
            return result;
        }

        public UVoiceFilter BuildFilter() => new UVoiceFilter {
            Enabled = FilterOn,
            Preset = SelectedFilterPreset?.Key ?? "custom",
            SampleRate = int.TryParse(SelectedSampleRate?.Key, out int rate) ? rate : 11025,
            Adpcm = Adpcm,
            TvSpeaker = TvSpeaker,
            Reverb = SelectedReverb?.Key ?? UVoiceFilter.Off,
            ReverbAmount = ReverbAmount / 100,
            Mix = FilterMix / 100,
            VolumeDb = FilterVolume,
        };

        // The filter is heard live: the audio thread reads the track's filter every block.
        void PushFilter() {
            if (loading) return;
            track.VoiceFilter = BuildFilter();
        }

        /// <summary>Rebuilds the track's character graph so the change is heard, without an undo step.</summary>
        public void PreviewCharacter() {
            if (!CanSpeak) return;
            var state = TrackVoice.With(project, track.TrackNo, BuildCharacter());
            if (state == null || state.SameExceptLayout(ExpressionGraphState.Capture(project))) return;
            var command = new SetExpressionGraphsCommand(project, state);
            docManager.ApplyTransient(new[] { command }, command.ValidateOptions);
            previewed = true;
        }

        /// <summary>Puts the track back as it was when the panel opened.</summary>
        public void Revert() {
            if (previewed) {
                var command = new SetExpressionGraphsCommand(project, originalGraphs);
                docManager.ApplyTransient(new[] { command }, command.ValidateOptions);
                previewed = false;
            }
            track.VoiceFilter = originalFilter?.Clone();
        }

        /// <summary>Keeps the edits as one undoable step.</summary>
        public void Apply() {
            var newCharacter = BuildCharacter();
            var finalGraphs = CanSpeak && characterEdited ? TrackVoice.With(project, track.TrackNo, newCharacter) : null;
            var finalFilter = BuildFilter();
            bool graphsChanged = finalGraphs != null && !finalGraphs.SameExceptLayout(originalGraphs);
            bool filterChanged = originalFilter == null ? finalFilter.Enabled : !originalFilter.SameSound(finalFilter);
            // Back to where the panel started, so undo returns there.
            Revert();
            if (!graphsChanged && !filterChanged) {
                return;
            }
            docManager.StartUndoGroup("command.voiceacting.edit");
            try {
                if (graphsChanged) {
                    if (newCharacter != null && !project.expressions.ContainsKey(VoiceCharacter.IntonationCurve)) {
                        docManager.ExecuteCmd(new ConfigureExpressionsCommand(project,
                            project.expressions.Values.Append(VoiceCharacter.IntonationDescriptor()).ToArray()));
                    }
                    docManager.ExecuteCmd(new SetExpressionGraphsCommand(project, finalGraphs!));
                }
                if (filterChanged) {
                    docManager.ExecuteCmd(new SetVoiceFilterCommand(track, originalFilter, finalFilter));
                }
            } finally {
                docManager.EndUndoGroup();
            }
        }
    }
}
