using System.Collections.Concurrent;
using System.Globalization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Unitree.Net.Core;
using Unitree.Net.Dds;
using Unitree.Net.Messages;
using Unitree.Net.Messages.Api;

namespace Unitree.Net.Control;

/// <summary>
/// Issues request/response calls against one of the robot's named services.
/// </summary>
/// <remarks>
/// Requests and responses travel on separate topics, so replies must be correlated by the request
/// identifier the caller generated. Any reply whose identifier is unknown — a late response to a call
/// that already timed out, or traffic from another controller — is discarded rather than surfaced.
/// </remarks>
public sealed class ServiceClient : IDisposable
{
    private readonly IDdsPublisher<ApiRequest> _requestPublisher;
    private readonly IDdsSubscriber<ApiResponse> _responseSubscriber;
    private readonly ConcurrentDictionary<long, TaskCompletionSource<ApiResponse>> _pending = new();
    private readonly CancellationTokenSource _pumpCancellation = new();
    private readonly Task _pumpTask;
    private readonly ILogger _logger;
    private readonly TimeSpan _defaultTimeout;
    private bool _disposed;

    /// <summary>
    /// Creates a client for <paramref name="serviceName"/>.
    /// </summary>
    /// <param name="participant">The DDS participant to create endpoints on.</param>
    /// <param name="serviceName">Service name, e.g. <c>sport</c>.</param>
    /// <param name="defaultTimeout">Default per-call timeout.</param>
    /// <param name="logger">Logger.</param>
    public ServiceClient(
        IDdsParticipant participant,
        string serviceName,
        TimeSpan defaultTimeout,
        ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(participant);
        ArgumentException.ThrowIfNullOrWhiteSpace(serviceName);

        ServiceName = serviceName;
        _defaultTimeout = defaultTimeout;
        _logger = logger ?? NullLogger.Instance;

        _requestPublisher = participant.CreatePublisher<ApiRequest>(Topics.RequestTopic(serviceName));
        _responseSubscriber = participant.CreateSubscriber<ApiResponse>(Topics.ResponseTopic(serviceName), 64);
        _pumpTask = Task.Run(() => PumpResponsesAsync(_pumpCancellation.Token), CancellationToken.None);
    }

    /// <summary>The service this client targets.</summary>
    public string ServiceName { get; }

    /// <summary>
    /// Invokes <paramref name="apiId"/> and waits for the response.
    /// </summary>
    /// <param name="apiId">The Unitree API identifier.</param>
    /// <param name="parameter">JSON parameter document, or <see langword="null"/>.</param>
    /// <param name="timeout">Overrides the default timeout.</param>
    /// <param name="cancellationToken">Cancels the wait.</param>
    /// <exception cref="TimeoutException">No response arrived in time.</exception>
    /// <exception cref="UnitreeServiceException">The robot reported a non-zero status.</exception>
    public async Task<ApiResponse> CallAsync(
        long apiId,
        string? parameter = null,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        ApiRequest request = ApiRequest.Create(apiId, parameter);
        long requestId = request.Header.Identity.Id;

        var completion = new TaskCompletionSource<ApiResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[requestId] = completion;

        try
        {
            _requestPublisher.Publish(request);

            ApiResponse response = await completion.Task
                .WaitAsync(timeout ?? _defaultTimeout, cancellationToken)
                .ConfigureAwait(false);

            response.EnsureSuccess();
            return response;
        }
        catch (TimeoutException)
        {
            throw new TimeoutException(
                $"Service '{ServiceName}' did not respond to API {apiId} within {(timeout ?? _defaultTimeout).TotalSeconds:0.#} s. " +
                "Confirm the service is running on the robot and that the DDS link is healthy.");
        }
        finally
        {
            _pending.TryRemove(requestId, out _);
        }
    }

    /// <summary>
    /// Invokes <paramref name="apiId"/> without waiting for a response.
    /// </summary>
    /// <remarks>
    /// Used for high-rate commands such as <c>Move</c>, where waiting for a round trip on every tick
    /// would cap the command rate at the link latency.
    /// </remarks>
    public void Send(long apiId, string? parameter = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _requestPublisher.Publish(ApiRequest.Create(apiId, parameter, noReply: true));
    }

    private async Task PumpResponsesAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (ApiResponse response in
                _responseSubscriber.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                if (_pending.TryRemove(response.Identity.Id, out TaskCompletionSource<ApiResponse>? completion))
                {
                    completion.TrySetResult(response);
                }
                else
                {
                    _logger.LogTrace(
                        "Discarded uncorrelated response {RequestId} for API {ApiId} on service {Service}.",
                        response.Identity.Id,
                        response.Identity.ApiId,
                        ServiceName);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Response pump for service {Service} failed.", ServiceName);
        }
    }

    /// <summary>Formats a float for a JSON parameter document using invariant culture.</summary>
    /// <remarks>
    /// Explicitly invariant: under a locale that uses a decimal comma, the default formatting would emit
    /// <c>0,3</c> and the robot's JSON parser would reject the whole document.
    /// </remarks>
    internal static string Json(float value) => value.ToString("0.######", CultureInfo.InvariantCulture);

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _pumpCancellation.Cancel();

        foreach (TaskCompletionSource<ApiResponse> pending in _pending.Values)
        {
            pending.TrySetCanceled();
        }

        _pending.Clear();
        _requestPublisher.Dispose();
        _responseSubscriber.Dispose();

        try
        {
            _pumpTask.Wait(TimeSpan.FromSeconds(1));
        }
        catch (AggregateException)
        {
            // Shutdown races are expected here and carry no useful information.
        }

        _pumpCancellation.Dispose();
    }
}
