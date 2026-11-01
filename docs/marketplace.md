# Publishing to the Visual Studio Marketplace

The [Marketplace workflow](../.github/workflows/marketplace.yml) takes the VSIX of a GitHub release, signs it with
the publisher's code signing certificate and publishes it to the
[Visual Studio Marketplace](https://marketplace.visualstudio.com/vs) with `VsixPublisher.exe`. It runs after the
[release workflow](../.github/workflows/release.yml) published a release (see [releasing.md](releasing.md)), and
waits for approval before anything is signed or published.

The listing's text is [`marketplace/overview.md`](../marketplace/overview.md); its categories, name and links are in
[`marketplace/publishManifest.json`](../marketplace/publishManifest.json). The icon, preview image, links and the
*Preview* label come from the extension's metadata (`DevContainersExtension.cs`) and `Resources/`.

## One-time setup (the publisher)

1. **Publisher.** Sign in at [marketplace.visualstudio.com/manage](https://marketplace.visualstudio.com/manage) with
   the Microsoft account that will own the extension, and create a publisher. Note its **ID** (not the display
   name).
2. **Token.** In Azure DevOps ([dev.azure.com](https://dev.azure.com), any organization of that account), create a
   personal access token: *User settings > Personal access tokens > New token*, **Organization: All accessible
   organizations**, scope **Marketplace > Manage**. Tokens expire; note the date.
3. **Code signing certificate.** A code signing certificate (from a certificate authority, for the publisher's
   name), exported as a `.pfx` with its private key. Encode it for the secret:

   ```powershell
   [Convert]::ToBase64String([IO.File]::ReadAllBytes('codesign.pfx')) | Set-Clipboard
   ```

4. **GitHub environment.** In the repository: *Settings > Environments > New environment* named `marketplace`.
   - **Required reviewers:** yourself. Every publish then waits for your approval in the workflow run.
   - **Deployment branches and tags:** *Selected*, with the tag pattern `v*`, so only release tags can publish.
   - **Environment secrets:** `MARKETPLACE_PAT` (step 2), `VSIX_SIGNING_CERTIFICATE` (step 3, base64) and
     `VSIX_SIGNING_PASSWORD`.
   - **Environment variable:** `MARKETPLACE_PUBLISHER` = the publisher ID from step 1.
5. **License.** Add a `LICENSE` file to the repository before the first public release; the listing links the
   repository, and people need to know the terms.

## Publishing a release

1. Make the GitHub release as [releasing.md](releasing.md) describes (push the tag).
2. The release workflow starts **Publish to the Visual Studio Marketplace** for the tag. Open that run and
   **Review deployments > Approve**.
3. The workflow checks the VSIX against the release's SHA-256, signs it (with an RFC 3161 timestamp, so the signature
   stays valid after the certificate expires), and publishes it. The Marketplace validates it, which takes a few
   minutes; then the new version is live, and Visual Studio offers the update to its users.

To publish an existing release again (after a failed run, or a release made by hand): **Actions > Publish to the
Visual Studio Marketplace > Run workflow** with the tag.

## When it fails

- *Missing: secret ...*: finish the setup above.
- *401* or *TF400813* from VsixPublisher: the token expired or lacks the Marketplace (Manage) scope, or was made
  for one organization only. Make a new one.
- *The extension already exists* with another publisher: `identity.internalName` in `publishManifest.json` is taken
  on the Marketplace; choose another, and keep it from then on.
- *Version already exists*: the Marketplace takes every version once. Bump `VersionPrefix` and release again.
