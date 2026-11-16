using Confluent.Kafka;

namespace FalconFX.MatchingEngine.Models;

/// <summary>
///     Carries an inbound order together with its originating Kafka offset so the
///     consumer can store (and therefore commit) the offset only after the engine has
///     matched the order and the resulting trades were handed to the producer.
/// </summary>
/// <remarks>
///     A null <see cref="Order" /> is a poison-pill skip marker. It must travel through
///     the same FIFO channel as real orders so its offset is released only after every
///     earlier order has been processed; storing it directly would let a crash commit
///     past messages that are still queued.
/// </remarks>
public readonly record struct OrderEnvelope(
    Order? Order,
    TopicPartitionOffset? KafkaOffset = null)
{
    public static OrderEnvelope Skip(TopicPartitionOffset offset) => new(null, offset);
}
