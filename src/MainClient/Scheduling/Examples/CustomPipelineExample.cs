namespace TrafficVista.Scheduling.Examples;

/// <summary>Runnable, local-only example of supplying external producer/consumer methods.</summary>
public static class CustomPipelineExample
{
    public sealed record WorkItem(int Id, string Payload);

    public static async Task<SchedulerSnapshot> RunAsync(CancellationToken token = default)
    {
        // Per-instance business state belongs to the caller, not the scheduler.
        var nextId = 0;

        Task<IReadOnlyList<WorkItem>> ProduceAsync(CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            if (nextId >= 100) return Task.FromResult<IReadOnlyList<WorkItem>>([]);
            var batch = Enumerable.Range(nextId, Math.Min(10, 100 - nextId))
                .Select(id => new WorkItem(id, $"payload-{id}")).ToArray();
            nextId += batch.Length;
            return Task.FromResult<IReadOnlyList<WorkItem>>(batch);
        }

        async Task<bool> ConsumeAsync(WorkItem item, CancellationToken ct)
        {
            await Task.Delay(1, ct).ConfigureAwait(false); // Replace with your asynchronous business operation.
            return item.Id >= 0;
        }

        await using var scheduler = AsyncTaskScheduler<WorkItem>.FromMethods(ProduceAsync, ConsumeAsync);
        await scheduler.StartAsync(new SchedulerOptions
        {
            WorkerCount = 8,
            QueueCapacity = 32,
            PollInterval = TimeSpan.Zero,
            CompleteWhenSourceEmpty = true
        }, token).ConfigureAwait(false);
        return await scheduler.Completion.ConfigureAwait(false);
    }
}
