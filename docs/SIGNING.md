# Verifying and producing distributions

## CI provenance (no owner certificate required)

The `Native CI` workflow builds each platform package, waits for all tests and integration checks, then uses the pinned `actions/attest` action to sign the archive hashes using GitHub OIDC and Sigstore. The `Tessera-attested-distribution` artifact includes source, three application archives, checksums, `provenance.sigstore.json` and machine-readable verification results.

Verify both the archive digest and the expected repository/workflow identity, rather than trusting an accompanying checksum alone:

```bash
gh attestation verify Tessera-0.2.0-preview.1-linux-x64.tar.gz \
  --repo wieslawsoltes/Tessera \
  --bundle provenance.sigstore.json \
  --signer-workflow wieslawsoltes/Tessera/.github/workflows/ci.yml
```

Check the verified source revision against the expected release commit. A feature-branch attestation proves a feature-branch build, not a released main build. Signed provenance is not Authenticode or Apple notarization; normal CI packages correctly retain `signed: false` in `build-info.json` for their executable-signing status.

## Native OS signatures

Configure a GitHub environment named `production-signing`, restrict it to main and add required reviewers. Add the following **environment secrets**, not repository files:

Windows: `WINDOWS_PFX_BASE64`, `WINDOWS_PFX_PASSWORD` (a valid code-signing certificate and private key).

macOS: `APPLE_P12_BASE64`, `APPLE_P12_PASSWORD`, `APPLE_TEAM_ID`, `APPLE_NOTARY_KEY_BASE64` (the App Store Connect API .p8 key), `APPLE_NOTARY_KEY_ID`, `APPLE_NOTARY_ISSUER_ID`.

Run `Native signed distribution` manually from main. It first requires a successful `Native CI` run for that exact SHA and repeats the test suite. The packaging command uses `--native-sign`; missing material or verification failure stops packaging. Windows uses SHA-256 Authenticode with RFC3161 timestamping and verifies each first-party PE. macOS signs Mach-O files inside-out and the bundle with hardened runtime, checks the team identifier, submits to Apple, requires Accepted status, staples and verifies the ticket, and assesses with Gatekeeper. The signed macOS archive uses ditto ZIP to preserve metadata. Temporary certificates, private keys and keychains are deleted in finally/trap handlers. Native-signed outputs also receive Sigstore provenance.

The scripts and workflow do not fabricate certificates. Until an actual credentialed run succeeds, do not label ordinary CI builds as notarized or SmartScreen-reputation-certified. Code signing does not automatically create SmartScreen reputation. Release installers, package-manager submission and automatic update delivery are separate distribution channels, not implied by a signed application archive.
