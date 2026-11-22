// Copyright (c) 2026 The Typhon contributors.
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

#nullable enable

namespace Typhon.Runtime
{
    using System;
    using System.Collections.Generic;
    using System.Runtime.CompilerServices;
    using System.Threading;
    using System.Threading.Tasks;

    internal sealed class PyAsyncNext
    {
        readonly Func<Task<dynamic?>> _advance;
        int _started;

        internal PyAsyncNext(Func<Task<dynamic?>> advance) => _advance = advance;

        public TaskAwaiter<dynamic?> GetAwaiter()
        {
            if (Interlocked.Exchange(ref _started, 1) != 0)
                throw new InvalidOperationException("cannot reuse already awaited anext()");
            return _advance().GetAwaiter();
        }
    }

    internal static partial class Builtins
    {
        public static PyAsyncNext anext(object? iterator) => AsyncNext(iterator, false, null);
        public static PyAsyncNext anext(object? iterator, object? @default) => AsyncNext(iterator, true, @default);

        static PyAsyncNext AsyncNext(object? iterator, bool hasDefault, object? fallback)
        {
            if (TryGetAttr(iterator, "__anext__", out var method, allowFallback: false))
            {
                // Python invokes the method at the call and awaits its result when the returned awaitable is driven.
                var awaitable = (object?)__invoke(method, []);
                if (!hasDefault && awaitable is PyAsyncNext next) return next;
                return new PyAsyncNext(() => AwaitNext(async () => await (dynamic)awaitable!, hasDefault, fallback));
            }
            if (iterator == null) throw NotAsyncIterator(iterator);
            Func<Task<dynamic?>> advance = BindAsyncIterator((dynamic)iterator);
            return new PyAsyncNext(() => AwaitNext(advance, hasDefault, fallback));
        }

        public static IAsyncEnumerable<dynamic?> __aiter_dyn(object? source)
        {
            if (TryGetAttr(source, "__aiter__", out var iter, allowFallback: false))
                return AdvanceAsyncIterator((object?)__invoke(iter, []));
            if (source == null) throw NotAsyncIterable(source);
            return AdaptAsyncIterable((dynamic)source);
        }

        static InvalidCastException NotAsyncIterable(object? value) =>
            new InvalidCastException($"'{(value == null ? "NoneType" : PyTypeName(value.GetType()))}' object is not async iterable");

        static IAsyncEnumerable<dynamic?> AdaptAsyncIterable(object source) => throw NotAsyncIterable(source);
        static IAsyncEnumerable<dynamic?> AdaptAsyncIterable<T>(IAsyncEnumerable<T> source) =>
            source is IAsyncEnumerator<T> ? AdvanceAsyncIterator(source) : AsyncObjects(source);

        static async IAsyncEnumerable<dynamic?> AsyncObjects<T>(IAsyncEnumerable<T> source)
        {
            await foreach (var item in source) yield return item;
        }

        static async IAsyncEnumerable<dynamic?> AdvanceAsyncIterator(object? iterator)
        {
            while (true)
            {
                object? value;
                try { value = await anext(iterator); }
                catch (StopAsyncIteration) { yield break; }
                yield return value;
            }
        }

        static async Task<dynamic?> AwaitNext(Func<Task<dynamic?>> advance, bool hasDefault, object? fallback)
        {
            try { return await advance(); }
            catch (StopAsyncIteration) when (hasDefault) { return fallback; }
        }

        static InvalidCastException NotAsyncIterator(object? value) =>
            new InvalidCastException($"'{(value == null ? "NoneType" : PyTypeName(value.GetType()))}' object is not an async iterator");

        static Func<Task<dynamic?>> BindAsyncIterator(object value) => throw NotAsyncIterator(value);

        static Func<Task<dynamic?>> BindAsyncIterator<T>(IAsyncEnumerable<T> value)
        {
            if (value is not IAsyncEnumerator<T>) throw NotAsyncIterator(value);
            return AsyncIterators<T>.Of.GetValue(value, iterator => new AsyncIterator<T>(((IAsyncEnumerable<T>)iterator).GetAsyncEnumerator())).Advance;
        }

        static class AsyncIterators<T>
        {
            internal static readonly ConditionalWeakTable<object, AsyncIterator<T>> Of = new();
        }

        sealed class AsyncIterator<T>
        {
            readonly IAsyncEnumerator<T> _iterator;
            int _running;
            bool _finished;

            internal AsyncIterator(IAsyncEnumerator<T> iterator) => _iterator = iterator;

            internal async Task<dynamic?> Advance()
            {
                if (Interlocked.Exchange(ref _running, 1) != 0)
                    throw new InvalidOperationException("anext(): asynchronous generator is already running");
                try
                {
                    if (_finished) throw new StopAsyncIteration();
                    try
                    {
                        if (await _iterator.MoveNextAsync()) return _iterator.Current;
                    }
                    catch
                    {
                        _finished = true;
                        throw;
                    }
                    _finished = true;
                    await _iterator.DisposeAsync();
                    throw new StopAsyncIteration();
                }
                finally { Volatile.Write(ref _running, 0); }
            }
        }
    }
}
