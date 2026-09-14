# Unified Verified Mods: user guide

## For players

1. Subscribe to [Unified Verified Mods on Paradox](https://mods.paradoxplaza.com/mods/159198/Windows), enable it in your playset and restart Cities: Skylines II. If you used a local development copy, close the game and rename `Mods/UVM` to `.UVM` before subscribing. Do the same for any other locally developed mod before subscribing to its published copy.
2. Open **Options → Unified Verified Mods → Scan** and select **Scan downloaded mods**.
3. Open **Report** and select a package. You will see its name, installed version, Paradox revision, evidence result and, for a validated matching build, **Verified by** with GitHub account names.
4. Inspect the source, signatures and account counts at [vezit.net](https://vezit.net). **Open browser copy** exports the current report; it is optional.

You need no Node installation, GitHub account or command-line tools to use the in-game report. It uses native C# settings and scans only when you request it. Hashing runs in the background; only mod IDs, relative file paths and hashes are sent. DLLs are never uploaded by the scanner.

**REPRODUCED** means matching signed build evidence. **PUBLISHED** means publisher hashes only. **UNKNOWN** means no matching release. **DISPUTED** means conflicting evidence. **UNVERIFIED** means qualifying evidence could not be validated. **UNAVAILABLE** means the check could not complete or there was no supported code to check. The downloaded cache can include disabled mods and old versions. UVM does not block subscriptions or disable other mods.

A matching build does not prove safety. An author can verify their own build; other-account builds are shown separately on the registry. Several GitHub accounts can still belong to one person.

## For mod authors

1. Keep source public on GitHub. Document the exact SDK, game and toolchain needed. Include a lockfile for UI dependencies and make the normal build deterministic.
2. Place `uvm.json` at the repository root. Describe the existing project and every package output, including native libraries, JavaScript and CSS. See this repository's recipe and the JourneyPlanner example.
3. Download the CLI from [vezit.net](https://vezit.net). The commands below use `uvm` as shorthand for the extracted `cli/UVM.Cli.exe`; invoke that executable explicitly if you have a different global `uvm` installed.
4. Register your signing identity once:

```powershell
uvm keygen
uvm key-proof --out uvm-key.json
# Post that public proof as a PUBLIC gist named uvm-key.json under your GitHub account.
uvm register --gist https://gist.github.com/YOUR-ACCOUNT/GIST-ID
```

The private key stays encrypted on your computer. Keep its passphrase outside your repository. The public proof expires for registration after one hour; create another challenge if needed.

5. Commit and push the exact source and recipe. Then, from the repository root:

```powershell
uvm publish --package artifacts/package --out artifacts/release.json
```

Use an empty package directory and ignore `artifacts/` in Git. UVM builds and records the source commit, toolchain, output hashes and signed publisher claim. Upload **that unchanged package** with the normal Paradox publishing workflow. Do not run a second build implicitly during upload. Tag the release commit.

6. Download the published version from Paradox and scan it. A publisher claim appears as PUBLISHED. A separate matching rebuild and signed attestation produces REPRODUCED. Your own rebuild remains self-verification; it does not count as another account.

If a release needs different bytes, use a new version and new receipt. Do not reuse a version label for a different manifest.

## For third-party verifiers

Use your own GitHub account and key. Register using the keygen, key-proof and register commands above. Never use the author's private key or copy their compiled DLL as your claimed rebuild.

1. Find the release on vezit.net and inspect its linked source and build recipe. Build scripts can execute code, so use source you have reviewed in a suitable build environment.
2. Save the selected release's `manifest` object from the public API as `release.json`. If the API lists conflicting manifests, inspect them rather than selecting one silently.
3. Clone the public GitHub repository, check out the full commit in the manifest, and install the recorded SDK and matching game/toolchain files. The checkout must be clean.
4. Rebuild into a new empty folder, compare, then sign your actual result:

```powershell
git checkout FULL-COMMIT-FROM-MANIFEST
uvm build --package artifacts/my-rebuild --out artifacts/my-build.json
uvm verify --manifest C:/evidence/release.json --package artifacts/my-rebuild
uvm attest --manifest C:/evidence/release.json --package artifacts/my-rebuild
```

`verify` without `--package` rebuilds and checks the recorded toolchain recipe. With `--package`, it only compares existing files. `attest` signs the actual comparison: matching files become reproduced; differing files become mismatch. Investigate a mismatch before submitting so a setup error is not mistaken for an intentional binary discrepancy. Do not claim you performed a security audit unless you actually did one.

Players can then see your GitHub identity and inspect your signature. Repeated submissions and multiple keys from the same GitHub numeric account ID count once. A registry cannot itself prove that a remote verifier executed a build.

## Build this project

Install .NET SDK 10.0.302, the .NET Framework 4.8 targeting pack and Cities: Skylines II. The mod needs no npm build:

```powershell
dotnet build UVM.Mod/UVM.Mod.csproj -c Release
```

Game references use the modding toolchain's `CSII_MANAGEDPATH` user variable; pass `-p:GameManagedPath=".../Cities2_Data/Managed"` for another installation. The only game package output is `UVM.dll`. The CLI and server have their own documented dependencies in their project files and README.

Original UVM code is [MIT licensed](LICENSE). Game assemblies and third-party dependencies retain their own licenses and are not included in this source repository.
