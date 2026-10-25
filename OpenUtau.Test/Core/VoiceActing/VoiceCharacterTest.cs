using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using OpenUtau.Core.ExpressionGraph;
using OpenUtau.Core.Pipeline;
using OpenUtau.Core.Render;
using OpenUtau.Core.Ustx;
using Xunit;

namespace OpenUtau.Core.VoiceActing {
    /// <summary>The voice acting nodes, and characters built from them.</summary>
    [Collection(RenderSingletonCollection.Name)]
    public class VoiceCharacterTest : IDisposable {
        const string Renderer = "TEST";
        readonly UProject previousProject;

        public VoiceCharacterTest() {
            var project = new UProject();
            Format.Ustx.AddDefaultExpressions(project);
            previousProject = DocManager.Inst.TakeProjectForTest(project);
            DocManager.Inst.CommandSink = cmd => cmd.Execute();
        }

        public void Dispose() {
            DocManager.Inst.CommandSink = null!;
            DocManager.Inst.TakeProjectForTest(previousProject);
        }

        sealed class TestRenderer : IRenderer {
            readonly Func<string, bool> supports;
            public TestRenderer(bool rendersPitch = false, Func<string, bool>? supports = null) {
                SupportsRenderPitch = rendersPitch;
                this.supports = supports ?? (_ => true);
            }
            public USingerType SingerType => USingerType.Classic;
            public bool SupportsRenderPitch { get; }
            public bool SupportsExpression(UExpressionDescriptor descriptor) => supports(descriptor.abbr);
            public RenderResult Layout(RenderPhrase phrase) => new RenderResult();
            public Task<RenderResult> Render(RenderPhrase phrase, Progress progress, int trackNo,
                    System.Threading.CancellationTokenSource cancellation, bool isPreRender = false,
                    RenderPhraseEvents? renderEvents = null) => throw new NotImplementedException();
            public RenderPitchResult LoadRenderedPitch(RenderPhrase phrase) => null!;
            public UExpressionDescriptor[] GetSuggestedExpressions(USinger singer, URenderSettings renderSettings) =>
                Array.Empty<UExpressionDescriptor>();
        }

        static UExpressionGraph Graph(params (string type, (string key, string value)[] parameters)[] nodes) {
            var graph = new UExpressionGraph { id = "g", name = "G", renderer = Renderer };
            foreach (var (type, parameters) in nodes) {
                var node = ExpressionGraphEdits.AddNode(graph, type, 0, 0);
                foreach (var (key, value) in parameters) {
                    node.Set(key, value);
                }
            }
            return graph;
        }

        /// <summary>
        /// The pipeline fixture: a phrase of notes from 0 to 960 (one second at 120 BPM) and another from 1200
        /// to 1920.
        /// </summary>
        static (UProject project, UTrack track, UVoicePart part) Fixture() {
            var (project, track, part) = PhraseSourceHashTest.BuildFixture();
            track.RendererSettings.renderer = Renderer;
            return (project, track, part);
        }

        static Dictionary<string, float[]> Evaluate(UExpressionGraph graph, int[] ticks, int? phraseStartGroup = null) {
            var (project, track, part) = Fixture();
            var program = ExpressionGraphProgram.Compile(graph, out var error);
            Assert.True(program != null, error);
            var source = PhraseSource.FromPart(project, track, part, 0);
            var context = phraseStartGroup is int g
                ? new GraphContext(source, phraseStart: source.PhraseGroups[g].Start)
                : new GraphContext(source);
            return program!.Evaluate(context, ticks);
        }

        static int[] Grid(int from, int to) =>
            Enumerable.Range(0, (to - from) / UCurve.interval + 1).Select(i => from + i * UCurve.interval).ToArray();

        [Fact]
        public void PhrasePositionRunsAcrossEachPhraseByTime() {
            var graph = Graph(
                (GraphNodeTypes.PhrasePosition, new (string, string)[0]),
                (GraphNodeTypes.CurveOutput, new[] { ("abbr", "tenc") }),
                (GraphNodeTypes.CurveOutput, new[] { ("abbr", "cstm") }));
            ExpressionGraphEdits.TryLink(graph, 1, 2, GraphNodeTypes.Value, "position");
            ExpressionGraphEdits.TryLink(graph, 1, 3, GraphNodeTypes.Value, "remaining");
            var ticks = new[] { 0, 480, 1200, 1560, 1920 };
            var result = Evaluate(graph, ticks);
            Assert.Equal(new[] { 0, 0.5, 0, 0.5, 1 }, result["tenc"].Select(x => Math.Round(x, 3)));
            // 960 ticks at 120 BPM are a second; the second phrase is 720 ticks.
            Assert.Equal(new[] { 1, 0.5, 0.75, 0.375, 0 }, result["cstm"].Select(x => Math.Round(x, 3)));
        }

        [Fact]
        public void ATickBetweenPhrasesBelongsToThePhraseBeingBuilt() {
            var graph = Graph(
                (GraphNodeTypes.PhrasePosition, new (string, string)[0]),
                (GraphNodeTypes.CurveOutput, new[] { ("abbr", "tenc") }));
            ExpressionGraphEdits.TryLink(graph, 1, 2, GraphNodeTypes.Value, "position");
            // 1100 is in the gap, nearer the second phrase; while building the first, it is past its end.
            Assert.Equal(1, Evaluate(graph, new[] { 1100 }, phraseStartGroup: 0)["tenc"][0]);
            Assert.Equal(0, Evaluate(graph, new[] { 1100 }, phraseStartGroup: 1)["tenc"][0]);
            Assert.Equal(0, Evaluate(graph, new[] { 1100 })["tenc"][0]);
        }

        [Theory]
        [InlineData(-300)]
        [InlineData(300)]
        public void IntonationDeclinesAndBendsAtThePhraseEnd(float final) {
            var graph = Graph(
                (GraphNodeTypes.Intonation, new[] {
                    ("declination", "200"), ("final", final.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                    ("arc", "0"), ("final_ms", "400"),
                }),
                (GraphNodeTypes.CurveOutput, new[] { ("abbr", "tenc") }));
            ExpressionGraphEdits.TryLink(graph, 1, 2, GraphNodeTypes.Value);
            var ticks = Grid(0, 960);
            var values = Evaluate(graph, ticks)["tenc"];
            Assert.Equal(100, values[0], 3);
            Assert.Equal(0, values[ticks.Length / 2], 1);
            Assert.Equal(-100 + final, values[^1], 1);
            // Until the ending, only the declination moves it.
            int endingStart = Array.FindIndex(ticks, t => t >= 960 - 384);
            for (int i = 1; i < endingStart; i++) {
                Assert.True(values[i] < values[i - 1]);
            }
        }

        [Fact]
        public void SyllableArcsAreCentredOnEachNote() {
            var graph = Graph(
                (GraphNodeTypes.Intonation, new[] { ("declination", "0"), ("final", "0"), ("arc", "40") }),
                (GraphNodeTypes.CurveOutput, new[] { ("abbr", "tenc") }));
            ExpressionGraphEdits.TryLink(graph, 1, 2, GraphNodeTypes.Value);
            var values = Evaluate(graph, Grid(0, 475))["tenc"];
            Assert.Equal(-40 * 2 / Math.PI, values[0], 2);
            Assert.Equal(40 * (1 - 2 / Math.PI), values[48], 2);
            Assert.InRange(values.Average(), -1, 1);
        }

        [Fact]
        public void FluctuationIsBoundedSmoothAndRepeatable() {
            UExpressionGraph Noise(string seed) {
                var graph = Graph(
                    (GraphNodeTypes.Fluctuation, new[] { ("depth", "20"), ("rate", "4"), ("octaves", "3"), ("seed", seed) }),
                    (GraphNodeTypes.CurveOutput, new[] { ("abbr", "tenc") }));
                ExpressionGraphEdits.TryLink(graph, 1, 2, GraphNodeTypes.Value);
                return graph;
            }
            var ticks = Grid(0, 4800);
            var values = Evaluate(Noise("0"), ticks)["tenc"];
            Assert.All(values, v => Assert.InRange(v, -20, 20));
            double mean = values.Average();
            Assert.InRange(Math.Sqrt(values.Average(v => (v - mean) * (v - mean))), 3, 15);
            for (int i = 1; i < values.Length; i++) {
                Assert.InRange(Math.Abs(values[i] - values[i - 1]), 0, 6);
            }
            Assert.Equal(values, Evaluate(Noise("0"), ticks)["tenc"]);
            Assert.Equal(values.Skip(100), Evaluate(Noise("0"), ticks.Skip(100).ToArray())["tenc"]);
            Assert.NotEqual(values, Evaluate(Noise("7"), ticks)["tenc"]);
        }

        static readonly string[] AllCurves = {
            Format.Ustx.DYN, Format.Ustx.BREC, Format.Ustx.TENC, Format.Ustx.GENC, Format.Ustx.GRWC, Format.Ustx.VOIC,
        };

        public static IEnumerable<object[]> Presets => VoiceCharacter.PresetNames.Select(p => new object[] { p });

        [Theory]
        [MemberData(nameof(Presets))]
        public void EveryPresetCompilesAndDrivesPitchAndWhatTheRendererSupports(string preset) {
            var character = VoiceCharacter.Preset(preset);
            foreach (bool rendersPitch in new[] { false, true }) {
                var graph = character.BuildGraph("c", Renderer, new TestRenderer(rendersPitch));
                var program = ExpressionGraphProgram.Compile(graph, out var error);
                Assert.True(program != null, error);
                Assert.True(program!.DrivesPitch);
                Assert.Equal(Format.Ustx.PITO, graph.preferredPitchCurve);
                Assert.Equal(AllCurves.OrderBy(a => a), program.CurveOutputs.OrderBy(a => a));
            }
            var only = character.BuildGraph("c", Renderer, new TestRenderer(supports: abbr => abbr == Format.Ustx.DYN));
            Assert.Equal(new[] { Format.Ustx.DYN }, ExpressionGraphProgram.Compile(only, out _)!.CurveOutputs);
        }

        [Fact]
        public void ClassicCharactersGrowlButWorldlineOnesDoNot() {
            var character = VoiceCharacter.Preset("angry");
            Assert.True(ExpressionGraphProgram.Compile(character.BuildGraph("a", Renderers.CLASSIC), out _)!
                .DrivesCurve(Format.Ustx.GRWC));
            Assert.False(ExpressionGraphProgram.Compile(character.BuildGraph("b", Renderers.WORLDLINE_R), out _)!
                .DrivesCurve(Format.Ustx.GRWC));
        }

        /// <summary>The fixture rendering with a character graph, with the pitch override set over 0 to 200.</summary>
        static (PhraseSource source, ExpressionGraphProgram program) CharacterFixture(VoiceCharacter character,
                Action<UProject, UVoicePart>? setup = null) {
            var (project, track, part) = Fixture();
            project.RegisterExpression(new UExpressionDescriptor("pitch override", Format.Ustx.PITO, 2400, 10800, 6000) {
                type = UExpressionType.MaskedCurve,
            });
            project.RegisterExpression(VoiceCharacter.IntonationDescriptor());
            project.RegisterExpression(new UExpressionDescriptor("breathiness (curve)", Format.Ustx.BREC, -100, 100, 0) {
                type = UExpressionType.Curve,
            });
            var graph = character.BuildGraph("character", Renderer, new TestRenderer());
            project.expressionGraphs = new List<UExpressionGraph> { graph };
            track.ExpressionGraph = graph.id;
            var pito = new UMaskedCurve(Format.Ustx.PITO);
            pito.Set(0, 6500, 200, 6500);
            part.maskedCurves.Add(pito);
            setup?.Invoke(project, part);
            var source = PhraseSource.FromPart(project, track, part, 0);
            Assert.NotNull(source.ExpressionGraph);
            return (source, source.ExpressionGraph!);
        }

        [Fact]
        public void CharacterSpeaksAroundTheDrawnPitchUnlessOverridden() {
            var character = VoiceCharacter.Preset("neutral");
            character.pitchOffset = 100;
            var (source, program) = CharacterFixture(character);
            var ticks = Grid(0, 955);
            var flat = Enumerable.Repeat(6000f, ticks.Length).ToArray();
            var zero = new float[ticks.Length];
            var pitch = new PhrasePitch(0, UCurve.interval, flat, zero, zero, flat);
            var context = new GraphContext(source, pitch, source.PhraseGroups[0].Start);
            var values = program.EvaluatePitch(context, ticks)!;

            // The pitch override wins where it has a value.
            Assert.All(values.Take(41), v => Assert.Equal(6500, v));
            // Elsewhere: the notes and PITD, plus the offset, the melody and the unsteadiness.
            double bound = character.declination / 2 + character.finalDepth + character.syllableArc
                + character.driftDepth + character.jitterDepth;
            for (int i = 41; i < ticks.Length; i++) {
                double drawn = 6000 + context.SampleCurve(Format.Ustx.PITD, ticks[i]) + character.pitchOffset;
                Assert.InRange(values[i] - drawn, -bound, bound);
            }
            Assert.True(values.Skip(41).Distinct().Count() > 100);
            // The statement ending falls.
            Assert.True(values[^1] < values[ticks.Length / 2] - 100);
        }

        [Fact]
        public void CharacterShapesTheVoiceTowardsThePhraseEnd() {
            var character = VoiceCharacter.Preset("neutral");
            var (source, program) = CharacterFixture(character);
            var context = new GraphContext(source, phraseStart: source.PhraseGroups[0].Start);
            var curves = program.Evaluate(context, new[] { 300, 955 });
            // Breath only rises at the end; loudness falls there, around the drawn DYN, by up to the shimmer.
            Assert.Equal(0, curves[Format.Ustx.BREC][0], 3);
            Assert.InRange(curves[Format.Ustx.BREC][1], 19, 20);
            double drawnEnd = context.SampleCurve(Format.Ustx.DYN, 955);
            Assert.InRange(curves[Format.Ustx.DYN][1] - drawnEnd,
                -character.endDecay - character.shimmerDepth, -0.95 * character.endDecay + character.shimmerDepth);
            Assert.InRange(curves[Format.Ustx.DYN][0] - context.SampleCurve(Format.Ustx.DYN, 300),
                -character.shimmerDepth, character.shimmerDepth);
        }

        [Fact]
        public void TheIntonationCurveTurnsAStatementIntoAQuestion() {
            var character = VoiceCharacter.Preset("neutral");
            character.driftDepth = character.jitterDepth = 0;
            float EndPitch(int intonation) {
                var (source, program) = CharacterFixture(character, (project, part) => {
                    var intc = new UCurve(project.expressions[VoiceCharacter.IntonationCurve]);
                    intc.xs.AddRange(new[] { 0, 1920 });
                    intc.ys.AddRange(new[] { intonation, intonation });
                    part.curves.Add(intc);
                });
                var flat = new[] { 6000f };
                var pitch = new PhrasePitch(955, UCurve.interval, flat, new float[1], new float[1], flat);
                var context = new GraphContext(source, pitch, source.PhraseGroups[0].Start);
                return program.EvaluatePitch(context, new[] { 955 })![0];
            }
            // 5 ticks before the end of a 400 ms ending: the ending is 1 - 5.2 / 400 of the way through.
            double ending = Math.Pow(1 - 5 * 500.0 / 480 / 400, 2);
            Assert.InRange(EndPitch(50) - EndPitch(0), character.finalDepth * ending - 0.1, character.finalDepth * ending + 0.1);
            Assert.InRange(EndPitch(100) - EndPitch(0), 2 * character.finalDepth * ending - 0.1, 2 * character.finalDepth * ending + 0.1);
            Assert.True(EndPitch(100) > EndPitch(-100) + 4 * character.finalDepth * ending - 1);
        }

        [Fact]
        public void AddingACharacterToATrackRegistersItsCurveAndUsesIt() {
            var project = DocManager.Inst.Project;
            project.tracks[0].RendererSettings.renderer = Renderers.CLASSIC;
            var character = VoiceCharacter.Preset("timid");
            character.name = "Mika";
            string id = character.AddToProject(project, Renderers.CLASSIC, 0, DocManager.Inst);

            Assert.True(project.expressions.ContainsKey(VoiceCharacter.IntonationCurve));
            Assert.Equal(id, project.tracks[0].ExpressionGraph);
            Assert.Null(project.defaultExpressionGraphs);
            Assert.Equal("Mika", project.expressionGraphs!.Single().name);
            // Validation clears the renderer of a track without a singer, as this one is.
            project.tracks[0].RendererSettings.renderer = Renderers.CLASSIC;
            Assert.Same(project.expressionGraphs!.Single(),
                ExpressionGraphProgram.GetEffectiveGraph(project, project.tracks[0]));

            string other = VoiceCharacter.Preset("angry").AddToProject(project, Renderers.CLASSIC, null, DocManager.Inst);
            Assert.NotEqual(id, other);
            Assert.Equal(other, project.defaultExpressionGraphs![Renderers.CLASSIC]);
        }
    }
}
