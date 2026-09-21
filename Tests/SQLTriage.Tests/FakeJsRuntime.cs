/* In the name of God, the Merciful, the Compassionate */

using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.JSInterop;

namespace SQLTriage.Tests
{
    /// <summary>
    /// A recording <see cref="IJSRuntime"/> for component render and handler tests.
    ///
    /// <para>Added 2026-09-01 for ruling R2(a): DataGrid now injects IJSRuntime for its per-action
    /// confirmation, and a Blazor component cannot be rendered at all when an injected service is
    /// missing from the provider — the three DataGrid render suites failed with "Cannot provide a
    /// value for property 'JS'" the moment the gate landed. So the fixtures supply one.</para>
    ///
    /// <para>It is a RECORDER, not a null object. <see cref="Answers"/> lets a test say what
    /// <c>confirm</c> returns, and <see cref="Calls"/> records what was asked — which is what makes
    /// it possible to prove that the confirmation actually gates the write rather than merely being
    /// displayed. A no-op stub would have let a confirm-and-ignore-the-answer implementation pass.</para>
    /// </summary>
    internal sealed class FakeJsRuntime : IJSRuntime
    {
        /// <summary>Every interop call, as (identifier, args).</summary>
        public List<(string Identifier, object?[]? Args)> Calls { get; } = new();

        /// <summary>
        /// Answer per JS identifier. An identifier with no entry returns the type's default —
        /// which for <c>confirm</c> means "not confirmed", the safe direction for a write gate.
        /// </summary>
        public Dictionary<string, object?> Answers { get; } = new(StringComparer.Ordinal);

        /// <summary>When set, every call throws it — the "no interop available" case.</summary>
        public Exception? ThrowOnInvoke { get; set; }

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args)
        {
            Calls.Add((identifier, args));
            if (ThrowOnInvoke is not null) throw ThrowOnInvoke;
            if (Answers.TryGetValue(identifier, out var answer) && answer is not null)
                return ValueTask.FromResult(Marshal<TValue>(answer));
            return ValueTask.FromResult(default(TValue)!);
        }

        /// <summary>
        /// Reproduces how a real runtime hands the browser's answer back: the value crosses as JSON
        /// and is read with <c>JsonSerializer.Deserialize&lt;TValue&gt;</c>.
        ///
        /// <para>This is not decoration. Until 2026-09-01 this method did <c>answer is TValue typed</c>,
        /// which lets a boxed <c>bool</c> satisfy <c>TValue = object</c> — something no real runtime
        /// ever produces, because <c>Deserialize&lt;object&gt;("true")</c> returns a
        /// <see cref="System.Text.Json.JsonElement"/>. DataGrid's confirmation gate asked for
        /// <c>&lt;object&gt;</c> and tested <c>is bool</c>, so in a browser it declined every answer
        /// while this fake reported it accepting. A fake that is more permissive than the real thing
        /// is not a test double, it is a second implementation that agrees with nobody.</para>
        ///
        /// <para>A type the JSON layer cannot produce (an interop object reference, say) falls back to
        /// the default rather than throwing, because those calls are incidental to the render tests
        /// that use this class.</para>
        /// </summary>
        private static TValue Marshal<TValue>(object answer)
        {
            try
            {
                var json = JsonSerializer.Serialize(answer);
                return JsonSerializer.Deserialize<TValue>(json)!;
            }
            catch (Exception ex) when (ex is JsonException or NotSupportedException)
            {
                return default!;
            }
        }

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args)
            => InvokeAsync<TValue>(identifier, args);
    }
}
