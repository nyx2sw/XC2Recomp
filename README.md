# Xenoblade Chronicles 2: PC Edition

[Read this in English](#english)

Criamos o XC2Recomp para jogar **Xenoblade Chronicles 2 — Nintendo Switch 2 Edition** no Windows, com tradução para português do Brasil e opções de gráficos dentro do próprio jogo. Você traz uma cópia extraída do seu jogo, e o launcher cuida da primeira instalação.

## Como jogar

1. Baixe o ZIP portátil para Windows na [página de releases](https://github.com/nyx2sw/XC2Recomp/releases/latest) e extraia em uma pasta num disco com pelo menos **40 GiB livres**.
2. Abra `Xenoblade2Launcher.exe` e selecione o arquivo `.nup` da **Nintendo Switch 2 Edition 3.0.1** que você extraiu do seu jogo.
3. Inicie a instalação e deixe o launcher preparar o jogo. Se o pacote incluir *Torna: The Golden Country* ou outros extras, o launcher prepara esse conteúdo também.

Você vai precisar de Windows 10 ou 11 de 64 bits, um processador com AVX2 e FMA, drivers gráficos com suporte a Vulkan e o Microsoft Edge WebView2 Runtime.

Se você já tem o jogo instalado, pode atualizar pelo próprio launcher. Seus saves e configurações ficam na instalação.

## O que dá para ajustar

- **Idioma.** Jogue em português com os nomes ocidentais, como Pyra e Mythra, ou com os nomes japoneses, como Homura e Hikari. Você também pode voltar ao texto original pelo launcher.
- **Imagem e desempenho.** Abra as Configurações de PC em **Sistema → Opções** para ajustar a resolução (até 4K), o limite de FPS e a qualidade de imagem. As opções de DLSS, DLAA, FSR e XeSS dependem do hardware e da configuração. Em **Configurações → Vídeo** no launcher, você pode preparar os shaders com antecedência para reduzir engasgos durante o jogo.
- **Saves e atalhos.** O launcher faz backup dos saves, importa saves compatíveis, adiciona um atalho à Steam e pode mostrar no Discord o que você está jogando.

## English

XC2Recomp brings **Xenoblade Chronicles 2 — Nintendo Switch 2 Edition** to Windows with an in-game PC settings menu and a Brazilian Portuguese translation. You can also play with the game's original text.

1. Extract the portable Windows ZIP from the [latest release](https://github.com/nyx2sw/XC2Recomp/releases/latest) to a drive with at least **40 GiB free**.
2. Open `Xenoblade2Launcher.exe`, select your own **Switch 2 Edition 3.0.1 `.nup`** dump, and start the setup. The launcher also sets up *Torna: The Golden Country* and other add-ons included in your dump.
3. Once you're in the game, open **System → Options** for PC graphics settings. The launcher can prepare shaders ahead of time to help reduce stutter and can install updates while keeping your saves and settings.

You'll need 64-bit Windows 10 or 11, an AVX2/FMA-capable CPU, Vulkan drivers, and the Microsoft Edge WebView2 Runtime.

The launcher can also import compatible saves, add a Steam shortcut, and share what you're playing on Discord. Graphics options include up to 4K and DLSS, DLAA, FSR and XeSS on compatible setups.

## Código, créditos e licenças / Source and credits

O XC2Recomp é um projeto independente e sem fins comerciais, feito por fãs. O port usa uma versão adaptada do [ScarletNX](https://github.com/scarletnx/ScarletNX), que deriva do SwitchRecomp da MZ Labs.

Para compilar o launcher, o runtime e as extensões, siga o [guia de compilação / build guide](BUILDING.md). O launcher, as extensões e as ferramentas deste repositório seguem a [GPL-3.0-ou-posterior](LICENSE). O SwitchRecomp usa a [Apache License 2.0](LICENSE.apache). As atribuições estão no [NOTICE](NOTICE).

XC2Recomp is an independent, non-commercial fan project built on a fork of ScarletNX, which comes from MZ Labs' SwitchRecomp. The build guide, licenses and attribution are linked above.
