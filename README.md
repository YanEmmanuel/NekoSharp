# NekoSharp

NekoSharp é um downloader de mangás em C# com arquitetura modular, interface desktop em GTK/libadwaita e pipeline de processamento de imagens para organização, conversão e empacotamento dos capítulos.

## Visão Geral

O projeto foi desenhado para separar claramente interface, domínio e ferramentas:

- `NekoSharp.App`: aplicação desktop (UI + ViewModels).
- `NekoSharp.Core`: scrapers, serviços de download, persistência de configurações e tratamento de Cloudflare.
- `NekoSharp.Tools`: CLI para geração de novos providers.
- `NekoSharp.Tests`: diretório reservado para testes automatizados.

## Recursos Principais

- Descoberta automática de providers via reflexão.
- Atualização dinâmica de providers externos via manifesto remoto (sem reinstalar o app).
- Download de capítulos em paralelo com limite configurável.
- Exportação em:
  - pasta com imagens
  - arquivo `.cbz`
- Conversão de imagens para `Original`, `JPEG`, `PNG` ou `WebP`.
- Compressão configurável com reencode e redimensionamento progressivo.
- SmartStitch para pós-processamento:
  - união vertical das páginas
  - detecção de cortes por comparação de pixels
  - recorte inteligente e salvamento no formato escolhido
- Persistência de configurações em SQLite.
- Janela de logs em tempo real.
- Suporte a desafios Cloudflare com reaproveitamento de credenciais temporárias.

## Providers Atualmente Implementados

- MangaDex
- Exyaoi (`3xyaoi.com`)
- fbsquadx (`fbsquadx.com`)

## Atualização Dinâmica de Providers

O app verifica, ao iniciar, um manifesto remoto de providers e baixa DLLs externas para:

- Linux: `~/.config/NekoSharp/providers`
- Windows: `%AppData%/NekoSharp/providers`

Configurações usadas no banco de settings:

- `Providers.DynamicUpdates.Enabled` (bool)
- `Providers.DynamicUpdates.ManifestUrl` (string)

Manifesto padrão esperado:

```json
{
  "providers": [
    {
      "name": "MeuProvider",
      "version": "2026.02.26",
      "assemblyUrl": "https://meu-cdn/providers/MeuProvider.dll",
      "sha256": "HEX_OPCIONAL",
      "enabled": true
    }
  ]
}
```

## Requisitos

- .NET SDK `10.0.x`
- Runtime nativo de `GTK4` e `libadwaita` disponível no sistema (para `NekoSharp.App`)
- Navegador Chrome/Chromium instalado (necessário quando houver desafio Cloudflare em alguns providers)

## Como Executar

### 1. Restaurar e compilar

```bash
dotnet restore NekoSharp.sln
dotnet build NekoSharp.sln -c Release
```

### 2. Rodar a aplicação desktop

```bash
dotnet run --project ./NekoSharp.App
```

### 3. Rodar a ferramenta de criação de provider

```bash
dotnet run --project ./NekoSharp.Tools -- new-scraper
```

## Build e Release (Windows + Linux)

O repositório já possui workflows para CI e release multiplataforma:

- `.github/workflows/build.yml`
  - valida `restore + build + test` em `windows-latest` e `ubuntu-latest`
  - publica artefatos de app para `win-x64` e `linux-x64`
- `.github/workflows/release.yml`
  - em tag `v*`, valida novamente build/test em Windows e Linux
  - gera assets de release para os dois sistemas

### Publish local Linux (linux-x64)

```bash
dotnet restore ./NekoSharp.App/NekoSharp.App.csproj -r linux-x64
dotnet publish ./NekoSharp.App/NekoSharp.App.csproj \
  -c Release -r linux-x64 --self-contained true \
  -p:PublishSingleFile=true \
  -o out/linux-x64
```

### Publish local Windows (win-x64)

```powershell
dotnet restore .\NekoSharp.App\NekoSharp.App.csproj -r win-x64
dotnet publish .\NekoSharp.App\NekoSharp.App.csproj `
  -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true `
  -p:IncludeNativeLibrariesForSelfExtract=true `
  -o out/win-x64
```

Observações (Windows):

- O pacote portátil gerado pelo CI/release já inclui o runtime GTK4/libadwaita.
- Abrir o `.exe` diretamente é suportado, desde que a pasta `gtk` esteja ao lado do executável.
- `Run-NekoSharp.cmd` continua suportado para iniciar o app com ambiente configurado.
- Diagnóstico rápido de carregamento nativo:

```powershell
.\NekoSharp.App.exe --native-smoke
```

## Fluxo de Uso da Aplicação

1. Abra o app.
2. Cole a URL do mangá.
3. Clique em buscar para carregar metadados e capítulos.
4. Selecione capítulos específicos ou baixe todos.
5. Ajuste formato, compressão, pasta de saída e SmartStitch em Configurações.
6. Acompanhe progresso e logs em tempo real.

## Pipeline Técnico

1. O `ScraperManager` identifica o provider compatível com a URL.
2. O scraper coleta dados do mangá, capítulos e páginas.
3. O `DownloadService` baixa páginas com retry e concorrência.
4. Imagens podem ser convertidas/comprimidas conforme configuração.
5. Opcionalmente o SmartStitch realiza pós-processamento.
6. Resultado final é salvo como pasta de imagens ou `.cbz`.

## Estrutura do Repositório

```text
NekoSharp/
├── NekoSharp.App/
├── NekoSharp.Core/
├── NekoSharp.Tools/
├── NekoSharp.Tests/
├── tools/
└── NekoSharp.sln
```

## Licença

Este projeto está licenciado sob a licença MIT. Veja o arquivo `LICENSE` para mais detalhes.
