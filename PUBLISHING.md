# Publishing

Releases are published to [NuGet](https://www.nuget.org/packages/Centrifugal.Centrifuge/) by
`.github/workflows/release.yml` when a `vX.Y.Z` tag is pushed. Nobody runs `dotnet nuget push` by hand.

## Releasing

1. **Bump the version** in a normal commit (or PR) to `main`, titled `bump to X.Y.Z`: `<Version>` in
   `src/Centrifugal.Centrifuge/Centrifugal.Centrifuge.csproj`. Bump by semver: before 1.0, a breaking
   change bumps the minor version (`0.1.x` → `0.2.0`), anything else bumps the patch.
2. **Wait for CI** to pass on `main`.
3. **Tag it.** Either push a tag, `git tag vX.Y.Z && git push origin vX.Y.Z`, and let the workflow
   create the GitHub release with generated notes; or create the release on GitHub first (Releases →
   Draft a new release → new tag `vX.Y.Z` on `main`, write the notes, publish).
4. The tag runs the release workflow: it builds and packs the package, pushes it to NuGet, and attaches
   it to the GitHub release.

A version already on NuGet is skipped, so rerunning the workflow after a failure finishes the release.
A rerun uses the workflow file of the tagged commit: if the workflow itself had to be fixed, move the
tag to the fixed commit before rerunning (only while the version isn't on NuGet yet).

## Release setup (one time, maintainers)

1. **NuGet Trusted Publishing.** On [nuget.org](https://www.nuget.org), signed in as `FZambia` (the
   `user` in `release.yml`), open Trusted Publishing and add a GitHub Actions policy: owner
   `centrifugal`, repository `centrifuge-csharp`, workflow file `release.yml`, environment `release`.
   The workflow then exchanges its GitHub OIDC token for a short-lived NuGet key at each release, so no
   API key is stored. (NuGet API keys now last at most 30 days: keys with a longer duration expired on
   November 1, 2026.)
2. **GitHub environment.** Create an environment named `release` (Settings → Environments); it needs
   no secrets. Under deployment branches and tags, allow only tags matching `v*`. Optionally add
   required reviewers: they then approve each release.
3. **Tag protection** (recommended). Add a tag ruleset for `v*` (Settings → Rules → Rulesets) that
   only maintainers can bypass, so only they can create, move or delete release tags.

## Troubleshooting

- **`403` from NuGet with "API key has expired" or "is invalid":** the workflow isn't using Trusted
  Publishing. Check that the job runs `NuGet/login` and that the policy on nuget.org names this
  repository, `release.yml` and the `release` environment exactly.
- **`NuGet/login` fails:** the policy doesn't match the run (repository, workflow file name or
  environment), or the job lacks `id-token: write`.
- **The package doesn't appear right away:** NuGet usually indexes a new version within about 15 minutes.
