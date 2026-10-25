using System.Collections.Generic;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using OpenUtau.App.ViewModels;
using OpenUtau.App.Views;
using OpenUtau.Core;
using OpenUtau.Core.Ustx;
using OpenUtau.Core.VoiceActing;
using Xunit;

namespace OpenUtau.UiTest {
    public class VoiceActingDialogTest {
        // A singer gives the track a renderer, which characters need; with no notes, nothing renders.
        class FakeSinger : USinger {
            public FakeSinger() {
                found = true;
            }
            public override string Id => "voice-acting-test";
            public override string Name => "Voice Acting Test";
            public override USingerType SingerType => USingerType.Classic;
        }

        static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

        [Fact]
        public void PreviewsKeepsAndUndoesInOneStep() => HeadlessUi.Run(() => {
            MainWindowTest.InitCore();
            HeadlessUi.Errors.Clear();
            var original = DocManager.Inst.Project;
            var project = Core.Format.Ustx.Create();
            var track = project.tracks[0];
            track.Singer = new FakeSinger();
            DocManager.Inst.ExecuteCmd(new LoadProjectNotification(project));
            project.ValidateFull();
            var owner = new Window { Width = 200, Height = 200 };
            owner.Show();
            try {
                Assert.False(string.IsNullOrEmpty(track.RendererSettings.renderer));

                // Cancel puts everything back, though it was heard.
                var dialog = VoiceActingDialog.Open(owner, track);
                HeadlessUi.Flush();
                var vm = dialog.ViewModel;
                Assert.True(vm.CanSpeak);
                Assert.Equal(VoiceActingViewModel.CharacterPage, vm.Page);
                Assert.False(vm.SpeechOn);
                HeadlessUi.SaveScreenshot(dialog, "VoiceActingCharacter");
                // Picking an emotion turns the character on.
                dialog.FindControl<ListBox>("EmotionTiles")!.SelectedIndex = 1;
                Assert.True(vm.SpeechOn);
                vm.PreviewCharacter();
                Assert.Equal("cheerful", TrackVoice.CharacterOf(project, track)!.emotion);
                vm.Page = VoiceActingViewModel.FilterPage;
                HeadlessUi.Flush();
                dialog.FindControl<ListBox>("FilterTiles")!.SelectedIndex = 3;
                Assert.True(vm.FilterOn);
                Assert.True(track.VoiceFilter!.Enabled);
                Assert.True(track.VoiceFilter.TvSpeaker);
                HeadlessUi.SaveScreenshot(dialog, "VoiceActingFilter");
                Click(dialog.FindControl<Button>("CancelButton")!);
                HeadlessUi.Flush();
                Assert.Null(track.VoiceFilter);
                Assert.Null(TrackVoice.CharacterOf(project, track));
                Assert.Null(project.expressionGraphs);

                // OK keeps it all, as one step to undo.
                dialog = VoiceActingDialog.Open(owner, track, VoiceActingViewModel.WaveVoicePage);
                HeadlessUi.Flush();
                vm = dialog.ViewModel;
                dialog.FindControl<ToggleSwitch>("WaveSwitch")!.IsChecked = true;
                Assert.True(vm.WaveOn);
                Assert.False(vm.SpeechOn);
                vm.Jumps = 6;
                vm.Height = 5;
                HeadlessUi.SaveScreenshot(dialog, "VoiceActingWaveVoice");
                vm.Page = VoiceActingViewModel.FilterPage;
                dialog.FindControl<ToggleSwitch>("FilterSwitch")!.IsChecked = true;
                Click(dialog.FindControl<Button>("OkButton")!);
                HeadlessUi.Flush();
                var kept = TrackVoice.CharacterOf(project, track)!;
                Assert.Equal(VoiceCharacter.NoEmotion, kept.emotion);
                Assert.Equal(6, kept.wave!.hopSemitones);
                Assert.Equal(500, kept.wave.lift);
                Assert.True(track.VoiceFilter!.Enabled);
                Assert.True(project.expressions.ContainsKey(VoiceCharacter.IntonationCurve));
                Assert.True(TrackVoice.IsActive(project, track));

                DocManager.Inst.Undo();
                Assert.Null(TrackVoice.CharacterOf(project, track));
                Assert.Null(track.VoiceFilter);
                DocManager.Inst.Redo();
                Assert.Equal(6, TrackVoice.CharacterOf(project, track)!.wave!.hopSemitones);
                Assert.True(track.VoiceFilter!.Enabled);

                // Reopened, the panel shows what the track has.
                dialog = VoiceActingDialog.Open(owner, track);
                HeadlessUi.Flush();
                vm = dialog.ViewModel;
                Assert.Equal(VoiceActingViewModel.WaveVoicePage, vm.Page);
                Assert.True(vm.WaveOn);
                Assert.Equal(6, vm.Jumps);
                Assert.True(vm.FilterOn);
                Assert.Equal("classic", vm.SelectedFilterPreset!.Key);
                Click(dialog.FindControl<Button>("CancelButton")!);
                HeadlessUi.Flush();
                Assert.Equal(6, TrackVoice.CharacterOf(project, track)!.wave!.hopSemitones);

                var errors = HeadlessUi.Errors.Snapshot();
                Assert.True(errors.Count == 0, string.Join("\n", errors));
            } finally {
                owner.Close();
                DocManager.Inst.ExecuteCmd(new LoadProjectNotification(original));
            }
        });

        [Fact]
        public void ATrackWithoutASingerStillGetsTheFilter() => HeadlessUi.Run(() => {
            MainWindowTest.InitCore();
            HeadlessUi.Errors.Clear();
            var original = DocManager.Inst.Project;
            var project = Core.Format.Ustx.Create();
            DocManager.Inst.ExecuteCmd(new LoadProjectNotification(project));
            var owner = new Window { Width = 200, Height = 200 };
            owner.Show();
            try {
                var dialog = VoiceActingDialog.Open(owner, project.tracks[0]);
                HeadlessUi.Flush();
                var vm = dialog.ViewModel;
                Assert.False(vm.CanSpeak);
                Assert.Equal(VoiceActingViewModel.FilterPage, vm.Page);
                vm.Page = VoiceActingViewModel.CharacterPage;
                HeadlessUi.Flush();
                HeadlessUi.SaveScreenshot(dialog, "VoiceActingNoSinger");
                vm.Page = VoiceActingViewModel.FilterPage;
                dialog.FindControl<ListBox>("FilterTiles")!.SelectedIndex = 2;
                Click(dialog.FindControl<Button>("OkButton")!);
                HeadlessUi.Flush();
                Assert.Equal(8000, project.tracks[0].VoiceFilter!.SampleRate);
                Assert.Null(project.expressionGraphs);
                var errors = HeadlessUi.Errors.Snapshot();
                Assert.True(errors.Count == 0, string.Join("\n", errors));
            } finally {
                owner.Close();
                DocManager.Inst.ExecuteCmd(new LoadProjectNotification(original));
            }
        });
    }
}
