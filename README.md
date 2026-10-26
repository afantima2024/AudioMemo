# AudioMemo

A native Windows audio recorder and notes workspace, built with C#/.NET 10, WPF,
WASAPI, NAudio, local Whisper, and optional local or cloud AI summaries.
**Recording is independent of transcription and summarization.**

Choose **Ollama on the same PC**, **GitHub Copilot CLI**, or the official
**Doubao, Qwen, and DeepSeek APIs**. Cloud APIs require your own developer API key
and explicit transcript-sharing consent. All summary providers remain optional.

## Run

The published, self-contained Windows x64 application is
`artifacts\app\AudioMemo.exe`. It is the only top-level file; everything else is
inside three categories in the `support` subfolder:

```text
artifacts\app\
  AudioMemo.exe
  support\
    Runtime\
      AudioMemo.exe
      AudioMemo.dll
      ...application and .NET runtime libraries...
      runtimes\
      zh-CN\
      ...other language folders...
    Docs\
      README.md
      PRIVACY.md
      THIRD-PARTY-NOTICES.txt
    Licenses\
```

Launch the top-level `AudioMemo.exe`. Keep it and the `support` folder together
when moving or sharing the app. The inner executable is used for isolated
speech processing. `Runtime` keeps application binaries, .NET, speech engines,
and language resources together in their required loading structure. `Docs`
contains the readme, privacy policy, and component overview; `Licenses` contains
the full third-party license texts. The bundled .NET desktop runtime is loaded
from `support\Runtime`; no separate .NET installation is needed. Do not move
individual DLLs or language folders. Settings, models, and recordings stay in
their existing locations.

Supported platform: Windows 11 x64. Local transcription also requires the
[Visual C++ 2022 x64 Redistributable](https://learn.microsoft.com/cpp/windows/latest-supported-vc-redist).
Whisper supports CPU (including a non-AVX fallback) and Vulkan GPU acceleration.
Vulkan requires a compatible GPU and graphics driver; no CUDA installation is
required. Automatic mode reports CPU fallback; explicit Vulkan mode reports
an initialization error rather than pretending GPU acceleration is active.
Recording needs neither a speech model nor an AI account.

### Runtime performance

Recording reuses its audio scratch buffer instead of allocating one every pump
cycle. Transcript display builds grouped text without repeatedly copying
fragment indices, avoids duplicate refreshes, and does not redraw command states
that have not changed. Speaker-reference lookups are reused only within each
render, so renaming or removing a speaker never leaves a stale cached name.

These optimizations do not change audio samples, recording/checkpoint timing,
transcript grouping boundaries, editing/recovery mappings, search behavior, or
AI model choices and inputs. Transcription and summarization speed still depends
on the chosen model, hardware, and provider; no lower-quality model or reduced
audio quality is substituted to make processing appear faster.

### First-use setup

The first launch for a Windows user opens a five-step setup wizard, including
when the portable program is copied to a new PC. Existing preferences from older
versions are retained without unexpectedly starting the wizard.

1. **Audio devices (required):** enable a microphone, computer playback, or both,
   select the endpoints, and check access. At least one enabled device is required.
   This check queries the device and mix format without recording or playing sound.
2. **Saving folder (required):** choose an absolute, writable folder and WAV/MP3
   output. A temporary one-byte write/read check is removed automatically.
   Device access and folder access are checked again before Finish.
3. **Local speech / live text (optional):** choose and download a Whisper model.
   New profiles start with Base and fast drafts off. Downloading the selected model
   successfully enables live text and final transcription when the Visual C++
   runtime is available. Fast drafts additionally need Base; speaker analysis has
   separate optional model downloads. The wizard reports the runtime prerequisite
   and links to Microsoft's installation instructions. No model downloads or
   software installations start automatically.
4. **LLM (optional):** open the existing local or online provider setup. Local
   setup provides the official Ollama installation link, recommended models,
   `ollama pull` commands, model discovery, and a connection test. Online setup
   supports Copilot CLI sign-in or the supported providers' own API keys and
   transcript-sharing consent. Whisper produces text; an LLM produces main points.
   They are separate, and neither is required just to save a recording.
5. **Review:** confirm the choices and finish. Closing the wizard before Finish
   does not save preference changes; a first-use launch then exits.

**Skip** is available only on the speech and LLM pages. Skipping speech keeps
recording available but turns off its automatic AI processing. Skipping the LLM
page turns off automatic summaries and startup model discovery, while retaining
any existing provider profile. Complete skipped setup later under
**Settings > Speech** or **Settings > AI main points**. Download cancellation keeps
any previously completed model; closing during a download cancels it before exit.

**Logo menu > Settings > Appearance > Run setup / reset...** offers two choices before
opening the wizard: **B**, selected by default, keeps current preferences and
saved credentials; **A** starts with default preferences and clears AudioMemo's
saved credentials and cloud-sharing approvals when the wizard is finished.
Neither choice deletes recordings, transcripts, main points, personal notes, or
downloaded models, or signs out external Copilot/Ollama sessions. Unsaved changes
in the Settings dialog are not applied. Canceling the wizard keeps previously
saved preferences. With A, choose the old recording folder to retain the same
visible library.

### Recording and notes

The toolbar fills a single full-width header, with minimize, maximize/restore,
and close always visible at the far right. The AudioMemo logo sits beside the
program name and remains visible when a narrow layout hides the name. Click the
logo (or focus it and press Enter/Space) to open the application menu. It includes
New Recording, the current Start/Continue/Pause/Resume action, Stop, Import
audio/video, Open saving folder, recording-list pin/show/hide, Main points and
My notes visibility, Settings, Message history, and Help & About. Actions use the
same availability and safety checks as their existing buttons. The visibility
items stay open for multiple changes and reflect changes made elsewhere.
In narrow layouts the recording-list command shows/hides its temporary preview
instead of claiming to pin a list that cannot fit.
Only the separate header Settings button has been removed; other existing
buttons remain. Caption
buttons show a focus outline for keyboard navigation, not a lingering border
after a mouse click. Blank header space remains draggable
when the toolbar wraps; normal window resizing and safe-close behavior remain.
Maximization uses the current monitor's exact work area at its current DPI,
including side/top taskbars, without leaving a resize-frame gap at the edges.
The main provider selector is labeled **LLM**. Transcript actions sit to the
right of the Transcript title and wrap when necessary. Hover over **Transcript**
to see speech/model information instead of another permanent header label.
**Main points** and **My notes** also show model information on title hover;
optional bottom status-bar model labels remain available.

1. Choose a microphone, a playback endpoint, or both. Uncheck an unused source;
   at least one must work. Playback means the sound going to that selected
   speaker/headset, not every output device at once.
2. Press **Start recording**, or use **+ New Recording** first to enter an
   optional name. The name dialog starts with `yyyy-MM-dd HHmmss - ` and places
   the caret after the prefix so you can append your name. The native window title
   follows the selected or pending conversation; an unnamed workspace uses
   **Recorder and notes**. A blank name becomes the actual start date/time
   (`yyyy-MM-dd HHmmss`); saved sessions can also be renamed from their menu.
   Choose WAV/MP3, 44.1/48 kHz, and mono/stereo in **Settings > Recording**.
   The same button becomes **Pause**, then **Resume**; paused time is excluded.
   The separate **Stop** button finalizes the saved audio.
   The language dropdown beside **Settings** selects **Auto-detect**,
   **Chinese (Simplified)**, or **English** for speech recognition. Changes save
   immediately and stay synchronized with **Settings > Speech**; they do not
   change the interface language or rewrite existing transcripts. Choose before
   recording or starting transcription: the dropdown is locked during recording,
   saving, and AI processing.
   The Windows taskbar icon has a large recording heart centered on the logo,
   without a white ring. At its largest, it is 62.5% of the icon width.
   While recording, it grows and lifts slightly with two smooth beats
   over a 2.4-second cycle, including when the window is minimized. The rest phase
   is fully transparent, leaving the original logo unobscured. The heart is red
   by default. **Settings > Appearance > Rainbow taskbar recording heart**
   optionally transitions through red, yellow, green, cyan, blue, and magenta
   over 14.4 seconds while keeping the heartbeat effect.
   It respects Windows' animation preference and stays solid in high-contrast
   mode. A still amber heart appears in the same position while paused, with the plain
   application logo once stopped. The main recording light is also larger and
   vertically centered. The logo artwork fills more of the taskbar icon: excess
   transparent margins are reduced to about 1% per side (at least half an output
   pixel), without stretching the supplied design. Windows fixes the available
   icon area; the animated heart stays fully inside the icon rather
   than floating outside it like a separate Windows-managed notification badge.
   Native icons include 16-256 pixel sizes for different Windows scaling levels;
   the recording heart stays in the center area at every scale.
3. Changing a device selector or source checkbox while recording or paused opens
   a confirmation showing the new microphone and playback sources. **Yes** applies
   the change; **No** keeps the current sources and restores the displayed
   selection. Device choices save automatically while idle; there is no separate
   **Apply devices** button. While recording,
   a failed replacement leaves the original sources running. When paused, the
   selected sources start on resume. Controls are briefly locked during a switch,
   and the last enabled source cannot be disabled. A failed idle preference save
   restores the previous choices. The existing **Ctrl+D / Apply devices** shortcut
   remains compatible with saved custom shortcuts and can retry saving preferences
   after a device switch whose settings write failed.
   Starting or resuming without valid audio sources shows a warning with device
   setup instructions. AudioMemo opens and checks the selected capture devices
   before creating a new recording entry or file, so an unavailable setup does
   not leave an empty recording in history. If one selected device fails but the
   other works, recording continues with the working source and a warning.
   A quiet microphone or silent computer playback is still a valid source.
4. In **Settings**, choose a saving folder and download a local speech model.
   Base multilingual is 141 MiB; Small multilingual is 465 MiB.
   Large-v3 Turbo Q5 is 548 MiB and is intended for a suitable GPU.
   Downloads are verified against pinned SHA-256 hashes.
5. Use **Transcribe / resume**, or enable automatic transcription after Stop.
   **Live text** starts processing after about two seconds of saved audio,
   using a persistent worker. Model loading/inference adds latency.
   By default, fast drafts use **Base on CPU**, then **Small** (or your selected
   final model) rebuilds the final transcript from unchanged audio after Stop
   if automatic final transcription is enabled. Existing model selections are
   preserved. Download both Base and the final model when using this mode.
   To keep only live text after stopping, turn off the existing **Automatically
   transcribe after stop** setting. Live main points can still update during
   recording. Stop finishes the current speech chunk, saves the draft, and skips
   the remaining backlog, refinement, speaker analysis, and final summaries.
   Later, **Transcribe / resume** completes the saved audio even with this option
   off. **Update main points** explains that final transcription is required
   until that work finishes. Closing a live-only job does not schedule unwanted
   final transcription for the next launch.
6. **Update main points** summarizes the transcript using your selected provider.
   Cloud providers require explicit transcript-sharing consent; Ollama runs locally.
   Settings can enable automatic summaries or live updates, approximately every
   90 seconds of processed audio. After final main points are saved, the same
   provider/model makes one additional request to create an **Overall summary**
   at the bottom of Main points. It consolidates the completed notes using the
   same main-points, decisions, action-items, and open-questions sections, without
   recording timestamps. This also happens when manually updating a saved
   recording, but not during live updates. Existing automatic-summary settings
   and sharing consent still apply.
   Both sections are saved in `summary.md`; browsing history does not request AI.
   If the overall step fails or is cancelled, the main points remain saved.
   **Update main points** resumes unfinished work without repeating completed
   batches. A never-summarized recording starts immediately. Changed transcript
   text, speaker assignments/names, or re-transcription starts a fresh summary
   immediately. If completed main points already match the transcript, a
   confirmation explains that another summary is unnecessary: **No**, the default,
   changes nothing; **Yes** regenerates using the currently selected LLM.
   Personal-note, Me-tag, and speaker-color changes do not invalidate main points.
   A confirmed regeneration remains resumable after cancellation or safe close,
   including when it stopped before its first successful checkpoint.
   **Update main points** sits at the right of the Main points header, immediately
   before that pane's **X**, with the title left. Hover over **Main points** for
   **Generated with: provider/model**, identifying the saved result separately
   from the currently selected LLM. Model attribution is not inserted into the
   displayed main-points bulletin, saved canonical summary, or AI input.
   Older results whose model
   was never recorded say so explicitly. Inherited Copilot selections are resolved
   when the job starts; an `auto` selection cannot identify a privately routed model.
7. **My notes** is editable and auto-saved separately. Existing notes are never
   sent to AI. The optional, manually requested **About me** section below can
   be inserted or refreshed with **Update my notes**, beside that pane's **X**,
   without replacing your other notes.

To export audio, right-click in **Transcript** and choose **Export audio (whole
recording)**. This rebuilds the recording's saved WAV/MP3 output using its audio
settings, not text-to-speech of the main points or notes. Selected rows and filters
do not restrict the export; a transcript is not required. The existing export
keyboard shortcut still works. Export remains unavailable during recording or
AI processing; multi-recording exports remain in the recording-history menu.

### My speaker tag and About me notes

Right-click a labeled transcript row and choose **Mark speaker as me...**.
Only one speaker is tagged per recording; this is your selection, not biometric
identification or an identity shared between recordings. The transcript and
speaker filter show **(Me)** beside that speaker without changing their actual
name. Optional names/nicknames help recognize mentions by other people.
Use **Edit my speaker tag...** or **Remove my speaker tag** to change it.
Tagging, removing a tag, and editing notes do not invoke AI.

Once the saved transcript is complete, click **Update my notes** or right-click
the transcript and choose **Summarize about me**. Both use the same protected
update process and the entire transcript, regardless of the current
speaker/search filter or row selection, and your selected local/online LLM.
Existing provider authentication and transcript-sharing consent apply. The prompt
asks for your contributions, mentions by others, actions/commitments, and open
questions, with supplied timestamps and attribution. Names, speaker labels, and
ambiguous pronouns are not proof of identity; review the generated result.

The result is inserted into the existing editable **My notes** text under
**About me**, with its model and generation time. It never runs automatically
after recording, tagging, transcription, or updating general main points.
A later manual request replaces only the single unchanged section previously
generated by AudioMemo. Surrounding notes and edits made while AI is working are
preserved. If that section was edited, removed, or duplicated, a confirmation
offers to append the new result while keeping everything, or cancel. A completed
result is retained for retry without another model request.
Hover over **My notes** to see the model used for the last applied AI section
separately from the selected LLM. An unfinished or unapplied replacement does not
relabel the existing notes. Older notes without reliable model metadata say so;
manual notes are not attributed to AI.

Successful batches and the exact AI-generated section are stored locally in
`about-me.json`. Editable notes, including edits to that section, never become
model input: incremental context comes only from the AI checkpoint. Safe close
can save and resume this manual request with its original model and Me-tag
identity, subject to current consent. Changing/removing the tag prevents an old
request from being applied to another person.

Text corrections and compatible recording continuation retain the tag. Moving
all of the tagged speaker's rows to another speaker remaps it to that corrected
identity. Muting all of that speaker's rows clears it; restoring audio does not
resurrect a deliberately cleared tag. Whole-recording speaker reanalysis clears
the tag and reports that you must mark yourself again. Editable notes remain
untouched by these operations; edited copies retain section-ownership tracking,
but not a reusable inference result for the old audio.

The processing strip shows **Elapsed** time alongside the existing progress bar,
without adding another permanent row. After recording stops, elapsed time counts
the post-recording processing run rather than the time spent recording.
**Step ETA** is an approximate local finish time for the current measurable step,
not a promise for the entire job. It uses completed audio chunks, summary batches,
or speaker-feature progress; later steps can take additional time. Model setup,
speaker segmentation/grouping, and single AI requests show **Estimating...** when
there is not enough reliable progress. Estimates reset at step boundaries and are
withdrawn when progress stalls instead of counting down past completion.
Local/online re-transcription has the same display; stopping and continuing a local
preview excludes paused time. Completed or canceled work retains its elapsed time,
and a new run starts fresh. These indicators require no additional AI requests.

Summary progress explicitly says **transcript batch X of Y**, not X/Y steps.
Each batch is a portion of transcript text combined with the main points saved
so far; it is not another transcription of the audio. The count covers batches
remaining in that summary run, including a resumed run. Final main points are
followed by a separate **Overall summary** request, outside the batch count.
Live main points are labeled as live. Speech-recognition counters identify
processed audio duration, and speaker-feature percentages describe only
feature extraction, not total completion or the number of successfully labeled
rows. Hover over the processing description or bar for this explanation.

Right-click the bottom status bar to independently show or hide **Messages**,
**Current LLM**, **Speech model**, **Library size**, **Recording count**, and
**Free disk space**. The menu stays open for multiple changes, and each choice
saves immediately. Ordinary notifications remain on the left; enabled information
wraps on the right. Hiding Messages does not hide warnings or the processing strip.
The count covers the current recording library. Library size adds the logical file
sizes in its known recording folders, including audio, text, and notes, not models
or unrelated folders; linked files are skipped and linked recording folders are
reported rather than followed. Size/free-space checks run in the background only
when enabled, refreshing about every 30 seconds and after library/settings changes.
Unavailable statistics are reported, not displayed as a successful zero.

**Message history** is available from the status-bar right-click menu, warning
banner, the **logo menu**, and the standalone **Help & About** window. It records application notices, warnings,
errors, and confirmation outcomes with their time, operation/reason, and message.
New entries appear immediately; **Load older messages** pages through earlier
ones. History survives restarts and is retained until you explicitly clear it.
The local journal is `%LOCALAPPDATA%\AudioMemo\Messages\message-history.jsonl`.
Common credentials/token patterns are redacted, but messages can contain file
paths, recording titles, and error details. It does not copy transcript/notes
editors or remembered vocabulary into the journal, and is not sent to AI.
Clearing it does not change recordings, notes, settings, or provider-side logs.
Failed history writes are reported and retried, not silently discarded.

**Logo menu > Help & About** credits **Afanti Ma** for the concept, ideas, and
guidance, and identifies the program as primarily programmed by AI. Its compact
copyright card shows **Copyright © 2026 Afanti Ma**, separately from the
third-party copyright/license notice. **User guide**, **Keyboard shortcuts**, and
**Message history** open their own windows instead of filling About with long
reference material.

Select a saved recording in the left history list and press **Continue recording**
(the same record/pause button, or the history context-menu command). A warning
shows the session title and audio path before anything is appended; **No** is the
default and leaves the existing recording unchanged. The same confirmation
applies to the record shortcut, but not to pausing/resuming an active recording
or starting a new conversation. New audio
appends to that session's safety WAV; the timer and new transcript timestamps
continue from its previous end, without inserting the time between recordings.
The session keeps its title, notes, earlier audio, transcript corrections, and
original output format. Stop refreshes the configured WAV/MP3 export. Subsequent
transcription and live-draft refinement preserve the pre-existing transcript;
automatic speaker analysis preserves existing labels, names, and colors.
Use **+ New Recording** to start a separate session instead. An interrupted
recording must be recovered before it can be continued. If continuation reports
a stale WAV header or length mismatch, use **Recover WAV** in that recording's
history context menu before retrying; invalid files are never overwritten just
to start a continuation.

**Import audio/video** in the recording list adds an existing WAV, MP3, M4A/AAC,
WMA, FLAC, or video soundtrack as a separate session. Audio-only imports keep
using Windows/NAudio codecs; unsupported codecs and multichannel audio files
are reported explicitly. Video imports use bundled FFmpeg/ffprobe and support
MP4/M4V, MOV, MKV, WebM, AVI, WMV/ASF, MPEG/VOB, transport streams, 3GP, and OGV
with supported, unprotected audio codecs. No separate FFmpeg installation is needed.

A video with one audio track imports directly. For multiple tracks, a temporary
chooser shows language, title, codec, channels, and the default track; select one
and confirm. Only that track is imported, and surround audio is mixed down to
stereo. Video frames, subtitles, and other tracks are not analyzed. The source
video is not copied to the library or modified. Audio timing, including an initial
audio delay, is retained relative to the video's start. Streaming URLs and
playlist inputs are not supported; video tools are restricted to local file/pipe
protocols and known video container demuxers.

Import streams into a
48 kHz stereo PCM16 safety WAV without modifying the original, then asks whether
to analyze now. **No** keeps the saved audio for later; **Yes** runs local
transcription and the enabled automatic speaker/summary steps. Missing speech
models are reported, not downloaded. Browsing other sessions only loads their
saved results and never starts another analysis. Cancelled/failed imports remove
their staging files; incomplete `.import-<GUID>` folders after a crash stay hidden.

Drag the **right edge of the recording list** to resize its width in both pinned
and floating/preview modes. The saved width is shared between modes and clamped
to the available workspace without changing height. With the grip focused,
Left/Right resizes, Home resets, and Escape cancels a drag.
The history and reading-pane dividers use thin 2-DIP lines with a wider transparent
drag area, so they stay easy to grab without showing thick bars. Reading-pane
dividers keep their keyboard resizing and follow horizontal/vertical layouts.

**Compare selected audio online**

Select one or more final transcript rows, then **Online comparison** in the
transcript toolbar or context menu. The popup has separate audio-capable
**Qwen ASR (`qwen3-asr-flash`)** and **OpenAI (`gpt-transcribe`)** connections;
the main-points model is not used. Set the provider/region and API key, optionally
**Save connection**, then review the ranges, size, request count, and upload
consent before **Upload selected audio**. A matching Qwen endpoint can reuse its
protected summary key, but never its summary-sharing consent.

Only the selected underlying segments are sent as 16 kHz mono WAV, one request
per segment to preserve the existing row timing and speaker labels. Gaps,
unselected segments, personal notes, local transcript text, and vocabulary are
not uploaded. Any other voices audible within a selected interval remain in
its audio; filtering speakers does not separate voices.

Limits checked against official documentation on 2026-10-20:

| Connection | Provider limit | Practical part length with the app's WAV format |
|---|---|---|
| [Qwen ASR](https://www.alibabacloud.com/help/en/model-studio/qwen-asr-api-reference) | 5 minutes and 10 MB including Base64 encoding | About 234 seconds; encoded size is the tighter limit |
| [OpenAI](https://developers.openai.com/api/docs/guides/speech-to-text) | 25 MB per file; no separate published duration limit | About 13 minutes |

The app reserves request overhead and verifies actual request sizes. Oversized
selections are blocked until you select fewer rows, switch to a larger-limit
model, or explicitly enable splitting. Splitting stays inside selected intervals
and may incur multiple charges. Requests run sequentially without automatic
retries. Account quotas/rate limits are separate; cancellation cannot retract
already-sent requests.

**Compare results** shows local/saved and online text side by side, with a
**Differences only** option. Entire rows are pink for different text, green for
exactly matching text, or gray when either side has no usable transcription.
Gray is not a confidence score; missing-result rows remain in Differences only.
The legend and row tooltips explain the states. Customize the light/dark colors
in **Settings > Appearance > Transcript comparison colors**.
**Use online result** copies recognized text into
the editable column; **Save transcript changes** is still required. Blank or
incomplete recognition does not erase existing text. Successful comparisons
are saved separately under the session's `online-comparisons` folder, so opening
the same selection can show them without another upload. Reopened online results
are compared with the currently saved transcript, never an older cached local
preview. Changed audio/ranges
invalidate a stored comparison. Original audio, timestamps, speaker labels,
and notes remain unchanged; an applied correction makes old main points stale.
Import and online-comparison commands can be assigned shortcuts in Settings.
They start unassigned to avoid conflicting with existing custom shortcuts.

**Settings > Recording > Automatic recording actions** includes optional
silence stopping. It is off by default (manual stop); the default timeout is
300 seconds (5 minutes), editable from 1 to 86400 seconds. All enabled inputs
must remain below -60 dBFS after their input gains for the entire timeout.
Activity is checked on each source before mixing, including both stereo channels,
so audio from either input resets the timer even if their signals cancel in the mix.
No incoming playback packets count as silence. Pausing/resuming, applying devices,
or losing an input restarts the timer; paused time never causes an automatic stop.
Background noise or music can prevent auto-stop: this detects silence, not the end
of a conversation. Automatic stop follows the normal save/export/transcription
workflow, retains the complete recording (including silence), and reports why it
stopped.

The same Recording page includes **Automatically analyze speakers after recording
stops**. Turn it off to skip automatic analysis after final transcription, including
the final pass of live transcription. This does not change the transcript table or
remove existing labels. **Identify speakers** remains available manually; model
downloads and the speaker count remain under **Settings > Speech**.

After Stop, animated progress bars in the status area and on the Windows taskbar
show that saving or AI processing is still running, including final transcription,
optional speaker analysis, and summaries. These are activity indicators, not
estimated percentages: model calls do not expose dependable overall progress.
The indicators turn solid green only when the work finishes successfully.
Failures are red; cancellation or completion with warnings is amber. The finished
indicator clears on the next activation, click, key press, or scroll in AudioMemo.
Returning while work is still running does not hide its progress. Manually
requested transcription, speaker analysis, summaries, and audio exports use the
same indicators. Windows controls native taskbar progress colors. The recording
badge is composed into the main icon rather than restricted to the small native
overlay slot.

The main toolbar's **LLM** selector chooses **Copilot CLI**, **Ollama**, **Doubao**, **Qwen**, or
**DeepSeek**, and applies a model choice without changing CLI preferences.
Cloud API choices use suggested or manually entered Model IDs; DeepSeek also
supports an explicit model-list refresh. Changing the provider/model requires a
successful short **synthetic sample test before activation**. The old selection
remains active while testing. Missing access, consent, or configuration opens the
LLM settings page; **Test and select** applies it only after a successful test.
Canceling or a failed test/save keeps the previous selection, with no automatic
fallback or model download. The sample contains no recording, transcript, or
notes, but an online provider/CLI may charge for it. Cancel AI, recording start,
and safe close cancel a pending selection test; accepted online requests cannot
be retracted. Browsing Settings and unrelated preference edits do not make a
selection-test request. The optional bottom provider/model label remains
available, cloud connections also show their endpoint host in its tooltip,
and a running summary reports its job's model.
Selections are locked during recording/AI work. An inherited CLI default is
labeled as such, not guessed from a recently used model.

The device selectors, recording indicator, Start/Pause/Resume, and Stop share
one row when there is enough room; commands wrap at narrower widths. Audio
output information remains underneath. The main window no longer has a title
input or format selector. The output profile is captured per recording; changing
Settings applies to new recordings, not existing sessions.

### Appearance

Settings has left-hand categories (**Appearance**, **Recording**,
**Transcription**, **AI summaries**, and **Keyboard shortcuts**)
with independently scrolling details on the right. Save and Cancel stay
available below the details.

In **Settings > Appearance**, select **Light (day)**, **Dark**, or **Follow
system**, then save. The choice applies to the current workspace and app dialogs
without restarting; Cancel leaves the current appearance unchanged. Follow
system tracks the Windows app color preference without changing Windows settings.
Existing installations default to Light. App-owned confirmation dialogs, neutral
transcript rows, and editable text cells use the selected theme. Table headers and
unused space keep that theme while AI processing disables editing, including
after stopping or continuing re-transcription. Windows-owned
file pickers and emergency startup/display-error messages remain system-managed.

**Show main points** and **Show my notes** control workspace visibility only.
With one side panel enabled, it fills the right-hand reading area beside the
transcript; with neither, the transcript fills the entire reading area without
empty dividers. Narrow windows keep their responsive stacked/pane-selector layout
and offer only enabled panels. Hidden notes remain saved, and hiding main points
does not disable automatic AI processing.
The small **X** in either panel header hides it even during recording. A compact
**Panels** button appears in the transcript header only when a panel is hidden;
use its checkboxes to bring either panel back without opening Settings.
Notes are saved before hiding, and panel visibility is remembered.

Set separate light/dark colors for **Selected rows**, **Search matches**, and
**Current match / text selection**. Each picker offers presets and an exact
`#RRGGBB` value. Selected rows and selected text use readable black or white text;
search-highlight shades adjust when necessary to keep the existing text readable.
Separate light/dark **Local / online comparison colors** control the whole-row
pink/different, green/matching, and gray/unclear backgrounds. These also choose
readable black or white text automatically; colors never change recognition results.
**Restore color defaults** resets both highlight and comparison colors in the
dialog; Save applies them.
Windows high contrast takes priority. Stronger panel borders, table lines, and
resize dividers distinguish the dark workspace's surfaces.

**Button appearance** offers four presets, with normal, primary, and disabled
previews. The preview does not change the workspace until Save.

| Preset | Appearance |
| --- | --- |
| Windows / Fluent | Rounded, filled buttons; the default |
| Office-like flat | Compact corners and a flat surface |
| Soft shadow | Rounded buttons with subtle depth |
| Solid outline | Transparent surface with a clear two-pixel border |

Content buttons, including toolbars, correction dialogs, and confirmations, use
the selected preset; window caption buttons retain their Windows-style appearance
and fixed size. Existing button padding and minimum sizes are retained;
decorative borders do not enlarge buttons when switching presets. All presets
retain hover, pressed, disabled, and keyboard-focus feedback. Checkbox captions
are centered vertically beside their squares. The supplied transparent logo is
used for the executable, package artwork, and **Help & About**.

The app is Per-Monitor-V2 DPI-aware and uses device-independent layout,
pixel-aligned edges, and the current monitor's available work area. Moving
between monitors or changing Windows scaling refits windows without changing
Windows settings. Narrow layouts temporarily unpin history and rearrange the
reading panes. Short, narrow workspaces provide a pane selector beside
**New Recording** for
Transcript, Main points, and My notes so each remains usable rather than being
compressed into a few pixels. Larger windows retain the resizable multi-pane
layout. Very small work areas can scroll the workspace instead of clipping
its controls or collapsing the reading area. The floating Transcript toolbar has
an **Actions** menu instead of a second row of buttons. In narrow panes, the
navigation buttons hide while Actions remains available.
Selection backgrounds are drawn behind the glyphs, with a contrasting
text brush in light and dark themes.

### Keyboard shortcuts and help

**Settings > Keyboard shortcuts** lets you click a field and press a combination.
Backspace/Delete disables that shortcut; **Restore default shortcuts** resets
the list. Conflicts, unsupported keys, and protected Windows/text-editing
commands are reported before Save. Cancel discards edits.
**Logo menu > Help & About > Keyboard shortcuts** opens a separate read-only
reference showing your saved shortcut choices and their scope. Help is also
available during recording and AI work. Editing
shortcuts remains in Settings, including changing or disabling the Help and
Settings shortcuts. Button/menu hints update after Save. The defaults below
are not additional fixed bindings after customization.

**Help & About > User guide** opens an offline, read-only guide in the current
interface language (English or Simplified Chinese). A topic selector covers
quick start, devices/recording, speech, LLM setup/privacy, main points, personal
notes/Me, navigation, uncertainty review, corrections/speakers, silence/restore,
library/import/export, workspace/shortcuts, recovery/backups, and troubleshooting.
Guide text is selectable for copying. The guide and shortcut list are created
only when their buttons are used; opening help never reads recordings, changes
settings, downloads models, or sends an AI request. Content is embedded in the
application's language resources, so it remains available after portable relocation.

| Default | Action |
| --- | --- |
| Ctrl+N | New Recording |
| Ctrl+R / Ctrl+Shift+R | Start, pause, or resume / Stop |
| Ctrl+F | Open/focus transcript search from the main window or search popup |
| F3 / Shift+F3 | Next / previous match |
| F2 / Ctrl+Shift+T | Edit text / re-transcribe selection |
| Ctrl+Shift+K | Correct selected speaker assignments |
| Ctrl+T / Ctrl+M | Transcribe / update main points |
| Ctrl+P / Ctrl+Shift+P / Ctrl+Alt+P | Replay / pause-resume / stop replay |
| Ctrl+B | Show/hide recording list |
| Alt+1 / Alt+2 / Alt+3 | Focus Transcript / Main points / My notes |
| Ctrl+, / F1 | Settings / Help & About |

The in-app list covers device/model refresh, export, filtering, copy, and other
main-window commands too. Commands respect their disabled/busy state.
Page Up/Down and Ctrl+Home/End are scoped to the transcript, preserving normal
navigation in notes and search fields. Ctrl+C/V/X/A/Z/Y remain native editing
commands. In the search field, Enter/Shift+Enter also moves next/previous.

## Transcript workspace and saved sessions

The compact device bar leaves more room for **Time | Speaker | Text**. Related
Whisper fragments are grouped for display, while precise underlying intervals
remain saved. Earlier line breaks came from Whisper's timing segments, not
necessarily sentence ends or speaker changes.
Rows use compact vertical padding without reducing the font size; wrapped text
still expands to show every line.

- New text follows the bottom. Scroll up to hold your position; scroll to the
  bottom, press **Ctrl+End**, or click **Latest** to resume following.
- A compact floating toolbar, initially in the lower-right of Transcript, provides
  **Actions**, **Top**, **Page up**, **Page down**, and **Bottom**.
  **Actions** contains the former second-row commands: speaker filtering, copy
  plain/annotated text, follow latest, replay, silence, edit text, search, and
  online comparison. Speaker filtering is a submenu whose checkboxes stay open
  for multiple selections. The Transcript title-row buttons, right-click menu,
  and replay controls remain in place.
  The toolbar stays available for short or empty transcripts, including when
  filters match no rows. It stays clear of the scrollbar and works during
  recording and AI work; each command retains its availability rules and shortcuts.
  Narrow panes retain Actions and the grip while hiding the four navigation
  buttons. The entire toolbar hides only when the viewport cannot fit it.
  Top pauses following; Bottom resumes it. Page buttons preserve row/text
  selection. Hover over a button to see its action and configured shortcut.
  Drag the dotted grip on its left to move it anywhere inside the transcript
  viewport, clear of the column headings and scrollbar. Its relative position
  is saved on release and kept across resizing, recording changes, and restarts.
  Dragging does not change selection or following. Escape cancels a drag;
  keyboard users can focus the grip and use arrow keys to move it or Home to
  restore the lower-right position.
- **Page Up / Page Down** scroll the transcript viewport, including when the
  caret is inside a text cell. **Ctrl+Home** goes to the beginning and pauses
  following; **Ctrl+End** goes to the end and resumes it. Page Down also resumes
  following when it reaches the bottom.
- **Copy all text** excludes times/names. The annotated copy includes both.
  Drag within a text cell to copy part of a sentence, or select rows in the
  Time/Speaker columns (Shift/Ctrl-click) and use their copy menu.
  Only one text range is highlighted at a time. Selecting elsewhere clears the
  old highlight immediately. Scrolling, paging, or dragging the scrollbar keeps
  the range and row selections, even when their rows are recycled off screen;
  Ctrl+C can still copy the selected text. Opening another session or changing
  the selected text invalidates an old range instead of highlighting unrelated text.
- **All speakers** opens a multi-select filter, including **Not labeled**.
  Check **All speakers** inside it to select everything, or uncheck it to clear
  the entire set. Unchecking one speaker from All keeps the others selected.
  Names update after renaming without losing the selected speaker IDs. Filtering
  keeps original row/time boundaries; it does not join across hidden speakers.
  The transcript copy buttons copy only matching rows while a filter is active
  (**Copy filtered text**). Session-menu copy/export still includes the whole
  transcript. Opening another session clears the filter; summaries are unchanged.
- **Recheck unlabeled speakers**, in the transcript and recording context menus,
  matches only unlabeled rows against existing speaker assignments in that
  recording. It requires a complete saved transcript, some existing labeled
  speech, and the downloaded local speaker models. It does not rerun full
  diarization, invent speakers, or change already-labeled rows, wording, names,
  colors, or your Me tag. Short, ambiguous, or mixed-voice rows remain unlabeled.
  Matching uses conservative local embedding comparisons, not authenticated
  identity; review the resulting labels. Changed assignments mark old main
  points as outdated without requesting a summary. Safe-close/resume supports
  this operation separately from full speaker analysis.
- Double-click a **Speaker** cell to choose a color for all rows of that speaker
  in the recording, including while AI is processing or live text is arriving.
  The row menu and configured speaker-color shortcut also offer this action.
  All **Not labeled** rows share one recording-local color; coloring them does
  not assign a speaker or claim they belong to the same person. This preference
  also applies to new unlabeled rows. Colors are saved with the transcript,
  survive compatible AI updates, and do not affect summary freshness.
  **Use neutral color** removes a custom color; speakers without a chosen color
  stay neutral. Text automatically uses readable black or white.
  Selected rows use the configured selection highlight instead, with a
  small speaker-color swatch still visible. Windows high contrast disables the
  decorative colors. Color changes do not modify audio or summary input.
- During AI processing, unavailable row/history-menu actions are disabled and
  dimmed, including speaker renaming, reassignment, and text corrections.
  Display-only speaker coloring, transcript copying, and safe saved-audio replay
  remain available. Color changes wait during audio file operations and are
  disabled when multiple recordings are selected or the app is closing.
- Drag the divider to resize transcript versus notes. The **<** button on the
  recording panel hides the library and remembers your preference; there is no
  duplicate toolbar Show list button. The remaining **>** edge tab opens a
  temporary overlay on hover or click, without moving the transcript. It stays
  open while the pointer is inside or a control is being dragged. Clicking
  outside the panel (including the title bar) or switching away hides the preview
  immediately without consuming the click. Moving the pointer out hides it
  after five seconds without panel interaction. **Pin** keeps it visible; a
  temporary preview does not change the saved show/hide preference.
- Drag **>** vertically to reposition it along the left edge. Its position is
  saved on release and scales with the window height. **Escape**, losing mouse
  capture, or switching away cancels a drag without saving its temporary position.
  The tab is 30 pixels in device-independent height, aligned with New Recording
  at its default position, and no longer reserves an empty full-height gutter.
- Right-click a saved session to rename it, open files, copy/export text,
  resume transcription, identify speakers, summarize, export MP3, or move the
  whole session to the Windows Recycle Bin. Deletion requires confirmation and
  is blocked during recording/processing. Recycling requires a local fixed
  drive; linked folders/files are rejected.
- In **Your recordings**, **Ctrl+click** selects individual recordings and
  **Shift+click** selects a range. **Ctrl+A** selects all when the list has focus;
  the **All** checkbox at the left of the selection row selects or clears the
  entire list, with recording/selection status aligned to its right.
  **Import audio/video** sits immediately left of **Open saving folder** at the bottom.
  Narrow layouts keep both buttons on one row, with full labels available in
  their tooltips. Selection survives scrolling, list refreshes, and switching
  between pinned and floating mode.
- Right-click a multi-selection to **Rename** recordings one by one in list
  order; Cancel keeps completed renames and stops the remaining prompts.
  **Open files** opens their common parent in Explorer with all selected
  recording folders highlighted together.
- Multi-selection **Export transcript** and **Export MP3** ask for one destination
  folder and export one file per recording, named after its history title.
  Transcript exports include timestamps and speakers, not personal notes.
  Invalid filename characters are replaced; duplicate titles or existing files
  receive numbered suffixes rather than being overwritten. MP3 export uses a
  matching saved MP3 or encodes the current WAV when none is available or the
  cache is older. Failures stop the batch and keep completed exports. Closing
  cancels remaining exports and does not resume that batch on the next launch.
- Right-click within a multi-selection to **move the selected recordings to
  Recycle Bin** with one confirmation listing every folder. Other single-recording
  actions, such as AI processing and continuation, remain disabled; the current
  preview remains visible. Recycling stops at the first failure or cancellation,
  reports how many folders were moved, and keeps surviving selections. Closing
  during recycling waits for the current folder operation and leaves the rest
  untouched; it never queues deletion for the next launch.

### Search the transcript

Click **Search...** immediately after **Edit text**, or press **Ctrl+F** anywhere
in the main window, to open a small search window. The table remains usable while
this window is open; it takes no permanent space above the transcript.
Search keeps every row visible, highlights matching words, and selects/jumps to
the active hit, including matches inside long wrapped rows. It searches the
**Text** column within the current speaker filter, not names, timestamps, notes,
other sessions, or hidden speakers. Enable **Show only matching transcript rows**
to filter the main table to results, intersected with the selected speakers.
The transcript copy buttons then copy only those displayed rows. Session-menu
copy/export and AI summary input still use the whole transcript. Uncheck the
option or clear the query to restore rows; this never edits a recording.

Leave **Fuzzy** unchecked for literal, case-insensitive substring matching;
punctuation is literal, not a regular expression. Check it for local
character-level typo tolerance: queries of 3-7 characters allow one insertion,
deletion, substitution, or adjacent transposition; queries of 8 or more allow
up to two edits. One- or two-character queries stay exact to avoid excessive
false matches. Fuzzy search is **not semantic or pinyin search** and can return
approximate matches you did not intend.

Use the arrow buttons or **F3 / Shift+F3** for next/previous, wrapping at either
end. **Enter / Shift+Enter** also work while the search field has focus.
The counter identifies the current occurrence. **Escape** or the clear button
removes highlights and restores rows within the current speaker filter. Search navigation pauses live
auto-follow; use **Latest** or **Ctrl+End** in the table to resume it.
Closing the search window keeps its query, Fuzzy choice, row-filter choice, and highlights for
**F3 / Shift+F3** navigation. Reopening restores the same search.

Search runs locally without a model/download or network request and never
changes transcript text. Queries are trimmed, Chinese query text is normalized
to Simplified Chinese, and matches stay within individual displayed rows.
The field allows up to 128 UTF-16 code units. New transcript text refreshes
results while retaining the current occurrence when possible. Opening another
session clears the query; search text is not saved.

### Replay transcript audio

After **Stop**, double-click a **Time** cell to play just that transcript row.
For a larger range, click the first time, Shift-click the last time, then choose
**Play selected range** (also available in the row menu). Ctrl-click selections
use the earliest selected start through the latest selected end. Playback stops
automatically at that end; pause, resume, stop, and progress controls appear
while listening. Drag or click the replay slider to seek inside the selected
range; arrow keys move one second and Page Up/Down move five seconds. Seeking
flushes old buffered audio and preserves the paused/playing state. Seeking to
the end finishes replay.

The range is continuous original audio, including intervening voices hidden by
a speaker filter; filtering does not isolate a voice acoustically. Boundaries
use the saved precise transcript intervals, which may be approximate ASR timing.
Audio is read from the safety WAV/RF64 in a bounded stream, without loading the
whole recording or modifying it, and uses the Windows default playback device.

Replay is disabled while recording **or paused** to prevent loopback from
recording the replay. Starting a recording, changing sessions, creating a new
workspace, deleting the session, or closing the app stops replay and releases
the file. Interrupted sessions must be recovered first. MP3 export does not
remove the safety WAV used for timestamp replay.

### Review unclear transcription before main points

Right-click the Transcript and choose **Review unclear transcript** to check the
complete, finished saved transcript, regardless of selected rows or display
filters. The selected LLM checks names, terms, numbers, and wording that may need
confirmation; explicit markers such as `(unclear)` or `（待确认）` are also collected
locally. This is a text-based aid, not acoustic confidence or proof of an error.
It may miss mistakes or flag legitimate wording, and never assumes a replacement
spelling such as changing a tool's name automatically.

The list shows each affected passage's timestamp, speaker, original text, quoted
uncertainties, and reasons. Replay uses the saved audio locally with up to one
second of surrounding context. Edit **Corrected text** to select a correction,
or clear **Correct** to keep that passage. **Apply selected corrections & continue**
saves only selected edits; **Keep all unchanged & continue** leaves all wording
untouched. Closing or cancelling applies nothing and stops the current AI task
before it proceeds to main points. **Check again with this LLM** explicitly
refreshes a completed analysis and asks before discarding unsaved edits.
An incomplete analysis instead offers **Retry review** or **Resume unfinished
review**; resuming keeps completed parts and sends only the unfinished parts.

**Settings > Speech > Review unclear transcript automatically** is **on by
default**. It runs after final transcription/speaker processing and before final
main points, never during recording, live text, or live summaries. A completed
check with no findings continues without a popup. Otherwise, review pauses the
task; a requested summary uses the approved saved text afterward. Manual review
does not itself request a summary. Confirmed decisions prevent another automatic
prompt for the unchanged transcript, including after changing summary models;
changed content or speaker assignments can require another review. A configurable
keyboard shortcut is available, unassigned by default.

The LLM pass uses the existing local/online provider and consent settings, and
adds model requests and processing time. It sends transcript text and speaker
labels, never audio or editable **My notes**. Missing model setup does not block
recording or force optional wizard steps: explicit markers remain available and
the application warns when no LLM check was possible. Failed/incomplete checks
are labeled; retry, deliberately continue, or cancel rather than treating partial
results as a clean transcript. Completed batches are saved locally for resumption.
Review responses preserve exact source quotes rather than treating the JSON as
summary prose. Literal line breaks/tabs inside JSON strings are safely escaped
without changing their decoded text. Other invalid formatting or mismatched
quotes trigger one visible retry of that part, never an endless retry or an
automatic transcript correction. A second invalid response leaves the review
explicitly incomplete.

### Correct text and re-transcribe selected audio

After recording and final transcription finish, select one **Time/Speaker** row,
Shift-select a range, or Ctrl-select separate rows. Right-click and choose
**Re-transcribe selection** to recognize those intervals again locally. Use
**Edit text** or **F2** to correct wording without running recognition.

The review dialog shows saved timing, speaker, **Local / saved transcript**, and
**Corrected text**. The saved-text column stays unchanged throughout the dialog;
recognition and manual edits update only Corrected text. Row colors compare the
saved text with the corrected draft: pink for differences, green for exact
matches, and gray for missing/unusable results. Untouched rows stay neutral;
pending rows keep their existing draft and color. Colors update immediately as
you edit. These use the same
configurable light/dark comparison colors as online results. Switching to
**Compare results** compares saved text with online text instead.
A displayed row can contain several saved Whisper segments;
these appear separately so **every existing timestamp and speaker ID stays
unchanged**. Recognition processes only the selected underlying intervals, not
the continuous span between them or rows hidden by a speaker filter. It cannot
isolate overlapping voices inside those intervals. Very short segments may have
less acoustic context than the original transcription.

Choose an already-downloaded speech model and the correct language for this
attempt; these choices do not change global model/language preferences. Review
the proposed text, make any further edits, then click **Save transcript changes**.
Intervals with no recognized text keep their current draft wording and are
marked gray. **Stop** interrupts recognition and enables review and saving of
finished rows; it then becomes **Continue**. Continue keeps manual edits to
finished rows and processes only the remaining rows/chunks, retrying an
interrupted chunk rather than repeating completed work. If stopped inside a
long row, its completed excerpt is shown separately as a read-only preview;
the row's existing text is not replaced by an incomplete prefix.

The resumable checkpoint stays in memory in this dialog. Model, language, and
vocabulary controls remain locked until the run finishes or **Discard preview**
restores its pre-run draft and comparison states. Cancel during recognition also restores that draft.
Failures retain completed work for review/retry but never save it automatically.
**Save transcript changes** is enabled while stopped and preserves unfinished
rows unless you edit them yourself. Closing the dialog discards unsaved work
and its in-memory checkpoint.

Saving updates only the transcript JSON/text, never the audio, MP3, speaker
labels, timestamps, personal notes, or unselected segments. Each segment needs
nonblank text; use **Silence selected...** to exclude unwanted content instead.
Existing AI main points remain visible but become outdated; **Update main
points** regenerates them from the corrected transcript. Editing does not send
an automatic summary request. Reviewed corrections are protected against later
full model/language regeneration; use selected re-transcription instead, or
clear the speaker filter and select all visible rows to review everything.
Saving replaces the saved text in place, and reopening the dialog uses that text
as the new baseline; this is not an edit-history or undo system.

### Correct or merge speakers

After recording and processing finish, select one or more rows, then choose
**Correct speaker...** from the row menu (default **Ctrl+Shift+K**). Pick an
existing speaker. Normally only the selected underlying segments are reassigned.
Check **Merge all of ...** to reassign every segment of the first selected
speaker, including segments outside the current view or selection.

The target keeps its name and color; unused source names/colors are removed.
Audio, wording, exact timing, and reviewed-text flags stay unchanged. Saving
changes the transcript revision and marks prior summaries outdated; it does not
request an AI summary or teach a voice-identification model. Double-clicking a
speaker still opens the color picker. This correction is separate from renaming.

Main points show a supplied time/range on one line and its note on the next
line with exactly two spaces of indentation. New summary requests include the
actual start and end of each source segment. Display formatting does not invent
timestamps or rewrite saved summaries just by opening them. Provider/model
information is available by hovering over the Main points heading.

### Remember names and terminology for future recognition

In the correction dialog, expand **Remembered vocabulary (optional)**. Highlight
a short corrected name or phrase in an editable cell, click **Add highlighted
words to vocabulary**, then **Save vocabulary separately**. You can also type
one term per line or manage the list in **Settings > Transcription**. Keep
**Use local vocabulary hints for speech recognition** enabled to use the list.

Vocabulary and transcript changes have **separate save actions**. You can try
unsaved vocabulary during re-transcription; saving transcript changes does not
save those vocabulary edits. Cancelling transcript edits does not undo
vocabulary that you explicitly saved separately. Settings changes still require
the normal Settings Save action.

Hints guide local live, final, and selected recognition; **they do not train
Whisper, guarantee a spelling, or replace words automatically**. Up to 200 terms
are saved, with at most 160 UTF-8 bytes per term. A conservative 200-byte prompt
window uses the most recently listed terms that fit. The UI shows how many fit
and the actual hint text; all other terms remain saved. Prefer a few relevant
names and technical terms, not whole sentences or transcripts. Irrelevant hints
can bias recognition incorrectly.

For accuracy, also select English or Chinese rather than automatic detection
when appropriate, try the already-installed Small or Large-v3 Turbo Q5 model,
and reduce noise/overlapping speech. A larger model or hints may help but cannot
reliably recover unclear audio. New vocabulary does not automatically rewrite
completed old transcripts; re-transcribe the affected selection to try it.

### Silence unwanted audio and remove transcript rows

After recording and final transcription finish, select the **Time/Speaker** rows
you want excluded, then click **Silence selected...** (also in the right-click menu).
To silence one speaker's sections, apply the speaker filter, right-click a row,
choose **Select all visible rows**, and then **Silence selected...**. Copying or
filtering alone still does not change the text sent for a full-session summary.

The preview lists every interval and the original, silenced, and resulting
durations. Every operation requires an explicit choice: **Edit this recording**
keeps the same history entry, ID, title, folder, and output profile; **Create a
copy** lets you choose a new name and WAV/MP3, leaving the current recording
unchanged. Neither choice is preselected. Copy settings you type are retained
if you switch between the choices. The dialog scrolls at small sizes while
Apply/Cancel remain available.

For ordinary reversible cleanup, editing the current recording avoids library
clutter. It does **not** eliminate backup storage: a hidden lossless recovery
copy must be retained. Choose a separate copy when you want independently
browsable versions.

Each selected interval is replaced with **equal-length silence**, not cut out.
The safety WAV keeps exactly the same frame count, duration, and timestamps. Unselected
samples, retained transcript timestamps, device-event times, and note time
references stay at their original positions. Selected transcript text is removed,
leaving timestamp gaps. MP3 output is newly encoded from the silenced WAV; it has
the same intended timeline, with normal codec padding/boundary effects.

Silencing mutes **every sound inside the selected intervals**, not just one
voice from simultaneous speakers. This version edits whole rows, not individual
words. If another transcript segment crosses a silence boundary, include that
row too; the app will not guess which words remain audible. Timing accuracy is
limited by the existing ASR timestamps. Selecting the entire transcript is
allowed: the resulting recording can contain only silence and no text, with its
duration intact.

The edited recording has a new transcript revision and old AI summary state is
cleared. Click **Update main points** to summarize only its remaining text.
Personal notes and generated-section ownership tracking are preserved, or copied
unchanged when creating a copy; editable notes are never sent to the summarizer.
Session metadata records cumulative muted intervals in `Working` edit mode.
A fixed `.restore-source` folder inside the recording keeps the unmuted audio
and transcript needed for selective restoration. Keep it with the recording.
Creating a copy of already-muted audio carries earlier recovery sound and text
into the new copy as well; it does not use silence as its original backup.
This is not secure erasure. To share muted sound, export the muted audio file,
not the entire recording folder containing recoverable original material.
Older cut-based copies keep their existing shortened audio/timeline and
are recognized as `Cut` edits; they are not silently converted or relabeled.
To preserve the original timing for an older edit, create a new silenced copy
from its original recording.

Editing uses a bounded streaming buffer and publishes changes only after the
audio/transcript and configured output are complete. Errors and cancellation
before publication leave the current recording unchanged. An unexpected process/OS exit can
leave a `.edit-<GUID>` staging folder in the recording library; incomplete
staging folders are not shown as recordings. Creating a separate copy needs
space for its edited WAV, its fixed unmuted recovery WAV, and configured output.
In-place changes use temporary files and a rollback journal inside that folder;
history loading recovers interrupted publications before showing the recording.
Temporary publication requires additional disk space but does not add permanent
history entries. In-place editing keeps the recording's output format/sample rate/channels;
MP3 or non-default WAV output is regenerated when its audio changes. Old summaries,
outdated optional exports, and stale processing checkpoints are invalidated.

### Restore muted audio and transcript text

Open a muted recording and click **Restore muted sections...** in the Transcript
header, use the same command in the recording's menu, or right-click the
transcript and choose **Show muted rows...**. The dialog lists all currently
muted rows with checkboxes, timestamps, original text/speaker, and **Play original**.
Adjacent muted rows can be restored separately even when their mute intervals
were merged. Gaps without saved text are included. Nothing is preselected.
Original-audio preview supports pause, seek, and stop; it does not select a row,
restore/save audio, or create another recording. Click rows to
toggle them, or choose **Restore all (select all)**, then **Restore selected
sections**. You can also recover a fully silenced recording with an empty transcript.

Selected audio and transcript text return at their original times in the **same
recording**, without creating another file in history. Unselected sections
remain muted and the hidden recovery backup stays unchanged; duration, retained audio
samples, device-event times, and note timestamps do not move. Restore all includes earlier silence edits, not
just the latest operation. Recovery never crosses an older `Cut` edit's changed
timeline. MP3 is newly encoded from the recovered safety WAV.

New working copies use their internal `.restore-source` data. For existing
`Silence`/`Restore` copies from older releases, **keep the original and intervening
edited-session folders** until the current leaf copy has been updated/upgraded.
Their source history is searched in the configured library and current session's
parent, not arbitrary disks. If a source is missing, restore its complete folder
from backup or Recycle Bin first. Updating an older copy that still has dependent
edited recordings is refused to protect their recovery history; select the latest
copy instead. Silence alone has no recoverable sound: deleting all recovery sources
makes restoration impossible.

Compatible current text corrections, speaker names, and speaker colors are
retained. Recovered speakers regain their original colors where available.
The recording's current shared Not labeled color, including a neutral reset,
is retained rather than restoring an older preference from the recovery source. If later
transcription or speaker analysis is incompatible with the source, the dialog
warns that the **edited recording's transcript** will be rebuilt from its recovery
source, excluding still-muted intervals. If a source
segment crosses a remaining mute boundary, select the related intervals or
Restore all; recovery will not guess which words belong on either side.

Personal notes are kept unchanged. Old AI summaries are cleared:
click **Update main points** to include the restored text.
Remaining muted text and personal notes are excluded from summary input.
Recovery is local, cancellable, streamed with bounded memory, and published only
after all output files are complete. It requires temporary space for a full-duration
safety WAV and configured output, not another permanent recording entry.

### Local speaker labels

Enable **Identify speakers after final transcription** and download the speaker
models in Settings (about 45 MiB): Pyannote segmentation 3.0 and 3D-Speaker
ERes2Net, run by sherpa-onnx in an isolated CPU worker. No Python is required.
Recognition/recording can still run if these optional models are absent.

Labels are assigned `spkr1`, `spkr2`, etc., in first-occurrence order **after
Stop**, not as live identity claims. Right-click a labeled row to rename its
speaker. All occurrences, saved TXT output, copy/export output, and matching
summary references refresh; personal notes and already exported external files
are not rewritten. Double-click the Speaker column to set a per-recording color
without changing names or summary text. Full reanalysis asks before replacing
labels, custom names, and labeled-speaker colors, including colors changed while
it runs: newly numbered labels may refer to different voices and start neutral.
The shared Not labeled color is retained. If labels change while a named
speaker's color picker is open, applying that choice is refused with a message
to select the speaker again, rather than coloring a different voice by mistake.

Automatic speaker counts are approximate: a voice may be split, two voices may
be merged, and overlapping/ambiguous text can remain unlabeled. Set the expected
count when known (for example, **1** for a single presenter). Labels are not
verified identities and are less reliable on short, noisy, or simultaneous
speech. Speaker analysis needs whole-recording audio plus a quadratic clustering
matrix; memory/native-index limits are checked before allocation. Unsupported
long recordings receive an explicit speaker-analysis error, without affecting
their saved audio or chunked transcription.

### Choosing a speech model

**Small** is a reasonable balanced Chinese/English model, particularly on CPU.
**Large-v3 Turbo Q5** is an optional stronger model when a compatible GPU is
available; better recognition is not guaranteed for every clip. These are
speech models, separate from the LLM used to summarize.

Fast Base drafts deliberately use CPU to avoid first-use GPU shader startup.
The accelerator setting applies to final transcription, or live transcription
when fast drafts are disabled. A Vulkan first inference can take tens of
seconds to warm up; subsequent requests can be much faster. The transcript
title's tooltip shows the actual model and backend, with a draft/final indicator.
Selecting the known speech language usually helps short-clip accuracy.

Use headphones when recording both sources to avoid capturing speaker audio
twice through the microphone. Gain controls are in Settings. Audio clipping,
late packets, device failures, and processing failures are surfaced in the UI.
Recording other people may require their consent; comply with applicable laws.

## Chinese interface and speech recognition

In **Settings > Appearance > Interface language**, choose **Simplified Chinese**
(the option is displayed in Chinese), then **Save settings**. The main window,
settings, controls, and application status messages change immediately without
restarting. English remains available, and the selection is remembered.
Existing installations keep their English interface until this is changed.

Under **Settings > Transcription > Speech language**, choose **Simplified Chinese (zh)** to
recognize Chinese speech with the installed multilingual Base, Small, or Turbo model.
**Automatic detection (auto)** and **English (en)** are also available; the
selector offers only these choices, not arbitrary language codes. Interface and
speech languages are independent, and changing them does not enable recording,
cloud sharing, or automatic summaries.

Chinese transcripts and AI summaries always use **Simplified Chinese**, including
live text, saved JSON/TXT/Markdown, and summary headings. A bundled, offline OpenCC
phrase dictionary converts Traditional Chinese emitted by Whisper, Ollama, or
Copilot; this does not depend on a model following a prompt. English remains
English, including English words in mixed-language speech. There is no Traditional
Chinese output setting, and no additional download or cloud request is required
for script conversion.

Older transcripts and summaries are also displayed in Simplified Chinese when
opened. Opening them does not rewrite their original files; resumed transcription
and successful summary updates save the normalized text. Existing custom speech
codes remain loadable for compatibility but are not offered by Settings.
Original audio, recording titles, and personal notes are not rewritten.
Device names and technical errors from Windows or the CLI remain as reported.

## Files, long recordings, and recovery

Each recording has a timestamped subfolder under the selected saving folder:

| File | Purpose |
| --- | --- |
| `audio.wav` | Continuous 48 kHz, stereo, PCM16 safety recording |
| `audio-output.wav` | Additional WAV when 44.1 kHz and/or mono output is selected |
| `audio.mp3` | Optional 192 kbps MP3 at the recording's chosen sample rate/channels |
| `session.json` | Recording metadata, output profile, device-change history, and processing progress |
| `transcript.json` / `transcript.txt` | Timestamped local transcript and resumable progress |
| `transcript.refining.json` | Temporary resumable final-pass checkpoint; the draft remains intact until promotion |
| `summary.json` / `summary.md` | AI summary and incremental summary state |
| `pending-processing.json` | Unfinished AI/export steps and the next-launch resume choice |
| `notes.md` | Your independently editable notes |

The lossless safety WAV is deliberately retained with every output profile.
Recording, transcription, replay, editing, and recovery continue to use that
48 kHz stereo file. After Stop, the chosen output
is generated in a bounded stream. Default WAV needs no extra file. If conversion
fails, the primary recording remains available and independent transcription
can continue; use **Export audio** to retry the session's saved profile.
The session menu can additionally export MP3.

Closing during recording, AI processing, or export asks for confirmation instead
of blocking with a wait-for-save message. **No** leaves the running work alone.
**Yes** stops capture, finalizes the safety WAV, cancels AI/export workers, and
remembers unfinished steps before exiting. It waits only for safe worker cleanup
and atomic file publication, not for the remaining AI analysis or a complete
audio conversion. Existing published exports, completed transcripts/main points,
and personal notes are retained. If saving the unfinished-work plan fails, the
app stays open and reports the error so closing can be retried safely.

On the next launch, recordings in the current saving folder with newly deferred
work offer **Resume unfinished work**. **Yes** resumes using the recorded model,
language, vocabulary, and automatic-analysis choices with current credentials and
sharing consent. Completed speech chunks, refinement, main-point batches, and
overall summaries reuse their existing checkpoints; the interrupted chunk,
speaker pass, or model request may need to run again. Cancelled audio exports
restart from the retained WAV. **No** starts no analysis, changes no audio or
completed results, and suppresses repeated startup offers for that paused plan.
Select the recording and use **Resume unfinished work** in the usual transcription
button or session menu whenever ready. The plan disappears when its steps finish.
Unsaved text in correction dialogs is not silently applied or made into a queued
selection-edit job.

Resuming an already-complete, unchanged summary checkpoint does not rewrite it
or make another AI request. A missing or stale Markdown export can be repaired
from that checkpoint without resending the transcript. The last completed batch
is not saved a second time just to finish the operation. The manual **Update main
points** action instead offers the explicit regeneration confirmation described
above; an accepted regeneration retains its own generation identifier in the
unfinished-work plan.

Atomic saves briefly retry temporary Windows file-sharing locks, including
audio-export publication. They do not truncate the existing file, force-close
other programs, change permissions, or remove read-only protection. Persistent
failures identify the target filename and full path; the full message is also
available in the warning tooltip. Close any application holding that file and
check the folder's write access before retrying. Warnings show when they occurred,
and a successful retry of the same AI action/session clears its earlier error without
hiding a different or newer warning.

The safety WAV consumes about **0.69 GB per hour**, plus the additional output:
approximately 86 MB/hour for a 192 kbps MP3, or 0.32-0.64 GB/hour for custom
PCM16 WAV. Choose a local NTFS/ReFS volume with sufficient
space; FAT32 cannot hold an individual file larger than 4 GB.

Memory usage for audio capture is bounded. Input timestamps share the Windows
performance clock, silent playback intervals remain silent, and the file header
is checkpointed with a durable flush about once per second. Files larger than
the RIFF limit automatically become **RF64 WAVs**; use an RF64-compatible player
for those files, or export MP3.

After an unexpected exit, select the interrupted session and click **Recover
WAV**. Recovery reconstructs lengths from complete PCM frames on disk without
discarding audio merely because the last header checkpoint was stale. A crash
or power loss can still lose data that had not reached durable storage.
Recovery does not claim to reconstruct missing/corrupt storage.

If one device fails, recording continues with the other working source. If all
sources fail, or storage cannot keep up, the session stops with an explicit
warning. The recording thread requests that Windows stay awake; manually
sleeping, hibernating, shutting down, or closing a laptop lid can still interrupt
recording. A long scheduling/sleep gap stops recording rather than filling hours
of artificial silence.

## AI and authentication

Audio is converted locally into 16 kHz mono PCM chunks for Whisper. Native
inference runs in a separate, lower-priority `AudioMemo.exe --speech-server`
process with a limited CPU thread count. It loads its model once per phase, not
once per chunk. Long recordings are transcribed from disk, not loaded into memory
in their entirety. Completed chunks are saved; cancellation or model failure can
be resumed without recording again. Live summaries run separately from speech
work, with at most one request in flight. Stop cancels unfinished live summary
work before final refinement, retaining successful summary checkpoints.
With **Automatically transcribe after stop** disabled, final processing waits
for an explicit **Transcribe / resume** instead; completed live main points remain.
Two-second chunk overlap reduces word-boundary losses.

### Local summaries with Ollama

Speech recognition and summarization use **different models**: Whisper turns
audio into text; an Ollama chat/instruct model turns that text into main points.
Installing one does not install the other.

1. Install [Ollama for Windows](https://ollama.com/download/windows) on each PC
   where local summaries will run. Use a current version for newer model families.
2. Download a model yourself, for example `ollama pull qwen2.5:7b`.
3. Start Ollama, then open **Settings > AI main points > Summary provider** and
   select **Ollama (local)**. The default server is `http://127.0.0.1:11434`.
   Use the server root, not an `/api` or `/v1` URL.
4. Click **Refresh local models**, select an installed model (or type its name),
   then **Test model and speed**. This uses a short built-in synthetic meeting,
   never your recordings or personal notes.
5. **Save settings**, then transcribe a saved recording and click
   **Update main points**. Automatic and live summaries use the selected provider.
   Ollama does not require Copilot login or cloud-sharing consent.

The Chinese UI has the same controls under **设置 > AI 摘要 > 摘要服务**. Expand the
local model guide for official model pages and copyable download commands.
AudioMemo does not install Ollama, download LLM weights, or accept model licenses
automatically. Ollama manages its own model storage, normally under
`%USERPROFILE%\.ollama\models`, independently of Whisper and AudioMemo's portable
folder.

Local mode permits only loopback HTTP/HTTPS endpoints, disables HTTP redirects
and proxies, and rejects cloud tags and model entries marked remote. It checks
that the selected model is downloaded before sending transcript text for
generation. There is **no automatic fallback to Copilot**. The Ollama installation
is separate trusted software: do not configure it or a replacement local server
to proxy private content to a cloud service. Tools are not supplied or executed.

The app reads RAM on the computer where it is running and warns if model weights
plus a modest 4 GiB reserve exceed currently available RAM. This is an advisory
screen, not a complete GPU/KV-cache capacity estimate; already loaded weights and
other applications can affect it. It also warns when a request exceeds **30
seconds**, or measured generation is below **5 tokens/second**. Cold loading and
server queues affect elapsed time. The speed test is not a guarantee of real-time
meeting performance.

Warnings never remove larger models from your choices. In Advanced settings,
context defaults to **32768 tokens**, with an editable range of 8192-1048576;
generation timeout defaults to **15 minutes**, with a 1-120 minute range. Use a
context supported by the model. More context uses more RAM/VRAM; smaller contexts
may not fit existing notes and the next transcript batch. Oversized inputs and
truncated/empty responses fail explicitly rather than silently replacing saved
notes. GPU selection/offload belongs to Ollama; inspect it with `ollama ps`.
For slower PCs, prefer a smaller Q4 instruct model and summarize after Stop.

### Typical local models and official download pages

These are options, **not an allowlist**. Any installed on-device Ollama model
that advertises text generation can be selected. Download sizes below are
approximate default-tag totals checked against Ollama's registry on 2026-10-13;
tags and quantizations can change. Runtime memory, context, and GPU buffers need
additional space. RAM ranges are planning estimates, not measured guarantees.

| Model / official page | Download | Suggested use / rough RAM planning |
| --- | --- | --- |
| [Qwen2.5 7B](https://ollama.com/library/qwen2.5:7b) | 4.36 GiB | Practical Chinese/English instruct starting point; 16-24 GiB RAM |
| [Qwen3 8B](https://ollama.com/library/qwen3:8b) | 4.87 GiB | Chinese/English option with thinking controls; 16-24 GiB RAM |
| [Qwen3.5 9B](https://ollama.com/library/qwen3.5:9b) | 6.10 GiB | Newer multilingual option; current Ollama, consider 24 GiB+ RAM |
| [DeepSeek-R1 8B](https://ollama.com/library/deepseek-r1:8b) | 4.87 GiB | Distilled reasoning model; 16-24 GiB RAM, possibly slower for concise notes |
| [DeepSeek-R1 14B](https://ollama.com/library/deepseek-r1:14b) | 8.37 GiB | More capacity; consider 24-32 GiB RAM |
| [GLM-4 9B](https://ollama.com/library/glm4:9b) | 5.08 GiB | Another Chinese-capable model; 16-24 GiB RAM |
| [Qwen3 30B-A3B](https://ollama.com/library/qwen3:30b) | 17.28 GiB | MoE option for a powerful PC; consider 32-64 GiB RAM |
| [GLM-4.7-Flash 30B-A3B](https://ollama.com/library/glm-4.7-flash) | 17.71 GiB | Chinese-capable MoE option; current Ollama, 32-64 GiB RAM |
| [DeepSeek-R1 32B](https://ollama.com/library/deepseek-r1:32b) | 18.49 GiB | Higher-capacity workstation option; consider 32-64 GiB RAM |
| [DeepSeek-R1 70B](https://ollama.com/library/deepseek-r1:70b) | 39.60 GiB | High-end workstation; consider 64-128 GiB RAM and substantial VRAM |
| [DeepSeek-R1 671B](https://ollama.com/library/deepseek-r1:671b) | 376.65 GiB | Large-memory/multi-GPU server class, not an ordinary desktop choice |

For example, use `ollama pull deepseek-r1:14b` or `ollama pull glm4:9b`, then
refresh the app's local model list. Downloading a MoE model still requires its
**entire** weight set, not just the active parameters. A large amount of system
RAM does not make CPU offload as fast as sufficient VRAM. Review model licenses,
including the base-model terms for distilled models, before redistribution or
commercial deployment. No model weights are bundled with AudioMemo.

Ollama thinking controls are discovered from `/api/show`: AudioMemo requests
thinking off when advertised, or a supported low level. Otherwise the model's
default applies. Reasoning traces are not treated as meeting notes. Reasoning
models can be slower and may hit the generation limit; an instruct model is often
the better first choice for ordinary meeting summaries.

### Chinese cloud summaries: Doubao, Qwen, and DeepSeek

These are direct connections to the providers' **official developer APIs**,
not automation of their consumer chat websites. A consumer account, chat
subscription, or Copilot login does not supply an API key or API credits.
Account activation, available models, region restrictions, and charges are
controlled by each provider.

| Provider | Official console / API keys | Model selection |
| --- | --- | --- |
| Doubao / 豆包 | [Volcengine Ark](https://console.volcengine.com/ark/region:ark+cn-beijing/apiKey) | Paste an activated text Model ID or your `ep-...` inference endpoint ID from the [official catalog/console](https://docs.volcengine.com/docs/ark/model-list?lang=zh). No account-specific ID is guessed. |
| Qwen / 千问 | [Alibaba Cloud Model Studio / Bailian](https://bailian.console.aliyun.com/) | Suggested IDs include `qwen3.7-plus`, `qwen3.8-flash`, `qwen3.8-max`, `qwen-plus`, and `qwen-flash`; check the [current catalog](https://www.alibabacloud.com/help/en/model-studio/text-generation-model) and your region's access. |
| DeepSeek | [DeepSeek API keys](https://platform.deepseek.com/api_keys) | Suggested IDs are `deepseek-flash` and `deepseek-v4-pro`. Use **Read available models** for your API's current list, or check the [official models/pricing](https://api-docs.deepseek.com/quick_start/pricing). |

1. Open **Settings > AI main points > Summary provider**, and choose the provider.
   In Chinese: **设置 > AI 摘要 > 摘要服务**.
2. Select the official base URL and a supported **text-chat** Model ID. Use a
   model supporting non-streaming chat completions, not an image, video, or
   embedding model. Suggested IDs are editable and do not guarantee account access.
3. Enter your API key in the masked field. Blank retains a configured key.
   Optionally click **Test sample (API charges may apply)**: this sends only
   built-in synthetic text, never your recordings, and checks the selected
   endpoint/key/model combination. It can incur API charges.
4. Approve sharing transcript text with the displayed provider and endpoint,
   then **Save settings**. Approval for Copilot or another provider/endpoint
   does not authorize this connection. Automatic/live summaries additionally
   require a configured, locally decryptable key and a selected model.
5. Transcribe a recording locally, then **Update main points**. Later model
   changes can be applied from the main toolbar. Recording and transcription
   remain usable without cloud credentials.

Doubao uses `https://ark.cn-beijing.volces.com/api/v3/`. Its model field starts
empty because the app cannot infer which text models or inference endpoints
your Ark account has activated.

Qwen's presets are Beijing
(`https://dashscope.aliyuncs.com/compatible-mode/v1/`) and Singapore
(`https://dashscope-intl.aliyuncs.com/compatible-mode/v1/`). **The key and endpoint
must match the region.** You may also paste your official workspace URL, such as
`https://YOUR-WORKSPACE.cn-beijing.maas.aliyuncs.com/compatible-mode/v1/`.
Workspace hosts in `cn-beijing`, `ap-southeast-1`, `us-east-1`, `cn-hongkong`,
and `ap-northeast-1` are supported. Use the URL from your console, not a guessed
workspace name. See the [official compatibility guide](https://www.alibabacloud.com/help/en/model-studio/compatibility-of-openai-with-dashscope).
Qwen and Doubao use catalog/manual IDs rather than automatic account model
discovery. DeepSeek defaults to `https://api.deepseek.com/` and also accepts
`https://api.deepseek.com/v1/`.

Keys are encrypted with **Windows DPAPI for your Windows account** before they
are stored in `%LOCALAPPDATA%\AudioMemo\settings.json`. They are bound to the
provider and exact canonical endpoint, including region/workspace/API path.
Changing endpoints never transfers a key or sharing permission. The settings
dialog never reveals a configured key; **Cancel** discards pending credential
and consent changes. **Forget key** removes only the selected endpoint's stored
key when settings are saved, not the provider's actual key. Re-enter credentials
if you move settings to another PC or Windows account. Do not share settings
backups as a way to distribute API access.

**Request fast, non-thinking summaries** is on by default. Clear it for an older
or thinking-only model that rejects the thinking option; the provider's default
then applies. Only final note text is saved, not separate reasoning fields.
Empty, refused, truncated, or oversized results are rejected without replacing
successful notes. There are no automatic retries, redirects, or provider
fallbacks. Generation has a five-minute deadline; discovery has a 30-second
deadline. Cancellation/timeouts cannot retract text already sent or guarantee
that provider processing or billing stops. Only transcript text and previous AI
main points are supplied, **not audio files or personal notes**.

### Copilot CLI summaries

Copilot CLI receives **transcript text and previous AI main points, not audio or
personal notes**. Long transcripts are summarized incrementally. Spoken
instructions are treated as untrusted meeting content; all model tools are
filtered out, shell/read/write/URL permissions are denied, built-in MCPs and
configured user MCP servers are disabled, and custom instructions are excluded.
The CLI's existing provider, account, billing, and data-retention policies still
apply. No promise is made that another program's globally configured hooks,
plugins, or telemetry are disabled; use a trusted CLI installation/profile.

AudioMemo reads `COPILOT_HOME\config.json`, or `%USERPROFILE%\.copilot\config.json`
by default, including the CLI's commented-JSON format. It honors `COPILOT_MODEL`,
reads recent model IDs, and queries `copilot help config` for model choices.
**Use current CLI default** passes no model override. A recent model is not
mistaken for a pinned default. An explicit AudioMemo model choice does not
rewrite CLI settings. Custom provider environment variables are inherited.

Install the official [Copilot CLI](https://docs.github.com/copilot/how-tos/copilot-cli).
Use **Settings > Sign in with Copilot CLI**, or run `copilot login`. Complete any
browser/device authentication yourself. The CLI manages credentials; AudioMemo
does not extract, copy, or store them. A custom provider uses its existing CLI
authentication rather than GitHub login. An installed `copilot.exe` is required;
shell-based npm `.cmd` shims are intentionally not executed with transcript data.

Live text and live main points are best-effort, not guaranteed real-time captions.
On slow CPUs or networks they lag while the audio continues recording normally.
Whisper can misrecognize speech, especially in noisy, quiet, or multilingual
recordings. Select a language explicitly or use Small for better accuracy.
AI main points are provisional and should be checked against the recording.
This version does not provide acoustic echo cancellation or arbitrary
imported-audio transcription.

AudioMemo settings and Whisper weights live in `%LOCALAPPDATA%\AudioMemo`. Recordings are
ordinary local files, not encrypted by the application; protect them with Windows
permissions and storage encryption as needed. See [PRIVACY.md](PRIVACY.md).

### Summary recovery

Successful summary batches are checkpointed in `summary.json` and `summary.md`.
Cancelling or encountering a later provider error preserves completed batches;
**Update main points** resumes from the saved position, including inside an
unusually long transcript segment. Provider/model/server changes start a fresh
summary without deleting the old one before a replacement succeeds. Replacing a
draft with final text or reanalyzing speakers changes the transcript revision,
invalidating old summary offsets. An outdated summary stays visible with an
update notice; its speaker references are not silently remapped to new voices.
A provisional
live summary can be finalized after final transcription completes even if no new
words were added.
Checkpoints created before the Simplified Chinese output policy are rebuilt on
the next requested summary update, since converted text can change saved character
offsets. Old summary files remain intact until a replacement succeeds.
Personal notes remain a separate file and are never included in either provider's
input.

## Build and test

Install the .NET 10 SDK on Windows 11:

```powershell
.\scripts\Get-FFmpeg.ps1
dotnet restore AudioMemo.sln
dotnet test AudioMemo.sln --no-restore
dotnet run --project src\AudioMemo\AudioMemo.csproj
```

Generated FFmpeg executables, libraries, downloaded source archives, and their
generated notices are not committed. `Get-FFmpeg.ps1` recreates them using the
URLs and checksums in `dependencies\ffmpeg.json`; the handwritten
`AudioMemo-FFmpeg-notice.txt` remains in source control. Build outputs, installers,
recordings, personal settings, and credentials must not be committed.

The included Windows x64 speaker DLL is a pinned sherpa-onnx 1.13.8 build with
**TTS disabled**, excluding unused Piper/eSpeak-ng code from the app. The stock
native package is explicitly excluded; its ONNX Runtime DLL is retained.
Ordinary .NET builds use the included no-TTS DLL and need no C++ compiler.
To rebuild it, use an existing Visual Studio installation with x64 C++ and
CMake/Ninja tools:

```powershell
.\scripts\Build-SpeakerRuntime.ps1
```

The script verifies the pinned source archive and uses upstream checksum-pinned
native dependencies. It does not install a compiler or Python. Review the
original dependency notices and rerun speaker/portable checks after rebuilding;
see `src\AudioMemo\Licenses\Speaker-runtime-build.txt` for source attribution.

Tests cover mixing and silence, bounded buffers, pause/resume, device changes and
failures, WAV/RF64 headers and recovery, actual Media Foundation MP3 encoding,
Whisper-compatible speech chunks, commented CLI configuration, transcript
overlap, language settings and translations, and long-transcript batching. They do not automatically capture a
physical microphone or playback endpoint.
Video-import checks generate tiny local files across supported container families
and verify audio-track selection, timing/downmix, cancellation, source preservation,
missing dependencies, and malformed-media handling without opening any device.
Chinese-script checks cover phrase-sensitive conversion, English/format
preservation, legacy text display, summary checkpoint migration, and saved output.
Workspace checks cover auto-follow/scroll-back, grouped text/copy formats,
speaker-label alignment/renaming, revision-aware summaries, safe session-path
validation, and the compact English/Chinese UI.
Review checks also cover speaker-filter selection/copy scope, the five-second
history-preview timer, exact replay byte ranges, file release, output failures,
and timestamp/range interactions. Set `AUDIOMEMO_TEST_REPLAY=1` to exercise the
real default playback device with a short generated **silent** clip; it never
opens a microphone.
Editing checks cover sample-exact retained audio and zero-filled muted spans,
unchanged timestamps/duration, legacy edit modes, source-file preservation, new MP3 output, cancellation,
stale-source rejection, hidden staging folders, and summary inputs that exclude
removed text and old summary context.
Correction checks cover exact selected-segment audio, immutable timing/speakers,
editable-cell commits, preview/cancel behavior, separate vocabulary saves,
stale-source and failed-commit protection, and the exact vocabulary byte/input
limits. These ordinary checks use synthetic audio and fake recognition workers,
not personal recordings or model downloads.

Optional integration tests use a locally generated speech sample and the current
CLI login. They are skipped unless explicitly enabled:

```powershell
$env:AUDIOMEMO_TEST_SPEECH = 'C:\test-data\synthetic-16khz-mono.wav'
$env:AUDIOMEMO_TEST_CHINESE_SPEECH = 'C:\test-data\synthetic-chinese-16khz-mono.wav'
$env:AUDIOMEMO_TEST_COPILOT = '1' # Sends only a built-in synthetic meeting; uses CLI credits.
dotnet test AudioMemo.sln --no-restore
```

The optional speech tests also check warm-worker reuse/cancellation, the native
vocabulary-hint handoff, and the Base-draft/Small-final pipeline; install both
models for these checks. The English sample should mention Friday, slides, and
the budget.
The optional Chinese speech sample should mention a meeting on Friday and an
approved budget. It verifies the Chinese language code reaches the real local
Whisper worker and that Chinese text is preserved when saved.

Local-provider tests use in-memory API responses for model discovery, routing,
cloud-model rejection, error handling, cancellation, speed/memory thresholds,
and resumable summary state. They do not install Ollama or download LLM weights.

Chinese cloud-provider tests use fake HTTP responses and synthetic keys. They
cover official endpoint restrictions, Windows-encrypted credential round trips,
provider/region consent isolation, request shapes, final Simplified Chinese
output, cancellation, error handling, and settings/main-window UI behavior.
They do not contact the APIs, incur charges, or establish that a real account
has model access. Use the explicit sample test in Settings for that check.

After installing Ollama and a local model yourself, opt in to a real synthetic
summary check (no private recording is used):

```powershell
$env:AUDIOMEMO_TEST_OLLAMA_MODEL = 'qwen2.5:7b'
dotnet test AudioMemo.sln --no-restore --filter 'Category=LocalLlm'
```

Publish a portable self-contained folder:

```powershell
.\scripts\Get-FFmpeg.ps1
.\scripts\Publish.ps1
```

`Get-FFmpeg.ps1` prepares the checksum-pinned LGPL shared Windows x64 build in
`dependencies\ffmpeg.json`; it does not install anything globally or change PATH.
The build requires these tools, just as it requires the native speaker runtime.
Only FFmpeg, ffprobe, and their shared libraries are bundled under
`support\Runtime\FFmpeg`. License texts, the exact FFmpeg source archive,
build recipes, and version/checksum information are in `support\Licenses\FFmpeg`.
Retain these materials and review FFmpeg and external-library license obligations
before redistribution or Store submission.

`Publish.ps1 -NuGetConfig C:\path\NuGet.Config` supports an approved alternative
package source when a network blocks NuGet.org. No package feeds or credentials
are embedded in the project. Use `-NoRestore` when the required dependencies are
already restored. Publishing creates the compact executable-plus-`support`
layout with `Runtime`, `Docs`, and `Licenses` categories. `-OutputDirectory`
must name a new or empty folder so stale files from earlier layouts cannot
remain beside the new categories. Existing files are never deleted automatically.
Publishing over a running release is blocked; prepare each update in a fresh
folder such as `-OutputDirectory artifacts\app-next`. Only Windows x64
Whisper CPU, no-AVX, and Vulkan native assets are included in the portable output.

To check the published layout, bundled runtime, and relocation to a folder with
spaces and Chinese characters, without opening the UI or recording audio:

```powershell
$env:AUDIOMEMO_TEST_PORTABLE = (Resolve-Path 'artifacts\app\AudioMemo.exe').Path
dotnet test AudioMemo.sln --no-restore --filter 'Category=Portable'
```

## Local release versions

`Directory.Build.props` contains the single `<Version>` for application releases.
Advance it before delivering an update: normally a patch increment for fixes or
a minor increment for a new feature. About, managed assembly versions, and
Windows file metadata use this value. Setup reads the published file version;
MSIX does the same unless an explicit Partner Center `-Version` is supplied.
The version in the MSIX source template is only a placeholder.

Release-number changes are local by default. Do not automatically commit, tag,
push, or publish a GitHub release; those operations require a request for the
current update. A previous push request does not authorize future pushes.
See `AGENTS.md` for the project release and data-preservation policy.

## Windows Setup.exe installer

For a conventional Windows 11 x64 installation, use
`artifacts\packages\AudioMemo-<version>-Setup-x64.exe`. Setup supports English and
Simplified Chinese and installs for the current user in
`%LOCALAPPDATA%\Programs\AudioMemo`, without administrator elevation. It creates
a Start menu shortcut; the desktop shortcut is optional. The application is
not started automatically. The installer is offline and includes the same
self-contained application, native engines, FFmpeg, documentation, and licenses
as the portable release.

Close AudioMemo normally before installing or uninstalling. Setup checks the
application's existing per-user session mutex and never force-closes it.
Settings/models in `%LOCALAPPDATA%\AudioMemo`, your selected recording folder,
and existing portable copies are not moved or deleted. On a new PC, the normal
setup wizard still requires a working audio source and saving folder; speech
models, LLMs/accounts, and the optional Visual C++ speech prerequisite are
configured separately. They are not silently downloaded by the installer.

Uninstall through **Windows Settings > Apps > Installed apps > AudioMemo**.
Only explicitly packaged program files and installer-created shortcuts are
removed. Recordings, preferences, models, message history, and user-added files
are retained. Nonempty folders are never recursively removed. Do not use the
program installation folder as a recording or backup folder.

The development installer is **unsigned**. Unlike MSIX it does not require
installing a publisher certificate, but Windows SmartScreen or organization
policies may still warn about or block it. Packaging does not change those
protections. A trusted code-signing certificate is needed for signed
distribution. The adjacent `.sha256` and `.manifest.json` files identify the
installer and its payload; a checksum does not prove publisher identity.

To rebuild from an existing portable publish, use PowerShell 7 and the official
[NSIS 3.13 or later](https://nsis.sourceforge.io/Download). The standalone NSIS
ZIP can be extracted without installing the compiler:

```powershell
.\scripts\Package-Setup.ps1 -CompilerPath 'C:\BuildTools\nsis-3.13\makensis.exe'
```

`-PublishedDirectory` defaults to `artifacts\app`; `-OutputDirectory` defaults to
`artifacts\packages`. Existing packages are not overwritten. The build snapshots
the portable files into an isolated staging folder, adds current docs and NSIS
notices, and emits exact payload hashes and explicit uninstall file entries.
It does not install or launch the result. See `packaging\INSTALLATION.txt` for
bilingual recipient instructions. Test installation, updates, and uninstallation
on a clean Windows 11 PC before broad distribution.

## Microsoft Store / MSIX

WPF desktop applications can be distributed through the Microsoft Store using
MSIX. This project includes application icons, a Windows 11 x64 manifest, and
packaging scripts. Install the Windows 11 SDK, publish, then run:

```powershell
.\scripts\Package-MSIX.ps1
```

The development output is an **unsigned** `artifacts\packages\AudioMemo-<major.minor.patch.revision>-x64.msix`.
This does not install anything or alter certificate trust. For Partner Center,
reserve the app name and pass its exact identity information:

```powershell
.\scripts\Package-MSIX.ps1 `
  -IdentityName 'YOUR.PARTNER.CENTER.IDENTITY' `
  -Publisher 'CN=YOUR-PARTNER-CENTER-PUBLISHER-ID' `
  -PublisherDisplayName 'Your publisher name' `
  -Version '1.0.0.0'
```

For local sideload testing, use `-CertificateThumbprint` with an existing trusted
code-signing certificate whose subject matches `-Publisher`. Do not distribute
private signing keys. The Store signs accepted submissions.

Before publishing, supply your publisher/support details and a public privacy
policy URL, review and include all dependency notices, run Windows App
Certification Kit, validate installation/update/uninstallation, and test on a
clean Windows 11 machine. Declare and justify the desktop `runFullTrust`
capability and microphone access. Document the optional external Copilot CLI and
Ollama dependencies and Visual C++ runtime prerequisite in the listing. MSIX packaging is
provided; Store certification, signing, submission, and approval are not
performed by this project.

## Source layout

`src\AudioMemo.Core` contains audio storage/mixing, data models, transcript
merging, and summary prompts. `src\AudioMemo` contains WPF, WASAPI capture,
Media Foundation export, model management, the isolated speech worker, and CLI
and Ollama integration. `Resources\AppStyles.xaml` holds the shared WPF theme.
`tests\AudioMemo.Tests` holds automated checks. `scripts` and
`packaging` hold portable, Setup.exe, and MSIX delivery tooling.

## Product direction

The current personal-use release prioritizes durable recording, recoverable
transcription/summaries, explicit local-versus-Copilot selection, and guided local
model setup. It is not a claim of Store certification or validated performance
for every computer/model combination.

Useful next additions, not yet implemented:

| Priority | Feature | Benefit |
| --- | --- | --- |
| Next | Recording search and tags | Find and organize a growing meeting library |
| Next | Markdown and SRT export | Reuse notes and subtitles in more formats |
| Later | Import existing WAV/MP3 recordings | Transcribe and summarize material recorded elsewhere |
| Later | Bookmarks and meeting templates | Mark important moments and tailor notes to interviews, lectures, or meetings |

Broader distribution should also add signed releases, clean-machine compatibility
checks, an update/recovery policy, support details, and repeatable multi-hour
recording endurance checks. These remain separate from installing a local LLM.
