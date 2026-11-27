// Copyright (c) Honua Geo Inc.
// SPDX-License-Identifier: Apache-2.0

#if HONUA_ASSIGNED_WORK_PILOT
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Honua.Field.Core.Enterprise;
using Honua.Field.Core.Storage;
using Honua.Mobile.Offline.Tasks;
using Honua.Sdk.Field.Tasks;

namespace Honua.Field.Core.Tests.Storage;

public sealed class AssignedWorkPilotTests
{
    [Fact]
    public async Task Cold_offline_start_restores_only_own_work_and_replays_the_same_command()
    {
        var directory = Directory.CreateTempSubdirectory();
        try
        {
            var path = Path.Combine(directory.FullName, "field.db");
            var persistence = new MemoryPersistence();
            var sessions = new AuthSessionStore();
            var manager = new AuthSessionManager(sessions, persistence);
            await manager.SignInAsync(Session("worker"));
            var task = new WorkTask
            {
                TaskId = Guid.NewGuid(), ServiceId = "assets", Assignee = "worker",
                Type = "inspection", Status = "assigned", Version = 3, Title = "Inspect"
            };
            var commandId = Guid.NewGuid();
            var sends = 0;
            var requests = new List<string>();
            var started = task with { Version = 4, Status = "in_progress" };
            var conflictId = Guid.NewGuid();
            var serverConflict = false;
            var forceFailedRefresh = false;
            var malformedReceipt = false;
            async Task<HttpResponseMessage> Serve(HttpRequestMessage request)
            {
                sends++;
                var account = request.Headers.Authorization?.Parameter;
                if (request.Method == HttpMethod.Get)
                {
                    Assert.Contains("assignee=" + account, request.RequestUri!.Query);
                    if (account != "worker")
                        return Ok(new WorkTaskPage { Items = [] });
                    if (forceFailedRefresh)
                        return request.RequestUri.Query.Contains("cursor=", StringComparison.Ordinal)
                            ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
                            : Ok(new WorkTaskPage { Items = [], NextCursor = "next" });
                    return Ok(new WorkTaskPage { Items = serverConflict ? [] : [requests.Count == 0 ? task : started] });
                }

                Assert.Equal("worker", account);
                requests.Add(await request.Content!.ReadAsStringAsync());
                if (requests.Count == 1)
                    throw new HttpRequestException("Receipt transport closed after commit.");
                if (serverConflict)
                {
                    var conflict = new HttpResponseMessage(HttpStatusCode.Conflict)
                    {
                        Content = JsonContent.Create(new { commandId = conflictId, currentVersion = 5, reason = "version_conflict" })
                    };
                    conflict.Headers.Add("Honua-Command-Outcome", "conflict");
                    return conflict;
                }

                return Ok(new WorkTaskReceipt
                {
                    Outcome = "duplicate", CommandId = commandId,
                    Task = malformedReceipt ? started with { TaskId = Guid.NewGuid() } : started
                });
            }

            AssignedWorkPilot Create(AuthSessionStore store, AuthSessionManager lifecycle, MobileRecordStore records) =>
                new(store, lifecycle, _ => Task.FromResult(records), () => new HttpClient(new TaskHandler(Serve)),
                    new Uri("https://tasks.test"), "assets");

            var pilot = Create(sessions, manager, await MobileRecordStore.OpenEncryptedAsync(path, "pilot-test-key"));
            await pilot.SynchronizeAsync(online: true);
            var initial = Assert.Single((await pilot.ReadAsync("worker")).Tasks);
            await pilot.QueueTransitionAsync("worker", initial.TaskId, new WorkTaskTransitionRequest
            {
                CommandId = commandId, ExpectedVersion = initial.Version, Action = "start"
            });
            var lostReceipt = await Assert.ThrowsAsync<WorkTaskApiException>(() => pilot.SynchronizeAsync(online: true));
            Assert.IsType<HttpRequestException>(lostReceipt.InnerException);

            var restarted = new AuthSessionStore("ignored-fallback");
            var restartManager = new AuthSessionManager(restarted, persistence);
            Assert.Equal("worker", (await restartManager.RestoreAsync())?.UserId);
            pilot = Create(restarted, restartManager, await MobileRecordStore.OpenEncryptedAsync(path, "pilot-test-key"));
            var beforeOffline = sends;
            await pilot.SynchronizeAsync(online: false);
            var offline = await pilot.ReadAsync("worker");
            Assert.Equal(beforeOffline, sends);
            Assert.Equal(initial.TaskId, Assert.Single(offline.Tasks).TaskId);
            Assert.Equal(AssignedCommandStatus.Pending, Assert.Single(offline.Commands).Status);

            await restartManager.SignInAsync(Session("another-worker"));
            Assert.Empty((await pilot.ReadAsync("another-worker")).Tasks);
            await Assert.ThrowsAsync<InvalidOperationException>(() => pilot.ReadAsync("worker"));
            await pilot.SynchronizeAsync(online: true);
            Assert.Single(requests);
            await restartManager.SignOutAsync();
            var signedOut = sends;
            await pilot.SynchronizeAsync(online: true);
            Assert.Equal(signedOut, sends);

            await restartManager.SignInAsync(Session("worker"));
            malformedReceipt = true;
            var invalidReceipt = await Assert.ThrowsAsync<WorkTaskApiException>(() => pilot.SynchronizeAsync(online: true));
            Assert.IsType<JsonException>(invalidReceipt.InnerException);
            malformedReceipt = false;
            var awaitingValidReceipt = await pilot.ReadAsync("worker");
            Assert.Equal(AssignedCommandStatus.Pending, Assert.Single(awaitingValidReceipt.Commands).Status);
            Assert.Equal(task.Version, Assert.Single(awaitingValidReceipt.Tasks).Version);
            forceFailedRefresh = true;
            var unavailable = await Assert.ThrowsAsync<WorkTaskApiException>(() => pilot.SynchronizeAsync(online: true));
            Assert.Equal((int)HttpStatusCode.ServiceUnavailable, unavailable.HttpStatus);
            forceFailedRefresh = false;
            var accepted = await pilot.ReadAsync("worker");
            var receipt = Assert.Single(accepted.Commands);
            Assert.Equal(AssignedCommandStatus.Confirmed, receipt.Status);
            Assert.Equal("duplicate", receipt.Receipt?.Outcome);
            Assert.Equal(started.Version, Assert.Single(accepted.Tasks).Version);
            Assert.Equal(3, requests.Count);
            Assert.All(requests, replay => Assert.Equal(requests[0], replay));

            serverConflict = true;
            await pilot.QueueTransitionAsync("worker", task.TaskId, new WorkTaskTransitionRequest
            {
                CommandId = conflictId, ExpectedVersion = started.Version, Action = "cancel"
            });
            await pilot.SynchronizeAsync(online: true);
            pilot = Create(restarted, restartManager, await MobileRecordStore.OpenEncryptedAsync(path, "pilot-test-key"));
            var evidence = await pilot.ReadAsync("worker");
            Assert.Empty(evidence.Tasks);
            var conflict = Assert.Single(evidence.Commands, command => command.CommandId == conflictId);
            Assert.Equal(AssignedCommandStatus.Conflict, conflict.Status);
            Assert.Equal(409, conflict.HttpStatus);
            Assert.Equal(5, conflict.CurrentVersion);
            Assert.False((await File.ReadAllBytesAsync(path)).AsSpan().StartsWith("SQLite format 3"u8));
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task Reconnect_refreshes_the_owning_session_before_replaying_pending_work()
    {
        var directory = Directory.CreateTempSubdirectory();
        try
        {
            var records = await MobileRecordStore.OpenEncryptedAsync(Path.Combine(directory.FullName, "field.db"), "pilot-test-key");
            var sessions = new AuthSessionStore();
            var refreshed = Session("worker") with { AccessToken = "renewed" };
            var refreshes = 0;
            var canRefresh = false;
            var manager = new AuthSessionManager(sessions, new MemoryPersistence(), (_, _) =>
            {
                refreshes++;
                return Task.FromResult(canRefresh ? refreshed : null);
            });
            var expired = Session("worker") with
            {
                RefreshToken = "rotate", ExpiresAtUtc = DateTimeOffset.UtcNow.AddMinutes(-1)
            };
            var expiredEvents = 0;
            manager.SessionExpired += (_, _) => expiredEvents++;
            var task = new WorkTask
            {
                TaskId = Guid.NewGuid(), ServiceId = "assets", Assignee = "worker",
                Type = "inspection", Status = "in_progress", Version = 2, Title = "Inspect"
            };
            var commandId = Guid.NewGuid();
            var sent = new List<HttpMethod>();
            Task<HttpResponseMessage> Serve(HttpRequestMessage request)
            {
                if (request.Headers.Authorization?.Parameter == "worker")
                {
                    Assert.Equal(HttpMethod.Get, request.Method);
                    return Task.FromResult(Ok(new WorkTaskPage { Items = [task with { Status = "assigned", Version = 1 }] }));
                }
                Assert.Equal("renewed", request.Headers.Authorization?.Parameter);
                sent.Add(request.Method);
                return Task.FromResult(request.Method == HttpMethod.Get
                    ? Ok(new WorkTaskPage { Items = [task] })
                    : Ok(new WorkTaskReceipt { Outcome = "accepted", CommandId = commandId, Task = task }));
            }
            var pilot = new AssignedWorkPilot(sessions, manager, _ => Task.FromResult(records),
                () => new HttpClient(new TaskHandler(Serve)), new Uri("https://tasks.test"), "assets");

            await manager.SignInAsync(Session("worker"));
            await pilot.SynchronizeAsync(online: true);
            await pilot.QueueTransitionAsync("worker", task.TaskId, new WorkTaskTransitionRequest
            {
                CommandId = commandId, ExpectedVersion = 1, Action = "start"
            });
            await manager.SignInAsync(expired);
            await pilot.SynchronizeAsync(online: false);
            Assert.Equal(0, refreshes);
            await pilot.SynchronizeAsync(online: true);
            Assert.Equal(1, expiredEvents);
            Assert.Empty(sent);

            await manager.SignInAsync(Session("worker"));
            Assert.Equal(AssignedCommandStatus.Pending, Assert.Single((await pilot.ReadAsync("worker")).Commands).Status);
            await manager.SignInAsync(expired);
            canRefresh = true;
            await pilot.SynchronizeAsync(online: true);
            Assert.Equal([HttpMethod.Post, HttpMethod.Get], sent);
            Assert.Equal(2, refreshes);
            Assert.Equal(AssignedCommandStatus.Confirmed, Assert.Single((await pilot.ReadAsync("worker")).Commands).Status);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task Account_switch_during_refresh_does_not_publish_work_for_previous_account()
    {
        var directory = Directory.CreateTempSubdirectory();
        try
        {
            var records = await MobileRecordStore.OpenEncryptedAsync(Path.Combine(directory.FullName, "field.db"), "pilot-test-key");
            var sessions = new AuthSessionStore();
            var manager = new AuthSessionManager(sessions, new MemoryPersistence());
            var began = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var resumed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var firstTask = new WorkTask
            {
                TaskId = Guid.NewGuid(), ServiceId = "assets", Assignee = "first",
                Type = "inspection", Status = "assigned", Version = 1, Title = "Inspect"
            };
            var secondTask = firstTask with { TaskId = Guid.NewGuid(), Assignee = "second" };
            async Task<HttpResponseMessage> Serve(HttpRequestMessage request)
            {
                if (request.Headers.Authorization?.Parameter == "first")
                {
                    began.SetResult();
                    await resumed.Task;
                    return Ok(new WorkTaskPage { Items = [firstTask] });
                }

                Assert.Equal("second", request.Headers.Authorization?.Parameter);
                return Ok(new WorkTaskPage { Items = [secondTask] });
            }

            var pilot = new AssignedWorkPilot(sessions, manager, _ => Task.FromResult(records),
                () => new HttpClient(new TaskHandler(Serve)), new Uri("https://tasks.test"), "assets");
            await manager.SignInAsync(Session("first"));
            var previousSync = pilot.SynchronizeAsync(online: true);
            await began.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await manager.SignInAsync(Session("second"));
            var currentSync = pilot.SynchronizeAsync(online: true);
            resumed.SetResult();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => previousSync);
            await currentSync;
            Assert.Equal(secondTask.TaskId, Assert.Single((await pilot.ReadAsync("second")).Tasks).TaskId);
            await manager.SignInAsync(Session("first"));
            Assert.Empty((await pilot.ReadAsync("first")).Tasks);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    private static AuthSession Session(string worker) => new()
    {
        UserId = worker, AccessToken = worker, ExpiresAtUtc = DateTimeOffset.UtcNow.AddHours(1)
    };

    private static HttpResponseMessage Ok<T>(T payload) =>
        new(HttpStatusCode.OK) { Content = JsonContent.Create(payload) };

    private sealed class TaskHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            send(request);
    }

    private sealed class MemoryPersistence : ISessionPersistence
    {
        private AuthSession? _session;

        public Task<AuthSession?> LoadAsync(CancellationToken cancellationToken = default) => Task.FromResult(_session);

        public Task SaveAsync(AuthSession session, CancellationToken cancellationToken = default)
        {
            _session = session;
            return Task.CompletedTask;
        }

        public Task ClearAsync(CancellationToken cancellationToken = default)
        {
            _session = null;
            return Task.CompletedTask;
        }
    }
}
#endif
