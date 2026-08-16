using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using MarkUpViewMini.Core.Activation;
using MarkUpViewMini.Infrastructure.Activation;

namespace MarkUpViewMini.Infrastructure.Tests.Activation;

public sealed class SingleInstanceCoordinatorTests
{
    [Fact]
    public void Protocol_normalizes_absolute_local_file_paths()
    {
        var path = Path.Combine(Path.GetTempPath(), "folder", "..", "document.md");
        var request = Request([path]);

        var payload = ActivationProtocol.Serialize(request);
        var actual = ActivationProtocol.Deserialize(payload);

        Assert.Equal(Path.GetFullPath(path), Assert.Single(actual.Paths));
    }

    [Theory]
    [MemberData(nameof(InvalidRequests))]
    public void Protocol_rejects_requests_outside_the_file_open_schema(ActivationRequest request)
    {
        Assert.Throws<InvalidDataException>(() => ActivationProtocol.Serialize(request));
    }

    [Fact]
    public void Protocol_rejects_more_than_32_paths()
    {
        var paths = Enumerable.Range(0, 33)
            .Select(index => Path.Combine(Path.GetTempPath(), $"document-{index}.md"))
            .ToArray();

        Assert.Throws<InvalidDataException>(() => ActivationProtocol.Serialize(Request(paths)));
    }

    [Fact]
    public void Protocol_accepts_a_serialized_payload_of_exactly_256_kibibytes()
    {
        var request = RequestWithSerializedPayloadLength(262_144);

        var payload = ActivationProtocol.Serialize(request);

        Assert.Equal(262_144, payload.Length);
    }

    [Fact]
    public void Protocol_rejects_a_serialized_payload_one_byte_over_256_kibibytes()
    {
        var request = RequestWithSerializedPayloadLength(262_145);
        Assert.All(request.Paths, path =>
        {
            Assert.Equal(path, Path.GetFullPath(path));
            Assert.InRange(ActivationProtocol.Serialize(Request([path])).Length, 1, 262_144);
        });

        var exception = Assert.Throws<InvalidDataException>(() => ActivationProtocol.Serialize(request));

        Assert.Null(exception.InnerException);
        Assert.Contains("payload", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(262145)]
    public async Task Protocol_rejects_frame_lengths_outside_1_through_256_kibibytes(int length)
    {
        var prefix = new byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(prefix, length);
        await using var stream = new MemoryStream(prefix);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            ActivationProtocol.ReadFrameAsync(stream, CancellationToken.None).AsTask());
    }

    [Fact]
    public void Protocol_rejects_unmapped_json_members()
    {
        var path = JsonEscape(Path.Combine(Path.GetTempPath(), "document.md"));
        var payload = Encoding.UTF8.GetBytes(
            $$"""{"version":1,"kind":1,"paths":["{{path}}"],"senderProcessId":42,"unexpected":true}""");

        Assert.Throws<InvalidDataException>(() => ActivationProtocol.Deserialize(payload));
    }

    [Fact]
    public void Protocol_rejects_duplicate_json_members()
    {
        var path = JsonEscape(Path.Combine(Path.GetTempPath(), "document.md"));
        var payload = Encoding.UTF8.GetBytes(
            $$"""{"version":1,"version":1,"kind":1,"paths":["{{path}}"],"senderProcessId":42}""");

        Assert.Throws<InvalidDataException>(() => ActivationProtocol.Deserialize(payload));
    }

    [Fact]
    public void Protocol_rejects_a_mapped_remote_drive_before_component_inspection()
    {
        var inspector = new StubActivationPathInspector(
            DriveType.Network,
            static _ => throw new InvalidOperationException("Remote components must not be inspected."));

        Assert.Throws<InvalidDataException>(() =>
            ActivationProtocol.Serialize(Request([@"Z:\document.md"]), inspector));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Protocol_rejects_an_existing_reparse_backed_ancestor_or_target(bool targetIsReparse)
    {
        var path = targetIsReparse
            ? @"C:\safe\document.md"
            : @"C:\safe\linked\document.md";
        var reparsePath = targetIsReparse ? path : @"C:\safe\linked";
        var inspector = new StubActivationPathInspector(
            DriveType.Fixed,
            candidate => candidate.Equals(reparsePath, StringComparison.OrdinalIgnoreCase)
                ? FileAttributes.ReparsePoint | (targetIsReparse ? 0 : FileAttributes.Directory)
                : FileAttributes.Directory);
        var payload = Encoding.UTF8.GetBytes(
            $$"""{"version":1,"kind":1,"paths":["{{JsonEscape(path)}}"],"senderProcessId":42}""");

        Assert.Throws<InvalidDataException>(() => ActivationProtocol.Deserialize(payload, inspector));
    }

    [Fact]
    public void Protocol_accepts_a_nonexistent_leaf_after_verified_local_ancestors()
    {
        var path = @"C:\safe\new-document.md";
        var inspector = new StubActivationPathInspector(
            DriveType.Fixed,
            candidate => candidate.Equals(@"C:\safe", StringComparison.OrdinalIgnoreCase)
                ? FileAttributes.Directory
                : null);

        var payload = ActivationProtocol.Serialize(Request([path]), inspector);
        var actual = ActivationProtocol.Deserialize(payload, inspector);

        Assert.Equal(path, Assert.Single(actual.Paths));
    }

    [Fact]
    public void Per_user_suffix_is_the_sha_256_of_the_windows_sid()
    {
        var sid = WindowsIdentity.GetCurrent().User!.Value;
        var expected = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sid))).ToLowerInvariant();

        Assert.Equal(expected, SingleInstanceCoordinator.CurrentUserSuffix);
        Assert.DoesNotContain(Environment.UserName, SingleInstanceCoordinator.CurrentUserSuffix,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Server_creation_requests_current_user_only_pipe_ownership()
    {
        PipeOptions? capturedOptions = null;
        await using var server = new NamedPipeActivationServer(
            UniqueApplicationId(),
            (pipeName, options) =>
            {
                capturedOptions = options;
                return new NamedPipeServerStream(
                    pipeName,
                    PipeDirection.InOut,
                    1,
                    PipeTransmissionMode.Byte,
                    options);
            });

        await server.StartAsync(static (_, _) => Task.CompletedTask, CancellationToken.None);

        Assert.NotNull(capturedOptions);
        Assert.True(capturedOptions.Value.HasFlag(PipeOptions.CurrentUserOnly));
    }

    [Fact]
    public async Task Forwarding_failure_without_acknowledgement_is_time_bounded()
    {
        var pipeName = UniqueApplicationId();
        await using var server = new NamedPipeServerStream(
            pipeName,
            PipeDirection.InOut,
            1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        var client = new NamedPipeActivationClient(pipeName, TimeSpan.FromMilliseconds(750));
        var elapsed = Stopwatch.StartNew();
        var forwarding = client.ForwardAsync(Request([]), CancellationToken.None);
        await server.WaitForConnectionAsync().WaitAsync(TimeSpan.FromSeconds(2));
        _ = await ActivationProtocol.ReadFrameAsync(server, CancellationToken.None);
        var disconnected = server.ReadAsync(new byte[1], CancellationToken.None).AsTask();

        var winner = await Task.WhenAny(forwarding, Task.Delay(TimeSpan.FromSeconds(5)));
        Assert.Same(forwarding, winner);
        await Assert.ThrowsAsync<TimeoutException>(() => forwarding);

        elapsed.Stop();
        Assert.InRange(elapsed.Elapsed, TimeSpan.FromMilliseconds(500), TimeSpan.FromSeconds(3));
        Assert.Equal(0, await disconnected.WaitAsync(TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public async Task Initial_primary_rejects_invalid_paths_before_starting_its_server()
    {
        var serverFactoryCalls = 0;
        await using var coordinator = new SingleInstanceCoordinator(
            UniqueApplicationId(),
            static (_, _) => Task.CompletedTask,
            _ =>
            {
                Interlocked.Increment(ref serverFactoryCalls);
                return new StubActivationServer();
            });

        await Assert.ThrowsAsync<InvalidDataException>(() => coordinator.StartOrForwardAsync(
            Request([@"\\server\share\document.md"]),
            CancellationToken.None));

        Assert.Equal(0, serverFactoryCalls);
    }

    [Fact]
    public async Task First_coordinator_listens_and_second_forwards_one_acknowledged_request()
    {
        var applicationId = UniqueApplicationId();
        var received = new TaskCompletionSource<ActivationRequest>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var primary = new SingleInstanceCoordinator(
            applicationId,
            (request, _) =>
            {
                received.TrySetResult(request);
                return Task.CompletedTask;
            });
        await using var secondary = new SingleInstanceCoordinator(applicationId, static (_, _) => Task.CompletedTask);
        var request = Request([Path.Combine(Path.GetTempPath(), "forwarded.md")]);

        Assert.Equal(SingleInstanceResult.Primary,
            await primary.StartOrForwardAsync(Request([]), CancellationToken.None));
        Assert.Equal(SingleInstanceResult.Forwarded,
            await secondary.StartOrForwardAsync(request, CancellationToken.None));

        var actual = await received.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(request.Version, actual.Version);
        Assert.Equal(request.Kind, actual.Kind);
        Assert.Equal(request.SenderProcessId, actual.SenderProcessId);
        Assert.Equal(request.Paths.Select(Path.GetFullPath), actual.Paths);
    }

    [Fact]
    public async Task Forward_failure_re_elects_after_the_stopping_primary_releases_its_mutex()
    {
        var applicationId = UniqueApplicationId();
        var forwardStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finishFailedForward = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var forwardFailed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var replacementHandled = new TaskCompletionSource<ActivationRequest>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        await using var coordinator = new SingleInstanceCoordinator(
            applicationId,
            (request, _) =>
            {
                replacementHandled.TrySetResult(request);
                return Task.CompletedTask;
            },
            pipeName => new NamedPipeActivationServer(pipeName),
            async (_, _, cancellationToken) =>
            {
                forwardStarted.TrySetResult();
                await finishFailedForward.Task.WaitAsync(cancellationToken);
                forwardFailed.TrySetResult();
                throw new IOException("the old listener is already disposed");
            },
            TimeSpan.FromSeconds(1));
        using var stoppingPrimaryMutex = new Mutex(
            initiallyOwned: false,
            coordinator.MutexName,
            out var createdNew);
        Assert.True(createdNew);

        var starting = coordinator.StartOrForwardAsync(Request([]), CancellationToken.None);
        await forwardStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        finishFailedForward.TrySetResult();
        await forwardFailed.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await Task.Delay(100);
        Assert.False(starting.IsCompleted);
        stoppingPrimaryMutex.Dispose();

        Assert.Equal(SingleInstanceResult.Primary, await starting);
        await using var third = new SingleInstanceCoordinator(
            applicationId,
            static (_, _) => Task.CompletedTask);
        var thirdRequest = Request([Path.Combine(Path.GetTempPath(), "after-handoff.md")]);
        Assert.Equal(
            SingleInstanceResult.Forwarded,
            await third.StartOrForwardAsync(thirdRequest, CancellationToken.None));
        Assert.Equal(
            thirdRequest.Paths,
            (await replacementHandled.Task.WaitAsync(TimeSpan.FromSeconds(2))).Paths);
    }

    [Fact]
    public async Task Forward_failure_does_not_create_a_duplicate_primary_while_the_mutex_remains()
    {
        var applicationId = UniqueApplicationId();
        var serverFactoryCalls = 0;
        await using var coordinator = new SingleInstanceCoordinator(
            applicationId,
            static (_, _) => Task.CompletedTask,
            _ =>
            {
                Interlocked.Increment(ref serverFactoryCalls);
                return new StubActivationServer();
            },
            static (_, _, _) => Task.FromException(new IOException("listener unavailable")),
            TimeSpan.FromMilliseconds(100));
        using var livePrimaryMutex = new Mutex(
            initiallyOwned: false,
            coordinator.MutexName,
            out var createdNew);
        Assert.True(createdNew);

        var exception = await Assert.ThrowsAsync<IOException>(() =>
            coordinator.StartOrForwardAsync(Request([]), CancellationToken.None));

        Assert.Equal("listener unavailable", exception.Message);
        Assert.Equal(0, serverFactoryCalls);
    }

    [Fact]
    public async Task Malformed_client_does_not_terminate_the_listener()
    {
        var applicationId = UniqueApplicationId();
        var received = new TaskCompletionSource<ActivationRequest>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var primary = new SingleInstanceCoordinator(
            applicationId,
            (request, _) =>
            {
                received.TrySetResult(request);
                return Task.CompletedTask;
            });
        Assert.Equal(SingleInstanceResult.Primary,
            await primary.StartOrForwardAsync(Request([]), CancellationToken.None));

        await SendMalformedFrameAsync(primary.PipeName);

        await using var secondary = new SingleInstanceCoordinator(applicationId, static (_, _) => Task.CompletedTask);
        var valid = Request([Path.Combine(Path.GetTempPath(), "still-listening.md")]);
        Assert.Equal(SingleInstanceResult.Forwarded,
            await secondary.StartOrForwardAsync(valid, CancellationToken.None));
        Assert.Equal(Path.GetFullPath(valid.Paths[0]), Assert.Single((await received.Task).Paths));
    }

    [Fact]
    public async Task Partial_frame_client_does_not_monopolize_the_listener()
    {
        var applicationId = UniqueApplicationId();
        var received = new TaskCompletionSource<ActivationRequest>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var primary = new SingleInstanceCoordinator(
            applicationId,
            (request, _) =>
            {
                received.TrySetResult(request);
                return Task.CompletedTask;
            });
        Assert.Equal(SingleInstanceResult.Primary,
            await primary.StartOrForwardAsync(Request([]), CancellationToken.None));
        await using var stalled = await ConnectAsync(primary.PipeName);
        var prefix = new byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(prefix, 100);
        await stalled.WriteAsync(prefix);
        await stalled.FlushAsync();

        await using var secondary = new SingleInstanceCoordinator(applicationId, static (_, _) => Task.CompletedTask);
        var valid = Request([Path.Combine(Path.GetTempPath(), "after-partial.md")]);
        Assert.Equal(
            SingleInstanceResult.Forwarded,
            await secondary.StartOrForwardAsync(valid, CancellationToken.None)
                .WaitAsync(TimeSpan.FromSeconds(4)));
        Assert.Equal(Path.GetFullPath(valid.Paths[0]), Assert.Single((await received.Task).Paths));
    }

    [Fact]
    public async Task At_capacity_overload_is_rejected_promptly_until_a_slot_is_released()
    {
        var applicationId = UniqueApplicationId();
        var firstHandlerStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirstHandler = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var allHandled = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var handledPaths = new ConcurrentQueue<string>();
        await using var primary = new SingleInstanceCoordinator(
            applicationId,
            async (request, cancellationToken) =>
            {
                var path = Assert.Single(request.Paths);
                handledPaths.Enqueue(path);
                if (handledPaths.Count == 1)
                {
                    firstHandlerStarted.TrySetResult();
                    await releaseFirstHandler.Task.WaitAsync(cancellationToken);
                }

                if (handledPaths.Count == NamedPipeActivationServer.MaximumConcurrentConnections + 1)
                {
                    allHandled.TrySetResult();
                }
            });
        Assert.Equal(SingleInstanceResult.Primary,
            await primary.StartOrForwardAsync(Request([]), CancellationToken.None));
        var paths = Enumerable.Range(1, NamedPipeActivationServer.MaximumConcurrentConnections)
            .Select(index => Path.Combine(Path.GetTempPath(), $"queued-{index}.md"))
            .ToArray();
        var laterPath = Path.Combine(Path.GetTempPath(), "after-overload.md");
        var forwards = new List<Task>();

        try
        {
            var firstConnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            forwards.Add(ForwardRawAsync(primary.PipeName, Request([paths[0]]), firstConnected));
            await firstHandlerStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            for (var index = 1; index < paths.Length; index++)
            {
                var connected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                forwards.Add(ForwardRawAsync(primary.PipeName, Request([paths[index]]), connected));
                await connected.Task.WaitAsync(TimeSpan.FromSeconds(2));
            }

            Assert.All(forwards, forward => Assert.False(forward.IsCompleted));
            Assert.False(releaseFirstHandler.Task.IsCompleted);
            for (var index = 1; index <= 2; index++)
            {
                var rejected = ForwardRawAsync(
                    primary.PipeName,
                    Request([Path.Combine(Path.GetTempPath(), $"overload-{index}.md")]));
                Assert.Same(
                    rejected,
                    await Task.WhenAny(rejected, Task.Delay(TimeSpan.FromSeconds(2))));
                await Assert.ThrowsAnyAsync<Exception>(() =>
                    rejected);
            }
        }
        finally
        {
            releaseFirstHandler.TrySetResult();
        }

        await Task.WhenAll(forwards).WaitAsync(TimeSpan.FromSeconds(10));
        var laterConnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await ForwardRawAsync(primary.PipeName, Request([laterPath]), laterConnected)
            .WaitAsync(TimeSpan.FromSeconds(2));
        await allHandled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(paths.Append(laterPath).Select(Path.GetFullPath), handledPaths);
    }

    [Fact]
    public async Task Handler_failure_sends_no_positive_ack_and_is_isolated_from_the_next_request()
    {
        var applicationId = UniqueApplicationId();
        var handledLater = new TaskCompletionSource<ActivationRequest>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var handlerCount = 0;
        await using var primary = new SingleInstanceCoordinator(
            applicationId,
            (request, _) =>
            {
                if (Interlocked.Increment(ref handlerCount) == 1)
                {
                    throw new IOException("first handler failed");
                }

                handledLater.TrySetResult(request);
                return Task.CompletedTask;
            });
        await using var first = new SingleInstanceCoordinator(applicationId, static (_, _) => Task.CompletedTask);
        await using var second = new SingleInstanceCoordinator(applicationId, static (_, _) => Task.CompletedTask);
        Assert.Equal(SingleInstanceResult.Primary,
            await primary.StartOrForwardAsync(Request([]), CancellationToken.None));
        var secondPath = Path.Combine(Path.GetTempPath(), "handler-continues.md");

        await Assert.ThrowsAnyAsync<Exception>(() =>
            first.StartOrForwardAsync(
                    Request([Path.Combine(Path.GetTempPath(), "handler-fails.md")]),
                    CancellationToken.None)
                .WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.Equal(
            SingleInstanceResult.Forwarded,
            await second.StartOrForwardAsync(Request([secondPath]), CancellationToken.None)
                .WaitAsync(TimeSpan.FromSeconds(2)));

        Assert.Equal(
            Path.GetFullPath(secondPath),
            Assert.Single((await handledLater.Task.WaitAsync(TimeSpan.FromSeconds(2))).Paths));
    }

    [Fact]
    public async Task Disposing_primary_sends_no_positive_ack_for_an_active_handler()
    {
        var applicationId = UniqueApplicationId();
        var handlerStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handlerCanceled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var primary = new SingleInstanceCoordinator(
            applicationId,
            async (_, cancellationToken) =>
            {
                handlerStarted.TrySetResult();
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    handlerCanceled.TrySetResult();
                    throw;
                }
            });
        Assert.Equal(SingleInstanceResult.Primary,
            await primary.StartOrForwardAsync(Request([]), CancellationToken.None));
        var client = new NamedPipeActivationClient(primary.PipeName, TimeSpan.FromSeconds(2));
        var forwarding = client.ForwardAsync(
            Request([Path.Combine(Path.GetTempPath(), "during-disposal.md")]),
            CancellationToken.None);
        await handlerStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(forwarding.IsCompleted);

        await primary.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));

        await handlerCanceled.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await Assert.ThrowsAnyAsync<Exception>(() => forwarding.WaitAsync(TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public async Task Disposing_at_capacity_primary_cancels_all_connections_without_false_acknowledgements()
    {
        var applicationId = UniqueApplicationId();
        var handlerStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var primary = new SingleInstanceCoordinator(
            applicationId,
            async (_, cancellationToken) =>
            {
                handlerStarted.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            });
        Assert.Equal(SingleInstanceResult.Primary,
            await primary.StartOrForwardAsync(Request([]), CancellationToken.None));
        var forwards = new List<Task>();
        try
        {
            for (var index = 0; index < NamedPipeActivationServer.MaximumConcurrentConnections; index++)
            {
                var connected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                forwards.Add(ForwardRawAsync(
                    primary.PipeName,
                    Request([Path.Combine(Path.GetTempPath(), $"dispose-queued-{index}.md")]),
                    connected));
                await connected.Task.WaitAsync(TimeSpan.FromSeconds(2));
            }

            await handlerStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.All(forwards, forward => Assert.False(forward.IsCompleted));
            var rejected = ForwardRawAsync(
                primary.PipeName,
                Request([Path.Combine(Path.GetTempPath(), "dispose-overload.md")]));
            Assert.Same(
                rejected,
                await Task.WhenAny(rejected, Task.Delay(TimeSpan.FromSeconds(2))));
            await Assert.ThrowsAnyAsync<Exception>(() => rejected);

            await primary.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));

            var forwardingFailures = forwards
                .Select(forward => Assert.ThrowsAnyAsync<Exception>(() => forward))
                .ToArray();
            await Task.WhenAll(forwardingFailures).WaitAsync(TimeSpan.FromSeconds(5));

            await using var replacement = new SingleInstanceCoordinator(
                applicationId,
                static (_, _) => Task.CompletedTask);
            Assert.Equal(
                SingleInstanceResult.Primary,
                await replacement.StartOrForwardAsync(Request([]), CancellationToken.None)
                    .WaitAsync(TimeSpan.FromSeconds(2)));
        }
        finally
        {
            await primary.DisposeAsync();
        }
    }

    [Fact]
    public async Task Disposing_the_primary_releases_the_listener_and_mutex()
    {
        var applicationId = UniqueApplicationId();
        var primary = new SingleInstanceCoordinator(applicationId, static (_, _) => Task.CompletedTask);
        Assert.Equal(SingleInstanceResult.Primary,
            await primary.StartOrForwardAsync(Request([]), CancellationToken.None));

        await primary.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));

        await using var replacement = new SingleInstanceCoordinator(applicationId, static (_, _) => Task.CompletedTask);
        Assert.Equal(SingleInstanceResult.Primary,
            await replacement.StartOrForwardAsync(Request([]), CancellationToken.None));
    }

    [Fact]
    public async Task Startup_failure_releases_mutex_when_server_disposal_also_fails()
    {
        var applicationId = UniqueApplicationId();
        var failed = new SingleInstanceCoordinator(
            applicationId,
            static (_, _) => Task.CompletedTask,
            _ => new StubActivationServer(
                startException: new IOException("start failed"),
                disposeException: new IOException("dispose failed")));

        await Assert.ThrowsAsync<IOException>(() =>
            failed.StartOrForwardAsync(Request([]), CancellationToken.None));

        await using var replacement = new SingleInstanceCoordinator(
            applicationId,
            static (_, _) => Task.CompletedTask);
        Assert.Equal(
            SingleInstanceResult.Primary,
            await replacement.StartOrForwardAsync(Request([]), CancellationToken.None)
                .WaitAsync(TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public async Task Disposal_failure_still_releases_mutex_for_immediate_reacquisition()
    {
        var applicationId = UniqueApplicationId();
        var primary = new SingleInstanceCoordinator(
            applicationId,
            static (_, _) => Task.CompletedTask,
            _ => new StubActivationServer(disposeException: new IOException("dispose failed")));
        Assert.Equal(
            SingleInstanceResult.Primary,
            await primary.StartOrForwardAsync(Request([]), CancellationToken.None));

        await Assert.ThrowsAsync<IOException>(() => primary.DisposeAsync().AsTask());

        await using var replacement = new SingleInstanceCoordinator(
            applicationId,
            static (_, _) => Task.CompletedTask);
        Assert.Equal(
            SingleInstanceResult.Primary,
            await replacement.StartOrForwardAsync(Request([]), CancellationToken.None)
                .WaitAsync(TimeSpan.FromSeconds(2)));
    }

    public static TheoryData<ActivationRequest> InvalidRequests()
    {
        var local = Path.Combine(Path.GetTempPath(), "document.md");
        return new TheoryData<ActivationRequest>
        {
            Request([local]) with { Version = 2 },
            Request([local]) with { Kind = (ActivationKind)99 },
            Request([local]) with { SenderProcessId = 0 },
            Request(["relative.md"]),
            Request([@"\\server\share\document.md"]),
            Request(["https://example.test/document.md"]),
            Request(["file:///C:/document.md"]),
            Request([Path.Combine(Path.GetTempPath(), "bad\0name.md")]),
        };
    }

    private static ActivationRequest Request(IReadOnlyList<string> paths) =>
        new(1, ActivationKind.FileOpen, paths, 42);

    private static string UniqueApplicationId() => $"MarkUpViewMini.Tests.{Guid.NewGuid():N}";

    private static async Task SendMalformedFrameAsync(string pipeName)
    {
        await using var pipe = await ConnectAsync(pipeName);
        var prefix = new byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(prefix, 0);
        await pipe.WriteAsync(prefix);
        await pipe.FlushAsync();
    }

    private static async Task<NamedPipeClientStream> ConnectAsync(string pipeName)
    {
        var pipe = new NamedPipeClientStream(
            ".",
            pipeName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        try
        {
            await pipe.ConnectAsync(5000, CancellationToken.None);
            return pipe;
        }
        catch
        {
            await pipe.DisposeAsync();
            throw;
        }
    }

    private static async Task ForwardRawAsync(
        string pipeName,
        ActivationRequest request,
        TaskCompletionSource? connected = null)
    {
        try
        {
            await using var pipe = await ConnectAsync(pipeName);
            await ActivationProtocol.WriteFrameAsync(
                pipe,
                ActivationProtocol.Serialize(request),
                CancellationToken.None);
            connected?.TrySetResult();
            var acknowledgement = new byte[1];
            await pipe.ReadExactlyAsync(acknowledgement, CancellationToken.None);
            if (acknowledgement[0] != 1)
            {
                throw new InvalidDataException("The primary returned an invalid acknowledgement.");
            }
        }
        catch (Exception exception)
        {
            connected?.TrySetException(exception);
            throw;
        }
    }

    private static string JsonEscape(string value) =>
        value.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal);

    private static ActivationRequest RequestWithSerializedPayloadLength(int targetLength)
    {
        const int componentCount = 42;
        const int maximumComponentLength = 200;
        var components = Enumerable.Range(0, ActivationProtocol.MaximumPathCount)
            .Select(_ => Enumerable.Repeat("a", componentCount).ToArray())
            .ToArray();

        string[] BuildPaths() => components
            .Select((parts, index) => $@"C:\p{index:D2}\{string.Join("\\", parts)}")
            .ToArray();

        var paths = BuildPaths();
        var remaining = targetLength - SerializedPayloadLength(paths);
        Assert.True(remaining >= 0, "The requested payload is smaller than the fixed schema fixture.");
        foreach (var pathComponents in components)
        {
            for (var index = 0; index < pathComponents.Length && remaining > 0; index++)
            {
                var added = Math.Min(maximumComponentLength - 1, remaining);
                pathComponents[index] += new string('a', added);
                remaining -= added;
            }
        }

        Assert.Equal(0, remaining);
        paths = BuildPaths();
        Assert.Equal(targetLength, SerializedPayloadLength(paths));
        return Request(paths);
    }

    private static int SerializedPayloadLength(IReadOnlyList<string> paths)
    {
        var escapedPaths = string.Join("\",\"", paths.Select(JsonEscape));
        var json = $$"""{"version":1,"kind":1,"paths":["{{escapedPaths}}"],"senderProcessId":42}""";
        return Encoding.UTF8.GetByteCount(json);
    }

    private sealed class StubActivationServer(
        Exception? startException = null,
        Exception? disposeException = null) : IActivationServer
    {
        public Task StartAsync(
            Func<ActivationRequest, CancellationToken, Task> handler,
            CancellationToken cancellationToken) =>
            startException is null ? Task.CompletedTask : Task.FromException(startException);

        public ValueTask DisposeAsync() =>
            disposeException is null ? ValueTask.CompletedTask : ValueTask.FromException(disposeException);
    }

    private sealed class StubActivationPathInspector(
        DriveType driveType,
        Func<string, FileAttributes?> inspect) : IActivationPathInspector
    {
        public DriveType GetDriveType(string rootPath) => driveType;

        public FileAttributes? GetExistingAttributes(string path) => inspect(path);
    }
}
