// Copyright (c) Honua Geo Inc.
// SPDX-License-Identifier: Apache-2.0

#if HONUA_ASSIGNED_WORK_PILOT
using System.Net.Http.Headers;
using Honua.Field.Core.Enterprise;
using Honua.Field.Core.Sync;
using Honua.Mobile.Offline.Tasks;
using Honua.Sdk.Field.Tasks;

namespace Honua.Field.Core.Storage;

/// <summary>Connects the signed-in Field session to its encrypted assigned-work partition.</summary>
public sealed class AssignedWorkPilot(
    IAuthSessionStore sessions,
    Func<CancellationToken, Task<MobileRecordStore>> openRecords,
    Func<HttpClient> createClient,
    Uri server,
    string serviceId)
{
    private readonly SemaphoreSlim _syncGate = new(1, 1);

    /// <summary>Reads cached work for the specified active user without accessing the network.</summary>
    public Task<AssignedWorkSnapshot> ReadAsync(string accountSubject, CancellationToken ct = default) =>
        UseAsync(accountSubject, (work, token) => work.ReadAsync(token), ct);

    /// <summary>Persists a versioned transition for replay by the owning user.</summary>
    public Task QueueTransitionAsync(
        string accountSubject, Guid taskId, WorkTaskTransitionRequest request, CancellationToken ct = default) =>
        UseAsync<object?>(accountSubject, async (work, token) =>
        {
            await work.QueueTransitionAsync(taskId, request, token).ConfigureAwait(false);
            return null;
        }, ct);

    /// <summary>Persists a versioned completion for replay by the owning user.</summary>
    public Task QueueCompletionAsync(
        string accountSubject, Guid taskId, WorkTaskCompletionRequest request, CancellationToken ct = default) =>
        UseAsync<object?>(accountSubject, async (work, token) =>
        {
            await work.QueueCompletionAsync(taskId, request, token).ConfigureAwait(false);
            return null;
        }, ct);

    /// <summary>Replays pending commands, then refreshes assigned work when connected and signed in.</summary>
    public async Task SynchronizeAsync(bool online, CancellationToken ct = default)
    {
        if (!online || sessions.Current is not { } session || session.IsExpired(DateTimeOffset.UtcNow))
        {
            return;
        }

        await _syncGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!ReferenceEquals(session, sessions.Current))
            {
                return;
            }

            await UseAsync<object?>(session.UserId, async (work, token) =>
            {
                await work.ReplayAsync(token).ConfigureAwait(false);
                await work.RefreshAsync(token).ConfigureAwait(false);
                return null;
            }, ct, session).ConfigureAwait(false);
        }
        finally
        {
            _syncGate.Release();
        }
    }

    private async Task<T> UseAsync<T>(
        string accountSubject, Func<OfflineAssignedWork, CancellationToken, Task<T>> action,
        CancellationToken ct, AuthSession? expectedSession = null)
    {
        var session = expectedSession ?? sessions.Current;
        if (session is null || session.IsExpired(DateTimeOffset.UtcNow)
            || !string.Equals(accountSubject, session.UserId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Assigned work requires the active owning account.");
        }

        using var operation = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var eventGate = new object();
        var observing = true;
        sessions.Changed += CancelIfSessionChanged;
        try
        {
            CancelIfSessionChanged(null, EventArgs.Empty);
            operation.Token.ThrowIfCancellationRequested();
            var records = await openRecords(operation.Token).ConfigureAwait(false);
            operation.Token.ThrowIfCancellationRequested();
            EndpointSecurity.EnsureSecureTransport(server);
            using var http = createClient();
            http.BaseAddress = server;
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", session.AccessToken);
            var scope = new AssignedWorkScope(server.AbsoluteUri, session.UserId, serviceId, session.UserId);
            var result = await action(records.CreateAssignedWork(new WorkTaskClient(http), scope), operation.Token)
                .ConfigureAwait(false);
            CancelIfSessionChanged(null, EventArgs.Empty);
            operation.Token.ThrowIfCancellationRequested();
            return result;
        }
        finally
        {
            sessions.Changed -= CancelIfSessionChanged;
            lock (eventGate)
            {
                observing = false;
            }
        }

        void CancelIfSessionChanged(object? sender, EventArgs args)
        {
            lock (eventGate)
            {
                if (observing && !ReferenceEquals(session, sessions.Current))
                {
                    operation.Cancel();
                }
            }
        }
    }
}
#endif
