using System.Linq;
using EArm.Engine.State;
using Xunit;
using static EArm.Tests.Engine.Review1A;

namespace EArm.Tests.Engine;

/// <summary>Reverse dependency order of the mode pass: dependencies recorded with another letter case are still dependencies, and a repeated dependency counts once.</summary>
public sealed class Review4K_ModePassTests
{
    private static StateEntry Group(string name, params string[] dependsOn)
    {
        return Entry(GroupId(name), Fixture.GroupType, Gid(name.GetHashCode() & 0xffff), "uniqueName", name) with { DependsOn = dependsOn };
    }

    [Fact]
    public void ADependencyListedTwiceUnderDifferentCase_CountsOnce_SoTheDependencyIsNotLeftToTheCycleFallback()
    {
        // x-a depends on x-b twice (once spelled with another case). x-c is independent. x-a goes first, which frees x-b, which sorts before x-c.
        var a = Group("x-a", GroupId("x-b"), GroupId("X-B"));
        var b = Group("x-b");
        var c = Group("x-c");

        var order = ModePass.OrderForDeletion([c, b, a]).Select(e => e.Id.Id).ToList();

        Assert.Equal([GroupId("x-a"), GroupId("x-b"), GroupId("x-c")], order);
    }

    [Fact]
    public void ADependencySpelledWithAnotherCase_IsFoundAmongTheEntries_AndOrdersThem()
    {
        var a = Group("x-a", GroupId("X-B"));
        var b = Group("x-b");

        var order = ModePass.OrderForDeletion([b, a]).Select(e => e.Id.Id).ToList();

        Assert.Equal([GroupId("x-a"), GroupId("x-b")], order);
    }

    [Fact]
    public void ADependent_IsDeletedBeforeItsDependency_WhateverTheIdOrder()
    {
        var dependency = Group("a-first");
        var dependent = Group("z-last", GroupId("a-first"));

        var order = ModePass.OrderForDeletion([dependency, dependent]).Select(e => e.Id.Id).ToList();

        Assert.Equal([GroupId("z-last"), GroupId("a-first")], order);
    }

    [Fact]
    public void ADependencyOnItself_OrOnAnEntryOutsideTheSet_IsIgnored()
    {
        var a = Group("x-a", GroupId("X-A"), GroupId("not-in-the-set"));
        var b = Group("x-b");

        var order = ModePass.OrderForDeletion([b, a]).Select(e => e.Id.Id).ToList();

        Assert.Equal([GroupId("x-a"), GroupId("x-b")], order);
    }

    [Fact]
    public void ACycle_StillYieldsEveryEntry_InPhaseOrder()
    {
        var a = Group("x-a", GroupId("x-b"));
        var b = Group("x-b", GroupId("x-a"));

        var order = ModePass.OrderForDeletion([b, a]).Select(e => e.Id.Id).ToList();

        Assert.Equal(2, order.Count);
        Assert.Contains(GroupId("x-a"), order);
        Assert.Contains(GroupId("x-b"), order);
    }
}
