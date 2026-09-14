# UVM — Unified Verified Mods

UVM records public, signed build evidence for Cities: Skylines II mods at https://vezit.net. Source: https://github.com/vicmrp/CS2-UnifiedVerifiedMods (MIT license). See [USER_GUIDE.md](USER_GUIDE.md) for separate player, author and third-party verifier instructions.
The registry stores metadata, file hashes, GitHub account proofs and signatures. It never accepts mod DLL uploads or builds untrusted source on the server.

## For players

Download `UVM-v0.3.0.zip` from https://vezit.net and extract it.

* **CLI:** the `cli` folder contains `UVM.Cli.exe` and its dependencies. Install the .NET 10 runtime, then run `./cli/UVM.Cli.exe help`. Use this executable directly if you already have another `uvm` tool installed. For the command examples below, `uvm` means this executable. Building mods additionally requires a .NET SDK and the CS2 modding toolchain.
* **Game mod:** while the game is closed, copy the `mod/UVM` folder into `%USERPROFILE%/AppData/LocalLow/Colossal Order/Cities Skylines II/Mods/`. Start the game and open **Options > Unified Verified Mods > Scan > Scan downloaded mods**. The **Report** tab shows the summary and lets you select any cached package to read its evidence and explanation inside the game. The previous report remains available after restarting. This interface uses native C# game settings, with no Node/npm UI dependency. **Open browser copy** is an optional HTML export. For the published version, subscribe to [Unified Verified Mods](https://mods.paradoxplaza.com/mods/159198/Windows), enable it in your playset, and restart. Rename an existing local Mods/UVM folder to .UVM before subscribing to avoid loading two copies.
* **Browser:** use **Check an installed mod** on vezit.net, enter the Paradox mod ID and select the package folder. SHA-256 runs locally; only paths and hashes are submitted.

The scanner reads downloaded Paradox package folders such as `155518_1`. Names, installed version labels, revision numbers and reported dates come from the game's local Paradox metadata, without extra web requests. Old revisions only use matching changelog entries, never the latest version label. Dates and names do not prove binary identity; verification uses the actual file hashes. A cache can contain disabled mods and older versions, so the report labels them as cached packages. Content-only packages without supported code are marked unavailable. Local WIP mods without a trustworthy Paradox ID can be checked explicitly with `uvm check --mod-id ID --package PATH`. V1 does not inject a badge into the Paradox subscription screen.

Reproduced results show **Verified by** with the GitHub accounts whose active signatures and matching executable coverage the client validated. Multiple keys from one GitHub numeric account ID count once. The registry supplies the public GitHub-to-key mapping. An author's own matching build is self-verification; it is not an independent audit.

```powershell
uvm scan --out scan-report.json
uvm check --mod-id 155518 --package C:/path/to/package
```

## What the states mean

* **UNKNOWN:** no matching release evidence. This says nothing about whether a mod is safe.
* **PUBLISHED:** a registered GitHub account signed the expected hashes. This is a publisher claim, not proof that the account owns the Paradox listing.
* **REPRODUCED:** at least one registered GitHub account signed matching rebuilt hashes. Clients validate the ECDSA signature before showing this state. Counts are distinct GitHub accounts, not keys or submissions; several accounts can still belong to one person.
* **DISPUTED:** an active mismatch report or conflicting manifest exists for that release version. Inspect the evidence.
* **UNVERIFIED:** stored evidence no longer qualifies, for example after key revocation.
* **UNAVAILABLE:** a scan could not complete, a package has unsupported paths, or the registry is offline. There is no green fallback.

Reproduction is an attested claim about a build, not a security audit. Malicious source can reproduce perfectly. Anyone can sign a claim; read the linked source, build recipe and GitHub identity and decide whom to trust. The server is trusted to map the public GitHub proof to the immutable GitHub numeric account ID and to report revocations; signatures allow checking the original payload independently. This is not an append-only transparency log.

## Establish your GitHub signing identity

No OAuth app is required. You keep an encrypted private key locally and prove GitHub ownership with a public gist.

```powershell
uvm keygen
uvm key-proof --out uvm-key.json
```

Choose a strong passphrase. The private key defaults to `%LOCALAPPDATA%/UVM/signing-key.p8`; back it up separately from its passphrase. `--key PATH` selects another key. For unattended signing, supply `UVM_KEY_PASSWORD` using your CI secret store. Never commit private keys or passwords.

1. Open https://gist.github.com while signed in to your GitHub account.
2. Create a **public** gist containing the generated file, named exactly `uvm-key.json`.
3. Within one hour run `uvm register --gist https://gist.github.com/YOUR-ACCOUNT/GIST-ID`.

The proof contains only a public key, signed challenge and signature. UVM asks GitHub who owns the gist. The challenge is bound to the key and registry; copying it to another account cannot take over an already registered key. Keep the proof public for inspection. Repeat the challenge if it expires. GitHub's public API rate limits apply; retry later if it is unavailable.

To revoke a lost/retired key, sign with that key or another active key of the same GitHub account:

```powershell
uvm revoke --target FULL-KEY-FINGERPRINT --key ACTIVE-KEY.p8
```

Revocation is permanent for that fingerprint and immediately excludes its claims from counts. If every private key is lost, register a fresh key through a new public gist owned by the same account, then revoke the old key.

## For mod authors

Add `uvm.json` at your repository root, commit it, and tag the exact release. For a normal CS2 toolchain project, name, version and game version come from `Properties/PublishConfiguration.xml` beside the project.

```json
{
  "schema": 1,
  "platform": "paradox",
  "modId": 156779,
  "build": {"project": "FarmAutoFill.csproj", "configuration": "Release"},
  "output": ["FarmAutoFill.dll"]
}
```

This is an illustrative mod ID and project name, not a verified release. If your project does not have PublishConfiguration.xml, also set `name`, `version`, and `game_version`. The repository defaults to `git remote get-url origin`; `repository` can explicitly supply `https://github.com/owner/repository`.

Ignore `artifacts/` in Git. Keep generated packages outside the checkout (or ignored) so the tree remains clean.

```powershell
git checkout vYOUR-VERSION
uvm publish --package C:/temp/my-release-package --out artifacts/release.json
# Upload that unchanged package normally to Paradox Mods.
```

`publish` builds before signing a `published` claim. `build` performs the same build and records the manifest without contacting the registry. Output folders must be empty. The build records a full Git commit, project path, configuration, SDK version, Game.dll hash, game version, and every declared output hash. It passes deterministic build and source PathMap properties and redirects the standard CS2 toolchain's output/deployment folders into a temporary directory. It preserves game post-processing; arbitrary custom project scripts can still perform their own actions, so inspect them first.

Output entries are literal package-relative file paths; V1 does not support globs or arbitrary command recipes. Include all generated executable files and important assets such as UI CSS. A project with custom UI build steps must put the declared outputs in the build/deploy output folder. V1 discovers `CSII_MANAGEDPATH` (process or Windows user environment), then the standard Steam location. For another installation use `--game-managed PATH`. Full game-reference/toolchain equality can require additional environment documentation; Game.dll alone does not fingerprint the entire toolchain.

Do not edit the compiled package after publishing its manifest. If the files change, publish a new version and matching commit.

## For third-party verifiers

Clone public source that you trust to execute, check out the release's full commit, obtain its manifest from the public API, and build.

```powershell
git checkout FULL-COMMIT-SHA
uvm verify --manifest C:/evidence/release.json
# Or set version in uvm.json and use: uvm verify
```

Without `--package`, `verify` rebuilds and compares with the manifest, requiring the recorded build recipe/toolchain. With `--package PATH`, it only compares that existing package locally. A local match does not create a signed attestation.

To submit your build assertion:

```powershell
uvm build --package C:/temp/rebuilt-package --out artifacts/my-build.json
uvm attest --manifest C:/evidence/release.json --package C:/temp/rebuilt-package
```

`attest` requires the clean manifest commit/repository and matching Game.dll. It hashes the supplied package and submits `reproduced` or `mismatch`; it is your signed assertion that you built that package. A registry cannot prove that a remote verifier actually ran a build. A mismatch uses exit code 2. Operational errors use exit code 1.

## API and signature protocol

All requests/responses use JSON. The schema is version 1.

```
GET  /api/v1/mods?q=...
GET  /api/v1/mods/paradox/{id}
GET  /api/v1/mods/paradox/{id}/releases/{version}
GET  /api/v1/releases/{uuid}/attestations
GET  /api/v1/keys/{fingerprint}
POST /api/v1/lookup                 {"mod_id":"155518","files":{"mod.dll":"sha256..."}}
POST /api/v1/keys/challenge         {"public_key":"base64..."}
POST /api/v1/keys                   {"gist_url":"https://gist.github.com/..."}
POST /api/v1/attestations           {"key_id":"sha256...","payload":"base64...","signature":"base64..."}
POST /api/v1/keys/{id}/revoke       same signed envelope
GET  /healthz
```

Write authentication uses signed payloads, not cookies or bearer tokens. Key registration uses a GitHub-owned signed challenge. Sign the exact decoded UTF-8 payload bytes with ECDSA P-256/SHA-256. Signatures are IEEE P1363 raw `r || s` (64 bytes), not DER. The public key is SEC1 uncompressed `04 || x || y` (65 bytes); its SHA-256 is the fingerprint. The payload includes `schema:1`, exact `registry:"https://vezit.net"`, `purpose`, `key_id`, UTC `issued_at`, random `nonce`, `manifest`, `built_files`, and `result`. New attestations have a 24-hour submission window with five minutes of forward clock allowance. Replaying an accepted envelope is idempotent. Duplicate JSON fields are rejected in signed payloads.

Paths are package-relative, case-insensitive ASCII, with no parent traversal, absolute paths, links, alternate data streams or trailing dot/space components. There are at most 256 files per package. Declared files must all match; every installed executable extension (`dll`, `exe`, `so`, `bundle`, `js`, `mjs`, `cjs`, `wasm`) must be covered. Extra non-code assets are outside the claim unless explicitly declared. File contents are never sent by lookup.

## Server operations

The production stack is a Coolify Docker Compose service in `hostinger-always-online / production`, on the Hostinger server. `coolify-compose.yaml` is the reproducible service definition. Coolify generates the Django secret and database password and manages the HTTPS route to port 8000. PostgreSQL has a persistent volume and no public port.

Source is in `server/`. Build a local Docker image on the Hostinger server with `docker build -t uvm-registry:0.2.0 server`, matching `coolify-compose.yaml`, then deploy/restart through **Coolify**. The deployment image tag is independent of the mod/CLI version. `pull_policy: never` makes this explicit; the image must exist on that server. The source archive contains the full implementation and tests. This V1 is managed by Coolify but does not yet have Git push auto-deployment.

The container runs without root, migrates its database on startup and serves through Gunicorn. Back up PostgreSQL before upgrades. Keep off-server database backups; a persistent volume alone is not a backup. Restore data into a separate test database before a production rollback. Existing signing keys, revocations and signatures must be preserved across deployments.

Local server development: Python 3.13, `pip install -r server/requirements.txt`, set `UVM_SECRET_KEY` and `UVM_DEBUG=1`, then run `python server/manage.py migrate` and `python server/manage.py runserver`. SQLite is used only when POSTGRES_HOST is absent. Never enable DEBUG in production.

Tests: `python server/manage.py test registry`; `dotnet build tests/UVM.Tests.csproj -c Release`; run both framework outputs with a temporary fixture directory, then `python tests/check_cross_runtime.py <fixture-directory>/cross-runtime-proof.json`. These tests use local evidence and never add fake attestations to production.

Reference documentation: [GitHub gist API](https://docs.github.com/en/rest/gists/gists), [Django deployment checklist](https://docs.djangoproject.com/en/5.2/howto/deployment/checklist/), [Coolify Compose](https://coolify.io/docs/applications/builds/docker-compose).
