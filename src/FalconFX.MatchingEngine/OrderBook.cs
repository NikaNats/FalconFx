using System.Runtime.CompilerServices;
using FalconFX.MatchingEngine.Models;

namespace FalconFX.MatchingEngine;

public delegate void TradeCallback(Trade trade);

public enum OrderResult
{
    Success,
    Rejected_PriceOutOfRange,
    Rejected_PoolExhausted
}

public sealed class OrderBook
{
    private readonly (int Head, int Tail)[] _asks;
    private readonly (int Head, int Tail)[] _bids;
    private readonly long _maxPrice;

    private readonly long _minPrice;
    private readonly OrderPool _pool;
    private readonly int _priceLevels;

    private int _askCount;

    // 🔥 O(1) Tracker-ები საუკეთესო ფასის ინდექსების მყისიერი პოვნისტვის
    private int _bestAskIndex;
    private int _bestBidIndex;
    private int _bidCount;

    public OrderBook(int poolSize) : this(90, 110, poolSize)
    {
    }

    public OrderBook(long minPrice = 90, long maxPrice = 110, int poolSize = 10_000_000)
    {
        _minPrice = minPrice;
        _maxPrice = maxPrice;
        _priceLevels = (int)(maxPrice - minPrice + 1);

        _asks = new (int Head, int Tail)[_priceLevels];
        _bids = new (int Head, int Tail)[_priceLevels];

        _pool = new OrderPool(poolSize);
        Clear();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public OrderResult ProcessOrder(Order incomingOrder, TradeCallback onTrade)
    {
        var priceIndex = (int)(incomingOrder.Price - _minPrice);

        if ((uint)priceIndex >= (uint)_priceLevels)
            return OrderResult.Rejected_PriceOutOfRange;

        var isBuy = incomingOrder.Side == OrderSide.Buy;
        var oppositeBook = isBuy ? _asks : _bids;
        var scaledPrice = incomingOrder.Price;

        // MATCHING LOOP
        while (incomingOrder.RemainingQuantity > 0 && (isBuy ? _askCount : _bidCount) > 0)
        {
            var bestIndex = isBuy ? _bestAskIndex : _bestBidIndex;
            if (bestIndex == -1) break;

            var bestPrice = bestIndex + _minPrice;

            var canMatch = isBuy
                ? bestPrice <= scaledPrice
                : bestPrice >= scaledPrice;

            if (!canMatch) break;

            var headIdx = oppositeBook[bestIndex].Head;
            ref var makerOrder = ref _pool.Get(headIdx);

            var tradeQuantity = Math.Min(incomingOrder.RemainingQuantity, makerOrder.Quantity);

            // Execute Trade Callback
            onTrade(new Trade(bestPrice, tradeQuantity, makerOrder.Id, incomingOrder.Id));

            incomingOrder.RemainingQuantity -= tradeQuantity;
            makerOrder.Quantity -= tradeQuantity;

            if (makerOrder.Quantity == 0)
            {
                RemoveNode(oppositeBook, bestIndex, headIdx, !isBuy);
                _pool.Return(headIdx);
            }
        }

        // ADD REMAINING TO BOOK
        if (incomingOrder.RemainingQuantity > 0)
            if (!AddToBook(incomingOrder, priceIndex))
                return OrderResult.Rejected_PoolExhausted;

        return OrderResult.Success;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool AddToBook(Order order, int priceIndex)
    {
        var isBuy = order.Side == OrderSide.Buy;
        var book = isBuy ? _bids : _asks;
        ref var level = ref book[priceIndex];

        var nodeIdx = _pool.Rent();
        if (nodeIdx == -1) return false;

        ref var node = ref _pool.Get(nodeIdx);
        node.Id = order.Id;
        node.Quantity = order.RemainingQuantity;
        node.Next = -1;
        node.Prev = -1;

        if (level.Head == -1)
        {
            level.Head = level.Tail = nodeIdx;

            if (isBuy)
            {
                _bidCount++;
                if (_bestBidIndex == -1 || priceIndex > _bestBidIndex)
                    _bestBidIndex = priceIndex; // განახლდეს უმაღლესი Bid
            }
            else
            {
                _askCount++;
                if (_bestAskIndex == -1 || priceIndex < _bestAskIndex)
                    _bestAskIndex = priceIndex; // განახლდეს უდაბლესი Ask
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
        ref var level = ref book[priceIndex];
        ref var node = ref _pool.Get(nodeIdx);

        var prev = node.Prev;
        var next = node.Next;

        if (prev != -1) _pool.Get(prev).Next = next;
        else level.Head = next;

        if (next != -1) _pool.Get(next).Prev = prev;
        else level.Tail = prev;

        // თუ ფასის დონე გაცარიელდა
        if (level.Head == -1)
        {
            if (isBid)
            {
                _bidCount--;
                if (priceIndex == _bestBidIndex)
                    UpdateBestBidIndex(); // ვიპოვოთ მომდევნო უმაღლესი Bid
            }
            else
            {
                _askCount--;
                if (priceIndex == _bestAskIndex)
                    UpdateBestAskIndex(); // ვიპოვოთ მომდევნო უდაბლესი Ask
            }
        }
    }

    private void UpdateBestAskIndex()
    {
        for (var i = _bestAskIndex + 1; i < _priceLevels; i++)
            if (_asks[i].Head != -1)
            {
                _bestAskIndex = i;
                return;
            }

        _bestAskIndex = -1;
    }

    private void UpdateBestBidIndex()
    {
        for (var i = _bestBidIndex - 1; i >= 0; i--)
            if (_bids[i].Head != -1)
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
        _bidCount = 0;
        _askCount = 0;
        _bestAskIndex = -1;
        _bestBidIndex = -1;
    }

    public (int BidCount, int AskCount) GetDepths()
    {
        return (_bidCount, _askCount);
    }
}