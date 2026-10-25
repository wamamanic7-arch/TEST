using System;
using System.Collections.Generic;
using System.Linq;
using OpenUtau.Core.Editing;
using OpenUtau.Core.ExpressionGraph;
using OpenUtau.Core.Render;
using OpenUtau.Core.Ustx;
using Xunit;

namespace OpenUtau.Core.VoiceActing {
    [Collection(RenderSingletonCollection.Name)]
    public class TrackVoiceTest : IDisposable {
        readonly UProject previousProject;
        readonly UProject project;

        public TrackVoiceTest() {
            project = new UProject();
            Format.Ustx.AddDefaultExpressions(project);
            project.tracks.Add(new UTrack(project));
            foreach (var track in project.tracks) {
                track.RendererSettings.renderer = Renderers.CLASSIC;
            }
            previousProject = DocManager.Inst.TakeProjectForTest(project);
            DocManager.Inst.CommandSink = cmd => cmd.Execute();
        }

        public void Dispose() {
            DocManager.Inst.CommandSink = null!;
            DocManager.Inst.TakeProjectForTest(previousProject);
        }

        static VoiceCharacter Wavy(string emotion) {
            var character = VoiceCharacter.Preset(emotion);
            character.wave = new WaveVoice { hopSemitones = 7, seed = 42 };
            return character;
        }

        [Fact]
        public void ACharacterLivesInItsGraphAndIsSavedWithTheProject() {
            TrackVoice.With(project, 0, Wavy("cheerful"))!.ApplyTo(project);
            project.tracks[0].VoiceFilter = UVoiceFilter.FromPreset("old_tv");
            Assert.True(TrackVoice.IsActive(project, project.tracks[0]));
            Assert.False(TrackVoice.IsActive(project, project.tracks[1]));

            var read = Yaml.DefaultDeserializer.Deserialize<UProject>(Yaml.DefaultSerializer.Serialize(project));
            read.tracks[0].RendererSettings.renderer = Renderers.CLASSIC;
            var character = TrackVoice.CharacterOf(read, read.tracks[0])!;
            Assert.Equal("cheerful", character.emotion);
            Assert.Equal(VoiceCharacter.Preset("cheerful").pitchOffset, character.pitchOffset);
            Assert.Equal(7, character.wave!.hopSemitones);
            Assert.Equal(42, character.wave.seed);
            Assert.True(read.tracks[0].VoiceFilter!.TvSpeaker);
            Assert.True(read.tracks[0].VoiceFilter!.SameSound(project.tracks[0].VoiceFilter!));
            Assert.Null(read.tracks[1].VoiceFilter);
        }

        [Fact]
        public void EditingRebuildsTheTracksOwnGraphInPlace() {
            TrackVoice.With(project, 0, VoiceCharacter.Preset("gloomy"))!.ApplyTo(project);
            string id = project.tracks[0].ExpressionGraph!;
            var changed = Wavy("angry");
            TrackVoice.With(project, 0, changed)!.ApplyTo(project);
            Assert.Equal(id, project.tracks[0].ExpressionGraph);
            Assert.Single(project.expressionGraphs!);
            Assert.Equal("angry", TrackVoice.CharacterOf(project, project.tracks[0])!.emotion);
            Assert.Contains(project.expressionGraphs!.Single().nodes, n => n.type == GraphNodeTypes.NoteRandom);
        }

        [Fact]
        public void AGraphSharedWithAnotherTrackIsLeftToIt() {
            TrackVoice.With(project, 0, VoiceCharacter.Preset("timid"))!.ApplyTo(project);
            string shared = project.tracks[0].ExpressionGraph!;
            project.tracks[1].ExpressionGraph = shared;
            TrackVoice.With(project, 0, VoiceCharacter.Preset("elderly"))!.ApplyTo(project);
            Assert.NotEqual(shared, project.tracks[0].ExpressionGraph);
            Assert.Equal("timid", TrackVoice.CharacterOf(project, project.tracks[1])!.emotion);
            Assert.Equal("elderly", TrackVoice.CharacterOf(project, project.tracks[0])!.emotion);

            // Now only track 1 uses the first graph: turning its character off removes it, and only it.
            TrackVoice.With(project, 1, null)!.ApplyTo(project);
            Assert.Null(project.tracks[1].ExpressionGraph);
            Assert.Equal(project.tracks[0].ExpressionGraph, project.expressionGraphs!.Single().id);
        }

        [Fact]
        public void TurningTheCharacterOffRemovesItsGraph() {
            TrackVoice.With(project, 0, VoiceCharacter.Plain())!.ApplyTo(project);
            Assert.Equal(VoiceCharacter.NoEmotion, TrackVoice.CharacterOf(project, project.tracks[0])!.emotion);
            TrackVoice.With(project, 0, null)!.ApplyTo(project);
            Assert.Null(project.tracks[0].ExpressionGraph);
            Assert.Null(project.expressionGraphs);
            Assert.Null(TrackVoice.CharacterOf(project, project.tracks[0]));
            Assert.False(TrackVoice.IsActive(project, project.tracks[0]));
        }

        [Fact]
        public void ATrackWithoutARendererCannotHaveACharacter() {
            project.tracks[1].RendererSettings.renderer = null;
            Assert.Null(TrackVoice.With(project, 1, VoiceCharacter.Preset("neutral")));
            Assert.NotNull(TrackVoice.With(project, 1, null));
        }

        [Fact]
        public void APlainVoiceIsTheNotesAsDrawn() {
            var plain = VoiceCharacter.Plain();
            Assert.Equal(0, plain.declination + plain.finalDepth + plain.syllableArc + plain.driftDepth
                + plain.jitterDepth + plain.shimmerDepth + plain.endDecay + plain.endBreath);
            Assert.Equal(VoiceCharacter.NoEmotion, VoiceCharacter.Preset(VoiceCharacter.NoEmotion).emotion);
        }

        [Fact]
        public void SettingTheFilterUndoes() {
            var track = project.tracks[0];
            var before = UVoiceFilter.FromPreset("clean");
            track.VoiceFilter = before;
            var command = new SetVoiceFilterCommand(track, before, UVoiceFilter.FromPreset("echo"));
            command.Execute();
            Assert.Equal(UVoiceFilter.SpaceEcho, track.VoiceFilter!.Reverb);
            command.Unexecute();
            Assert.True(track.VoiceFilter!.SameSound(before));
            Assert.NotSame(before, track.VoiceFilter);
            new SetVoiceFilterCommand(track, track.VoiceFilter, null).Execute();
            Assert.Null(track.VoiceFilter);
        }

        [Fact]
        public void PresetsAreEnabledAndDistinct() {
            var presets = UVoiceFilter.PresetNames.Select(UVoiceFilter.FromPreset).ToList();
            Assert.All(presets, p => Assert.True(p.Enabled));
            for (int i = 0; i < presets.Count; i++) {
                for (int j = i + 1; j < presets.Count; j++) {
                    Assert.False(presets[i].SameSound(presets[j]));
                }
            }
            Assert.Equal("classic", UVoiceFilter.FromPreset("nonsense").Preset);
        }

        [Fact]
        public void TheRhythmCoversEveryPartOfTheTrack() {
            UVoicePart Part(int trackNo, int position, params int[] notes) {
                var part = new UVoicePart { trackNo = trackNo, position = position, Duration = 3840 };
                foreach (int n in notes) {
                    part.notes.Add(project.CreateNote(60, n, 100));
                }
                project.parts.Add(part);
                return part;
            }
            var first = Part(0, 0, 10, 130, 250);
            var second = Part(0, 1920, 0, 100);
            var other = Part(1, 0, 10, 130);
            WaveVoiceRhythm.RunOnTrack(project, 0, 8, DocManager.Inst);
            Assert.Equal(new[] { 0, 240, 480 }, first.notes.Select(n => n.position));
            Assert.Equal(new[] { 0, 240 }, second.notes.Select(n => n.position));
            Assert.All(first.notes.Concat(second.notes), n => Assert.Equal(240, n.duration));
            Assert.Equal(new[] { 10, 130 }, other.notes.Select(n => n.position));
        }
    }
}
