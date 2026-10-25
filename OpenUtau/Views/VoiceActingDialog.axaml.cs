using System;
using System.Collections.Generic;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using OpenUtau.App.ViewModels;
using OpenUtau.Core;
using OpenUtau.Core.Ustx;
using ReactiveUI;
using ReactiveUI.Primitives;

namespace OpenUtau.App.Views {
    /// <summary>
    /// A track's Voice Acting panel. Like Track Polish, it doesn't block the main window, every edit is heard right
    /// away, OK keeps the edits and anything else puts the track back. One window per track.
    /// </summary>
    public partial class VoiceActingDialog : Window, ICmdSubscriber {
        static readonly Dictionary<UTrack, VoiceActingDialog> open = new Dictionary<UTrack, VoiceActingDialog>();

        readonly VoiceActingViewModel viewModel;
        readonly UTrack? track;
        // Character edits re-render the track, so they are previewed once the sliders settle.
        readonly DispatcherTimer previewTimer;
        bool revertOnClose = true;

        public VoiceActingDialog() : this(null) { }

        public VoiceActingDialog(UTrack? track) {
            InitializeComponent();
            this.track = track;
            DataContext = viewModel = track == null
                ? new VoiceActingViewModel()
                : new VoiceActingViewModel(DocManager.Inst.Project, track, DocManager.Inst);
            previewTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(350), DispatcherPriority.Normal, (_, _) => {
                previewTimer!.Stop();
                viewModel.PreviewCharacter();
                NotifyTrackHeader();
            });
            viewModel.CharacterChanged += () => {
                previewTimer.Stop();
                previewTimer.Start();
            };
            viewModel.WhenAnyValue(x => x.FilterOn).Subscribe(_ => NotifyTrackHeader());
            if (track != null) {
                DocManager.Inst.AddSubscriber(this);
            }
        }

        public VoiceActingViewModel ViewModel => viewModel;

        /// <summary>Opens the panel for a track, or brings its open one to the front, on a tab if given.</summary>
        public static VoiceActingDialog Open(Window owner, UTrack track, int? page = null) {
            if (!open.TryGetValue(track, out var dialog)) {
                dialog = new VoiceActingDialog(track);
                open[track] = dialog;
                dialog.Show(owner);
            } else {
                dialog.Activate();
            }
            if (page != null) {
                dialog.viewModel.Page = page.Value;
            }
            return dialog;
        }

        protected override void OnClosed(EventArgs e) {
            base.OnClosed(e);
            previewTimer.Stop();
            if (track == null) {
                return;
            }
            DocManager.Inst.RemoveSubscriber(this);
            if (open.TryGetValue(track, out var dialog) && dialog == this) {
                open.Remove(track);
            }
            if (revertOnClose) {
                viewModel.Revert();
            }
            NotifyTrackHeader();
        }

        /// <summary>
        /// Closes, keeping the track as it is, when another project is loaded or the track is removed. Follows
        /// renames.
        /// </summary>
        public void OnNext(UCommand cmd, bool isUndo) {
            if (track == null || !(cmd is TrackCommand || cmd is LoadProjectNotification)) {
                return;
            }
            Dispatcher.UIThread.Post(() => {
                if (!open.TryGetValue(track, out var dialog) || dialog != this) {
                    return;
                }
                if (cmd is LoadProjectNotification || !DocManager.Inst.Project.tracks.Contains(track)) {
                    revertOnClose = false;
                    Close();
                } else {
                    viewModel.TrackName = track.TrackName;
                }
            });
        }

        void NotifyTrackHeader() {
            if (track != null) {
                MessageBus.Current.SendMessage(new MixFxChangedNotification(track.TrackNo));
            }
        }

        void OnOkClicked(object? sender, RoutedEventArgs e) {
            previewTimer.Stop();
            revertOnClose = false;
            viewModel.Apply();
            Close();
        }

        void OnCancelClicked(object? sender, RoutedEventArgs e) {
            Close();
        }
    }
}
