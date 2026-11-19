# Releasing a new version

Releases are made by GitHub Actions from a version tag. Installed copies of Space Hunter see the new version the
next time they open, and can update in one click.

## Once: the release key

Run `tools\make-release-key.ps1` (Windows PowerShell, signed in to GitHub with `gh auth login`). It stores the
private key as the repository secret `RELEASE_SIGNING_KEY` and writes the public key to `update-key/`. Commit
`update-key/`. Never make a second key: copies built with the first one would refuse updates signed with another.

## Every release

1. Write what changed under a new heading in `CHANGELOG.md`, e.g. `## 1.1.0 (2026-12-01)`.
2. Put the same number in `VERSION` (`1.1.0`).
3. Commit, then tag and push:

   ```bash
   git tag v1.1.0
   git push origin main v1.1.0
   ```

GitHub Actions then builds both apps, tests the signature check, signs `SpaceHunter.exe` and `SpaceHunter-mac.zip`
with the release key, and publishes a Release named "Space Hunter 1.1.0", with the changelog section as its notes.
If the tag and `VERSION` don't match, nothing is published.

## How installed copies update

When it opens, Space Hunter asks GitHub for the latest release (one request; nothing about you is sent). If it's
newer than itself, a bar offers **Update & relaunch**. Updating downloads the new app and its `.sig` file, checks
the signature against the public key built into the app, swaps the app and reopens it. If the signature doesn't
match, nothing is changed. Offline, nothing happens.
