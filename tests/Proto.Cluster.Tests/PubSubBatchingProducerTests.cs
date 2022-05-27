﻿// -----------------------------------------------------------------------
// <copyright file = "PubSubTests.cs" company = "Asynkron AB">
//      Copyright (C) 2015-2022 Asynkron AB All rights reserved
// </copyright>
// -----------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Proto.Cluster.PubSub;
using Xunit;

namespace Proto.Cluster.Tests;

[SuppressMessage("ReSharper", "AccessToDisposedClosure")]
public class PubSubBatchingProducerTests
{
    private readonly List<PublisherBatchMessage> _batchesSent = new();

    private Task Record(PublisherBatchMessage batch)
    {
        _batchesSent.Add(batch);
        return Task.CompletedTask;
    }

    private Task Fail(PublisherBatchMessage _) => throw new TestException();

    private Task Wait(PublisherBatchMessage _) => Task.Delay(1000);

    private async Task WaitThenFail(PublisherBatchMessage _)
    {
        await Task.Delay(500);
        throw new TestException();
    }

    public class MockPublisher : IPublisher
    {
        private readonly Func<PublisherBatchMessage, Task> _publish;

        public MockPublisher(Func<PublisherBatchMessage, Task> publish) => _publish = publish;

        public async Task<PublishResponse> PublishBatch(string topic, PublisherBatchMessage batch, CancellationToken ct = default)
        {
             await _publish(batch);
             return new PublishResponse();
        }
    }

    private record TestMessage(int Number);

    [SuppressMessage("Design", "CA1064:Exceptions should be public")]
    private class TestException : Exception
    {
    }

    [Fact]
    public async Task Producer_sends_messages_in_batches()
    {
        await using var producer = new BatchingProducer(new MockPublisher(Record), "topic", 10);

        var tasks = Enumerable.Range(1, 10000)
            .Select(i => producer.ProduceAsync(new TestMessage(i)))
            .ToList();

        await Task.WhenAll(tasks);

        _batchesSent.Any(b => b.Envelopes.Count > 1).Should().BeTrue("messages should be batched");
        _batchesSent.All(b => b.Envelopes.Count <= 10).Should().BeTrue("batches should not exceed configured size");

        var allSentNumbers = _batchesSent
            .SelectMany(b => b.Envelopes)
            .Cast<TestMessage>()
            .Select(m => m.Number)
            .OrderBy(n => n)
            .ToArray();
        allSentNumbers.Should().Equal(Enumerable.Range(1, 10000), "all messages should be sent");
    }

    [Fact]
    public async Task Publishing_through_stopped_producer_throws()
    {
        var producer = new BatchingProducer(new MockPublisher(Record), "topic", 10);
        await producer.DisposeAsync();

        var sutAction = () => producer.ProduceAsync(new TestMessage(1));
        await sutAction.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task All_pending_tasks_complete_when_producer_is_stopped()
    {
        var producer = new BatchingProducer(new MockPublisher(Wait), "topic", 5);
        var tasks = Enumerable.Range(1, 100).Select(i => producer.ProduceAsync(new TestMessage(i))).ToArray();

        await producer.DisposeAsync();

        // the first batch might complete processing, so we skip it
        tasks.Skip(5).All(t => t.IsCompleted).Should().BeTrue("all pending messages should complete");
        tasks.Skip(5).All(t => t.IsCanceled).Should().BeTrue("all pending tasks should have status canceled");
    }

    [Fact]
    public async Task All_pending_tasks_complete_when_producer_fails()
    {
        var producer = new BatchingProducer(new MockPublisher(WaitThenFail), "topic", 5);
        var tasks = Enumerable.Range(1, 100).Select(i => producer.ProduceAsync(new TestMessage(i))).ToArray();

        try
        {
            await Task.WhenAll(tasks);
        }
        catch (TestException)
        {
            // this is expected since Task.WhenAll will rethrow
        }

        tasks.All(t => t.IsCompleted).Should().BeTrue("all pending messages should complete");
        tasks.All(t => t.IsFaulted).Should().BeTrue("all pending tasks should have status faulted");
    }

    [Fact]
    public async Task Publishing_through_failed_producer_throws()
    {
        await using var producer = new BatchingProducer(new MockPublisher(Fail), "topic", 10);
        var sutAction = () => producer.ProduceAsync(new TestMessage(1));
        await sutAction.Should().ThrowAsync<TestException>(); // here we get the exception thrown during publish

        sutAction = () => producer.ProduceAsync(new TestMessage(1));
        await sutAction.Should()
            .ThrowAsync<InvalidOperationException>(); // we get InvalidOperationException because we can no longer produce new messages
    }

    [Fact]
    public async Task Throws_when_queue_full()
    {
        await using var producer = new PubSub.BatchingProducer(new MockPublisher(Wait), "topic", 1, 10);

        var sutAction = () => {
            for (var i = 0; i < 20; i++)
            {
                _ = producer.ProduceAsync(new TestMessage(i));
            }
        };

        sutAction.Should().Throw<ProducerQueueFullException>();
    }
}