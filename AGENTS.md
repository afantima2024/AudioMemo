# Release policy

- `Directory.Build.props` is the single source of the local release version.
  Advance it for a delivered application update; use a patch increment by
  default and a minor increment for a new feature. Keep About, Windows file
  metadata, Setup.exe, and MSIX versions consistent.
- Release-number changes are local. Do not automatically commit, tag, push, or
  publish a GitHub release. Do those operations only when explicitly requested
  for the current task; an earlier push request is not standing permission.
- Never commit recordings, personal settings, credentials, model weights,
  build output, or downloaded dependency caches. The FFmpeg files are recreated
  with `scripts\Get-FFmpeg.ps1`; retain the pinned dependency manifest.
- Stage application updates separately. Ask for fresh approval to replace an
  installed copy, never force-close AudioMemo, and preserve user data and a
  verified rollback copy.
