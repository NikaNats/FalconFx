using Confluent.Kafka;
using FalconFX.Protos;

namespace FalconFX.Benchmarks;

/// <summary>
///     Allocation-free stand-in for <see cref="IProducer{TKey,TValue}" />.
///     NSubstitute intercepts every Produce call through a generated proxy (reflection,
///     allocation, call handlers), which dominates a 250k-trade benchmark and measures
///     the mocking framework instead of the pipeline. This fake simply counts calls.
/// </summary>
internal sealed class CountingProducer(Action onProduced) : IProducer<long, TradeExecuted>
{
    public long ProducedCount { get; private set; }

    public void Produce(string topic, Message<long, TradeExecuted> message,
        Action<DeliveryReport<long, TradeExecuted>>? deliveryHandler = null)
    {
        ProducedCount++;
        onProduced();
    }

    public void Produce(TopicPartition topicPartition, Message<long, TradeExecuted> message,
        Action<DeliveryReport<long, TradeExecuted>>? deliveryHandler = null)
    {
        ProducedCount++;
        onProduced();
    }

    public Task<DeliveryResult<long, TradeExecuted>> ProduceAsync(string topic,
        Message<long, TradeExecuted> message, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public Task<DeliveryResult<long, TradeExecuted>> ProduceAsync(TopicPartition topicPartition,
        Message<long, TradeExecuted> message, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public int Poll(TimeSpan timeout) => 0;

    public int Flush(TimeSpan timeout) => 0;

    public void Flush(CancellationToken cancellationToken = default)
    {
    }

    public void InitTransactions(TimeSpan timeout)
    {
    }

    public void BeginTransaction()
    {
    }

    public void CommitTransaction(TimeSpan timeout)
    {
    }

    public void CommitTransaction()
    {
    }

    public void AbortTransaction(TimeSpan timeout)
    {
    }

    public void AbortTransaction()
    {
    }

    public void SendOffsetsToTransaction(IEnumerable<TopicPartitionOffset> offsets,
        IConsumerGroupMetadata groupMetadata, TimeSpan timeout)
    {
    }

    public int AddBrokers(string brokers) => 0;

    public void SetSaslCredentials(string username, string password)
    {
    }

    public Handle Handle => throw new NotSupportedException();

    public string Name => "counting-producer";

    public void Dispose()
    {
    }
}
