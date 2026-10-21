using System;
using EArm.Graph.Throttling;
using Xunit;

namespace EArm.Graph.Tests;

/// <summary><see cref="TokenBucket"/>: starts full, refills continuously, lets a reservation go into debt and says how long it waits, refunds, resizes.</summary>
public sealed class TokenBucketTests
{
    private static readonly TimeSpan Period = TimeSpan.FromSeconds(10);

    private static TimeSpan At(double seconds)
    {
        return TimeSpan.FromSeconds(seconds);
    }

    [Fact]
    public void ABucketStartsFull()
    {
        var bucket = new TokenBucket(3_500, Period, At(100));

        Assert.Equal(3_500, bucket.Capacity);
        Assert.Equal(3_500, bucket.Available(At(100)));
        Assert.Equal(TimeSpan.Zero, bucket.Reserve(3_500, At(100)));
        Assert.Equal(0, bucket.Available(At(100)));
    }

    [Fact]
    public void ABucketRefillsContinuouslyUpToItsCapacity()
    {
        var bucket = new TokenBucket(10, Period, At(0));
        bucket.Reserve(10, At(0));

        Assert.Equal(2.5, bucket.Available(At(2.5)), 9);
        Assert.Equal(7, bucket.Available(At(7)), 9);
        Assert.Equal(10, bucket.Available(At(10)), 9);
        Assert.Equal(10, bucket.Available(At(3_600)), 9);
    }

    [Fact]
    public void AnEarlierTimeNeverRefillsOrDrains()
    {
        var bucket = new TokenBucket(10, Period, At(5));
        bucket.Reserve(4, At(5));

        Assert.Equal(6, bucket.Available(At(1)), 9);
        Assert.Equal(6, bucket.Available(At(5)), 9);
    }

    [Fact]
    public void AReservationGoesIntoDebtAndWaitsUntilItIsCovered()
    {
        var bucket = new TokenBucket(10, Period, At(0));

        Assert.Equal(TimeSpan.Zero, bucket.Reserve(10, At(0)));
        Assert.Equal(At(3), bucket.Reserve(3, At(0)));

        // The debt accumulates: the next reservation waits behind the earlier one.
        Assert.Equal(At(5), bucket.Reserve(2, At(0)));
        Assert.Equal(-5, bucket.Available(At(0)), 9);

        // Half-way through the debt the remaining wait is half.
        Assert.Equal(3.5, bucket.Reserve(1, At(2.5)).TotalSeconds, 6);
    }

    [Fact]
    public void AdjustChargesMoreOrGivesBackButNeverAboveTheCapacity()
    {
        var bucket = new TokenBucket(10, Period, At(0));

        bucket.Adjust(4, At(0));
        Assert.Equal(6, bucket.Available(At(0)), 9);

        bucket.Adjust(-2, At(0));
        Assert.Equal(8, bucket.Available(At(0)), 9);

        bucket.Adjust(-100, At(0));
        Assert.Equal(10, bucket.Available(At(0)), 9);

        bucket.Adjust(25, At(0));
        Assert.Equal(-15, bucket.Available(At(0)), 9);
        Assert.Equal(At(16), bucket.Reserve(1, At(0)));
    }

    [Fact]
    public void ResizeMovesTheBalanceByTheDifferenceAndChangesTheRate()
    {
        var bucket = new TokenBucket(3_500, Period, At(0));
        bucket.Reserve(3_500, At(0));

        bucket.Resize(8_000, Period, At(0));

        Assert.Equal(8_000, bucket.Capacity);
        Assert.Equal(4_500, bucket.Available(At(0)), 9);
        Assert.Equal(TimeSpan.Zero, bucket.Reserve(4_500, At(0)));

        // 800 units per second now.
        Assert.Equal(At(1), bucket.Reserve(800, At(0)));
        Assert.Equal(8_000, bucket.Available(At(60)), 9);
    }

    [Fact]
    public void ResizingDownCanPutTheBalanceIntoDebt()
    {
        var bucket = new TokenBucket(8_000, Period, At(0));
        bucket.Reserve(6_000, At(0));

        bucket.Resize(3_500, Period, At(0));

        Assert.Equal(-2_500, bucket.Available(At(0)), 9);
        Assert.Equal(At(2_850d / 350), bucket.Reserve(350, At(0)));
    }

    [Fact]
    public void AReservationGivenBackAfterACancellationLeavesTheBalanceAsItWas()
    {
        var bucket = new TokenBucket(10, Period, At(0));
        bucket.Reserve(10, At(0));

        var wait = bucket.Reserve(5, At(0));
        bucket.Adjust(-5, At(0));

        Assert.Equal(At(5), wait);
        Assert.Equal(0, bucket.Available(At(0)), 9);
        Assert.Equal(At(5), bucket.Reserve(5, At(0)));
    }

    [Fact]
    public void TheArgumentsAreValidated()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new TokenBucket(0, Period, At(0)));
        Assert.Throws<ArgumentOutOfRangeException>(() => new TokenBucket(10, TimeSpan.Zero, At(0)));
        var bucket = new TokenBucket(10, Period, At(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => bucket.Resize(-1, Period, At(0)));
    }
}
