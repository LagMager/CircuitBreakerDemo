using System.Diagnostics;
using System.Net;
using Polly;
using Polly.CircuitBreaker;

var builder = WebApplication.CreateBuilder(args);

// Set USE_CIRCUIT_BREAKER=false before starting the app to compare both modes.
var useCircuitBreaker = builder.Configuration.GetValue("USE_CIRCUIT_BREAKER", true);

builder.Services.AddHttpClient("dependency");
builder.Services.AddSingleton<DemoCounters>();
builder.Services.AddSingleton<CircuitBreakerStateProvider>();
builder.Services.AddSingleton(serviceProvider =>
    CreateBreaker(serviceProvider.GetRequiredService<CircuitBreakerStateProvider>()));

var app = builder.Build();

app.MapGet("/", () => Results.Ok(new
{
    message = "Open /compare for the live table, /demo/run for JSON, or /demo?fail=true for one request.",
    useCircuitBreaker
}));

// This endpoint stands in for an unreliable external service.
app.MapGet("/dependency", async (bool? fail, DemoCounters counters, HttpContext context) =>
{
    counters.Increment();
    if (fail == true)
    {
        // A slow failure makes the cost of repeated calls visible in the demo.
        await Task.Delay(300, context.RequestAborted);
    }

    return fail == true
        ? Results.StatusCode(StatusCodes.Status503ServiceUnavailable)
        : Results.Ok(new { message = "Dependency is healthy" });
});

// One request runs the same ten failing calls in each mode and reports the numbers.
app.MapGet("/demo/run", async (
    HttpContext context,
    IHttpClientFactory clientFactory,
    ResiliencePipeline<HttpResponseMessage> breaker,
    CircuitBreakerStateProvider state) => Results.Ok(
        await RunScenario(context, clientFactory, useCircuitBreaker, breaker, state)));

// A fresh breaker makes this comparison repeatable without restarting the app.
app.MapGet("/compare", async (HttpContext context, IHttpClientFactory clientFactory) =>
{
    var before = await RunScenario(context, clientFactory, false, null, null);
    var comparisonState = new CircuitBreakerStateProvider();
    var after = await RunScenario(context, clientFactory, true, CreateBreaker(comparisonState), comparisonState);
    var savedPercent = Math.Round(100.0 * (before.ElapsedMs - after.ElapsedMs) / before.ElapsedMs);

    var html = $$"""
        <!doctype html>
        <html lang="es">
        <head>
          <meta charset="utf-8">
          <meta name="viewport" content="width=device-width, initial-scale=1">
          <title>Circuit breaker: antes y después</title>
          <style>
            body { font-family: system-ui, sans-serif; max-width: 760px; margin: 3rem auto; padding: 0 1rem; color: #17212b; }
            h1 { line-height: 1.2; }
            table { border-collapse: collapse; width: 100%; margin: 2rem 0; }
            th, td { padding: .8rem; border-bottom: 1px solid #ccd3da; text-align: left; }
            th:not(:first-child), td:not(:first-child) { text-align: right; }
            thead { background: #edf2f7; }
            .highlight { font-size: 1.25rem; font-weight: 700; color: #126a43; }
            .note { color: #52606d; }
          </style>
        </head>
        <body>
          <h1>Sin breaker → con breaker</h1>
          <p>Diez solicitudes consecutivas a una dependencia que tarda 300 ms y responde HTTP 503.</p>
          <table>
            <thead><tr><th>Métrica</th><th>Sin breaker</th><th>Con breaker</th></tr></thead>
            <tbody>
              <tr><td>Solicitudes intentadas</td><td>{{before.TotalRequests}}</td><td>{{after.TotalRequests}}</td></tr>
              <tr><td>Llamadas a la dependencia</td><td>{{before.DependencyCalls}}</td><td>{{after.DependencyCalls}}</td></tr>
              <tr><td>Respuestas HTTP 503</td><td>{{before.FailedResponses}}</td><td>{{after.FailedResponses}}</td></tr>
              <tr><td>Llamadas evitadas</td><td>{{before.SkippedCalls}}</td><td>{{after.SkippedCalls}}</td></tr>
              <tr><td>Tiempo total</td><td>{{before.ElapsedMs}} ms</td><td>{{after.ElapsedMs}} ms</td></tr>
            </tbody>
          </table>
          <p class="highlight">{{savedPercent}} % menos tiempo en esta ejecución</p>
          <p class="note">Las llamadas a la dependencia fallan, pero esta página muestra el resumen de ambos escenarios. Los tiempos pueden variar.</p>
          <p><a href="/compare">Repetir comparación</a></p>
        </body>
        </html>
        """;

    return Results.Content(html, "text/html; charset=utf-8");
});

app.MapGet("/demo", async (
    bool? fail,
    HttpContext context,
    IHttpClientFactory clientFactory,
    ResiliencePipeline<HttpResponseMessage> breaker,
    CircuitBreakerStateProvider state,
    DemoCounters counters) =>
{
    var client = clientFactory.CreateClient("dependency");
    var url = $"{context.Request.Scheme}://{context.Request.Host}/dependency?fail={fail ?? true}";
    var dependencyWasCalled = false;

    try
    {
        async ValueTask<HttpResponseMessage> CallDependency(CancellationToken cancellationToken)
        {
            dependencyWasCalled = true;
            return await client.GetAsync(url, cancellationToken);
        }

        using var response = useCircuitBreaker
            ? await breaker.ExecuteAsync(CallDependency, context.RequestAborted)
            : await CallDependency(context.RequestAborted);

        var status = (int)response.StatusCode;
        return Results.Json(new
        {
            message = response.IsSuccessStatusCode ? "Dependency succeeded" : "Dependency returned an error",
            useCircuitBreaker,
            circuitState = useCircuitBreaker ? state.CircuitState.ToString() : "Disabled",
            dependencyWasCalled,
            totalDependencyCalls = counters.Value
        }, statusCode: status);
    }
    catch (BrokenCircuitException)
    {
        return Results.Json(new
        {
            message = "Circuit is open; request rejected without calling the dependency",
            useCircuitBreaker,
            circuitState = state.CircuitState.ToString(),
            dependencyWasCalled,
            totalDependencyCalls = counters.Value
        }, statusCode: StatusCodes.Status503ServiceUnavailable);
    }
});

app.Run();

static ResiliencePipeline<HttpResponseMessage> CreateBreaker(CircuitBreakerStateProvider state) =>
    new ResiliencePipelineBuilder<HttpResponseMessage>()
        .AddCircuitBreaker(new CircuitBreakerStrategyOptions<HttpResponseMessage>
        {
            ShouldHandle = args => ValueTask.FromResult(
                args.Outcome.Result?.StatusCode == HttpStatusCode.ServiceUnavailable),
            FailureRatio = 1.0,
            MinimumThroughput = 3,
            SamplingDuration = TimeSpan.FromSeconds(30),
            BreakDuration = TimeSpan.FromSeconds(10),
            StateProvider = state
        })
        .Build();

static async Task<DemoSummary> RunScenario(
    HttpContext context,
    IHttpClientFactory clientFactory,
    bool useBreaker,
    ResiliencePipeline<HttpResponseMessage>? breaker,
    CircuitBreakerStateProvider? state)
{
    const int totalRequests = 10;
    var dependencyCalls = 0;
    var failedResponses = 0;
    var skippedCalls = 0;
    var client = clientFactory.CreateClient("dependency");
    var url = $"{context.Request.Scheme}://{context.Request.Host}/dependency?fail=true";
    var stopwatch = Stopwatch.StartNew();

    for (var i = 0; i < totalRequests; i++)
    {
        async ValueTask<HttpResponseMessage> CallDependency(CancellationToken cancellationToken)
        {
            dependencyCalls++;
            return await client.GetAsync(url, cancellationToken);
        }

        try
        {
            using var response = useBreaker
                ? await breaker!.ExecuteAsync(CallDependency, context.RequestAborted)
                : await CallDependency(context.RequestAborted);
            if (response.StatusCode == HttpStatusCode.ServiceUnavailable)
            {
                failedResponses++;
            }
        }
        catch (BrokenCircuitException)
        {
            skippedCalls++;
        }
    }

    stopwatch.Stop();
    return new DemoSummary(
        useBreaker ? "with breaker" : "without breaker",
        totalRequests,
        dependencyCalls,
        failedResponses,
        skippedCalls,
        stopwatch.ElapsedMilliseconds,
        useBreaker ? state!.CircuitState.ToString() : "Disabled");
}

sealed record DemoSummary(
    string Mode,
    int TotalRequests,
    int DependencyCalls,
    int FailedResponses,
    int SkippedCalls,
    long ElapsedMs,
    string CircuitState);

sealed class DemoCounters
{
    private int _calls;

    public int Value => Volatile.Read(ref _calls);
    public void Increment() => Interlocked.Increment(ref _calls);
}
