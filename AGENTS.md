# Working on SyncSyncAV

[Русский](AGENTS.ru.md) · [Build guide](engine/README.md)

## Code and checks

- The Windows UI and sync logic live in `source/`. Edit command labels and hints in both languages in `source/Strings.xml`. If a user-visible action changes, update the corresponding section in both READMEs.
- Check protocol changes at both ends. The client is in `source/BridgeClient.cs` and the VirtualDub2 bridge is in `engine/bridge/`. The pinned VirtualDub2 and input plugin revisions are in `engine/upstream.json`. Keep changes to upstream code in `engine/patches/` together with the matching revision. Preserve licenses and attribution.
- Run `./source/build.ps1` from the repository root for a quick shell build. Run `./tests/test.ps1` for session and transport tests. For changes to opening files, selecting playlists or controlling panes, run `python tests/file_drop.py <built-app-directory>`. The test needs the built app with its `Engine` folder. Exercise both panes and both languages when the change affects them.

## Git and private data

Set the project identity and enable its hooks in a new clone.

```powershell
git config --local user.name "SyncSyncAV contributors"
git config --local user.email project@localhost.invalid
git config --local core.hooksPath .githooks
```

Review the staged diff before committing and the outgoing commits before pushing. The hooks check for personal data and verify authorship. The packager checks the history and the contents of the ZIPs. Keep personal email addresses, profile paths, movie ISOs, old builds, archived project history, user settings and session logs on the workstation. Use the synthetic fixtures in `tests/` for reproducible tests.

## Working with GitHub

- The main branch is `main`. When the user asks for a draft, show it before pushing changes to GitHub. Push branches, create tags and publish releases within the scope of the current request.
- Use local builds for development. **Build Windows ZIPs** on GitHub Actions produces the release candidate. To release it, enter the ID of a successful build from `main` in **Release Windows ZIPs**. By default this workflow verifies both ZIPs. Enable **Publish** when asked to publish a release.
- Both archives move from Actions to the release on a GitHub-hosted runner. After publication, check the tagged commit and compare both asset names, sizes and SHA-256 digests with the original build. Use anonymous HTTP HEAD requests to check the public download links without downloading the ZIPs to the workstation.
