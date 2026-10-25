using System;
using System.Globalization;
using System.Linq;
using OpenUtau.Core.ExpressionGraph;
using OpenUtau.Core.Render;
using OpenUtau.Core.Ustx;

namespace OpenUtau.Core.VoiceActing {
    /// <summary>
    /// How a character speaks: the melody of its phrases, the small unsteadiness of a human voice and its tone.
    /// A character becomes an expression graph that adds all of this to whatever is drawn, at render time, so
    /// it follows every edit to the notes. Its values become node parameters, tunable in the graph editor.
    /// </summary>
    public class VoiceCharacter {
        /// <summary>
        /// The curve that shapes each phrase ending: 0 falls as a statement does, 50 stays level, 100 rises as a
        /// question does, and below 0 falls harder.
        /// </summary>
        public const string IntonationCurve = "intc";

        public static UExpressionDescriptor IntonationDescriptor() =>
            new UExpressionDescriptor("intonation (curve)", IntonationCurve, -100, 100, 0) { type = UExpressionType.Curve };

        public string name = "Character";
        /// <summary>
        /// The preset the character started from, one of <see cref="PresetNames"/>, or <see cref="NoEmotion"/>
        /// for a voice without the speech melody, as <see cref="Plain"/> makes.
        /// </summary>
        public string emotion = "neutral";
        public const string NoEmotion = "off";

        /// <summary>Added to the pitch, in cents.</summary>
        public float pitchOffset = 0;
        /// <summary>Scales the whole speech melody: declination, endings and syllable arcs.</summary>
        public float expressiveness = 1;
        /// <summary>Cents the pitch drifts down from a phrase's start to its end.</summary>
        public float declination = 150;
        /// <summary>Cents a phrase ending falls (or, by the intonation curve, rises).</summary>
        public float finalDepth = 250;
        /// <summary>How long a phrase ending takes, in ms. Also the span of the other end-of-phrase effects.</summary>
        public float finalMs = 400;
        /// <summary>Cents each syllable arcs up in its middle.</summary>
        public float syllableArc = 30;

        /// <summary>Slow wandering of the pitch, within plus or minus this many cents.</summary>
        public float driftDepth = 25;
        public float driftRate = 3;
        /// <summary>Fast unsteadiness of the pitch, within plus or minus this many cents.</summary>
        public float jitterDepth = 6;
        public float jitterRate = 25;
        /// <summary>Unsteadiness of the loudness, in DYN units (tenths of a dB).</summary>
        public float shimmerDepth = 15;

        /// <summary>DYN units the loudness drops by at the end of each phrase.</summary>
        public float endDecay = 30;
        /// <summary>Breathiness (BREC) throughout, and added at the end of each phrase.</summary>
        public float breathiness = 0;
        public float endBreath = 20;
        /// <summary>Tension (TENC) throughout.</summary>
        public float tension = 0;
        /// <summary>Gender (GENC) throughout: negative is brighter and younger, positive deeper.</summary>
        public float gender = 0;
        /// <summary>Growl (GRWC) at the end of each phrase, as vocal fry.</summary>
        public float endFry = 0;
        /// <summary>Percent of the voicing (VOIC) kept: low values whisper.</summary>
        public float voicing = 100;
        public int seed = 0;

        /// <summary>The game-like Wave Voice way of talking, on top of the emotion; null for none.</summary>
        public WaveVoice? wave;

        public VoiceCharacter Clone() {
            var c = (VoiceCharacter)MemberwiseClone();
            c.wave = wave?.Clone();
            return c;
        }

        public static readonly string[] PresetNames = {
            "neutral", "cheerful", "gloomy", "angry", "whisper", "timid", "elderly",
        };

        /// <summary>A starting point for a kind of character, by one of <see cref="PresetNames"/>.</summary>
        public static VoiceCharacter Preset(string preset) {
            if (preset == NoEmotion) {
                return Plain();
            }
            var c = new VoiceCharacter { name = preset, emotion = preset };
            switch (preset) {
                case "cheerful":
                    c.pitchOffset = 150; c.expressiveness = 1.4f; c.syllableArc = 45; c.finalDepth = 200;
                    c.driftDepth = 30; c.tension = 15; c.breathiness = -10; c.gender = -10; c.endDecay = 20;
                    break;
                case "gloomy":
                    c.pitchOffset = -150; c.expressiveness = 0.6f; c.declination = 250; c.finalDepth = 300;
                    c.driftDepth = 15; c.shimmerDepth = 20; c.endDecay = 60;
                    c.breathiness = 30; c.endBreath = 35; c.tension = -20;
                    break;
                case "angry":
                    c.pitchOffset = 100; c.expressiveness = 1.3f; c.finalDepth = 350; c.jitterDepth = 9;
                    c.shimmerDepth = 25; c.endDecay = 10; c.tension = 45; c.breathiness = -20; c.endFry = 20;
                    break;
                case "whisper":
                    c.expressiveness = 0.5f; c.voicing = 15; c.breathiness = 80; c.endBreath = 10; c.tension = -30;
                    break;
                case "timid":
                    c.pitchOffset = 50; c.expressiveness = 0.7f; c.finalDepth = 150; c.driftDepth = 35;
                    c.jitterDepth = 10; c.endDecay = 50; c.breathiness = 25;
                    break;
                case "elderly":
                    c.pitchOffset = -50; c.driftDepth = 45; c.jitterDepth = 18; c.shimmerDepth = 30;
                    c.breathiness = 20; c.tension = 10; c.gender = 15; c.endFry = 30;
                    break;
            }
            return c;
        }

        /// <summary>
        /// A voice without speech melody, unsteadiness or tone of its own: the notes as drawn, for Wave Voice alone.
        /// </summary>
        public static VoiceCharacter Plain() => new VoiceCharacter {
            name = NoEmotion,
            emotion = NoEmotion,
            declination = 0,
            finalDepth = 0,
            syllableArc = 0,
            driftDepth = 0,
            jitterDepth = 0,
            shimmerDepth = 0,
            endDecay = 0,
            endBreath = 0,
        };

        /// <summary>
        /// Takes the melody and unsteadiness measured in a recording; keeps the tone, which a recording of the
        /// actor doesn't say for the character.
        /// </summary>
        public VoiceCharacter FittedTo(VoiceProfile profile) {
            var c = Clone();
            // What the profile measures per unit of depth of the character's fluctuation nodes at their default
            // rates, as measured on the nodes' own output.
            const float driftPerDepth = 0.236f;
            const float jitterPerDepth = 0.133f;
            const float shimmerPerDepth = 0.0423f;
            // An actor's take also moves on purpose in these bands, so the fit stops where the shakiest preset
            // (elderly) is.
            c.driftDepth = Math.Clamp(profile.DriftCents / driftPerDepth, 0, 60);
            c.jitterDepth = Math.Clamp(profile.JitterCents / jitterPerDepth, 0, 20);
            c.shimmerDepth = Math.Clamp(profile.ShimmerDb * 10 / shimmerPerDepth, 0, 30);
            c.declination = Math.Clamp(profile.DeclinationCents, -200, 600);
            // The graph falls by finalDepth where the intonation curve is 0.
            c.finalDepth = Math.Clamp(-profile.FinalCents, 0, 800);
            // Syllable arcs scale with how widely the voice ranges, about 6 semitones in calm speech.
            c.syllableArc = Math.Clamp(30 * (profile.HighPitch - profile.LowPitch) / 6, 0, 120);
            // Aperiodicity of about 0.08 is a clear voice; 0.3 is very breathy.
            c.breathiness = Math.Clamp((profile.Aperiodicity - 0.08f) / 0.22f * 80, -50, 90);
            c.expressiveness = 1;
            return c;
        }

        /// <summary>The character as a graph for a renderer, driving what that renderer supports.</summary>
        public UExpressionGraph BuildGraph(string id, string renderer) {
            IRenderer? instance = null;
            try {
                instance = Renderers.GetOrCreate(renderer);
            } catch {
                instance = null;
            }
            return BuildGraph(id, renderer, instance);
        }

        internal UExpressionGraph BuildGraph(string id, string renderer, IRenderer? instance) {
            var graph = new UExpressionGraph {
                id = id,
                name = name,
                renderer = renderer,
                preferredPitchCurve = Format.Ustx.PITO,
                character = Clone(),
            };
            UGraphNode Node(string type, float x, float y, params (string key, float value)[] values) {
                var node = ExpressionGraphEdits.AddNode(graph, type, x, y);
                foreach (var (key, value) in values) {
                    node.Set(key, value);
                }
                return node;
            }
            UGraphNode Curve(string type, string abbr, float x, float y) {
                var node = Node(type, x, y);
                node.Set("abbr", abbr);
                return node;
            }
            UGraphNode Pitch(string source, float x, float y) {
                var node = Node(GraphNodeTypes.PitchInput, x, y);
                node.Set("source", source);
                return node;
            }
            void Link(UGraphNode from, UGraphNode to, string port, string? fromPort = null) =>
                ExpressionGraphEdits.TryLink(graph, from.id, to.id, port, fromPort);
            UGraphNode Add(UGraphNode a, UGraphNode b, float x, float y) {
                var node = Node(GraphNodeTypes.Add, x, y);
                Link(a, node, "a");
                Link(b, node, "b");
                return node;
            }
            UGraphNode Fluctuation(float depth, float rate, int seedOffset, float x, float y) {
                var node = Node(GraphNodeTypes.Fluctuation, x, y, ("depth", depth), ("rate", rate), ("octaves", 2));
                node.Set("seed", (seed + seedOffset).ToString(CultureInfo.InvariantCulture));
                return node;
            }
            UGraphNode Scale(UGraphNode a, float factor, float x, float y) {
                var node = Node(GraphNodeTypes.Multiply, x, y, ("b", factor));
                Link(a, node, "a");
                return node;
            }
            bool Supports(string abbr) => GraphNodeTypes.CanDriveCurve(
                new UExpressionDescriptor(abbr, abbr, -100, 100, 0) { type = UExpressionType.Curve }, instance);

            // The pitch the renderer's default graph has, plus the speech melody and the voice's unsteadiness;
            // the pitch override, as loaded from a recording or drawn, replaces all of it where it has a value.
            UGraphNode underneath;
            if (instance?.SupportsRenderPitch == true) {
                var rendered = Curve(GraphNodeTypes.MaskedCurveInput, Format.Ustx.RPIT, 200, 20);
                Link(Pitch("notes", 20, 20), rendered, "fallback");
                underneath = Add(rendered, Curve(GraphNodeTypes.CurveInput, Format.Ustx.PITD, 200, 120), 380, 60);
            } else {
                var bends = Add(Pitch("pitch_bend", 20, 0), Pitch("vibrato", 20, 70), 200, 30);
                bends = Add(bends, Pitch("mod_plus", 20, 140), 290, 90);
                underneath = Add(bends, Curve(GraphNodeTypes.CurveInput, Format.Ustx.PITD, 200, 160), 380, 130);
            }
            var ending = Node(GraphNodeTypes.MapRange, 200, 240,
                ("in_min", 0), ("in_max", 100), ("out_min", -finalDepth), ("out_max", finalDepth));
            Link(Curve(GraphNodeTypes.CurveInput, IntonationCurve, 20, 240), ending, GraphNodeTypes.Value);
            var intonation = Node(GraphNodeTypes.Intonation, 380, 240,
                ("declination", declination), ("arc", syllableArc), ("final_ms", finalMs));
            Link(ending, intonation, "final");
            var melody = Scale(intonation, expressiveness, 560, 240);
            var drift = Fluctuation(driftDepth, driftRate, 0, 380, 380);
            var jitter = Fluctuation(jitterDepth, jitterRate, 1, 380, 520);
            var pitch = Add(underneath, melody, 740, 130);
            pitch = Add(pitch, Add(drift, jitter, 560, 450), 830, 200);
            pitch = Add(pitch, Node(GraphNodeTypes.Constant, 740, 300, ("value", pitchOffset)), 920, 260);
            // Wave Voice: each syllable on a pitch of its own, scooping up into it, the whole voice lifted.
            float waveRows = 0;
            if (wave != null) {
                const float top = 680;
                var hop = Node(GraphNodeTypes.NoteRandom, 380, top,
                    ("min", -wave.hopSemitones * 100), ("max", wave.hopSemitones * 100), ("step", 100));
                hop.Set("seed", (wave.seed + seed).ToString(CultureInfo.InvariantCulture));
                var scoopIn = Node(GraphNodeTypes.MapRange, 200, top + 150,
                    ("in_min", 0), ("in_max", 1), ("out_min", -wave.scoop), ("out_max", 0));
                Link(Node(GraphNodeTypes.NoteEnvelope, 20, top + 150, ("attack_ms", wave.scoopMs)), scoopIn,
                    GraphNodeTypes.Value);
                var wavePitch = Add(Add(hop, scoopIn, 560, top + 60),
                    Node(GraphNodeTypes.Constant, 560, top + 160, ("value", wave.lift)), 740, top + 100);
                pitch = Add(pitch, wavePitch, 1010, 380);
                waveRows = 340;
            }
            var overrides = Curve(GraphNodeTypes.MaskedCurveInput, Format.Ustx.PITO, 1100, 260);
            Link(pitch, overrides, "fallback");
            Link(overrides, Node(GraphNodeTypes.PitchOutput, 1280, 260), GraphNodeTypes.Value, GraphNodeTypes.Value);

            // 0 until the last finalMs of a phrase, then up to 1 at its end.
            var phraseEnd = Node(GraphNodeTypes.MapRange, 200, 660 + waveRows,
                ("in_min", finalMs / 1000), ("in_max", 0), ("out_min", 0), ("out_max", 1));
            phraseEnd.Set("clamp", "true");
            Link(Node(GraphNodeTypes.PhrasePosition, 20, 660 + waveRows), phraseEnd, GraphNodeTypes.Value, "remaining");

            float row = 840 + waveRows;
            void Drive(string abbr, Func<UGraphNode, float, UGraphNode> value, float height = 150) {
                if (!Supports(abbr)) {
                    return;
                }
                var drawn = Curve(GraphNodeTypes.CurveInput, abbr, 20, row);
                Link(value(drawn, row), Curve(GraphNodeTypes.CurveOutput, abbr, 920, row), GraphNodeTypes.Value);
                row += height;
            }
            Drive(Format.Ustx.DYN, (drawn, y) => {
                var dyn = Add(
                    Add(drawn, Fluctuation(shimmerDepth, 12, 2, 200, y + 40), 560, y),
                    Scale(phraseEnd, -endDecay, 560, y + 70), 740, y);
                if (wave == null) {
                    return dyn;
                }
                // Wave Voice: loud on each pulse of the beat and fading through it; each syllable clipped short.
                var pulse = Node(GraphNodeTypes.Lfo, 380, y + 40,
                    ("rate", wave.pulsesPerBeat), ("amplitude", -wave.pulseDepth / 2));
                pulse.Set("shape", "saw");
                pulse.Set("unit", "beat");
                var chop = Node(GraphNodeTypes.MapRange, 380, y + 250,
                    ("in_min", 0), ("in_max", 1), ("out_min", -wave.chopDepth), ("out_max", 0));
                Link(Node(GraphNodeTypes.NoteEnvelope, 200, y + 200, ("attack_ms", 5), ("release_ms", wave.releaseMs)),
                    chop, GraphNodeTypes.Value);
                return Add(dyn, Add(pulse, chop, 560, y + 200), 830, y + 80);
            }, wave == null ? 150 : 420);
            Drive(Format.Ustx.BREC, (drawn, y) => Add(
                Add(drawn, Node(GraphNodeTypes.Constant, 200, y + 40, ("value", breathiness)), 560, y),
                Scale(phraseEnd, endBreath, 560, y + 70), 740, y));
            Drive(Format.Ustx.TENC, (drawn, y) =>
                Add(drawn, Node(GraphNodeTypes.Constant, 200, y + 40, ("value", tension)), 740, y));
            Drive(Format.Ustx.GENC, (drawn, y) =>
                Add(drawn, Node(GraphNodeTypes.Constant, 200, y + 40, ("value", gender + (wave?.gender ?? 0))), 740, y));
            Drive(Format.Ustx.GRWC, (drawn, y) => Add(drawn, Scale(phraseEnd, endFry, 560, y + 50), 740, y));
            Drive(Format.Ustx.VOIC, (drawn, y) => Scale(drawn, voicing / 100, 740, y));
            return graph;
        }

        /// <summary>
        /// Adds the character's graph for a renderer as one undoable step, with the intonation curve if the project
        /// lacks it. Given a track, the track uses it; otherwise it becomes the renderer's default if there is none.
        /// Returns the graph's id.
        /// </summary>
        public string AddToProject(UProject project, string renderer, int? trackNo = null, DocManager? docManager = null) {
            docManager ??= DocManager.Inst;
            var draft = new ExpressionGraphEdits.Draft(project);
            string id = draft.NewId(name);
            draft.Graphs.Add(BuildGraph(id, renderer));
            if (trackNo is int track && track >= 0 && track < draft.TrackOverrides.Count) {
                draft.TrackOverrides[track] = id;
            } else {
                draft.Defaults.TryAdd(Renderers.GetExpressionGraphSlot(renderer), id);
            }
            docManager.StartUndoGroup("command.voiceacting.character");
            try {
                if (!project.expressions.ContainsKey(IntonationCurve)) {
                    docManager.ExecuteCmd(new ConfigureExpressionsCommand(
                        project, project.expressions.Values.Append(IntonationDescriptor()).ToArray()));
                }
                docManager.ExecuteCmd(new SetExpressionGraphsCommand(project, draft.ToState()));
            } finally {
                docManager.EndUndoGroup();
            }
            return id;
        }
    }
}
