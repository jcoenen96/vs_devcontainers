# Releasing

A release is a version tag. The [release workflow](../.github/workflows/release.yml) builds the VSIX for the tag,
runs the unit tests and publishes a GitHub release with the VSIX, its SHA-256 and the version's changelog section.
Publishing to the Visual Studio Marketplace is a separate step (see [marketplace.md](marketplace.md) once it
exists).

## Versions

- One version for everything, in `Directory.Build.props` (`VersionPrefix`). The VSIX manifest takes it from the
  assembly version, so Visual Studio and the Marketplace show `0.9.0`.
- [Semantic Versioning](https://semver.org/). Until 1.0 the extension is a preview: GitHub marks `0.x` releases as
  pre-releases, and a minor version may change behavior.
- CI builds of pull requests carry the same assembly version but an informational version with `-ci.<run>`, so a
  VSIX from a pull request never claims to be a release.

## Steps

1. In a pull request:
   - set `VersionPrefix` in `Directory.Build.props` to the new version,
   - in `CHANGELOG.md`, rename `## [Unreleased]`'s content to a `## [X.Y.Z] - YYYY-MM-DD` section, leave an empty
     `## [Unreleased]` above it, and update the links at the bottom.
2. Merge it, after the end-to-end tests passed against Podman, and the latest nightly run against Docker Desktop.
3. Tag the merge commit and push the tag:

   ```
   git tag v0.9.0
   git push origin v0.9.0
   ```

4. The release workflow checks that the tag matches `VersionPrefix` and that the changelog has the section, then
   publishes the release. If it fails after the tag exists, fix the cause and run it again from **Actions > Release
   > Run workflow** with the tag.

## Before 1.0

What still stands between the preview and 1.0 is tracked in the issues labeled `1.0`. The known limitations are in
the changelog of each release.
