/* In the name of God, the Merciful, the Compassionate */

#nullable enable

using System;
using System.Collections.Generic;
using Microsoft.Extensions.DependencyInjection;
using SQLTriage.Data.Services;
using SQLTriage.Data.Services.Licensing;
using Xunit;

namespace SQLTriage.Tests
{
    /// <summary>
    /// Guards the composition of the browser lane's container against the desktop's.
    ///
    /// ServerModeService.RegisterSharedSingletons calls AddSharedServices first — which registers
    /// a FRESH instance of every shared singleton — and then overrides the stateful ones with the
    /// running WPF app's instances. Anything it forgets to override silently becomes a second,
    /// divergent copy: the browser mutates one object while every other consumer reads another.
    ///
    /// IBundleAccessor is the case that bit: only the INTERFACE was forwarded, so the concrete
    /// BundleAccessor stayed fresh, and LicenseService — which takes the concrete type — activated
    /// a licence onto an orphan.
    /// </summary>
    public class CompositionParityTests
    {
        /// <summary>
        /// Minimal stand-in for the running WPF container. Returns an instance only for the types
        /// explicitly seeded; everything else resolves null, which is exactly what TryAdd&lt;T&gt;
        /// treats as "not present in the WPF container" and skips.
        /// </summary>
        private sealed class SeededProvider : IServiceProvider
        {
            private readonly Dictionary<Type, object> _instances = new();

            public SeededProvider Seed<T>(object instance)
            {
                _instances[typeof(T)] = instance;
                return this;
            }

            public object? GetService(Type serviceType) =>
                _instances.TryGetValue(serviceType, out var instance) ? instance : null;
        }

        [Fact]
        public void BrowserLane_ResolvesTheDesktopBundleAccessor_ThroughBothTheInterfaceAndTheConcreteType()
        {
            var desktopAccessor = new BundleAccessor();
            var wpf = new SeededProvider()
                .Seed<IBundleAccessor>(desktopAccessor)
                .Seed<BundleAccessor>(desktopAccessor);

            var services = new ServiceCollection();
            services.AddLogging();
            ServerModeService.RegisterSharedSingletons(services, wpf);

            using var browser = services.BuildServiceProvider();

            Assert.Same(desktopAccessor, browser.GetRequiredService<IBundleAccessor>());
            Assert.Same(desktopAccessor, browser.GetRequiredService<BundleAccessor>());
        }

        /// <summary>
        /// Control for the test above. With ONLY the interface forwarded — the shape this file was
        /// written for — the concrete registration from AddSharedServices still wins and the browser
        /// lane gets its own BundleAccessor. If this ever starts passing Assert.Same, the assertion
        /// above has stopped discriminating and is no longer evidence of anything.
        /// </summary>
        [Fact]
        public void ForwardingOnlyTheInterface_LeavesTheConcreteTypeAsASeparateInstance()
        {
            var desktopAccessor = new BundleAccessor();
            var wpf = new SeededProvider()
                .Seed<IBundleAccessor>(desktopAccessor);

            var services = new ServiceCollection();
            services.AddLogging();
            ServerModeService.RegisterSharedSingletons(services, wpf);

            using var browser = services.BuildServiceProvider();

            Assert.Same(desktopAccessor, browser.GetRequiredService<IBundleAccessor>());
            Assert.NotSame(desktopAccessor, browser.GetRequiredService<BundleAccessor>());
        }
    }
}
