# StrikeEQ — Equalizador Gamer para Jogos Competitivos

Software desktop nativo para Windows (`.NET 8 / WPF` + DLL nativa C++20) com visual **Preto & Vermelho com degradês**, interface simplificada sem termos técnicos e latência **~0 ms** direto no seu fone.

## Menus do Aplicativo

- **Equalizador**: gráfico interativo em degradê vermelho com destaque na zona **★ PASSOS**, 10 sliders verticais, botões rápidos **`★ + Passos`** / **`− Graves`** / **`Zerar`** e o controle simples do **Modo Passos & Proteção de Tiros**.
- **Perfis de Jogo**: grade limpa de presets prontos (**CS2**, **Valorant**, **Warzone / COD**, **Apex Legends**, **Rainbow Six Siege**, **Footsteps Hyper-Boost**, **Modo Noturno** e **Flat**) + criação e salvamento de perfis personalizados.
- **Testar Som**: ouça cenários simulados (*Passos + Tiros*, *Apenas Passos Esq/Dir*, *Ruído de Calibração* ou seu próprio `.WAV`) comparando o equalizador ligado/desligado na hora.
- **Saída de Áudio**: tela dedicada para escolher seu fone de ouvido, ativar/desativar o StrikeEQ direto no fone em 1 clique (DLL nativa `StrikeEQ.Apo.dll` ~0 ms, sem terceiros), preferências de bandeja/auto-detecção e atalhos globais (`Ctrl+Alt+B`, `Ctrl+Alt+N`, `Ctrl+Alt+1..4`).

## Como Compilar e Executar

```powershell
# Gerar executável único portátil (publish\StrikeEQ.exe — funciona em qualquer pasta)
dotnet publish .\src\StrikeEQ\StrikeEQ.csproj -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o .\publish

# Executar o aplicativo portátil
Start-Process .\publish\StrikeEQ.exe

# Executar a suíte de testes do motor DSP
dotnet run --project .\tests\StrikeEQ.Tests\StrikeEQ.Tests.csproj -c Release
```
