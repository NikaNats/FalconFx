using MatchingEngine.Models;

namespace MatchingEngine;

// დელეგატი, რომელსაც გამოვიძახებთ გარიგების დროს (Alloc-Free)
public delegate void TradeCallback(Trade trade);

public class OrderBook
{
    private readonly (int, int)[] _asks = new (int, int)[21];

    // ფასი -> (Head Index, Tail Index) - Array for 90-110 prices
    private readonly (int head, int tail)[] _bids = new (int, int)[21];
    private readonly OrderPool _pool;
    private int _askCount;
    private int _bidCount;

    public OrderBook(int poolSize = 1000000)
    {
        _pool = new OrderPool(poolSize);
        Clear(); // Initialize
    }

    public void ProcessOrder(Order incomingOrder, TradeCallback onTrade)
    {
        var priceIndex = (int)(incomingOrder.Price - 90);
        
        // Bounds checking to prevent crashes
        if (priceIndex < 0 || priceIndex >= 21)
        {
            // Reject order - price out of range
            return;
        }
        
        var oppositeBook = incomingOrder.Side == OrderSide.Buy ? _asks : _bids;
        var scaledPrice = (long)incomingOrder.Price;

        while (incomingOrder.RemainingQuantity > 0 && (incomingOrder.Side == OrderSide.Buy ? _askCount : _bidCount) > 0)
        {
            // Find best price index
            var bestIndex = -1;
            if (incomingOrder.Side == OrderSide.Buy)
            {
                for (var i = 0; i < 21; i++)
                    if (_asks[i].Item1 != -1)
                    {
                        bestIndex = i;
                        break;
                    }
            }
            else
            {
                for (var i = 20; i >= 0; i--)
                    if (_bids[i].head != -1)
                    {
                        bestIndex = i;
                        break;
                    }
            }

            if (bestIndex == -1) break;

            long bestPrice = bestIndex + 90;
            var headIdx = oppositeBook[bestIndex].Item1;

            var canMatch = incomingOrder.Side == OrderSide.Buy
                ? bestPrice <= scaledPrice
                : bestPrice >= scaledPrice;

            if (!canMatch) break;

            // Get first order in queue
            ref var makerOrder = ref _pool.Get(headIdx);

            var tradeQuantity = Math.Min((long)incomingOrder.RemainingQuantity, makerOrder.Quantity);

            var trade = new Trade(
                bestPrice,
                tradeQuantity,
                makerOrder.Id,
                incomingOrder.Id
            );

            onTrade(trade);

            incomingOrder.RemainingQuantity -= tradeQuantity;
            makerOrder.Quantity -= tradeQuantity;

            if (makerOrder.Quantity == 0)
            {
                // Remove from list
                RemoveNode(oppositeBook, bestIndex, headIdx);
                _pool.Return(headIdx);
            }
        }

        if (incomingOrder.RemainingQuantity > 0) AddToBook(incomingOrder, priceIndex);
    }

    private void AddToBook(Order order, int priceIndex)
    {
        var book = order.Side == OrderSide.Buy ? _bids : _asks;
        ref var level = ref book[priceIndex];

        var nodeIdx = _pool.Rent();
        ref var node = ref _pool.Get(nodeIdx);
        node.Id = order.Id;
        node.Quantity = (long)order.RemainingQuantity;
        node.Next = -1;
        node.Prev = -1;

        if (level.Item1 == -1)
        {
            level.Item1 = level.Item2 = nodeIdx;
            if (book == _bids) _bidCount++;
            else _askCount++;
        }
        else
        {
            _pool.Get(level.Item2).Next = nodeIdx;
            node.Prev = level.Item2;
            level.Item2 = nodeIdx;
        }
    }

    private void RemoveNode((int, int)[] book, int priceIndex, int nodeIdx)
    {
        ref var level = ref book[priceIndex];
        ref var node = ref _pool.Get(nodeIdx);
        var prev = node.Prev;
        var next = node.Next;

        if (prev != -1)
            _pool.Get(prev).Next = next;
        else
            // Was head
            level.Item1 = next;

        if (next != -1)
            _pool.Get(next).Prev = prev;
        else
            // Was tail
            level.Item2 = prev;

        if (level.Item1 == -1)
        {
            if (book == _bids) _bidCount--;
            else _askCount--;
        }
    }

    public void Clear()
    {
        _pool.Reset();
        for (var i = 0; i < 21; i++)
        {
            _bids[i] = (-1, -1);
            _asks[i] = (-1, -1);
        }

        _bidCount = 0;
        _askCount = 0;
    }

    public (int BidCount, int AskCount) GetDepths()
    {
        return (_bidCount, _askCount);
    }
}