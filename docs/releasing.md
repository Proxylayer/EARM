# Releasing

EARM ships as the .NET tool package [`Proxylayer.EARM`](https://www.nuget.org/packages/Proxylayer.EARM) (command `earm`). A release is published by
`.github/workflows/release.yml` when a version tag is pushed. The workflow uses **NuGet trusted publishing**: it exchanges the run's GitHub OIDC token for a
nuget.org API key that is valid for about an hour, so no long-lived key is stored in GitHub
([Learn: trusted publishing](https://learn.microsoft.com/en-us/nuget/nuget-org/trusted-publishing)).

## One-time setup

1. **nuget.org policy.** Sign in to nuget.org with an account that may manage `Proxylayer.EARM` (it is owned by `ProxyLayer`), open the user menu →
   **Trusted Publishing** → **Add**, and fill in:

   | Field | Value |
   |---|---|
   | Policy name | `EARM-Publish` (any label) |
   | Package owner | `ProxyLayer` (the owner of `Proxylayer.EARM`) |
   | Repository owner | `ProxyLayer` |
   | Repository | `EARM` |
   | Workflow file | `release.yml` (the file name only, not the path) |
   | Environment | leave empty (the publish job runs in no GitHub environment) |
   | Packages | `Proxylayer.EARM` (the exact id, no wildcard) |

   nuget.org binds the policy to the repository's and owner's permanent GitHub ids. When it cannot read them up front (a private repository), the policy
   starts "temporarily active" for 7 days and the first successful publish binds it; if the 7 days pass without one, restart the window on the same page.

2. **GitHub secret.** Add the Actions **secret** `NUGET_USER` = the nuget.org **username of the account that created the policy** (the name shown when
   signed in; a person's account, not the `ProxyLayer` organization, and not an email address). It can be an organization secret (Proxylayer org →
   Settings → Secrets and variables → Actions → Secrets, with access for `EARM`) or a repository secret; a repository secret of the same name wins.
   Keep it a secret, not a variable: the run log of this public repository masks a secret but prints a variable. Do not write the value into this
   repository.

3. **Retire the old key.** After the first trusted publish succeeds, delete any long-lived API key that was used to push `Proxylayer.EARM` by hand
   (nuget.org → API Keys).

## Releasing a version

1. On a branch: move the `## [Unreleased]` entries of `CHANGELOG.md` under `## [x.y.z] - <date>`, set `<VersionPrefix>x.y.z</VersionPrefix>` in
   `Directory.Build.props`, and commit it as `chore: release x.y.z`.
2. Merge the branch into `main` (CI runs the full build and test matrix on Windows and Linux).
3. Tag the commit on `main` that carries the version, and push the tag:

   ```bash
   git tag -a v1.0.1 -m "v1.0.1"
   ```

   ```bash
   git push origin v1.0.1
   ```

The workflow then:

- refuses a tag that does not equal `v` + `VersionPrefix`, or that points at a commit not on `main`;
- builds Release (zero warnings), runs `test.ps1`, and packs the tool with the deterministic-path switches `deploy.ps1` uses;
- signs in to nuget.org with `NuGet/login` and pushes the package (`--skip-duplicate`, so re-running a release that already reached nuget.org succeeds);
- keeps the pushed `.nupkg` as a run artifact for 90 days.

nuget.org validates and indexes a new version within minutes; `dotnet tool update -g Proxylayer.EARM` picks it up after that.

## Notes

- The policy names the workflow **file**: renaming `release.yml` stops publishing until the policy is updated.
- The package carries no licence metadata until a licence is chosen (see the comment in `src/EArm.Cli/EArm.Cli.csproj`); nuget.org accepts it and shows
  no licence on the package page.
- `deploy.ps1` stays the local path: it packs and installs from `artifacts/nupkg` only and never pushes to nuget.org.
