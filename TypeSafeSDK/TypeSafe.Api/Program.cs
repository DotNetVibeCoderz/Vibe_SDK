using TypeSafeSdk;
var builder = WebApplication.CreateBuilder(args);
builder.Services.AddSingleton(new TypeSafeClient(TypeSafeOptions.FromEnvironment()));
var app = builder.Build();
app.MapPost("/api/classify", async (TicketRequest request, TypeSafeClient client, CancellationToken ct) => await client.SystemOneAsync(new { document = request.Document }, Choice.Create("billing", "technical", "other"), ct));
app.MapGet("/health", () => Results.Ok(new { status = "ok", service = "TypeSafe API" }));
app.Run();
public sealed record TicketRequest(string Document);
