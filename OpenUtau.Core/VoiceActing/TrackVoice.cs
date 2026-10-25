using System.Linq;
using OpenUtau.Core.ExpressionGraph;
using OpenUtau.Core.Ustx;

namespace OpenUtau.Core.VoiceActing {
    /// <summary>A track's voice acting: its voice character, kept in an expression graph, and its voice filter.</summary>
    public static class TrackVoice {
        /// <summary>A copy of the character the track speaks as, or null if its graph isn't a voice character.</summary>
        public static VoiceCharacter? CharacterOf(UProject project, UTrack track) =>
            ExpressionGraphProgram.GetEffectiveGraph(project, track)?.character?.Clone();

        /// <summary>Whether the track uses voice acting: a character or the voice filter.</summary>
        public static bool IsActive(UProject project, UTrack track) =>
            track.VoiceFilter?.Enabled == true || ExpressionGraphProgram.GetEffectiveGraph(project, track)?.character != null;

        /// <summary>
        /// The project's expression graphs with the track speaking as a character, or, given null, without one.
        /// The track's own character graph, used by no other track, is rebuilt in place; otherwise the track gets a
        /// new one. Without a character, the track's own character graph is removed. Null when the track has no
        /// renderer to build a graph for.
        /// </summary>
        public static ExpressionGraphState? With(UProject project, int trackNo, VoiceCharacter? character) {
            var draft = new ExpressionGraphEdits.Draft(project);
            string? current = draft.TrackOverrides[trackNo];
            var currentGraph = current == null ? null : draft.Find(current);
            bool own = currentGraph?.character != null
                && draft.TrackOverrides.Where((id, i) => i != trackNo && id == current).Count() == 0
                && !draft.Defaults.ContainsValue(current!);
            if (character == null) {
                if (currentGraph?.character != null) {
                    draft.TrackOverrides[trackNo] = null;
                    if (own) {
                        draft.Remove(current!);
                    }
                }
                return draft.ToState();
            }
            string? renderer = project.tracks[trackNo].RendererSettings?.renderer;
            if (string.IsNullOrEmpty(renderer)) {
                return null;
            }
            string id = own ? current! : draft.NewId(string.IsNullOrEmpty(character.name) ? "character" : character.name);
            var graph = character.BuildGraph(id, renderer);
            int index = draft.Graphs.FindIndex(g => g.id == id);
            if (index >= 0) {
                draft.Graphs[index] = graph;
            } else {
                draft.Graphs.Add(graph);
            }
            draft.TrackOverrides[trackNo] = id;
            return draft.ToState();
        }
    }

    /// <summary>Sets a track's voice filter, undoably. Changes only what the track's voice is filtered with, after rendering.</summary>
    public class SetVoiceFilterCommand : UCommand {
        public readonly UTrack Track;
        readonly UVoiceFilter? before;
        readonly UVoiceFilter? after;

        /// <param name="before">What to restore on undo: the track's filter before it was edited.</param>
        public SetVoiceFilterCommand(UTrack track, UVoiceFilter? before, UVoiceFilter? after) {
            Track = track;
            this.before = before?.Clone();
            this.after = after?.Clone();
        }

        public override ValidateOptions ValidateOptions => new ValidateOptions {
            SkipTiming = true,
            SkipPhonemizer = true,
            SkipPhoneme = true,
        };
        public override Pipeline.ImpactSet Impact => Pipeline.ImpactSet.None;
        public override string ToString() => "Set voice filter";
        public override void Execute() => Track.VoiceFilter = after?.Clone();
        public override void Unexecute() => Track.VoiceFilter = before?.Clone();
    }
}
