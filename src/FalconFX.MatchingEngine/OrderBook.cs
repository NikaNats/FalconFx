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
    private readonly OrderPool _pool;

    private readonly long _minPrice;
    private readonly long _maxPrice;
    private readonly int _priceLevels;

    private int _askCount;
    private int _bidCount;

    // 1. კონსტრუქტორი მხოლოდ Pool-ის ზომის მისათითებლად (ტესტებისთვის)
    public OrderBook(int poolSize) : this(90, 110, poolSize)
    {
    }

    // 2. სრული კონსტრუქტორი
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

    public OrderResult ProcessOrder(Order incomingOrder, TradeCallback onTrade)
    {
        var priceIndex = (int)(incomingOrder.Price - _minPrice);

        if (priceIndex < 0 || priceIndex >= _priceLevels)
            return OrderResult.Rejected_PriceOutOfRange;

        var oppositeBook = incomingOrder.Side == OrderSide.Buy ? _asks : _bids;
        var scaledPrice = (long)incomingOrder.Price;

        while (incomingOrder.RemainingQuantity > 0 && (incomingOrder.Side == OrderSide.Buy ? _askCount : _bidCount) > 0)
        {
            var bestIndex = FindBestPriceIndex(incomingOrder.Side);
            if (bestIndex == -1) break;

            long bestPrice = bestIndex + _minPrice;

            bool canMatch = incomingOrder.Side == OrderSide.Buy
                ? bestPrice <= scaledPrice
                : bestPrice >= scaledPrice;

            if (!canMatch) break;

            var headIdx = oppositeBook[bestIndex].Head;
            ref var makerOrder = ref _pool.Get(headIdx);

            var tradeQuantity = Math.Min((long)incomingOrder.RemainingQuantity, makerOrder.Quantity);

            onTrade(new Trade(bestPrice, tradeQuantity, makerOrder.Id, incomingOrder.Id));

            incomingOrder.RemainingQuantity -= tradeQuantity;
            makerOrder.Quantity -= tradeQuantity;

            if (makerOrder.Quantity == 0)
            {
                RemoveNode(oppositeBook, bestIndex, headIdx);
                _pool.Return(headIdx);
            }
        }

        if (incomingOrder.RemainingQuantity > 0)
        {
            if (!AddToBook(incomingOrder, priceIndex))
                return OrderResult.Rejected_PoolExhausted;
        }

        return OrderResult.Success;
    }

    private int FindBestPriceIndex(OrderSide side)
    {
        if (side == OrderSide.Buy)
        {
            for (var i = 0; i < _priceLevels; i++)
                if (_asks[i].Head != -1) return i;
        }
        else
        {
            for (var i = _priceLevels - 1; i >= 0; i--)
                if (_bids[i].Head != -1) return i;
        }
        return -1;
    }

    private bool AddToBook(Order order, int priceIndex)
    {
        var book = order.Side == OrderSide.Buy ? _bids : _asks;
        ref var level = ref book[priceIndex];

        var nodeIdx = _pool.Rent();
        if (nodeIdx == -1) return false;

        ref var node = ref _pool.Get(nodeIdx);
        node.Id = order.Id;
        node.Quantity = (long)order.RemainingQuantity;
        node.Next = -1;
        node.Prev = -1;

        if (level.Head == -1)
        {
            level.Head = level.Tail = nodeIdx;
            if (book == _bids) _bidCount++;
            else _askCount++;
        }
        else
        {
            _pool.Get(level.Tail).Next = nodeIdx;
            node.Prev = level.Tail;
            level.Tail = nodeIdx;
        }

        return true;
    }

    private void RemoveNode((int Head, int Tail)[] book, int priceIndex, int nodeIdx)
    {
        ref var level = ref book[priceIndex];
        ref var node = ref _pool.Get(nodeIdx);

        var prev = node.Prev;
        var next = node.Next;

        if (prev != -1) _pool.Get(prev).Next = next;
        else level.Head = next;

        if (next != -1) _pool.Get(next).Prev = prev;
        else level.Tail = prev;

        if (level.Head == -1)
        {
            if (book == _bids) _bidCount--;
            else _askCount--;
        }
    }

    public void Clear()
    {
        _pool.Reset();
        Array.Fill(_bids, (-1, -1));
        Array.Fill(_asks, (-1, -1));
        _bidCount = 0;
        _askCount = 0;
    }

    // 3. აღდგენილი მეთოდი ტესტებისთვის!
    public (int BidCount, int AskCount) GetDepths()
    {
        return (_bidCount, _askCount);
    }
}