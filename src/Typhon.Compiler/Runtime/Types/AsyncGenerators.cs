// Copyright (c) 2026 The Typhon contributors.
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

#nullable enable

namespace Typhon.Runtime
{
    using System;
    using System.Collections.Generic;
    using System.Threading;
    using System.Threading.Tasks;

    internal interface IPyAsyncGenerator { }

    /// <summary>A single async generator cursor shared by async iteration, anext(), and send/throw/close.</summary>
    internal sealed class PyAsyncGenerator<T> : IAsyncEnumerable<T>, IAsyncEnumerator<T>, IPyAsyncGenerator
    {
        Func<Task<object?>>? _body;
        TaskCompletionSource<(bool Yielded, T Value)>? _output;
        TaskCompletionSource<object?>? _resume;
        int _running;
        bool _started, _done;

        public void __start_async(Func<Task<object?>> body) => _body = body;

        public Task<object?> Yield(T value)
        {
            _resume = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _output!.SetResult((true, value));
            return _resume.Task;
        }

        async Task RunBody()
        {
            try
            {
                await _body!();
                _output!.SetResult((false, default!));
            }
            catch (Exception end) when (end is StopAsyncIteration or StopIteration)
            {
                string name = end is StopAsyncIteration ? "StopAsyncIteration" : "StopIteration";
                var error = new InvalidOperationException($"async generator raised {name}", end);
                PyExceptionState.SetAttribute(error, "__cause__", end);
                PyExceptionState.SetAttribute(error, "__context__", end);
                _output!.SetException(error);
            }
            catch (Exception error) { _output!.SetException(error); }
        }

        async Task<T> Advance(object? sent, Exception? error)
        {
            if (Interlocked.Exchange(ref _running, 1) != 0)
                throw new InvalidOperationException("asynchronous generator is already running");
            try
            {
                if (_done) throw error ?? new StopAsyncIteration();
                if (!_started && error != null) { _done = true; throw error; }
                if (!_started && sent != null) throw new InvalidCastException("can't send non-None value to a just-started async generator");

                var output = new TaskCompletionSource<(bool Yielded, T Value)>(TaskCreationOptions.RunContinuationsAsynchronously);
                _output = output;
                if (!_started)
                {
                    _started = true;
                    _ = RunBody();
                }
                else if (error != null) _resume!.SetException(error);
                else _resume!.SetResult(sent);

                (bool yielded, T value) = await output.Task;
                if (yielded) return value;
                throw new StopAsyncIteration();
            }
            catch when (_started)
            {
                _done = true;
                throw;
            }
            finally { Volatile.Write(ref _running, 0); }
        }

        public PyAsyncGenerator<T> __aiter__() => this;
        public PyAsyncNext __anext__() => asend(null);
        public PyAsyncNext asend(object? value) => new(async () => await Advance(value, null));
        public PyAsyncNext athrow(object? exception) =>
            new(async () => _done ? null : await Advance(null, Builtins.__prepare_exception(exception, null, false)));
        public PyAsyncNext aclose() => new(Close);

        async Task<dynamic?> Close()
        {
            if (_done) return null;
            try { await Advance(null, new GeneratorExit()); }
            catch (GeneratorExit) { return null; }
            catch (StopAsyncIteration) { return null; }
            throw new InvalidOperationException("async generator ignored GeneratorExit");
        }

        public T Current { get; private set; } = default!;

        public async ValueTask<bool> MoveNextAsync()
        {
            try { Current = await Advance(null, null); return true; }
            catch (StopAsyncIteration) { return false; }
        }

        // Leaving an async-for loop does not close the Python iterator. aclose() is explicit.
        public ValueTask DisposeAsync() => default;
        public IAsyncEnumerator<T> GetAsyncEnumerator(CancellationToken cancellationToken = default) => this;
    }

    internal static partial class Builtins
    {
        public static PyAsyncNext __anext__<T>(this IAsyncEnumerable<T> source) => anext(source);
        public static PyAsyncNext asend<T>(this IAsyncEnumerable<T> source, object? value) => AsyncGenerator(source).asend(value);
        public static PyAsyncNext athrow<T>(this IAsyncEnumerable<T> source, object? error) => AsyncGenerator(source).athrow(error);
        public static PyAsyncNext aclose<T>(this IAsyncEnumerable<T> source) => AsyncGenerator(source).aclose();

        static PyAsyncGenerator<T> AsyncGenerator<T>(IAsyncEnumerable<T> source) => source as PyAsyncGenerator<T> ??
            throw new InvalidCastException("object is not an async generator");
    }
}
