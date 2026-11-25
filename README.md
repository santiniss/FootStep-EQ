# FootStep EQ — Equalizador Gamer para Jogos Competitivos

Software desktop nativo para Windows (`.NET 8 / WPF` + DLL nativa C++20) com visual **Preto & Vermelho com degradês**, interface simplificada em menus laterais e latência **~0 ms** direto no seu fone — sem depender de programas de terceiros.

## Menus do Aplicativo

- **Equalizador**: gráfico interativo em degradê vermelho com destaque na zona **★ PASSOS**, 10 sliders verticais, botões rápidos **`★ + Passos`** / **`− Graves`** / **`Zerar`** e o controle simples do **Modo Passos & Proteção de Tiros**.
- **Perfis de Jogo**: grade limpa de presets prontos (**CS2**, **Valorant**, **Warzone / COD**, **Apex Legends**, **Rainbow Six Siege**, **Footsteps Hyper-Boost**, **Modo Noturno** e **Flat**) + criação e salvamento de perfis personalizados.
- **Testar Som**: ouça cenários simulados (*Passos + Tiros*, *Apenas Passos Esq/Dir*, *Ruído de Calibração* ou seu próprio `.WAV`) comparando o equalizador ligado/desligado na hora.
- **Saída de Áudio**: tela dedicada para escolher seu fone de ouvido, ativar/desativar o FootStep EQ direto no fone em 1 clique (`FootStepEQ.Apo.dll`, ~0 ms, 100% User-Mode compatível com Vanguard/FACEIT), preferências de bandeja/auto-detecção, atalhos globais (`Ctrl+Alt+B`, `Ctrl+Alt+N`, `Ctrl+Alt+1..4`) e verificador de atualizações via GitHub Releases.

## Como Compilar e Executar

```powershell
# Compilar a DLL nativa C++ (FootStepEQ.Apo.dll)
.\native\StrikeEQ.Apo\build_apo.ps1

# Publicar o executável portátil (publish\FootStepEQ.exe)
dotnet publish .\src\StrikeEQ\StrikeEQ.csproj -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o .\publish

# Executar o aplicativo
Start-Process .\publish\FootStepEQ.exe

# Executar a suíte de testes (DSP + COM APO nativo)
dotnet run --project .\tests\StrikeEQ.Tests\StrikeEQ.Tests.csproj -c Release
```
