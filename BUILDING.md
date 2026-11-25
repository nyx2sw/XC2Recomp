# Building from Source

## Prerequisites (Windows x64)

- **.NET 10 SDK** (for `Xenoblade2Launcher.exe` and `XC2.Core.Tests`)
- **Node.js** (used by `launcher/build.ps1` for `node --check` on `ui/app.js`)
- **Clang / llvm-mingw** (`clang`, `llvm-strip`) + **CMake >= 3.25** + **Ninja** (for the ScarletNX runtime fork and native C extensions)
- **Python 3.10+** (for `tools/audit_public_repo.py` and optional `tools/build_shader_catalog.py`)

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

*(Note: Build with `-DCMAKE_BUILD_TYPE=Release` and `-ffile-prefix-map` instead of running `llvm-strip --strip-all` post-link — stripping PE sections from an unsigned Windows executable can trigger Windows Defender heuristic false positives.)*

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

This creates a clean, stripped, privacy-verified portable folder (`Xenoblade2Launcher.exe` + `runtime/`) ready to zip for GitHub Releases, containing **zero copyrighted game data**.

---

## 5. Run the Pre-Push Privacy & Copyright Audit

Before committing or pushing to a public repository, always run:

```powershell
python tools/audit_public_repo.py
```

The script fails non-zero if any game assets, saves, ROMs, `.nup` files, debug `.pdb`s, large binaries, personal paths/usernames, or missing attribution notices are detected.
