# Voice Acting

Tools for making voicebanks speak lines rather than sing them. Characters get speech intonation and the small
unsteadiness of a human voice automatically. A take you record yourself can set a line's timing, melody and loudness.

## The Voice Acting panel

Click the speech bubble on a track's header (next to the settings gear), or right-click the track and choose
**Voice Acting...**. The panel has three tabs. Each starts with an On/Off switch and one sentence on what it does,
and every control has a tooltip:

- **Character**: pick an emotion tile (Neutral, Cheerful, Gloomy, Angry, Whisper, Timid, Elderly). Then adjust
  Expressiveness, Pitch and Breathiness.
- **Wave Voice**: game-style talking (see [Wave Voice](#wave-voice)), with sliders for note jumps, bounce-in,
  voice height and size, beat pulse and choppiness. **New melody** re-rolls the notes. The **Rhythm** buttons put
  every syllable on the track on 8th or 16th notes.
- **Filter**: the [PS1 filter](#ps1-filter). Pick a tile (Clean, Classic, Crunchy, Old TV, Echo), then fine-tune it.

Changes are heard right away. **OK** keeps them as one undo step; **Cancel** or closing the window puts the track
back. The Rhythm buttons edit the notes immediately and undo separately. The speech bubble lights up while a track
has a character or the filter on. The Filter tab works on any track, even a recorded one; the other two need a
singer.

## Voice characters

A voice character is an expression graph. It adds the following on top of whatever you draw, at render time, so
it follows every edit to the notes:

| What | Where it goes | Character value |
| --- | --- | --- |
| Pitch drifting down over each phrase | pitch | `declination` (cents) |
| Phrase endings that fall (statements) or rise (questions) | pitch | `finalDepth`, `finalMs`, plus the `intc` curve |
| A small arc on each syllable | pitch | `syllableArc` |
| Slow pitch wander and fast pitch unsteadiness | pitch | `driftDepth`/`driftRate`, `jitterDepth`/`jitterRate` |
| Loudness unsteadiness, and loudness falling at phrase ends | DYN | `shimmerDepth`, `endDecay` |
| Breathiness, plus extra breath at phrase ends | BREC | `breathiness`, `endBreath` |
| Tension, gender, vocal fry at phrase ends, whisper | TENC, GENC, GRWC, VOIC | `tension`, `gender`, `endFry`, `voicing` |
| A fixed register shift | pitch | `pitchOffset` (cents) |

Only the curves the track's renderer supports are driven. Curves you draw still apply: each output is the drawn
curve plus the character's part. The pitch override (PITO) replaces the generated pitch wherever it has a value.

**Make one:** open the expression graph editor and click **New Voice Character...**, then pick a renderer and a
preset: Neutral, Cheerful, Gloomy, Angry, Whisper, Timid or Elderly. Every value above is a node parameter, so you
can tune the character in the graph. Select it for a track in the track settings, or tick *Default for this
renderer*.

**Questions and emphasis:** draw the **intonation (curve)** (`intc`) over a phrase ending. 0 (the default) falls
like a statement, 50 stays level, 100 rises like a question, and negative values fall harder.

### Wave Voice

Wave Voice is a game-like, sing-song way of talking, as in Animal Crossing or Friday Night Funkin'. It is a separate
setting from the emotion and works with or without one: switch it on in the Voice Acting panel's **Wave Voice** tab.
The character then also gets:

| What | Where it goes | `WaveVoice` value (default) |
| --- | --- | --- |
| Each syllable hops to its own pitch, a random whole semitone | pitch | `hopSemitones` (±4), `seed` |
| A quick scoop up into each syllable | pitch | `scoop` (120 cents), `scoopMs` (40) |
| A higher, smaller, toy-like voice | pitch, GENC | `lift` (300 cents), `gender` (-30) |
| Loudness that pulses with the beat, loud on each pulse and fading through it | DYN | `pulsesPerBeat` (2), `pulseDepth` (60) |
| Syllables clipped short at their ends | DYN | `releaseMs` (50), `chopDepth` (150) |

The hops depend only on where each note starts, so editing one syllable doesn't reshuffle the others. Change
`seed` on the **Random per Note** node for a different tune. The pulse follows the project's tempo.

For the rhythm, use the panel's **Rhythm** buttons for the whole track. For just some notes, select them and use
**Voice acting: Wave Voice rhythm (8th notes)** or **(16th notes)** in the piano roll's **Notes** menu. Each
syllable then takes exactly one step of that length, in order. A pause of at least a step starts a new phrase on
the nearest step, with at least a step of rest.

## PS1 filter

The PS1 filter makes a track's voice sound as if a PlayStation played it. It is stored on the track
(`UTrack.VoiceFilter`) and runs on the rendered voice before the track's volume, pan and Track Polish, in
playback and in both mixdown and per-track export. In order:

1. A 6th-order low-pass, then resampling to the game sample rate: 22050, 11025 or 8000 Hz.
2. **Console compression**: an encode and decode through the SPU's own sample format. That is 4-bit ADPCM in
   28-sample blocks, with the best of its five predictor filters and shift chosen per block, so the artifacts are
   the console's.
3. **Reverb** (optional): a small SPU-style reverb at the game rate. It is a Room, Hall or Space echo made of damped
   feedback delays into all-pass diffusers. It is in the style of the console's reverb, not a copy of its
   registers.
4. Playback at 44.1 kHz with the SPU's 4-point Gaussian interpolation, which gives the PS1 its muffled sound.
5. **Old TV speaker** (optional): mono, band-limited to about 280 Hz–4.2 kHz and slightly overdriven.
6. **Filter amount** blends the original voice back in, time-aligned with the processed one. **Volume** sets the
   level.

The filter delays the track by about 32 game-rate samples (1.5 ms at 22 kHz, 4 ms at 8 kHz).

### Graph nodes

The *Voice Acting* category in the graph editor has the building blocks, for making your own characters:

- **Speech Intonation**: a phrase's melody in cents. It declines from `+declination/2` to `-declination/2`, bends
  by `final` over the last `final_ms` (negative falls), and arcs each note by `arc`. All three inputs are ports, so
  curves can drive them.
- **Fluctuation**: smooth fractal noise within ±`depth`, at `rate` Hz, over `octaves`. It depends only on project
  time and `seed`, so it stays the same however notes group into phrases.
- **Position in Phrase**: `position` from 0 to 1 by time, plus `elapsed` and `remaining` in seconds.
- **Random per Note**: one random value per note between `min` and `max`, rounded to `step` if set. It holds over
  the note and the consonants leading into it.

## Following a recording

Record or import your take of the line as a wave part on another track, at the same time as the character's voice
part. In the piano roll, the **Notes** menu then has:

- **Voice acting: notes from recording**: one note per spoken syllable, at the syllable's pitch, replacing the
  notes under the take. Then type the lyrics. On an empty part this uses the whole take and lengthens the part.
- **Voice acting: align notes to recording**: moves the selected notes (or all of them) onto the take's
  syllables, in order. Use it when you wrote the lyrics first. Syllables are merged or split until there is one
  per note.
- **Voice acting: follow recording's pitch and dynamics**: copies the take's melody and loudness onto the part.
  The pitch goes into the pitch override if the track's graph draws pitch there (voice characters do), otherwise
  into PITD. The melody moves to the register of the notes, so a line you act in your own register comes out in
  the character's. Loudness goes into DYN at 60% strength.
- **Voice acting: make character from recording**: measures the take's drift, jitter, shimmer, breathiness,
  declination and phrase endings, then gives the track a new character with those values.

The analysis needs no downloads. Pitch comes from YIN (de Cheveigné & Kawahara, 2002), loudness from RMS, and
syllables from loudness dips between voiced peaks (after de Jong & Wempe, 2009).

## Code

- `VoiceCharacter`: character values, presets, `BuildGraph`, `AddToProject`, `FittedTo`.
- `WaveVoice`: Wave Voice values (`VoiceCharacter.wave`) and `Rhythm`.
- `TrackVoice`: a track's character (kept in its graph's `character`), building the graph state for the panel,
  and `SetVoiceFilterCommand`.
- `Ustx/UVoiceFilter.cs`, `SignalChain/VoiceFilterSource.cs`, `SignalChain/Effects/Ps1Filter.cs`,
  `SignalChain/Effects/Ps1Adpcm.cs`: the PS1 filter.
- App: `Views/VoiceActingDialog`, `ViewModels/VoiceActingViewModel`, and the button on `Controls/TrackHeader`.
- `VoiceAnalysis`: `VoiceAnalyzer` (pitch, loudness, aperiodicity every 10 ms) and `VoiceProfile`.
- `SpeechSegmenter`: syllable segmentation and fitting segments to a note count.
- `PerformanceTransfer`: `ReferenceTake` (finding and analyzing the take) and notes/curves from it.
- `Editing/VoiceActingBatchEdits.cs`: the piano roll commands.
- `ExpressionGraph/GraphNodeTypes.cs`: the `intonation`, `fluctuation`, `phrase_position` and `note_random` nodes.

## Not done yet

- Recording from the microphone inside OpenUtau. For now, record elsewhere and import the file.
- Aligning by phonemes (forced alignment) rather than by syllable count.
- The panel can't switch off a character that comes from a renderer's *default* graph rather than the track's
  own. Untick "Default for this renderer" in the graph editor instead.
- Character edits re-render the track, so while it is playing they are heard from the next play.
- Following breathiness from the take (aperiodicity is measured but not yet written to BREC).
- Speech-specific phonemizers or voicebanks. Singing voicebanks speak well enough for "talkloid" work, but
  speech-trained banks would sound more natural.
