using System;

namespace EArm.Graph.Throttling;

/// <summary>
/// A token bucket that refills <see cref="Capacity"/> tokens per period, continuously, and starts full. <see cref="Reserve"/> takes the tokens at once
/// (the balance may go negative: the debt is what later requests wait for) and says how long the caller must wait before sending, so each request
/// waits exactly once and the order of reservations is the order of sending. Times are offsets on one monotonic clock. Not thread-safe: the owner
/// serialises access.
/// </summary>
internal sealed class TokenBucket
{
    private double _capacity;
    private double _perSecond;
    private double _tokens;
    private TimeSpan _updated;

    public TokenBucket(double capacity, TimeSpan period, TimeSpan now)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(period, TimeSpan.Zero);
        _capacity = capacity;
        _perSecond = capacity / period.TotalSeconds;
        _tokens = capacity;
        _updated = now;
    }

    public double Capacity => _capacity;

    /// <summary>The balance at <paramref name="now"/> (negative while reservations are waiting).</summary>
    public double Available(TimeSpan now)
    {
        Refill(now);
        return _tokens;
    }

    /// <summary>Takes <paramref name="amount"/> tokens and returns how long to wait until they are covered (zero when they were available).</summary>
    public TimeSpan Reserve(double amount, TimeSpan now)
    {
        Refill(now);
        _tokens -= amount;
        return Wait(_tokens);
    }

    /// <summary>Charges <paramref name="amount"/> more tokens (positive) or gives tokens back (negative); the balance never exceeds the capacity.</summary>
    public void Adjust(double amount, TimeSpan now)
    {
        Refill(now);
        _tokens = Math.Min(_capacity, _tokens - amount);
    }

    /// <summary>A new capacity (and so rate) over <paramref name="period"/>; the balance moves by the difference, as if the bucket had always had it.</summary>
    public void Resize(double capacity, TimeSpan period, TimeSpan now)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
        Refill(now);
        _tokens += capacity - _capacity;
        _capacity = capacity;
        _perSecond = capacity / period.TotalSeconds;
    }

    private TimeSpan Wait(double balance)
    {
        return balance >= 0 ? TimeSpan.Zero : TimeSpan.FromSeconds(-balance / _perSecond);
    }

    private void Refill(TimeSpan now)
    {
        if (now > _updated)
        {
            _tokens = Math.Min(_capacity, _tokens + ((now - _updated).TotalSeconds * _perSecond));
            _updated = now;
        }
    }
}
