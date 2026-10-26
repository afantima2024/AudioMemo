# AudioMemo privacy information

AudioMemo records only after you press **Start recording**. It can capture the
selected microphone and/or the audio being played through the selected Windows
playback endpoint. Pausing stops both capture sources. Stop ends capture and
saves the recording. Obtain any required permission from other participants.

## Local data

The built-in **Help & About > User guide** and **Keyboard shortcuts** reference
work offline. Opening them does not read recordings, download models, change
preferences, or send data to an AI provider.

Recordings, transcripts, AI main points, personal notes, session metadata, and
device-change history are stored in the folder you select. Settings, downloaded
speech models, and temporary speech-processing chunks are stored under
`%LOCALAPPDATA%\AudioMemo`. Completed/cancelled processing removes its temporary
chunks; an unexpected process exit can leave a temporary chunk in the `Work`
subfolder. Files are not encrypted by AudioMemo. A folder synchronized by another
application, or located on a network share, follows that application's or
storage provider's behavior.

Application notices, warnings, errors, and confirmation outcomes are retained
locally in `%LOCALAPPDATA%\AudioMemo\Messages\message-history.jsonl`, with their
timestamp, severity, operation/reason, and message. Message history survives
restarts without automatic expiry; clear it explicitly from the history window.
It is not encrypted or sent to an analytics service or AI provider. Messages
can contain file paths, recording titles, names, and diagnostic details. Common
credential/token patterns are redacted, but this is not a guarantee that every
possible secret format can be recognized. Transcript/notes editors and private
vocabulary previews are not copied into the journal. Clearing local history
does not change recordings, notes, settings, backups, or provider logs.
Failed journal writes remain pending in memory for retry and are reported;
an application exit before successful retry can lose those unsaved messages.

Recording always preserves a 48 kHz stereo safety WAV. Your chosen 44.1/48 kHz,
mono/stereo WAV or MP3 is generated locally after Stop. Closing during conversion
can pause that export for a later restart from the retained safety WAV.
Non-default WAV output creates `audio-output.wav` alongside the safety file;
MP3 creates `audio.mp3`. Conversion does not replace the source. The output
profile is stored in settings and per-session metadata, and is inherited by
edited/restored copies.

Confirming closure during processing saves unfinished work locally in
`pending-processing.json` beside that recording. The plan contains the session
identifier, audio length/modification time, remaining operation types, processing
model/language/provider/endpoint choices, speaker options, vocabulary terms,
summary-generation and Me-tag identifiers for a deferred personal summary, and
whether to offer resumption at startup. It does not copy audio, transcript text,
personal notes, API keys, credentials, or sharing consent into the plan.
Existing transcript/refinement and summary files remain the progress checkpoints.
Declining the next-launch offer starts no processing and leaves audio/completed
results unchanged; the declined plan remains available for manual resumption
without repeatedly prompting. Finishing all queued steps removes the plan.
Resumption uses current authentication and consent, not previously granted access.
An interrupted model request or speaker pass may be repeated, and a cancelled
export restarts locally. Cancelling or closing cannot retract content already sent
to an online provider or prevent charges for a request it already accepted.
Unsaved correction-dialog drafts remain memory-only and are not applied or
persisted as queued selection-edit jobs when closing the app.

Exact/fuzzy transcript search and speaker-row coloring run entirely on this PC,
without an AI provider, download, or network request. Search queries are kept
only in memory, not saved to settings or recording files. Searching/highlighting
does not change recordings, saved transcripts, session exports, or AI summary
input. Optional matching-row filtering changes the main-table display and its
copy buttons; session-menu copy/export still includes the whole transcript.
User-selected speaker colors are saved with speaker IDs in that recording's
transcript JSON, not derived from additional voice analysis. A separate optional
`UnlabeledSpeakerColor` value applies to all Not labeled rows in that recording;
it does not assign an identity or claim those rows belong to one person.
Changing a color
updates only this display metadata, not audio, text exports, personal notes,
or summary state. Colors can be changed during AI processing and live recording
without canceling or restarting inference. AI saves retain current colors and
neutral resets. Colors are preserved through compatible corrections and
edited/restored copies; full speaker reanalysis replaces labeled-speaker colors
along with names because the identities may differ. The shared Not labeled
preference is retained, including when temporarily no unlabeled rows remain.
Selected transcript character ranges are held only in memory so scrolling does
not lose them; they are not written to settings, transcripts, or comparison files.
The Light/Dark/Follow system choice, highlight/comparison colors, button preset,
rainbow taskbar recording-light preference, and
history-edge-tab position/recording-list width, main-points/personal-notes visibility,
and custom keyboard shortcuts are stored in local
settings. Shortcuts are handled only inside AudioMemo and its search popup;
they do not install a system-wide keyboard hook. Color and appearance
changes make no AI/network request; following system appearance does not change
Windows settings. Unsaved appearance changes are discarded when Settings is
cancelled.

Timestamp/range replay reads the saved safety WAV/RF64 locally and sends it
only to the Windows default playback device. It does not upload or rewrite
audio. Replay is disabled during recording and recording pauses, and is stopped
before starting another recording, to avoid capturing the replay through
loopback. Seeking only repositions this local playback stream and does not change
audio or transcript timestamps. Speaker filters affect transcript display/copy only: replaying a
selected time span still includes all original voices in that span.

**Edit text / Re-transcribe selection** works locally on selected saved
transcript intervals. Re-transcription reads the original safety WAV into
temporary local speech chunks and uses an already-downloaded Whisper model.
It does not upload or modify audio, download a new model, or request a summary.
Results update only **Corrected text** in the review dialog until **Save transcript
changes**. **Local / saved transcript** stays unchanged for comparison; row colors
are computed locally as you edit. **Stop**
keeps completed rows and audio-chunk progress in memory for review and **Continue**;
an unfinished row's completed excerpt is preview-only and does not replace its
existing full text. This checkpoint is not saved across dialog/app restarts.
Discarding the preview restores the draft from before that recognition run.
Saving while stopped applies reviewed row text, retaining unfinished rows unless
you edit them yourself. Saving
replaces only the transcript JSON/text in place and marks changed segments as
reviewed; timestamps, speaker labels, and personal notes are preserved.
Cancelling discards unsaved transcript edits. There is no separate manual-edit
history. Existing summaries, exports, backups, and previously shared text are
not erased; a new summary must be explicitly requested.

**Review unclear transcript** scans explicit markers locally and uses the
currently selected LLM for a separate text-only uncertainty check. Automatic
review is on by default and can be disabled under **Settings > Speech**; it runs
only for completed saved transcription, before final main points, not during
recording/live text. The same existing provider, authentication, and sharing
consent rules apply. Only transcript passages and their speaker display names
are included, never audio, private vocabulary lists, previous summaries, or
editable **My notes**. The extra requests can incur provider charges. Local
Ollama uses its configured endpoint; an endpoint on another computer is not
the same as processing entirely on this PC.
An invalid review response can trigger one additional request for the same
transcript part with stricter formatting instructions. No private notes or
failed model response are added to that retry. Resuming an incomplete check
does not resend completed parts when the source, provider, model, and endpoint
still match.

The recording-local `transcript-review.json` retains quoted possible
uncertainties, model explanations, source/accepted fingerprints, provider/model/
endpoint, check time, and batch progress. It is not encrypted and may contain
sensitive spoken information. It stores neither credentials nor private notes.
Unselected corrections and cancelled edit drafts stay memory-only. Approved
changes use the existing transcript correction path, preserving audio, timing,
speaker labels, and personal notes; the original summary is not erased. A
pending summary proceeds using the approved text only after confirmation.
An unchanged accepted transcript is not automatically reviewed again.
Cancelling or closing cannot retract content already sent to the model.

**Correct speaker** also runs locally. It can reassign selected transcript
segments or merge every segment of one speaker into an existing speaker.
It changes speaker IDs/revision and removes unused name/color metadata, but
does not change audio, wording, timestamps, or reviewed-text flags. Prior
summaries become outdated; saving does not send a summary request or train
a voice model.

**Recheck unlabeled speakers** compares local voice embeddings from unlabeled
rows with reference audio from this recording's current labeled rows. It changes
only confidently matched, previously unlabeled segments. Existing labels, names,
colors, wording, reviewed-text flags, and the Me tag are preserved; uncertain or
very short speech remains unlabeled. These are estimates, not verified
identities. The worker uses the already-downloaded local speaker model and makes
no network request. Embeddings stay in memory and are not saved as a
cross-recording profile. A temporary plan contains speaker IDs, frame ranges,
segment indices, and a source fingerprint, not transcript words or personal
notes; it is removed after completion/cancellation. An unexpected exit can
leave a plan in the local Work folder. Changed assignments invalidate prior
summary reuse without starting a summary request.

**Mark speaker as me** saves one recording-local speaker ID, a tag identifier,
and optional names/nicknames in the transcript JSON. It is a user-supplied label,
not biometric identification or a cross-recording voice profile. Tag changes
make no model or network request and do not alter audio or spoken text.
Whole-recording speaker reanalysis clears the tag to avoid reusing a label for
another person; compatible continuation and manual corrections preserve or
explicitly remap it. Removing a tag does not delete existing notes or backups.

**Update my notes** and **Summarize about me** run the same operation only when
explicitly requested. The selected summary
provider receives the full transcript in bounded portions, the marked speaker's
ID/current display name and optional names, and previous AI-generated checkpoint
text from that request. Speaker/search filters and selected rows do not restrict
this input. The same local/online provider and endpoint-specific consent rules
apply as for main points. Audio files and existing editable **My notes** are
never sent, including user edits to a generated About me section.

The result is inserted into the editable `notes.md`, with provider/model and
generation time. Refreshing replaces only one exact, unchanged previously
generated section. If it cannot be found unambiguously, the app asks before
appending instead and otherwise leaves notes unchanged. Other notes and typing
performed during processing are preserved. `about-me.json` stores inference
progress, source/identity fingerprints, model information (including the last
applied section's provider/model for the title tooltip), and exact AI-generated
section text for safe replacement/retry; it does not copy surrounding private
notes or incorporate user-edited notes into model context. This local file is
not encrypted. Edited/restored copies keep only generated-section ownership
tracking and known applied-model attribution alongside unchanged notes, not a
resumable result for the old source. Showing model information in pane-title
tooltips reads only local metadata/notes and does not make an AI request.
In-place audio changes leave notes/tracking intact but invalidate inference reuse
through the changed transcript fingerprint.

Optional remembered vocabulary is stored as **plain text** in the local
`settings.json`, separate from the transcript. Names/terms are passed in memory
and through redirected standard input to the local Whisper worker, not on its
process command line. No model weights are trained or changed, and the list is
not sent to a summary provider. Words that appear in a resulting transcript can
of course be included in a later summary under that provider's sharing settings.
Use focused terms, and remove or disable hints in Settings when no longer needed.
Saving vocabulary separately takes effect even if you later cancel transcript
edits; unsaved vocabulary changes are discarded when the dialog closes.

**Silence selected** asks each time whether to edit the current recording or
create a separate copy. Neither choice is preselected. It replaces selected
audio with equal-length silence and removes corresponding transcript text,
without shortening the recording or shifting remaining timestamps. In-place
editing keeps the history identity/folder; copying leaves the current recording
unchanged. Current metadata records affected intervals and edit mode. Older
cut-based timelines are not silently changed. New summaries use only remaining
transcript text; old AI summary state and outdated exports are invalidated.
Personal notes and generated-section ownership tracking are kept, or copied
unchanged for a separate copy, and are not included in summary input.

A hidden `.restore-source` folder keeps a lossless original-audio/transcript
backup for recovery, including earlier muted sound when copying an already-muted
recording. In-place editing avoids extra history entries, not backup disk space.
This is **not secure erasure**: do not share the entire recording folder if
muted material should remain private. Share only the exported muted audio.
Muting does not remove material from older exports, backups, sync services, or
providers to which it was previously sent. Editing makes no network requests.
Publication uses temporary files and rollback metadata; an unexpected exit can
leave `.edit-<GUID>` staging data or a pending in-place transaction. Retain the
recording folder intact so interrupted publication and recovery can finish.

**Show muted rows / Restore muted sections** lists original text, speakers,
timestamps, and selectable currently muted rows. **Play original** streams the
retained source audio locally to the Windows default playback device, with
pause/seek/stop. Preview does not select, restore, save, or upload anything and
creates no additional recording. Adjacent rows can be restored separately.
Recovery copies selected original audio and corresponding transcript text back
at their original times in the **same recording**, without creating another
history entry. Unselected sections stay muted; the recovery backup is preserved.

Recovery requires the stored source WAV/transcript and valid edit metadata;
silence alone cannot recreate missing audio. Older dependent edit chains can
also require original/intervening session folders in the recording library or
current session's parent. Keep those sources or restore complete folders from
backup/Recycle Bin. In-place changes that would break dependent recordings are
refused. Current personal notes and generated-section ownership tracking remain
unchanged, while old AI summary state is invalidated. Compatible corrections
and speaker metadata are retained. If merging is not possible, a warning
explains that the transcript will be rebuilt from the recovery source while
excluding remaining muted sections. Recovery sends nothing to AI; a later
explicit summary uses retained/recovered transcript text under the selected
provider's sharing rules, never personal notes.

Recording and default speech recognition do not upload microphone or playback
audio. Default recognition uses local Whisper model weights. The separate,
explicitly approved online-comparison feature is described below.
Model downloads connect to Hugging Face and
its download infrastructure; those services receive ordinary download request
metadata such as your IP address. No recording is included in the request.

Optional speaker analysis also stays on this PC. Its Pyannote and 3D-Speaker
model downloads use the official sherpa-onnx GitHub releases/CDN, receiving normal
download metadata but no audio. Speaker labels and your custom display names are
saved with the transcript. They are estimates, not authenticated identities.
CPU/Vulkan inference does not send audio to a cloud speech service.

Chinese transcripts and AI main points are normalized to Simplified Chinese
using bundled OpenCC dictionaries entirely on this PC. This conversion makes no
network request and does not translate English. Previously saved generated text
is normalized for display without rewriting files merely because they are opened.
Original audio, titles, and personal notes are not converted.

## Imported audio/video and optional online comparison

**Import audio/video** reads a file you choose and creates an independent 48 kHz
stereo WAV session in the recording library. It stores the source's file name,
not its full path, in local session metadata. The original file is not changed,
and importing itself sends no data to a model. After import, you choose whether
to run local transcription and your configured automatic speaker/summary steps.
Declining keeps the audio for later. Browsing the library only reads saved results.
Interrupted imports can leave a hidden `.import-<GUID>` staging folder.

For video files, bundled FFmpeg/ffprobe run locally in background child processes.
When several audio tracks are present, you choose one before extraction.
Language and title tags are shown locally in that chooser. Only the selected
audio track is decoded; surround audio is mixed down to stereo. Video frames,
subtitles, and other tracks are not sent to AI or copied into the recording
library. Video probing/extraction disables network media protocols and accepts
known local video containers, not streaming URLs or playlists. Cancelling stops
the media process and removes partial import staging. No account, model download,
or cloud permission is required for extraction; later AI work follows the
existing transcription and summary consent rules.

**Online comparison** is an optional exception to local-only speech recognition.
It sends selected audio to **Qwen ASR** (Beijing/Singapore official endpoints) or
**OpenAI transcription**, only after per-run approval in the review popup.
The popup shows the selected intervals, model, exact destination, request count,
estimated size, and provider limits before sending. Summary-sharing consent does
not approve audio uploads. Opening the popup, changing history, and viewing a
saved comparison do not send audio.

Each selected transcript segment is converted locally to 16 kHz mono PCM WAV.
Only those segments are sent; unselected gaps, source filenames/paths, local
transcript text, vocabulary, and personal notes are not included. Speaker filters
do not isolate a voice: every voice audible inside a selected interval is part
of its audio. Qwen requests embed the WAV as Base64; OpenAI uses a multipart file
named `selection.wav`. Requests contain no model tools or web-search instructions.
Temporary speech chunks are removed after completion/cancellation, but a crash
can leave a chunk in the local `Work` folder.

Provider/model and optional API-key settings are separate from summary settings.
Saved keys use the same Windows-account DPAPI and exact provider/endpoint binding
described below. A Qwen key can be reused only at an identical endpoint. Saving a
connection is explicit; an unsaved key is used only by that popup. Consent is not
persisted. Oversized selections are blocked unless you reduce the selection,
change the model, or explicitly allow multiple requests. There is no automatic
retry or fallback to a different provider. Cancellation stops pending work but
cannot recall already-uploaded audio, undo processing, or reverse provider fees.
Provider retention, account quotas, billing, and processing policies still apply.

Successful results are saved locally in `online-comparisons` under the session,
including selection indices/times, local and online text, provider/model/endpoint,
completion time, legacy missing-local-result flags, and an audio-file version.
Reopened online results are compared against the currently saved transcript;
cached local previews or their flags never replace that baseline. Comparison
colors are applied locally and do not infer or request confidence scores.
These comparison files contain no API
key or audio bytes and are not encrypted. They can be reopened without uploading
again. **Use online result** prepares edits; **Save transcript changes** is still
required to replace selected text. Audio, timestamps, speaker labels, and notes
remain unchanged. Cancelling the editor leaves the transcript unchanged but keeps
a successfully saved comparison and any separately saved connection settings.
Deleting a session removes its comparison files with the rest of that session;
it does not delete copies retained by a cloud provider or in backups/sync services.

## Optional summaries

You choose **Ollama (local)**, **Copilot CLI**, or the official **Doubao, Qwen,
or DeepSeek API** in Settings. Switching providers does not upload old recordings.
AudioMemo never automatically falls back to another provider after a failure.

Changing the selected LLM/model runs a short, built-in synthetic connection and
model-access test before activation. It uses no recording, transcript, personal
notes, or vocabulary. A configured cloud API or Copilot CLI may send that sample
to its provider and incur charges. Selection validation requires the applicable
sharing consent and authentication. Missing/failed configuration opens setup;
successful testing is required before saving the new selection. Canceling or a
failed test/save retains the previous selection and downloads no model.
Starting recording, Cancel AI, or closing cancels a pending test, but cannot
retract accepted online requests or guarantee that billing stops. Opening
Settings or changing unrelated preferences does not itself run this test.

After final main points are updated, one additional request sends those completed
AI main points to the **same selected provider/model** to create a timestamp-free
overall summary. It does not send audio, personal notes, or additional files.
The same sharing consent applies; cloud/CLI usage may incur another charge.
Live updates do not generate this overall section. Completed overall summaries
are cached with the main points, displayed without requests when browsing history,
and invalidated when their main points change. Failed/cancelled overall work keeps
the completed main points for an explicit retry, without resending completed
transcript batches. Both sections are included in the saved Markdown export.

### Ollama on this PC

AudioMemo sends transcript text and previous AI main points to the configured
loopback Ollama HTTP/HTTPS endpoint. It does not send recording files or personal
notes. Local mode disables HTTP proxies and redirects and rejects cloud model
tags and model metadata marked remote. The selected model must already be
downloaded; AudioMemo does not download LLM weights automatically.

Ollama is separately installed software. Keep it configured for local inference
and review its logging, storage, and privacy settings. A custom local server
could forward requests elsewhere; AudioMemo cannot audit that server's internals.
Ollama normally stores model weights under `%USERPROFILE%\.ollama\models`, not
inside AudioMemo's recording or portable folders.

The model/speed test sends only built-in synthetic meeting text. Memory readings
and generation timings are used to display local performance warnings and are
not sent to an analytics service. Clicking a model/download link opens the
official website in your browser, which receives ordinary web request metadata;
the link does not contain meeting content. The model guide does not accept
licenses or start downloads on your behalf.

### Doubao, Qwen, and DeepSeek official APIs

After your explicit consent for the selected provider and endpoint, AudioMemo
sends transcript text and previous AI main points (or the explicitly requested
About me identity context and AI checkpoint described above) over HTTPS to that official
API. It does **not** send recording files, personal notes, or other providers'
credentials. Audio is transcribed locally first. Requests supply no tools.
Only final summary text is saved, not separate reasoning fields. Changing the
provider, model, or endpoint starts fresh summary processing rather than
continuing a different connection's summary checkpoint.

Copilot consent never authorizes these APIs. Keys and consent are scoped to
the provider and canonical endpoint, including Qwen region/workspace and the
API base path. A different endpoint needs its own key and sharing approval.
One approved endpoint is retained per provider; approving a different endpoint
replaces that provider's previous approval. Revoke the active endpoint's sharing
consent in Settings and save to prevent further recording-summary requests.
This does not revoke the key at the provider or retract previously sent text.

API keys are protected with Windows DPAPI for your Windows account, with
provider/endpoint binding, and stored as ciphertext in
`%LOCALAPPDATA%\AudioMemo\settings.json`. The masked field never displays an
existing key. Plaintext is used in memory for the selected request's bearer
authorization. DPAPI is not protection from software already running with
access to your Windows account; protect that account and settings backups.
Moving settings to another account or PC may require re-entering the keys.
Credential/consent edits remain in the settings dialog until Save; Cancel
discards them. Forget removes the selected endpoint's local key on Save.
It does not invalidate the provider's key or erase copies in older backups.

Opening a provider's settings makes no API request. Activating a different
provider/model requires the synthetic selection test described above.
**Read available models** explicitly sends an authenticated discovery request
to DeepSeek; it contains no meeting text. Qwen and Doubao use supplied/manual
Model IDs and official catalog links instead. **Test sample** explicitly sends
only a built-in synthetic meeting to the selected provider, even without
recording-sharing consent, and may incur API charges. An unsaved key entered
in that dialog is used for an explicitly requested test/discovery; Cancel
does not undo an API request already made. Opening console/catalog links
sends ordinary browser request metadata, never meeting content in the link.

Connections are restricted to supported official HTTPS hosts and API paths,
with redirects and cookies disabled. The normal system proxy configuration
still applies. AudioMemo performs no automatic retry or fallback. Raw API
error response bodies are not shown or saved because they may echo private
content. API account activation, authorization, charges, logging, retention,
and regional processing policies belong to the selected provider. Consumer
chat login/subscription and developer API access are distinct. Review those
terms before sharing a transcript.

### Copilot CLI

With your explicit transcript-sharing consent, AudioMemo starts your installed
Copilot CLI and supplies transcript text and previously generated AI main points.
The CLI sends them to the provider configured in your CLI account/environment.
Depending on that configuration this may be GitHub Copilot, Azure, or another
provider. Personal notes and the recording file are not supplied.

Provider authentication, authorization, usage charges, logging, telemetry, and
retention are controlled by the CLI/provider and their policies. CLI transcripts
may be retained in CLI session/log storage even when AudioMemo recordings are
removed. AudioMemo does not extract or store your CLI credentials. Model tools
are disabled for summary requests, but your trusted CLI installation/profile
remains separate software.

Automatic and live summaries are off by default. You can revoke Copilot sharing
consent in Settings; this does not disable local Ollama summaries or authorize
another cloud provider. Cancel AI terminates AudioMemo's current speech/CLI
child process or cancels its local/cloud HTTP request. It does not shut down
the shared Ollama server or retract text already sent. Cancellation and
timeouts cannot guarantee that a cloud provider stops processing or billing.
Successful summary batches remain saved so processing can resume later.

## Control and retention

AudioMemo adds no analytics service and does not automatically delete recordings.
The session menu can move an entire recording folder to the Windows Recycle Bin
after confirmation; this includes audio, transcripts, summaries, and personal
notes. It is blocked during recording/processing and for linked folders or
non-local/non-fixed storage. Restore it through Windows Recycle Bin if needed.
You can also inspect, copy, back up, or delete recording subfolders in File
Explorer. Removing local files does not remove copies held by backup/sync tools
or a model provider. Check those services separately. The safety WAV is retained
even after successful MP3 export to protect the original recording.

This document describes the application behavior. Before distributing an
official Store release, the publisher must add its identity, privacy/support
contact, any applicable legal disclosures, and publish a policy URL.
