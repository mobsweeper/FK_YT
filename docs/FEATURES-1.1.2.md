# FK_YT 1.1.2: usage and limits

This guide describes the portable and installed apps with application version `1.1.2`. See [downloads and installation](RELEASE-1.1.2.md), [licenses](LICENSING-1.1.2.md) and [matching source access](SOURCE-ACCESS-1.1.2.txt).

## Audio playback

The sidebar **Audio mode** checkbox applies to the next video or when you leave the playback screen and open it again. Changing the checkbox does not replace the current playback immediately. Live broadcasts continue to play video and audio.

For ordinary videos, when the extractor marks an available original audio track with `language_preference=10`, the app prefers that original separate audio stream. If no suitable original separate stream is available, it selects the lowest-resolution available original stream containing both video and audio and plays its sound with the video track disabled. Original audio takes priority over a dubbed separate stream or a lower-resolution dubbed combined stream.

When the response has no original-track marker, the app does not guess the language: it retains the compatibility selection of a separate audio stream, then the lowest-resolution available combined stream. **It cannot guarantee that every dubbed track is blocked.** Combined-stream fallback still receives video data, so it does not guarantee audio-only network traffic or a particular bandwidth saving. The selected account is preserved. Video-quality and subtitle controls are unavailable during audio playback.

## Live playback from the available beginning

Live playback from the available beginning uses native yt-dlp fragment workers. App-owned video and audio fragment workers feed mpv through owned pipes, using the selected account. This is a playback path, not a recording feature. Short-lived fragment staging is separate from mpv's playback packet cache.

The start is limited to fragments YouTube still makes available. This cannot recover expired server DVR segments, guarantee the original first second, or provide an arbitrary server-side DVR timeline. If the beginning is unavailable, use **LIVE** to open the current broadcast edge. LIVE preserves an intentional pause. Rewinding is limited to the local ranges mpv has actually confirmed, including possible gaps.

After live EOF or disconnection, recovery checks the broadcast status and error classification. Automatic reconnection has a cumulative maximum of **three attempts**, after delays of **2, 5 and 10 seconds**. After that limit, a final status check may confirm that the broadcast ended; it does not start a fourth automatic load. Authentication, access and request-limit failures stop recovery. Manual pause, stop, media/account changes and incognito changes cancel recovery. Automatic next-video playback requires confirmed broadcast end. Use LIVE for a manual retry after the automatic limit.

Live chat, scheduled-broadcast waiting and recording are not included. Available formats and selected-account access can prevent playback; unsupported account/transport combinations report an error rather than silently selecting another account.

## Cache and memory

Confirmed live playback uses a **6 GiB (6,442,450,944-byte) backward compressed-packet cache budget**, with a 32 MiB forward budget. It grows as data arrives; 6 GiB is not preallocated and is **not an RSS or whole-process RAM limit**. Decoder buffers, GPU textures, audio, thumbnails, the UI and transport buffers are separate. There is no guaranteed rewind duration or promise to retain every received segment.

Ordinary playback keeps the 32 MiB forward / 8 MiB backward settings; one next-video preload can add another configured cache. Separate audio/video tracks normally share the mpv timeline cache, rather than each reserving a full 6 GiB. mpv playback disk caching remains disabled. The live fragment path can use temporary, app-owned fragment files; explicit downloads are separate and remain on disk.

## Existing settings and data

YouTube watch-history sending remains **off by default** and requires an explicit choice in Settings. Normal-mode local history and resume positions remain separate. Enabling sending does not upload old local history. Accepted records use the existing account/channel-scoped queue; Held records require **Resume held records**, and Failed or Unconfirmed attempts are not automatically resent. A send acknowledgement is separate from confirmation in YouTube history.

Incognito blocks account-history sending and local history/resume saving without changing the saved sending choice. It does not hide the IP address or delete deliberate downloads. Preserve the database and settings together in a closed-app backup.

Manual experimental yt-dlp/Deno updates apply on the next app start. They do not update Python, mpv or FFmpeg. Completed tool versions are retained without automatic old-version cleanup or a total storage cap. Original source archives cover the original bundled tools; later downloaded versions require their own notices and corresponding sources before redistribution.

## Verification boundary

These behavior descriptions do not guarantee compatibility with every video or account. The release body records checks actually performed on the final files; a metadata/fixture check does not establish actual playback or audible language. The cause of an earlier intermittent immediate exit remains unconfirmed. See the [verification limits](RELEASE-1.1.2.md#verification-limits). Historical guides apply to their documented versions.

FK_YT-owned code and documents remain MIT. Original external runtime and legal-notice bytes are unchanged; the external corresponding-source ZIPs, versions and digests remain those identified in [SOURCE-ACCESS-1.1.2.txt](SOURCE-ACCESS-1.1.2.txt). Existing source/rebuild limitations remain in force.
