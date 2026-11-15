using System.Reflection;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Components;

namespace StudyLife.App.Parity.Tests;

/// <summary>
/// The shared client's components get their services by @inject (compiled to [Inject]
/// properties). Blazor resolves them when the component is created, so a service the native app
/// never registered throws before any of the component's own code - the Setup page did not open
/// in TestFlight 1.12.5 because of IServerLinks (earlier: LocalDateNames, MarketplaceClient).
/// MauiProgram.cs mirrors StudyLife.Client/Program.cs by hand; these tests are the enforcement.
/// MauiProgram cannot be built here (MAUI workload), so its registrations are read from the
/// source text and matched against the real injected types found by reflection.
/// </summary>
public class DiParityTests
{
    // Service types the BlazorWebView host or a library registration provides without an explicit
    // line in MauiProgram.cs. Every entry says who registers it; this is not a place to silence a gap.
    private static readonly HashSet<string> ProvidedByHost = new()
    {
        "NavigationManager",    // AddMauiBlazorWebView (BlazorWebView's own navigation manager)
        "IJSRuntime",           // AddMauiBlazorWebView
        "IServiceProvider",     // always available from the container
        "I18nText",             // AddI18nText (Toolbelt.Blazor.I18nText)
    };

    // Registered in Program.cs but deliberately absent from MauiProgram.cs. Empty today.
    private static readonly HashSet<string> ClientOnly = new();

    private static readonly Regex Registration = new(
        @"Services\s*\.\s*Add(?:Scoped|Singleton|Transient)\s*<\s*(?<svc>[\w.]+)\s*(?:,\s*[\w.]+\s*)?>",
        RegexOptions.Compiled);
    private static readonly Regex HttpClientRegistration = new(
        @"Services\s*\.\s*AddHttpClient\s*<\s*(?<svc>[\w.]+)", RegexOptions.Compiled);

    [Fact]
    public void Every_service_injected_by_a_client_component_is_registered_in_the_app()
    {
        var registered = Registered(Read("src/StudyLife.App/MauiProgram.cs"));
        var injected = InjectedServiceTypes();
        Assert.NotEmpty(injected); // reflection found nothing = the test itself rotted

        var missing = injected
            .Where(t => !registered.Contains(t) && !ProvidedByHost.Contains(t))
            .OrderBy(t => t, StringComparer.Ordinal)
            .ToList();

        Assert.True(missing.Count == 0,
            "Injected by StudyLife.Client components but not registered in MauiProgram.cs "
            + "(a page using one throws on open): " + string.Join(", ", missing));
    }

    [Fact]
    public void Every_registration_of_the_client_program_exists_in_the_app()
    {
        var client = Registered(Read("studylife/src/StudyLife.Client/Program.cs"));
        var app = Registered(Read("src/StudyLife.App/MauiProgram.cs"));
        Assert.NotEmpty(client);
        Assert.NotEmpty(app);

        var missing = client.Except(app).Except(ClientOnly).OrderBy(t => t, StringComparer.Ordinal).ToList();

        Assert.True(missing.Count == 0,
            "Registered in StudyLife.Client/Program.cs but not in MauiProgram.cs: " + string.Join(", ", missing));
    }

    private static HashSet<string> Registered(string source)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match m in Registration.Matches(source)) set.Add(Simple(m.Groups["svc"].Value));
        foreach (Match m in HttpClientRegistration.Matches(source)) set.Add(Simple(m.Groups["svc"].Value));
        // The scoped HttpClient is registered with a factory lambda, no generic argument.
        if (Regex.IsMatch(source, @"Services\s*\.\s*AddScoped\s*\(\s*sp\s*=>") && source.Contains("new HttpClient("))
            set.Add("HttpClient");
        return set;
    }

    private static string Simple(string name) => name[(name.LastIndexOf('.') + 1)..];

    private static HashSet<string> InjectedServiceTypes()
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        const BindingFlags All = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
        foreach (var type in typeof(StudyLife.Client.Services.IServerLinks).Assembly.GetTypes())
        {
            if (!typeof(IComponent).IsAssignableFrom(type)) continue;
            foreach (var prop in type.GetProperties(All))
            {
                if (prop.GetCustomAttribute<InjectAttribute>() is null) continue;
                var name = prop.PropertyType.Name;
                var tick = name.IndexOf('`');
                set.Add(tick < 0 ? name : name[..tick]);
            }
        }
        return set;
    }

    private static string Read(string repoRelative)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "StudyLife.App.slnx")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return File.ReadAllText(Path.Combine(dir!.FullName, repoRelative));
    }
}
