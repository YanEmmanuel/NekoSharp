# NekoSharp.Tools

CLI utilities for NekoSharp.

## Provider Scraper Wizard

Run the interactive wizard to create a new provider scraper:

```bash
dotnet run --project ./NekoSharp.Tools -- new-scraper
dotnet run --project ./NekoSharp.Tools -- create
dotnet run --project ./NekoSharp.Tools -- new
```

The wizard creates a scraper file under `NekoSharp.Core/Providers/{SiteName}/{SiteName}Scraper.cs`.
The provider is automatically discovered and registered at app startup — no plugin configuration needed.

```
