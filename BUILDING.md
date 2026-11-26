# Building from Source

## Prerequisites (Windows x64)

- **.NET 10 SDK** (for `Xenoblade2Launcher.exe` and `XC2.Core.Tests`)
- **Node.js** (used by `launcher/build.ps1` for `node --check` on `ui/app.js`)
- **Clang / llvm-mingw** (`clang`, `llvm-strip`) + **CMake >= 3.25** + **Ninja** (for the ScarletNX runtime fork and native C extensions)
- **Python 3.10+** (for the repository audit, menu overlay, and optional shader catalog)

---

## 1. Build the Runtime (`ScarletNX`, branch `xc2-sw2-native`)

Clone the companion runtime repository (`xc2-sw2-native` branch) alongside this repository, e.g. `../ScarletNX`, and build with Clang + Ninja (universal `x86-64-v3` target):

```powershell
cmake -S ../ScarletNX -B ../snx_build -G Ninja `
  -DCMAKE_BUILD_TYPE=Release `
  -DCMAKE_C_COMPILER=clang `
  -DCMAKE_CXX_COMPILER=clang++ `
  -DCMAKE_C_FLAGS="-O3 -march=x86-64-v3 -flto=thin -ffile-prefix-map=$PWD=/src" `
  -DCMAKE_CXX_FLAGS="-O3 -march=x86-64-v3 -flto=thin -ffile-prefix-map=$PWD=/src" `
  -DSNX_UPSCALE_FSR3=ON -DSNX_DEPLOY=OFF

cmake --build ../snx_build --target scarletnx -j
ctest --test-dir ../snx_build -R "shader_direct|state_decode|hactool" --output-on-failure
```

Use the `Release` configuration for distribution. Keep the build under test paired with its exact executable SHA-256. A Defender result on one build does not establish the result for a different build from the same source.

---

## 2. Build the Native Extensions (`native_ui.dll` & `presence.dll`)

Pass the path to your ScarletNX checkout (for `src/nx/runtime/native_ui.h`):

```powershell
./extensions/native_ui/build.ps1 -SnxRoot ../ScarletNX
./extensions/presence/build.ps1  -SnxRoot ../ScarletNX
```

Both scripts compile with `-Wall -Wextra -Werror -O2 -ffile-prefix-map`, strip debug symbols, and run their C unit test suites (`test_model.exe`, `model_test.exe`).

---

## 3. Build & Test the Launcher

```powershell
./launcher/build.ps1
```

This checks `launcher/ui/app.js` syntax, runs `XC2.Core.Tests`, and publishes a self-contained single-file `Xenoblade2Launcher.exe` (with `<Deterministic>true</Deterministic>` and `<PathMap>` enabled so no local workspace paths are embedded).

---

## 4. Assemble a Portable Release Archive

```powershell
./tools/build_release.ps1 -SnxBuildDir ../snx_build -SnxRoot ../ScarletNX -OutDir release/XC2-PC-Edition
```

This stages `Xenoblade2Launcher.exe` and its `runtime/` dependencies. The archive excludes game packages, saves, and compiled game code. Optional overlays supplied at build time are included as separate resource packs.

For a package with the native PC menu available in the original language, pass `-GameRomfsDir` with the original 3.0.1 RomFS and `-PcUiAssetsDir` with the prepared native-menu assets. The packager reads the base index, makes a menu-only overlay in `runtime/pc_ui/original_menu`, and records the base index checksum and archive length. Pass `-LocalizationSourceDir` to include the two PT-BR overlays.

The export includes `release-manifest.json` with both Git commits, hashes of any working-tree changes, packaged file hashes, and the packager's test results. Pass `-RuntimeTestLog` with a passing CTest log from the runtime build. With `-Zip`, the packager also writes a matching `<zip>.sha256` file. Upload the ZIP and its checksum together for the launcher updater to accept the version.

The ZIP is checked against its manifest after compression. Pass `-PrivateRoot $env:USERPROFILE` to check archive members for that absolute profile path. Scan the exact packaged executable and archive with Defender active before distributing them. Preserve any flagged version as release evidence and use the [Microsoft software developer submission portal](https://www.microsoft.com/en-us/wdsi/filesubmission) to request a review.

For distribution, provide the corresponding source for the build and its license notices. Pass `-ThirdPartyLicenseDir` to include the notices collected from the dependencies used by that build. Keep the source archive next to the binary release.

The updater recovers interrupted file replacements at startup. If the launcher was interrupted between renaming its old executable and installing the new one and the main executable is missing, copy `Xenoblade2Launcher.exe.old` to `Xenoblade2LauncherRecovery.exe` in the same directory and run the copy. The recovery reads `.xc2-update.json` from that directory to restore the package.

---

## 5. Run the Pre-Push Privacy & Copyright Audit

Before committing or pushing to a public repository, always run:

```powershell
python tools/audit_public_repo.py
```

The script fails non-zero if any game assets, saves, ROMs, `.nup` files, debug `.pdb`s, large binaries, personal paths/usernames, or missing attribution notices are detected.
