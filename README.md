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
