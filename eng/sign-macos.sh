#!/usr/bin/env bash
# Fail closed: Developer ID, notarization and stapling all have to succeed before packaging.
set -euo pipefail
app=${1:?App bundle required}
for name in APPLE_P12_BASE64 APPLE_P12_PASSWORD APPLE_TEAM_ID APPLE_NOTARY_KEY_BASE64 APPLE_NOTARY_KEY_ID APPLE_NOTARY_ISSUER_ID; do
  test -n "${!name:-}" || { echo "Missing signing secret: $name" >&2;exit 1; }
done
work=$(mktemp -d "$RUNNER_TEMP/tessera-sign.XXXXXX")
chmod 700 "$work"
keychain="$work/signing.keychain-db"
cleanup() { security delete-keychain "$keychain" >/dev/null 2>&1 || true;rm -rf "$work"; }
trap cleanup EXIT
export SIGN_WORK="$work"
python3 - <<'PY'
import os,base64,pathlib
for variable,name in [('APPLE_P12_BASE64','identity.p12'),('APPLE_NOTARY_KEY_BASE64','notary.p8')]:
    p=pathlib.Path(os.environ['SIGN_WORK'])/name;p.write_bytes(base64.b64decode(os.environ[variable],validate=True));p.chmod(0o600)
PY
password=$(openssl rand -hex 32)
echo "::add-mask::$password"
security create-keychain -p "$password" "$keychain"
security set-keychain-settings -lut 21600 "$keychain"
security unlock-keychain -p "$password" "$keychain"
security import "$work/identity.p12" -k "$keychain" -P "$APPLE_P12_PASSWORD" -T /usr/bin/codesign -T /usr/bin/security
security set-key-partition-list -S apple-tool:,apple:,codesign: -s -k "$password" "$keychain" >/dev/null
identity=$(security find-identity -v -p codesigning "$keychain" | awk '/Developer ID Application/ {print $2;exit}')
test -n "$identity" || { echo 'No Developer ID Application identity found.' >&2;exit 1; }
# Sign Mach-O dependencies inside-out, then the bundle. Never use --deep to mask missing nested signatures.
while IFS= read -r -d '' file; do
  if /usr/bin/file -b "$file" | grep -q 'Mach-O'; then
    codesign --force --options runtime --timestamp --keychain "$keychain" --sign "$identity" "$file"
  fi
done < <(find "$app/Contents/MacOS" -type f -print0)
codesign --force --options runtime --timestamp --keychain "$keychain" --sign "$identity" --entitlements eng/macos-entitlements.plist "$app"
codesign --verify --strict --verbose=2 "$app"
codesign -dvv "$app" 2>&1 | grep -Fx "TeamIdentifier=$APPLE_TEAM_ID"
ditto -c -k --keepParent "$app" "$work/submission.zip"
xcrun notarytool submit "$work/submission.zip" --key "$work/notary.p8" --key-id "$APPLE_NOTARY_KEY_ID" --issuer "$APPLE_NOTARY_ISSUER_ID" --wait --output-format json > "$work/notary-result.json"
python3 - <<'PY'
import json,os,pathlib
r=json.loads((pathlib.Path(os.environ['SIGN_WORK'])/'notary-result.json').read_text())
if r.get('status')!='Accepted':raise SystemExit('Apple did not accept the notarization submission.')
print('Notarization accepted:',r['id'])
PY
xcrun stapler staple "$app"
xcrun stapler validate "$app"
spctl --assess --type execute --verbose=2 "$app"
