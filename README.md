# XC2Recomp — Xenoblade Chronicles 2: PC Edition

A native Windows PC launcher, First-Run `.nup` setup pipeline, and runtime extensions for **Xenoblade Chronicles 2 — Nintendo Switch 2 Edition (v3.0.1)**, powered by a customized fork of [ScarletNX](https://github.com/scarletnx/ScarletNX) (derived from **SwitchRecomp by MZ Labs**).

> **Legal Notice — Bring Your Own Game**
>
> This repository contains **only original source code, build scripts, and clean-room tooling**.
> **No copyrighted game files, ROMs, `.nup`/`.nsp`/`.xci` packages, shaders, artwork, audio, saves, encryption keys, or console firmware are included or distributed.**
> To use this software, you must supply your own legally obtained *Xenoblade Chronicles 2 — Nintendo Switch 2 Edition (v3.0.1)* `.nup` dump (`Title ID: 0400E95004038001`).

---

## Sobre o Projeto (Português)

**XC2Recomp** é um projeto independente que traz *Xenoblade Chronicles 2 — Nintendo Switch 2 Edition (v3.0.1)* de forma nativa para PC (Windows), com recompilação estática (AOT), launcher próprio e melhorias modernas de imagem e desempenho — incluindo resoluções até 4K, 60–240 FPS, DLSS 4.5 / FSR 3.1, menu nativo de opções de PC dentro do jogo, backup automático de saves e integração com Discord e Steam.

A versão pronta na aba **Releases** já acompanha a **tradução e localização completa em Português do Brasil (PT-BR)**, pronta para usar assim que você aplica o seu arquivo `.nup`, com troca em 1 clique no launcher sem alterar os arquivos originais do jogo:

- **PT-BR (Localização Ocidental):** textos, menus, interface e diálogos completos em português mantendo os nomes oficiais do ocidente (*Pyra, Mythra, Malos, Morag, Alrest*).
- **PT-BR (Nomes Originais JP):** tradução completa preservando os nomes originais do roteiro japonês (*Homura, Hikari, Metsu, Meleph, Alst*), pensada especialmente para quem joga com áudio em japonês.
- **Original do Jogo:** permite voltar ao texto original intacto quando quiser.

> Basta baixar o pacote da Release, abrir o `Xenoblade2Launcher.exe` e arrastar/selecionar o seu arquivo `.nup` da Switch 2 Edition 3.0.1 — o instalador cuida da extração, das expansões (incluindo *Torna*), da compilação e ativa as artes, trilha sonora, efeitos e tradução automaticamente.

---

## Highlights

- **Guided First-Run `.nup` Setup Pipeline** (`XC2.Core`):
  - Validates NUP v1 header, Title ID (`0400E95004038001`), and TOC + per-file SHA-256 integrity.
  - Streaming Zstd extraction of ExeFS, RomFS, and `control.nacp` (v3.0.1).
  - Clean-room C# `exefs/subsdk1` stub generator (reads only Build ID and exported symbol names from `exefs_host_bypass/subsdk1` to expose dynamic NVN2 thunk slots).
  - Deterministic Nintendo Switch Horizon `.romfs` builder for Add-On Content (Torna, Expansion Pass packs, JP Voice Pack).
  - Automatic AOT recompilation (`Ounce64 → x86-64-v3 AVX2/FMA`) with pre-seeded indirect-branch hints (`xenoblade2.hints`).
- **Native Win32 + WebView2 Launcher** (`XC2.Launcher`):
  - Zero Electron overhead; keyboard + Xbox/DualSense/Switch Pro gamepad navigation.
  - Hardware-aware preset recommendations (NVIDIA DLSS 4.5 / DLAA, AMD FSR 3.1, Intel XeSS, 60–240 FPS, up to 4K).
  - Live `xenoblade2.ini` hot-reload while playing.
  - Safe atomic save backups, Switch emulator save import, mod/localization manager, and byte-exact Steam non-Steam shortcut integration.
  - Pure managed C# runtime media extractor reading artwork (`bf2.arh`/`bf2.ard`), BGM (`.nop`), and UI SFX (`system.pck`) directly from the player's own extracted `romfs` (zero copyrighted media embedded in the repository).
  - Optional 1-click **NVIDIA DLSS 4.5 / DLAA** installer (on-demand download directly from NVIDIA's official `NVIDIA/DLSS` repository with Windows Authenticode verification — never redistributed in this repo/zip).
  - Optional built-in pure C# **Shader Catalog Builder** (`xc2_shaders.snxs`) in Onboarding & Settings to pre-compile shaders once from the user's own `romfs` + expansions (no Python needed).
- **Native Runtime Extensions** (`extensions/`):
  - `native_ui.dll`: In-game PC Settings extension (`System → Options`), gracefully preserving original menus when custom layout assets are absent.
  - `presence.dll`: Discord Rich Presence showing live region, landmark, Driver, Blade, and session timer.

---

## Quick Start (Pre-built Release)

1. Download and extract the portable Release archive to any folder with at least **40 GiB free** on the drive (e.g., `D:\Games\XC2-PC-Edition\`).
   - Expected folder layout:
     ```text
     XC2-PC-Edition/
       Xenoblade2Launcher.exe
       runtime/
         xenoblade2.exe
         libLLVM-23.dll, libzstd.dll, libc++.dll, ...
         xenoblade2.hints
         pc_ui/
           native_ui.dll
           presence.dll
           presence_names.tsv
         localization/
           pt-br/
           pt-br-jp/
         licenses/
     ```
2. Ensure **Microsoft Edge WebView2 Runtime** (pre-installed on Windows 10/11) and up-to-date **Vulkan GPU drivers** are installed.
3. Open `Xenoblade2Launcher.exe`.
4. Drag and drop (or select) your legal **Xenoblade Chronicles 2 — Nintendo Switch 2 Edition 3.0.1 `.nup`** file and click **Iniciar instalação agora**.
5. *(Optional — Zero shader stutter)* In Onboarding Step 2 or **Configurações › Vídeo**, click **Preparar shaders agora** to build `shader_cache/xc2_shaders.snxs` locally from your own extracted `romfs` + expansions (or run `python tools/build_shader_catalog.py` from CLI).
6. *(Optional — NVIDIA RTX DLSS 4.5 / DLAA)* In Onboarding Step 2 or **Configurações › Vídeo**, click **Baixar DLSS oficial** to fetch the Authenticode-signed `nvngx_dlss.dll` directly from NVIDIA's official `NVIDIA/DLSS` repo (otherwise AMD FSR 3.1 / Intel XeSS is used automatically).

---

## Repository Structure

```text
launcher/              .NET 10 Windows launcher (App, Core, Tests, embedded WebView2 UI)
extensions/
  native_ui/           In-game PC Settings extension (C + unit tests)
  presence/            Discord Rich Presence extension (C + unit tests)
tools/
  build_shader_catalog.py  Local read-only shader catalog builder (from user's own romfs)
  xc2_archive.py           Read-only ARH1/ARD reader helper
  build_release.ps1        Portable Release packager (stripped, privacy-audited)
  audit_public_repo.py     Pre-push copyright & privacy verifier
  xenoblade2.hints         Indirect-branch address hints for AOT recompilation
BUILDING.md            Full build instructions (runtime, launcher, extensions, release zip)
LICENSE                GNU General Public License v3.0 or later
LICENSE.apache         Apache License 2.0 (SwitchRecomp upstream)
NOTICE                 Required attribution notice (SwitchRecomp by MZ Labs & ScarletNX)
```

---

## Building from Source

See [BUILDING.md](BUILDING.md) for complete build steps for the runtime fork (`xc2-sw2-native`), launcher, extensions, and portable Release archive.

---

## Credits & Licensing

- This project includes and builds upon software from **SwitchRecomp by MZ Labs** (licensed under the **Apache License 2.0**; see [LICENSE.apache](LICENSE.apache) and [NOTICE](NOTICE)) and **ScarletNX** (licensed under the **GNU General Public License v3.0 or later**; see [LICENSE](LICENSE)).
- The launcher, extensions, and tooling in this repository are licensed under **GPL-3.0-or-later**.
- *Xenoblade Chronicles 2* is a trademark of Nintendo / Monolith Soft. This project is an independent, non-commercial technical project with no affiliation with Nintendo or Monolith Soft.
