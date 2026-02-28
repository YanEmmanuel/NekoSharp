using System.Text;
using Spectre.Console;

namespace NekoSharp.Tools;

internal static class Program
{
    private enum ScraperTemplateKind
    {
        Standard,
        WordPressMadara
    }

    private static readonly string RootDir = Directory.GetCurrentDirectory();
    private static readonly string ProvidersDir = Path.Combine(RootDir, "NekoSharp.Core", "Providers");

    private static int Main(string[] args)
    {
        var command = args.Length > 0 ? args[0] : string.Empty;
        command = NormalizeCommand(command);

        return command switch
        {
            "new-scraper" => RunNewScraperWizard(),
            _ => ShowMenu()
        };
    }

    private static int ShowMenu()
    {
        AnsiConsole.Clear();
        WriteBanner();

        var choice = AnsiConsole.Prompt(
            new SelectionPrompt<string>()
                .Title("[bold cyan]O que você quer fazer?[/]")
                .AddChoices(
                    "🆕  Criar novo provider (new-scraper)",
                    "❌  Sair"));

        return choice switch
        {
            _ when choice.Contains("new-scraper") => RunNewScraperWizard(),
            _ => 0
        };
    }

    private static int RunNewScraperWizard()
    {
        AnsiConsole.Clear();
        WriteBanner();
        AnsiConsole.MarkupLine("[bold green]📝 Provider Scraper Wizard[/]");
        AnsiConsole.WriteLine();

        var siteName = Ask("Nome do site (ex: MangaDex)", "MySite");
        var className = Ask("Nome da classe Scraper", $"{siteName}Scraper");
        var baseUrl = Ask("Base URL do site", "https://example.com");
        var templateKind = AskTemplateKind();

        AnsiConsole.WriteLine();
        var table = new Table().Border(TableBorder.Rounded).BorderColor(Color.Cyan1);
        table.AddColumn("Campo");
        table.AddColumn("Valor");
        table.AddRow("Tipo", GetTemplateKindLabel(templateKind));
        table.AddRow("Site", siteName);
        table.AddRow("Classe", className);
        table.AddRow("Namespace", $"NekoSharp.Core.Providers.{siteName}");
        table.AddRow("Base URL", baseUrl);
        table.AddRow("Caminho", $"NekoSharp.Core/Providers/{siteName}/{className}.cs");
        AnsiConsole.Write(table);
        AnsiConsole.WriteLine();

        if (!AnsiConsole.Confirm("Confirma a criação?", true))
        {
            AnsiConsole.MarkupLine("[yellow]Cancelado.[/]");
            return 1;
        }

        var targetDir = Path.Combine(ProvidersDir, siteName);
        var scraperFile = Path.Combine(targetDir, $"{className}.cs");

        if (File.Exists(scraperFile))
        {
            AnsiConsole.MarkupLine($"[red]Erro:[/] arquivo já existe: {Markup.Escape(scraperFile)}");
            return 1;
        }

        AnsiConsole.Status()
            .Spinner(Spinner.Known.Dots)
            .SpinnerStyle(Style.Parse("cyan"))
            .Start("Criando provider...", _ =>
            {
                Directory.CreateDirectory(targetDir);
                WriteScraper(scraperFile, siteName, className, baseUrl, templateKind);
            });

        AnsiConsole.MarkupLine("[green]✔ Provider criado![/]");
        AnsiConsole.WriteLine();

        var rule = new Rule("[cyan]Próximo passo[/]").LeftJustified();
        AnsiConsole.Write(rule);
        AnsiConsole.MarkupLine($"  Edite: [cyan]{Markup.Escape(scraperFile)}[/]");
        if (templateKind == ScraperTemplateKind.Standard)
            AnsiConsole.MarkupLine("  Implemente os métodos TODO.");
        else
            AnsiConsole.MarkupLine("  Ajuste os seletores com overrides no template, se necessário.");
        AnsiConsole.MarkupLine("  O provider será registrado automaticamente ao iniciar o app.");
        AnsiConsole.WriteLine();

        var rule2 = new Rule("[cyan]Comandos úteis[/]").LeftJustified();
        AnsiConsole.Write(rule2);
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("  [grey]# Compilar o projeto[/]");
        AnsiConsole.MarkupLine("  [white]dotnet build[/]");
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("  [grey]# Rodar o app[/]");
        AnsiConsole.MarkupLine("  [white]dotnet run --project ./NekoSharp.App[/]");
        AnsiConsole.WriteLine();

        return 0;
    }

     

    private static string NormalizeCommand(string command)
    {
        return command.ToLowerInvariant() switch
        {
            "create" or "new" or "new-scraper" => "new-scraper",
            _ => command
        };
    }

    private static string Ask(string prompt, string defaultValue)
    {
        return AnsiConsole.Prompt(
            new TextPrompt<string>($"[cyan]{Markup.Escape(prompt)}[/]")
                .DefaultValue(defaultValue)
                .ShowDefaultValue());
    }

    private static ScraperTemplateKind AskTemplateKind()
    {
        var choice = AnsiConsole.Prompt(
            new SelectionPrompt<string>()
                .Title("[cyan]Tipo de scaffold[/]")
                .AddChoices(
                    "Padrão (IScraper completo)",
                    "Template Madara (somente provider baseado em template)"));

        return choice.StartsWith("Template Madara", StringComparison.OrdinalIgnoreCase)
            ? ScraperTemplateKind.WordPressMadara
            : ScraperTemplateKind.Standard;
    }

    private static string GetTemplateKindLabel(ScraperTemplateKind templateKind)
    {
        return templateKind switch
        {
            ScraperTemplateKind.WordPressMadara => "Template Madara",
            _ => "Padrão"
        };
    }

    private static void WriteBanner()
    {
        AnsiConsole.Write(new FigletText("NekoSharp").Centered().Color(Color.Cyan1));
        AnsiConsole.WriteLine();
    }

    private static void WriteScraper(
        string filePath,
        string siteName,
        string className,
        string baseUrl,
        ScraperTemplateKind templateKind)
    {
        var source = templateKind switch
        {
            ScraperTemplateKind.WordPressMadara => BuildMadaraTemplateProvider(siteName, className, baseUrl),
            _ => BuildStandardProvider(siteName, className, baseUrl)
        };

        File.WriteAllText(filePath, source);
    }

    private static string BuildStandardProvider(string siteName, string className, string baseUrl)
    {
        var sb = new StringBuilder();
        sb.AppendLine("using NekoSharp.Core.Interfaces;");
        sb.AppendLine("using NekoSharp.Core.Models;");
        sb.AppendLine("using NekoSharp.Core.Services;");
        sb.AppendLine();
        sb.AppendLine($"namespace NekoSharp.Core.Providers.{siteName};");
        sb.AppendLine();
        sb.AppendLine($"public sealed class {className} : IScraper");
        sb.AppendLine("{");
        sb.AppendLine($"    public string Name => \"{siteName}\";");
        sb.AppendLine($"    public string BaseUrl => \"{baseUrl}\";");
        sb.AppendLine();
        sb.AppendLine("    private readonly HttpClient _http;");
        sb.AppendLine();
        sb.AppendLine($"    public {className}() : this(null) {{ }}");
        sb.AppendLine();
        sb.AppendLine($"    public {className}(LogService? logService)");
        sb.AppendLine("    {");
        sb.AppendLine("        HttpMessageHandler handler = logService != null");
        sb.AppendLine("            ? new LoggingHttpHandler(logService, new HttpClientHandler())");
        sb.AppendLine("            : new HttpClientHandler();");
        sb.AppendLine("        _http = new HttpClient(handler);");
        sb.AppendLine($"        _http.BaseAddress = new Uri(\"{baseUrl}\");");
        sb.AppendLine("    }");
        sb.AppendLine();
        sb.AppendLine("    public bool CanHandle(string url)");
        sb.AppendLine("    {");
        sb.AppendLine("        return url.StartsWith(BaseUrl, StringComparison.OrdinalIgnoreCase);");
        sb.AppendLine("    }");
        sb.AppendLine();
        sb.AppendLine("    public Task<Manga> GetMangaInfoAsync(string url, CancellationToken ct = default)");
        sb.AppendLine("    {");
        sb.AppendLine("        // TODO: Implement manga metadata scraping");
        sb.AppendLine($"        return Task.FromResult(new Manga {{ Name = \"{siteName}\" }});");
        sb.AppendLine("    }");
        sb.AppendLine();
        sb.AppendLine("    public Task<List<Chapter>> GetChaptersAsync(string url, CancellationToken ct = default)");
        sb.AppendLine("    {");
        sb.AppendLine("        // TODO: Implement chapter list scraping");
        sb.AppendLine("        return Task.FromResult(new List<Chapter>());");
        sb.AppendLine("    }");
        sb.AppendLine();
        sb.AppendLine("    public Task<List<Page>> GetPagesAsync(Chapter chapter, CancellationToken ct = default)");
        sb.AppendLine("    {");
        sb.AppendLine("        // TODO: Implement page image scraping");
        sb.AppendLine("        return Task.FromResult(new List<Page>());");
        sb.AppendLine("    }");
        sb.AppendLine("}");

        return sb.ToString();
    }

    private static string BuildMadaraTemplateProvider(string siteName, string className, string baseUrl)
    {
        var sb = new StringBuilder();
        sb.AppendLine("using NekoSharp.Core.Providers.Templates;");
        sb.AppendLine("using NekoSharp.Core.Services;");
        sb.AppendLine();
        sb.AppendLine($"namespace NekoSharp.Core.Providers.{siteName};");
        sb.AppendLine();
        sb.AppendLine($"public sealed class {className} : WordPressMadaraScraper");
        sb.AppendLine("{");
        sb.AppendLine($"    public override string Name => \"{siteName}\";");
        sb.AppendLine();
        sb.AppendLine($"    public {className}() : this(null, null) {{ }}");
        sb.AppendLine();
        sb.AppendLine($"    public {className}(LogService? logService) : this(logService, null) {{ }}");
        sb.AppendLine();
        sb.AppendLine($"    public {className}(LogService? logService, CloudflareCredentialStore? cfStore)");
        sb.AppendLine($"        : base(\"{baseUrl}\", logService, cfStore)");
        sb.AppendLine("    { }");
        sb.AppendLine("}");

        return sb.ToString();
    }
}
