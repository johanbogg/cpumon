using System;
using System.Threading.Tasks;

public sealed class WebStartupOptions
{
    public int     Port           { get; init; } = 47202;
    public bool    UseTls         { get; init; } = true;
    public bool    BehindProxy    { get; init; } = false;
    public string? CertPath       { get; init; }
    public string? CertPassword   { get; init; }
    public string? OperatorPath   { get; init; }   // overrides AppPaths.DataFile("operator.json"); tests use this
    public string? BindAddress    { get; init; }   // default "0.0.0.0"; tests use "127.0.0.1"
}

// Composes every web-side module onto an existing engine+platform pair and surfaces the
// bootstrap URL through the supplied platform service when no operator account exists yet.
// One instance per server lifetime; Dispose tears down host + per-process state stores in
// the right order.
public sealed class WebStartup : IDisposable
{
    public WebHost              Host       { get; }
    public OperatorStore        Operators  { get; }
    public SessionStore         Sessions   { get; }
    public BootstrapTokenIssuer Bootstrap  { get; }
    public RateLimiter          RateLimit  { get; }
    public SnapshotCache        Snapshots  { get; }

    WebStartup(WebHost host, OperatorStore operators, SessionStore sessions,
               BootstrapTokenIssuer bootstrap, RateLimiter rateLimit, SnapshotCache snapshots)
    {
        Host       = host;
        Operators  = operators;
        Sessions   = sessions;
        Bootstrap  = bootstrap;
        RateLimit  = rateLimit;
        Snapshots  = snapshots;
    }

    public static async Task<WebStartup> StartAsync(ServerEngine engine,
                                                    ServerDashboardController controller,
                                                    IServerPlatformServices platform,
                                                    WebStartupOptions opts)
    {
        var operators = new OperatorStore(opts.OperatorPath ?? AppPaths.DataFile("operator.json"));
        var sessions  = new SessionStore();
        var bootstrap = new BootstrapTokenIssuer();
        var rateLimit = new RateLimiter();
        var snapshots = new SnapshotCache(engine);

        var host = new WebHost();
        await host.StartAsync(new WebHostOptions
        {
            Port          = opts.Port,
            BindAddress   = opts.BindAddress ?? "0.0.0.0",
            UseTls        = opts.UseTls,
            CertPath      = opts.CertPath,
            CertPassword  = opts.CertPassword,
            BehindProxy   = opts.BehindProxy,
            ServerVersion = Proto.AppVersion,
            StartedAt     = DateTime.UtcNow,
            Log           = engine.Log,
            ConfigureRoutes = (app, ctx) =>
            {
                WebAuthApi.Map(app, operators, sessions, bootstrap, rateLimit, ctx);
                WebDashboardApi.Map(app, engine, controller, sessions, ctx);
                WebClientActionsApi.Map(app, engine, controller, sessions, ctx);
                WebSnapshotApi.Map(app, engine, snapshots, sessions, ctx);
                WebOfflineApi.Map(app, engine, sessions, ctx);
                WebApprovedApi.Map(app, engine, sessions, ctx);
                WebAlertsApi.Map(app, engine.Alerts, sessions, ctx);
                WebLogApi.Map(app, engine, sessions, ctx);
                WebSocketApi.Map(app, engine, controller, sessions, ctx);
            },
        }).ConfigureAwait(false);

        var scheme = opts.UseTls ? "https" : "http";
        engine.Log.Add($"Web UI listening on {scheme}://{opts.BindAddress ?? "0.0.0.0"}:{host.Port}", Th.Cyan);

        WebBootstrap.MaybeIssueAndShow(operators, bootstrap, platform,
            token => $"{scheme}://localhost:{host.Port}/setup?t={token}",
            engine.Log);

        return new WebStartup(host, operators, sessions, bootstrap, rateLimit, snapshots);
    }

    public void Dispose()
    {
        Snapshots.Dispose();
        Host.DisposeAsync().AsTask().ConfigureAwait(false).GetAwaiter().GetResult();
        Bootstrap.Dispose();
        Sessions.Dispose();
    }
}
