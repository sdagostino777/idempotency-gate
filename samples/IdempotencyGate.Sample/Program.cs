using IdempotencyGate;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddIdempotencyGate(options =>
{
    options.Ttl = TimeSpan.FromHours(24);
    options.RequireKey = true;
});

var app = builder.Build();

app.UseIdempotencyGate();

var charges = new Dictionary<string, string>(StringComparer.Ordinal);

// A payment that takes a while, exactly like a real one: this is the window in which
// a client retry used to charge the customer twice.
app.MapPost("/payments", async (PaymentRequest payment, HttpContext context) =>
{
    await Task.Delay(1500, context.RequestAborted);

    var id = Guid.NewGuid().ToString("N");
    charges[id] = $"{payment.Amount} {payment.Currency}";

    return Results.Ok(new { chargeId = id, amount = payment.Amount, currency = payment.Currency });
});

app.MapGet("/charges", () => charges);

app.Run();

internal sealed record PaymentRequest(decimal Amount, string Currency);
