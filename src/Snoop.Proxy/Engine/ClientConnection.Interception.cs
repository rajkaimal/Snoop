using System.Globalization;
using Snoop.Core.Bodies;
using Snoop.Core.Diagnostics;
using Snoop.Core.Http;
using Snoop.Core.Sessions;
using Snoop.Proxy.Http;
using Snoop.Proxy.Interception;
using Snoop.Proxy.Rules;
using Snoop.Proxy.Upstream;

namespace Snoop.Proxy.Engine;

/// <summary>
/// The path for requests that rules or breakpoints apply to. Unlike the normal path, the request
/// body is read completely before anything is sent, so it can be changed; responses are held in
/// full only when a response breakpoint applies.
/// </summary>
internal sealed partial class ClientConnection
{
    private const string BreakpointEdit = "edited at a breakpoint";

    /// <summary>The interception plan for a request, or null to proxy it untouched.</summary>
    private InterceptionPlan? PlanInterception(Session session)
    {
        if (_context.Interceptor is not { } interceptor)
        {
            return null;
        }

        try
        {
            var plan = interceptor.Plan(session.Request);
            return plan.IsEmpty ? null : plan;
        }
#pragma warning disable CA1031 // A faulty rule must never break traffic.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            Log.Error($"Could not evaluate rules for session #{session.Id}.", ex);
            return null;
        }
    }

    private async Task<bool> ForwardInterceptedAsync(
        BufferedConnectionStream client,
        RequestHead head,
        Uri url,
        Session session,
        BodyLength requestFraming,
        CapturedBody? requestBody,
        InterceptionPlan plan,
        CancellationToken cancellationToken)
    {
        // 1. The whole request, so rules and breakpoint edits can change any part of it.
        byte[]? body = null;
        if (requestBody is not null)
        {
            var reader = new CapturingReadStream(requestFraming.CreateReader(client), requestBody);
            body = await ReadAllAsync(reader, Options.BodyCapture.MaxCapturedBytes, cancellationToken).ConfigureAwait(false);
            requestBody.Complete();
            if (body is null)
            {
                const string tooLarge = "The request body is too large to hold for a rule or breakpoint.";
                FinishWithError(session, tooLarge, SessionState.Failed);
                await ClientResponses.WriteErrorAsync(client, 413, tooLarge, cancellationToken).ConfigureAwait(false);
                return false;
            }
        }

        var original = new EditableRequest(head.Method, url, HopByHopHeaders.Strip(head.Headers), body);
        var request = original.Clone();

        // 2. Rules.
        var outcome = await RuleEngine.ApplyToRequestAsync(request, plan.Rules, cancellationToken).ConfigureAwait(false);
        foreach (var rule in outcome.AppliedRules)
        {
            session.AddModifiedBy($"rule '{rule}'");
        }

        if (outcome.Result == RuleResult.Block)
        {
            RecordRequest(session, original, request);
            FinishWithError(session, $"Blocked by rule '{outcome.DecidedBy}'.", SessionState.Aborted);
            return false;
        }

        if (outcome.Result == RuleResult.Respond)
        {
            RecordRequest(session, original, request);
            var answer = outcome.Response!;
            ApplyResponseHeaderRules(session, answer.Headers, plan);
            session.Flags |= SessionFlags.AutoResponded;
            session.Timings.MarkRequestSent();
            return await WriteHeldResponseAsync(client, head, session, answer, "HTTP/1.1", cancellationToken).ConfigureAwait(false);
        }

        // 3. Request breakpoint.
        if (plan.BreakOnRequest)
        {
            var beforeBreak = request.Clone();
            var decision = await PauseAsync(session, new PausedExchange(session, request), cancellationToken).ConfigureAwait(false);
            if (decision == BreakpointDecision.Abort)
            {
                RecordRequest(session, original, request);
                FinishWithError(session, "Aborted at a breakpoint.", SessionState.Aborted);
                await ClientResponses.WriteErrorAsync(client, 502, "The request was stopped at a breakpoint in Snoop.", cancellationToken).ConfigureAwait(false);
                return false;
            }

            if (request.DiffersFrom(beforeBreak))
            {
                session.AddModifiedBy(BreakpointEdit);
            }
        }

        if (!IsHttpUrl(request.Url))
        {
            RecordRequest(session, original, request);
            const string invalid = "The edited URL must be an absolute http:// or https:// URL.";
            FinishWithError(session, invalid, SessionState.Failed);
            await ClientResponses.WriteErrorAsync(client, 400, invalid, cancellationToken).ConfigureAwait(false);
            return false;
        }

        RecordRequest(session, original, request);

        // 4. Send it.
        HttpResponseMessage response;
        try
        {
            var content = request.Body is { Length: > 0 } bytes ? new MemoryStream(bytes, writable: false) : null;
            response = await _context.Upstream.SendAsync(
                request.Method,
                request.Url,
                HopByHopHeaders.Strip(request.Headers),
                content,
                content?.Length,
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (IsUpstreamError(ex, cancellationToken) || ex is FormatException or ArgumentException)
        {
            var message = Describe(ex);
            FinishWithError(session, message, SessionState.Failed);
            await ClientResponses.WriteErrorAsync(client, 502, $"Could not get a response from {request.Url.Authority}: {message}", cancellationToken).ConfigureAwait(false);
            return false;
        }

        // 5. The response: streamed (header rules only) or held (response breakpoint).
        try
        {
            session.Timings.MarkRequestSent();
            return plan.BreakOnResponse
                ? await HoldResponseAsync(client, head, session, request, response, plan, cancellationToken).ConfigureAwait(false)
                : await RelayResponseAsync(client, head, session, response, null, plan.HasResponseHeaderActions ? plan : null, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            UpstreamHttpClient.DisposeExchange(response);
        }
    }

    /// <summary>Reads the whole response, lets rules and the user change it, then sends it to the app.</summary>
    private async Task<bool> HoldResponseAsync(
        BufferedConnectionStream client,
        RequestHead head,
        Session session,
        EditableRequest request,
        HttpResponseMessage response,
        InterceptionPlan plan,
        CancellationToken cancellationToken)
    {
        var status = (int)response.StatusCode;
        var reason = string.IsNullOrEmpty(response.ReasonPhrase) ? ReasonPhrases.Get(status) : response.ReasonPhrase;
        var version = ClientResponses.FormatVersion(response.Version);
        var limit = Options.BodyCapture.MaxCapturedBytes;
        if (response.Content.Headers.ContentLength > limit)
        {
            session.Note = "The response was too large to hold at the breakpoint, so it was passed on unchanged.";
            return await RelayResponseAsync(client, head, session, response, null, plan.HasResponseHeaderActions ? plan : null, cancellationToken).ConfigureAwait(false);
        }

        var headers = UpstreamHttpClient.CollectResponseHeaders(response);
        byte[] body = [];
        if (MessageFraming.ResponseMayHaveBody(head.Method, status))
        {
            var upstreamBody = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            await using (upstreamBody.ConfigureAwait(false))
            {
                var read = await ReadAllAsync(upstreamBody, limit, cancellationToken).ConfigureAwait(false);
                if (read is null)
                {
                    const string tooLarge = "The response was too large to hold at the breakpoint.";
                    FinishWithError(session, tooLarge, SessionState.Failed);
                    await ClientResponses.WriteErrorAsync(client, 502, tooLarge, cancellationToken).ConfigureAwait(false);
                    return false;
                }

                body = read;
            }
        }

        // Show the server's response while paused.
        session.Response = new ResponseInfo(status, reason, version, headers.Clone()) { Body = CapturedBody.FromBytes(body, options: Options.BodyCapture) };
        session.State = SessionState.ReceivingResponse;
        session.Timings.MarkResponseHeaders();
        _context.Updated(session);

        var editable = new EditableResponse(status, reason, headers, body);
        ApplyResponseHeaderRules(session, editable.Headers, plan);
        var beforeBreak = editable.Clone();
        var decision = await PauseAsync(session, new PausedExchange(session, request, editable), cancellationToken).ConfigureAwait(false);
        if (decision == BreakpointDecision.Abort)
        {
            FinishWithError(session, "Aborted at a breakpoint; the connection to the app was closed.", SessionState.Aborted);
            return false;
        }

        if (editable.DiffersFrom(beforeBreak))
        {
            session.AddModifiedBy(BreakpointEdit);
        }

        if (editable.StatusCode is < 100 or > 999)
        {
            editable.StatusCode = status;
        }

        return await WriteHeldResponseAsync(client, head, session, editable, version, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Sends a complete response held in memory (made by a rule, or edited at a breakpoint) and records it.</summary>
    private async Task<bool> WriteHeldResponseAsync(
        BufferedConnectionStream client,
        RequestHead head,
        Session session,
        EditableResponse response,
        string version,
        CancellationToken cancellationToken)
    {
        var mayHaveBody = MessageFraming.ResponseMayHaveBody(head.Method, response.StatusCode);
        var headers = HopByHopHeaders.Strip(response.Headers);
        var declaredLength = headers.Get("Content-Length");
        headers.Remove("Content-Length");
        if (mayHaveBody)
        {
            headers.Add("Content-Length", response.Body.Length.ToString(CultureInfo.InvariantCulture));
        }
        else if (declaredLength is not null && (response.StatusCode == 304 || string.Equals(head.Method, "HEAD", StringComparison.OrdinalIgnoreCase)))
        {
            headers.Add("Content-Length", declaredLength);
        }

        var reason = string.IsNullOrWhiteSpace(response.ReasonPhrase) ? ReasonPhrases.Get(response.StatusCode) : response.ReasonPhrase.Trim();
        var recorded = new ResponseInfo(response.StatusCode, reason, version, headers.Clone())
        {
            Body = mayHaveBody ? CapturedBody.FromBytes(response.Body, options: Options.BodyCapture) : null,
        };
        var previous = session.Response;
        session.Response = recorded;
        previous?.Body?.Dispose();
        session.State = SessionState.ReceivingResponse;
        session.Timings.MarkResponseHeaders();
        _context.Updated(session);

        var keepAlive = head.WantsKeepAlive;
        headers.Add("Connection", keepAlive ? "keep-alive" : "close");
        var responseHead = HttpHeadWriter.SerializeResponse(head.IsHttp10 ? "HTTP/1.0" : "HTTP/1.1", response.StatusCode, reason, headers);
        await client.WriteAsync(responseHead, cancellationToken).ConfigureAwait(false);
        if (mayHaveBody && response.Body.Length > 0)
        {
            await client.WriteAsync(response.Body, cancellationToken).ConfigureAwait(false);
        }

        await client.FlushAsync(cancellationToken).ConfigureAwait(false);
        session.Complete();
        _context.Finished(session);
        return keepAlive;
    }

    /// <summary>Holds the session at a breakpoint until the user decides.</summary>
    private async Task<BreakpointDecision> PauseAsync(Session session, PausedExchange exchange, CancellationToken cancellationToken)
    {
        var previous = session.State;
        session.State = SessionState.Paused;
        session.Flags |= SessionFlags.Breakpoint;
        _context.Updated(session);
        BreakpointDecision decision;
        try
        {
            decision = await _context.Interceptor!.PauseAsync(exchange, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (session.State == SessionState.Paused)
            {
                session.State = previous;
            }

            _context.Updated(session);
        }

        cancellationToken.ThrowIfCancellationRequested();
        return decision;
    }

    private void ApplyResponseHeaderRules(Session session, HttpHeaderList headers, InterceptionPlan plan)
    {
        foreach (var rule in RuleEngine.ApplyToResponseHeaders(headers, plan.Rules))
        {
            session.AddModifiedBy($"rule '{rule}'");
        }
    }

    /// <summary>When the request changed, records the version actually sent and keeps the original.</summary>
    private void RecordRequest(Session session, EditableRequest original, EditableRequest request)
    {
        if (!request.DiffersFrom(original) || session.OriginalRequest is not null)
        {
            return;
        }

        session.OriginalRequest = session.Request;
        session.Request = new RequestInfo(request.Method, request.Url, session.Request.HttpVersion, request.Headers.Clone())
        {
            Body = request.Body is null ? null : CapturedBody.FromBytes(request.Body, options: Options.BodyCapture),
        };
        _context.Updated(session);
    }

    /// <summary>Reads a stream to the end. Null when it holds more than <paramref name="maxBytes"/>.</summary>
    private static async Task<byte[]?> ReadAllAsync(Stream source, long maxBytes, CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        var block = new byte[CopyBufferSize];
        while (true)
        {
            var read = await source.ReadAsync(block, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                return buffer.ToArray();
            }

            if (buffer.Length + read > maxBytes)
            {
                return null;
            }

            buffer.Write(block, 0, read);
        }
    }

    private static bool IsHttpUrl(Uri url) =>
        url.IsAbsoluteUri && (url.Scheme == Uri.UriSchemeHttp || url.Scheme == Uri.UriSchemeHttps) && !string.IsNullOrEmpty(url.Host);
}
