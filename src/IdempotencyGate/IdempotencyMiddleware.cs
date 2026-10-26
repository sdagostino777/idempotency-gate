using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace IdempotencyGate;

/// <summary>
/// Middleware that makes unsafe HTTP methods safe to retry.
/// <para>
/// The first request with a given <c>Idempotency-Key</c> executes and its response is stored.
/// Any later request with the same key and the same payload gets that stored response replayed
/// instead of running the handler again. A request that arrives while the first one is still
/// running is rejected with 409 rather than being allowed to execute in parallel.
/// </para>
/// </summary>
public sealed class IdempotencyMiddleware
{
    /// <summary>Header added to replayed responses so clients can tell them apart.</summary>
    public const string ReplayedHeader = "Idempotency-Replayed";

    private readonly RequestDelegate _next;
    private readonly IIdempotencyStore _store;
    private readonly IdempotencyOptions _options;
    private readonly ILogger<IdempotencyMiddleware> _logger;

    /// <summary>Creates the middleware. Registered by <see cref="IdempotencyGateExtensions.UseIdempotencyGate"/>.</summary>
    public IdempotencyMiddleware(
        RequestDelegate next,
        IIdempotencyStore store,
        IOptions<IdempotencyOptions> options,
        ILogger<IdempotencyMiddleware> logger)
    {
        _next = next;
        _store = store;
        _options = options.Value;
        _logger = logger;
    }

    /// <summary>Handles one request.</summary>
    public async Task InvokeAsync(HttpContext context)
    {
        if (!_options.Methods.Contains(context.Request.Method))
        {
            await _next(context);
            return;
        }

        var key = context.Request.Headers[_options.HeaderName].FirstOrDefault();

        if (string.IsNullOrWhiteSpace(key))
        {
            if (!_options.RequireKey)
            {
                await _next(context);
                return;
            }

            await WriteProblemAsync(
                context, StatusCodes.Status400BadRequest, "idempotency_key_required",
                "Idempotency-Key header missing",
                $"A {context.Request.Method} request must carry the {_options.HeaderName} header.");
            return;
        }

        if (key.Length > _options.MaxKeyLength)
        {
            await WriteProblemAsync(
                context, StatusCodes.Status400BadRequest, "idempotency_key_too_long",
                "Idempotency-Key too long",
                $"The key may not exceed {_options.MaxKeyLength} characters.");
            return;
        }

        var fingerprint = await FingerprintAsync(context.Request, context.RequestAborted);

        IdempotencyBeginResult begin;
        try
        {
            begin = await _store.BeginAsync(
                key, fingerprint, _options.Ttl, _options.InProgressLease, context.RequestAborted);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Fail open: a broken store must not take the endpoint down with it. The request runs
            // unprotected and the failure is loud in the logs instead.
            _logger.LogError(ex, "Idempotency store unavailable; executing request without protection");
            await _next(context);
            return;
        }

        switch (begin.Status)
        {
            case IdempotencyBeginStatus.Replay:
                await ReplayAsync(context, begin.Record!);
                return;

            case IdempotencyBeginStatus.InProgress:
                context.Response.Headers.RetryAfter = _options.RetryAfterSeconds.ToString();
                await WriteProblemAsync(
                    context, StatusCodes.Status409Conflict, "idempotency_request_in_progress",
                    "A request with this key is still running",
                    "Retry once the original request has completed. The response will then be replayed.");
                return;

            case IdempotencyBeginStatus.KeyReuse:
                _logger.LogWarning(
                    "Idempotency key {Key} reused with a different payload", key);
                await WriteProblemAsync(
                    context, StatusCodes.Status422UnprocessableEntity, "idempotency_key_reused",
                    "Idempotency-Key reused with a different request",
                    "A key belongs to exactly one request payload. Use a new key for a new request.");
                return;
        }

        await ExecuteAndStoreAsync(context, key, begin);
    }

    private async Task ExecuteAndStoreAsync(HttpContext context, string key, IdempotencyBeginResult begin)
    {
        var originalBody = context.Response.Body;
        using var buffer = new MemoryStream();
        context.Response.Body = buffer;

        try
        {
            await _next(context);

            var status = context.Response.StatusCode;
            var bytes = buffer.ToArray();

            // CancellationToken.None on purpose: if the client disconnects mid-flight, the key must
            // still be released or completed, otherwise it stays claimed until the lease expires.
            try
            {
                if (status >= StatusCodes.Status500InternalServerError)
                {
                    await _store.ReleaseAsync(key, CancellationToken.None);
                }
                else if (bytes.Length <= _options.MaxCachedResponseBytes)
                {
                    await _store.CompleteAsync(
                        key, status, context.Response.ContentType, bytes, _options.Ttl, CancellationToken.None);
                }
                else
                {
                    _logger.LogWarning(
                        "Response of {Bytes} bytes for key {Key} exceeds MaxCachedResponseBytes; key released, response not replayable",
                        bytes.Length, key);
                    await _store.ReleaseAsync(key, CancellationToken.None);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Could not persist idempotency state for key {Key}", key);
            }

            buffer.Position = 0;
            await buffer.CopyToAsync(originalBody, context.RequestAborted);
        }
        catch
        {
            try
            {
                await _store.ReleaseAsync(key, CancellationToken.None);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Could not release idempotency key {Key}", key);
            }

            throw;
        }
        finally
        {
            context.Response.Body = originalBody;
        }
    }

    private static async Task ReplayAsync(HttpContext context, IdempotencyRecord record)
    {
        context.Response.StatusCode = record.StatusCode;
        context.Response.Headers[ReplayedHeader] = "true";

        if (!string.IsNullOrEmpty(record.ContentType))
        {
            context.Response.ContentType = record.ContentType;
        }

        if (record.Body is { Length: > 0 })
        {
            await context.Response.Body.WriteAsync(record.Body, context.RequestAborted);
        }
    }

    private static async Task<string> FingerprintAsync(HttpRequest request, CancellationToken cancellationToken)
    {
        request.EnableBuffering();

        byte[] bodyHash;
        if (request.ContentLength is > 0 or null)
        {
            using var copy = new MemoryStream();
            await request.Body.CopyToAsync(copy, cancellationToken);
            request.Body.Position = 0;
            bodyHash = SHA256.HashData(copy.GetBuffer().AsSpan(0, (int)copy.Length));
        }
        else
        {
            bodyHash = SHA256.HashData(ReadOnlySpan<byte>.Empty);
        }

        var canonical = string.Join(
            '\n',
            request.Method,
            request.Path.Value ?? string.Empty,
            request.QueryString.Value ?? string.Empty,
            Convert.ToHexString(bodyHash));

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    private static async Task WriteProblemAsync(
        HttpContext context, int status, string code, string title, string detail)
    {
        context.Response.StatusCode = status;
        context.Response.ContentType = "application/problem+json";

        await context.Response.WriteAsJsonAsync(
            new
            {
                type = "https://www.rfc-editor.org/rfc/rfc9110",
                title,
                status,
                code,
                detail,
            },
            cancellationToken: context.RequestAborted);
    }
}
