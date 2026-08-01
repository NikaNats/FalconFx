using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using FalconFX.MatchingEngine.Models;

namespace FalconFX.MatchingEngine;

public delegate void TradeCallback(Trade trade);

public enum OrderResult : byte
{
    Success = 0,
    Rejected_PriceOutOfRange = 1,
    Rejected_PoolExhausted = 2
}

/// <summary>
///     Ultra-low latency, zero-allocation L2 Order Book implementation.
///     Uses intrusive doubly-linked list structures embedded in pre-allocated OrderPool arrays,
///     achieving O(1) order insertion, cancellation, and execution without GC pressure.
/// </summary>
public sealed class OrderBook
{
    // Price level heads/tails stored in dense arrays for O(1) indexing
    private readonly (int Head, int Tail)[] _asks;
    private readonly (int Head, int Tail)[] _bids;
    private readonly long _maxPrice;

    private readonly long _minPrice;

    private readonly OrderPool _pool;
    private readonly int _priceLevels;
    private int _askLevelCount;
    private int _bestAskIndex;

    // O(1) Index Trackers for Best Bid and Best Ask
    private int _bestBidIndex;

    // Trackers for active non-empty price level counts
    private int _bidLevelCount;

    /// <summary>
    ///     Initializes an OrderBook supporting price levels from minPrice to maxPrice.
    ///     Default price range supports simulation ticks [90..110].
    /// </summary>
    public OrderBook(int poolSize = 10_000_000)
        : this(90, 110, poolSize)
    {
    }

    public OrderBook(long minPrice, long maxPrice, int poolSize = 10_000_000)
    {
        if (minPrice >= maxPrice)
            throw new ArgumentOutOfRangeException(nameof(minPrice), "minPrice must be less than maxPrice.");

        _minPrice = minPrice;
        _maxPrice = maxPrice;
        _priceLevels = (int)(maxPrice - minPrice + 1);

        _asks = new (int Head, int Tail)[_priceLevels];
        _bids = new (int Head, int Tail)[_priceLevels];

        _pool = new OrderPool(poolSize);
        Clear();
    }

    /// <summary>
    ///     Processes an incoming order against the order book.
    ///     Matches opposite orders or adds remaining quantity to book. Zero heap allocations.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public OrderResult ProcessOrder(Order incomingOrder, TradeCallback onTrade)
    {
        var priceIndex = (int)(incomingOrder.Price - _minPrice);

        // Fast unsigned range check (0 <= priceIndex < _priceLevels)
        if ((uint)priceIndex >= (uint)_priceLevels)
            return OrderResult.Rejected_PriceOutOfRange;

        var isBuy = incomingOrder.Side == OrderSide.Buy;
        var oppositeBook = isBuy ? _asks : _bids;
        var scaledPrice = incomingOrder.Price;

        while (incomingOrder.RemainingQuantity > 0 && (isBuy ? _askLevelCount : _bidLevelCount) > 0)
        {
            var bestIndex = isBuy ? _bestAskIndex : _bestBidIndex;
            if (bestIndex == -1) break;

            var bestPrice = bestIndex + _minPrice;

            var canMatch = isBuy
                ? bestPrice <= scaledPrice
                : bestPrice >= scaledPrice;

            if (!canMatch) break;

            // Direct Unsafe array lookup bypasses bounds checks on hot path
            var headIdx = Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(oppositeBook), bestIndex).Head;
            ref var makerOrder = ref _pool.Get(headIdx);

            var tradeQuantity = Math.Min(incomingOrder.RemainingQuantity, makerOrder.Quantity);

            // Trigger Trade Execution Callback
            onTrade(new Trade(bestPrice, tradeQuantity, makerOrder.Id, incomingOrder.Id));

            incomingOrder.RemainingQuantity -= tradeQuantity;
            makerOrder.Quantity -= tradeQuantity;

            // If Maker Order fully filled -> Remove node from list and return index to pool
            if (makerOrder.Quantity == 0)
            {
                RemoveNode(oppositeBook, bestIndex, headIdx, !isBuy);
                _pool.Return(headIdx);
            }
        }

        if (incomingOrder.RemainingQuantity > 0)
            if (!AddToBook(in incomingOrder, priceIndex))
                return OrderResult.Rejected_PoolExhausted;

        return OrderResult.Success;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool AddToBook(in Order order, int priceIndex)
    {
        var isBuy = order.Side == OrderSide.Buy;
        var book = isBuy ? _bids : _asks;

        ref var level = ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(book), priceIndex);

        var nodeIdx = _pool.Rent();
        if (nodeIdx == -1) return false;

        // Initialize node in struct pool (all fields populated)
        ref var node = ref _pool.Get(nodeIdx);
        node.Id = order.Id;
        node.Price = order.Price;
        node.Quantity = order.RemainingQuantity;
        node.Side = (byte)order.Side;
        node.Next = -1;
        node.Prev = -1;

        if (level.Head == -1)
        {
            level.Head = level.Tail = nodeIdx;

            if (isBuy)
            {
                _bidLevelCount++;
                if (_bestBidIndex == -1 || priceIndex > _bestBidIndex)
                    _bestBidIndex = priceIndex; // Update highest bid
            }
            else
            {
                _askLevelCount++;
                if (_bestAskIndex == -1 || priceIndex < _bestAskIndex)
                    _bestAskIndex = priceIndex; // Update lowest ask
            }
        }
        else
        {
            _pool.Get(level.Tail).Next = nodeIdx;
            node.Prev = level.Tail;
            level.Tail = nodeIdx;
        }

        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void RemoveNode((int Head, int Tail)[] book, int priceIndex, int nodeIdx, bool isBid)
    {
        ref var level = ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(book), priceIndex);
        ref var node = ref _pool.Get(nodeIdx);

        var prev = node.Prev;
        var next = node.Next;

        if (prev != -1) _pool.Get(prev).Next = next;
        else level.Head = next;

        if (next != -1) _pool.Get(next).Prev = prev;
        else level.Tail = prev;

        // If price level emptied, update trackers
        if (level.Head == -1)
        {
            if (isBid)
            {
                _bidLevelCount--;
                if (priceIndex == _bestBidIndex)
                    UpdateBestBidIndex();
            }
            else
            {
                _askLevelCount--;
                if (priceIndex == _bestAskIndex)
                    UpdateBestAskIndex();
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void UpdateBestAskIndex()
    {
        for (var i = _bestAskIndex + 1; i < _priceLevels; i++)
            if (Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(_asks), i).Head != -1)
            {
                _bestAskIndex = i;
                return;
            }

        _bestAskIndex = -1;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void UpdateBestBidIndex()
    {
        for (var i = _bestBidIndex - 1; i >= 0; i--)
            if (Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(_bids), i).Head != -1)
            {
                _bestBidIndex = i;
                return;
            }

        _bestBidIndex = -1;
    }

    public void Clear()
    {
        _pool.Reset();
        Array.Fill(_bids, (-1, -1));
        Array.Fill(_asks, (-1, -1));
        _bidLevelCount = 0;
        _askLevelCount = 0;
        _bestAskIndex = -1;
        _bestBidIndex = -1;
    }

    /// <summary>
    ///     Returns active price level depths for Bids and Asks.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public (int BidCount, int AskCount) GetDepths()
    {
        return (_bidLevelCount, _askLevelCount);
    }

    /// <summary>
    ///     Returns the best Bid price currently in the order book (-1 if empty).
    /// </summary>
    public long GetBestBidPrice()
    {
        return _bestBidIndex == -1 ? -1 : _bestBidIndex + _minPrice;
    }

    /// <summary>
    ///     Returns the best Ask price currently in the order book (-1 if empty).
    /// </summary>
    public long GetBestAskPrice()
    {
        return _bestAskIndex == -1 ? -1 : _bestAskIndex + _minPrice;
    }
}