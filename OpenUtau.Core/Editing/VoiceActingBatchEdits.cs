using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using OpenUtau.Core.Ustx;
using OpenUtau.Core.VoiceActing;

namespace OpenUtau.Core.Editing {
    /// <summary>
    /// An edit from the recording under a voice part: a wave part on another track, such as the actor's take of
    /// the line. The analysis runs off the UI thread; the edit is applied on it as one undoable step.
    /// </summary>
    public abstract class VoiceActingEdit : BatchEdit {
        public abstract string Name { get; }
        public bool IsAsync => true;

        public void Run(UProject project, UVoicePart part, List<UNote> selectedNotes, DocManager docManager) {
            RunAsync(project, part, selectedNotes, docManager, (_, _) => { }, CancellationToken.None);
        }

        public void RunAsync(UProject project, UVoicePart part, List<UNote> selectedNotes, DocManager docManager,
                Action<int, int> setProgressCallback, CancellationToken cancellationToken) {
            setProgressCallback(0, 1);
            var wave = ReferenceTake.Find(project, part);
            var take = wave == null ? null : ReferenceTake.Analyze(project, part, wave, part.notes.Count == 0);
            if (take == null) {
                docManager.ExecuteCmd(new ErrorMessageNotification(new MessageCustomizableException(
                    "No recording under the part", "<translate:errors.voiceacting.norecording>",
                    new Exception(), showStackTrace: false)));
                return;
            }
            if (cancellationToken.IsCancellationRequested) {
                return;
            }
            var apply = Prepare(project, part, selectedNotes, take, docManager);
            setProgressCallback(1, 1);
            if (apply != null) {
                docManager.PostOnUIThread(apply);
            }
        }

        /// <summary>What to do on the UI thread, or null for nothing.</summary>
        protected abstract Action? Prepare(UProject project, UVoicePart part, List<UNote> selectedNotes,
            ReferenceTake take, DocManager docManager);

        protected static Action? Execute(DocManager docManager, IReadOnlyList<UCommand> commands) {
            if (commands.Count == 0) {
                return null;
            }
            return () => {
                docManager.StartUndoGroup("command.batch.note");
                try {
                    foreach (var command in commands) {
                        docManager.ExecuteCmd(command);
                    }
                } finally {
                    docManager.EndUndoGroup();
                }
            };
        }

        /// <summary>Lengthens the part to hold notes up to a part tick, if it is shorter.</summary>
        protected static void Fit(UProject project, UVoicePart part, int end, List<UCommand> commands) {
            if (end > part.Duration) {
                commands.Insert(0, new ResizeVoicePartCommand(project, part, end - part.Duration, false));
            }
        }
    }

    /// <summary>Replaces the notes under the recording with one note per spoken syllable.</summary>
    public class NotesFromRecording : VoiceActingEdit {
        public override string Name => "pianoroll.menu.notes.voiceacting.notesfromrecording";

        protected override Action? Prepare(UProject project, UVoicePart part, List<UNote> selectedNotes,
                ReferenceTake take, DocManager docManager) {
            var notes = PerformanceTransfer.CreateNotes(project, part, take);
            if (notes.Count == 0) {
                return null;
            }
            int from = notes[0].position, to = notes[^1].End;
            var replaced = part.notes.Where(n => n.End > from && n.position < to).ToList();
            var commands = new List<UCommand>();
            if (replaced.Count > 0) {
                commands.Add(new RemoveNoteCommand(part, replaced));
            }
            commands.Add(new AddNoteCommand(part, notes));
            Fit(project, part, to, commands);
            return Execute(docManager, commands);
        }
    }

    /// <summary>Moves the selected notes, or all of them, onto the recording's syllables, in order.</summary>
    public class AlignNotesToRecording : VoiceActingEdit {
        public override string Name => "pianoroll.menu.notes.voiceacting.alignnotes";

        protected override Action? Prepare(UProject project, UVoicePart part, List<UNote> selectedNotes,
                ReferenceTake take, DocManager docManager) {
            var notes = (selectedNotes.Count > 0 ? selectedNotes : part.notes.ToList()).OrderBy(n => n.position).ToList();
            var targets = PerformanceTransfer.AlignNotes(project, part, notes, take);
            var commands = new List<UCommand>();
            for (int i = 0; i < notes.Count && i < targets.Count; i++) {
                var (position, duration) = targets[i];
                if (position != notes[i].position) {
                    commands.Add(new MoveNoteCommand(part, notes[i], position - notes[i].position, 0));
                }
                if (duration != notes[i].duration) {
                    commands.Add(new ResizeNoteCommand(part, notes[i], duration - notes[i].duration));
                }
            }
            if (targets.Count > 0) {
                Fit(project, part, targets[^1].position + targets[^1].duration, commands);
            }
            return Execute(docManager, commands);
        }
    }

    /// <summary>
    /// Copies the recording's melody and loudness onto the part, moved to the notes' register: into the pitch
    /// override if the track's expression graph draws pitch there, else into PITD; and into DYN.
    /// </summary>
    public class FollowRecording : VoiceActingEdit {
        public override string Name => "pianoroll.menu.notes.voiceacting.followrecording";

        protected override Action? Prepare(UProject project, UVoicePart part, List<UNote> selectedNotes,
                ReferenceTake take, DocManager docManager) {
            bool pitchOverride = ExpressionGraph.ExpressionGraphProgram.PrefersPitchOverride(
                project, project.tracks[part.trackNo]);
            return Execute(docManager, PerformanceTransfer.FollowPitchAndDynamics(project, part, take, pitchOverride));
        }
    }

    /// <summary>
    /// Makes the track a voice character with the melody, unsteadiness and breathiness measured in the recording.
    /// </summary>
    public class CharacterFromRecording : VoiceActingEdit {
        public override string Name => "pianoroll.menu.notes.voiceacting.characterfromrecording";

        protected override Action? Prepare(UProject project, UVoicePart part, List<UNote> selectedNotes,
                ReferenceTake take, DocManager docManager) {
            var profile = VoiceProfile.Of(take.Analysis);
            string? renderer = project.tracks[part.trackNo].RendererSettings?.renderer;
            if (profile == null || string.IsNullOrEmpty(renderer)) {
                return null;
            }
            var character = VoiceCharacter.Preset("neutral").FittedTo(profile);
            character.name = System.IO.Path.GetFileNameWithoutExtension(take.Wave.name ?? "recording");
            return () => character.AddToProject(project, renderer, part.trackNo, docManager);
        }
    }
}
