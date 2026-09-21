/* In the name of God, the Merciful, the Compassionate */

#nullable enable

using System.Collections.Generic;
using SQLTriage.Data.Models;
using SQLTriage.Data.Services;
using Xunit;

namespace SQLTriage.Tests;

/// <summary>
/// The distinct-union target selection behind /server-configuration's multi-instance picker (2026-08-20
/// item 5): the union of every enabled connection's server list, deduplicated so one instance never
/// appears twice even when two connection profiles both name it.
/// </summary>
public sealed class MultiInstanceTargetResolverTests
{
    private static ServerConnection Conn(string id, string serverNames) =>
        new() { Id = id, ServerNames = serverNames };

    [Fact]
    public void ResolveDistinctTargets_EmptyInput_ReturnsEmpty()
    {
        var result = MultiInstanceTargetResolver.ResolveDistinctTargets(System.Array.Empty<ServerConnection>());
        Assert.Empty(result);
    }

    [Fact]
    public void ResolveDistinctTargets_UnionsAcrossConnections_PreservingFirstSeenOrder()
    {
        var connections = new[]
        {
            Conn("c1", "SRV1;SRV2"),
            Conn("c2", "SRV3"),
        };

        var result = MultiInstanceTargetResolver.ResolveDistinctTargets(connections);

        Assert.Equal(new[] { "SRV1", "SRV2", "SRV3" }, System.Linq.Enumerable.Select(result, t => t.ServerName));
    }

    [Fact]
    public void ResolveDistinctTargets_DedupsAServerNamedByTwoConnections_KeepingTheFirstOwner()
    {
        var c1 = Conn("c1", "SHARED-SRV");
        var c2 = Conn("c2", "SHARED-SRV");

        var result = MultiInstanceTargetResolver.ResolveDistinctTargets(new[] { c1, c2 });

        var target = Assert.Single(result);
        Assert.Equal("SHARED-SRV", target.ServerName);
        Assert.Same(c1, target.Owner);   // first connection to name it owns it, not the last
    }

    [Fact]
    public void ResolveDistinctTargets_IsCaseInsensitiveOnServerName()
    {
        var c1 = Conn("c1", "sql01");
        var c2 = Conn("c2", "SQL01");

        var result = MultiInstanceTargetResolver.ResolveDistinctTargets(new[] { c1, c2 });

        Assert.Single(result);
    }

    [Fact]
    public void ResolveDistinctTargets_WithinOneConnectionsOwnList_AlsoDedups()
    {
        // GetServerList itself can carry a repeat if an operator pastes the same host twice into
        // one profile's ServerNames field — the union must not surface it twice either.
        var connection = Conn("c1", "SRV1;SRV1;SRV2");

        var result = MultiInstanceTargetResolver.ResolveDistinctTargets(new[] { connection });

        Assert.Equal(new[] { "SRV1", "SRV2" }, System.Linq.Enumerable.Select(result, t => t.ServerName));
    }

    [Fact]
    public void ResolveDistinctTargets_SkipsAConnectionWithNoServers()
    {
        var empty = Conn("c1", "");
        var real = Conn("c2", "SRV1");

        var result = MultiInstanceTargetResolver.ResolveDistinctTargets(new[] { empty, real });

        var target = Assert.Single(result);
        Assert.Equal("SRV1", target.ServerName);
    }
}
