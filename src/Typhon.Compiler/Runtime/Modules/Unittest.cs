// Copyright (c) 2026 The Typhon contributors.
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

#nullable enable
#pragma warning disable

namespace Typhon.Stdlib
{
    using System;
    using System.Collections;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.Linq;
    using System.Reflection;
    using System.Runtime.CompilerServices;
    using Typhon.Runtime;

    /// <summary>The unittest case, result and suite protocols used by Python test runners.</summary>
    internal static class unittest
    {
        internal class SkipTest : PyExceptionBase
        {
            public SkipTest(params object?[] args) : base(args) { }
        }

        static (Type, Exception, object?) ExceptionInfo(Exception exception) =>
            (exception.GetType(), exception, null);

        static Type ExceptionType(object? info) => info switch
        {
            Exception exception => exception.GetType(),
            ITuple tuple when tuple.Length == 3 && tuple[0] is Type type => type,
            _ => throw new InvalidCastException("expected an exception or an exception info tuple"),
        };

        internal class TestResult
        {
            public long testsRun;
            public bool failfast, shouldStop, buffer;
            public List<(object, string)> errors = new(), failures = new(), skipped = new(), expectedFailures = new();
            public List<object> unexpectedSuccesses = new();
            public List<(string, double)> collectedDurations = new();

            object? _originalOut, _originalError;
            io.StringIO? _out, _error;
            bool _mirrorOutput;

            public TestResult() { }
            public TestResult(object? stream = null, object? descriptions = null, object? verbosity = null) { }

            // Hooks for a custom result object.
            public virtual void startTestRun() { }
            public virtual void stopTestRun() { }
            public virtual void addSuccess(object test) { }

            public virtual void startTest(object test)
            {
                testsRun++;
                _mirrorOutput = false;
                if (!buffer) return;
                _originalOut = sys_.stdout;
                _originalError = sys_.stderr;
                sys_.stdout = _out = new io.StringIO();
                sys_.stderr = _error = new io.StringIO();
            }

            public virtual void stopTest(object test)
            {
                if (_out == null) return;
                sys_.stdout = _originalOut;
                sys_.stderr = _originalError;
                if (_mirrorOutput)
                {
                    if (_originalOut != null) PyConsole.Write(_originalOut, Captured("Stdout", _out));
                    if (_originalError != null) PyConsole.Write(_originalError, Captured("Stderr", _error));
                }
                _out.close();
                _error!.close();
                _out = _error = null;
                _originalOut = _originalError = null;
                _mirrorOutput = false;
            }

            static string Captured(string label, io.StringIO? stream)
            {
                string text = stream?.getvalue() ?? "";
                return text.Length == 0 ? "" : $"\n{label}:\n{text}{(text.EndsWith('\n') ? "" : "\n")}";
            }

            string Format(object? info)
            {
                var exception = info is Exception e ? e : info is ITuple { Length: 3 } t ? t[1] as Exception : null;
                if (exception == null) throw new InvalidCastException("expected an exception or an exception info tuple");
                return string.Concat(traceback.format_exception(exception)) + Captured("Stdout", _out) + Captured("Stderr", _error);
            }

            void Record(List<(object, string)> outcomes, object test, object? err)
            {
                if (failfast) stop();
                outcomes.Add((test, Format(err)));
                _mirrorOutput = true;
            }

            public virtual void addError(object test, object? err) => Record(errors, test, err);
            public virtual void addFailure(object test, object? err) => Record(failures, test, err);
            public virtual void addSkip(object test, string reason) => skipped.Add((test, reason));
            public virtual void addExpectedFailure(object test, object? err) => expectedFailures.Add((test, Format(err)));
            public virtual void addUnexpectedSuccess(object test)
            {
                if (failfast) stop();
                unexpectedSuccesses.Add(test);
            }
            public virtual void addDuration(object test, double elapsed) => collectedDurations.Add((Builtins.str(test), elapsed));
            public virtual void addSubTest(object test, object subtest, object? err)
            {
                if (err == null) return;
                Type assertion = test is TestCase testcase ? testcase.failureException : typeof(AssertionError);
                Record(assertion.IsAssignableFrom(ExceptionType(err)) ? failures : errors, subtest, err);
            }
            public virtual bool wasSuccessful() => errors.Count == 0 && failures.Count == 0 && unexpectedSuccesses.Count == 0;
            public virtual void stop() => shouldStop = true;
        }

        internal class TestCase : IPyRepr
        {
            public readonly string _testMethodName;
            readonly Stack<(object Callable, object?[] Args)> _cleanups = new();
            TestResult? _runningResult;
            bool _successful;
            @case._SubTest? _subtest;

            public TestCase() : this("runTest") { }
            public TestCase(string methodName)
            {
                _testMethodName = methodName;
                if (methodName != "runTest" && FindTest() == null)
                    throw new ArgumentException($"no such test method in <class '{ClassName}'>: {methodName}");
            }

            public virtual void setUp() { }
            public virtual void tearDown() { }
            public virtual TestResult defaultTestResult() => new();
            public virtual long countTestCases() => 1;
            public virtual Type failureException =>
                GetType().GetField(nameof(failureException), BindingFlags.Static | BindingFlags.Public | BindingFlags.FlattenHierarchy)?.GetValue(null) as Type
                ?? typeof(AssertionError);

            string ClassName
            {
                get
                {
                    Type type = GetType(), outer = type;
                    while (outer.DeclaringType != null) outer = outer.DeclaringType;
                    string module = outer == typeof(unittest) ? "unittest.case"
                        : outer.GetCustomAttribute<PySourceAttribute>()?.Module ?? Builtins.__module_of(type);
                    return module + "." + type.Name;
                }
            }
            MethodInfo? FindTest() => GetType().GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .FirstOrDefault(m => m.Name == _testMethodName);
            void InvokeTest()
            {
                if (FindTest() == null) throw new MissingMemberException($"'{GetType().Name}' object has no attribute '{_testMethodName}'");
                new PyBoundMethod(this, _testMethodName, includeNonPublic: true).__call__([]);
            }
            public virtual string id() => ClassName + "." + _testMethodName;
            public virtual string? shortDescription() => FindTest() is MethodInfo method
                ? method.GetCustomAttribute<PySourceAttribute>()?.DocString?.Trim().Split('\n')[0].Trim() : "No test";
            public virtual string __repr__() => $"<{ClassName} testMethod={_testMethodName}>";
            public override string ToString() => $"{_testMethodName} ({ClassName}.{_testMethodName})";
            public override bool Equals(object? other) =>
                other is TestCase test && test.GetType() == GetType() && test._testMethodName == _testMethodName;
            public override int GetHashCode() => HashCode.Combine(GetType(), _testMethodName);

            bool RunPart(Action part)
            {
                try { part(); return true; }
                catch (StopSubtests) { return false; }
                catch (Exception e) when (e is not KeyboardInterrupt && _runningResult != null)
                {
                    _successful = false;
                    if (e is SkipTest) _runningResult.addSkip(this, Builtins.str(e));
                    else if (failureException.IsInstanceOfType(e)) _runningResult.addFailure(this, ExceptionInfo(e));
                    else _runningResult.addError(this, ExceptionInfo(e));
                    return false;
                }
            }

            public virtual TestResult run(dynamic? result = null)
            {
                bool ownsRun = result == null;
                var outcomes = ownsRun ? defaultTestResult() : (object?)result as TestResult
                    ?? throw new InvalidCastException("result must be a unittest.TestResult");
                if (ownsRun) outcomes.startTestRun();
                outcomes.startTest(this);
                try
                {
                    _runningResult = outcomes;
                    _successful = true;
                    long start = Stopwatch.GetTimestamp();
                    if (RunPart(setUp))
                    {
                        RunPart(InvokeTest);
                        RunPart(tearDown);
                    }
                    doCleanups();
                    outcomes.addDuration(this, Stopwatch.GetElapsedTime(start).TotalSeconds);
                    if (_successful) outcomes.addSuccess(this);
                    return outcomes;
                }
                finally
                {
                    _runningResult = null;
                    outcomes.stopTest(this);
                    if (ownsRun) outcomes.stopTestRun();
                }
            }

            public TestResult __call__(dynamic? result = null) => run((object?)result);

            public virtual void debug()
            {
                setUp();
                InvokeTest();
                tearDown();
                doCleanups();
            }

            public virtual void addCleanup(object function, params object?[] args) => _cleanups.Push((function, args));
            public virtual bool doCleanups()
            {
                while (_cleanups.TryPop(out var cleanup))
                    RunPart(() => Builtins.__invoke(cleanup.Callable, cleanup.Args));
                return _runningResult == null || _successful;
            }

            public virtual void skipTest(object? reason) => throw new SkipTest(reason);
            public virtual void fail(object? msg = null) => throw (Exception)Builtins.__invoke(failureException, [msg]);
            public virtual void assertEqual(object? first, object? second, object? msg = null)
            {
                if (!Builtins.eq(first, second)) Assertion($"{Builtins.repr(first)} != {Builtins.repr(second)}", msg);
            }
            public virtual void assertNotEqual(object? first, object? second, object? msg = null)
            {
                if (Builtins.eq(first, second)) Assertion($"{Builtins.repr(first)} == {Builtins.repr(second)}", msg);
            }
            public virtual void assertTrue(object? expr, object? msg = null)
            {
                if (!Builtins.truthy(expr)) Assertion($"{Builtins.repr(expr)} is not true", msg);
            }
            public virtual void assertFalse(object? expr, object? msg = null)
            {
                if (Builtins.truthy(expr)) Assertion($"{Builtins.repr(expr)} is not false", msg);
            }
            public virtual void assertIsNone(object? obj, object? msg = null)
            {
                if (obj != null) Assertion($"{Builtins.repr(obj)} is not None", msg);
            }
            public bool longMessage = true;
            void Assertion(string standard, object? msg) => fail(msg == null ? standard : longMessage ? standard + " : " + Builtins.str(msg) : msg);

            public SubTestScope subTest([System.Runtime.InteropServices.Optional] object? msg, Dictionary<string, object?>? __kwargs = null) =>
                new(this, ReferenceEquals(msg, Missing.Value) ? @case.NoMessage : msg, __kwargs);

            sealed class StopSubtests : Exception { }

            internal sealed class SubTestScope
            {
                readonly TestCase _test;
                readonly object? _message;
                readonly Dictionary<string, object?>? _parameters;
                @case._SubTest? _previous;
                bool _previousSuccess;

                internal SubTestScope(TestCase test, object? message, Dictionary<string, object?>? parameters)
                { _test = test; _message = message; _parameters = parameters; }

                public object? __enter__()
                {
                    if (_test._runningResult == null) return null;
                    _previous = _test._subtest;
                    _previousSuccess = _test._successful;
                    _test._successful = true;
                    var parameters = new OrderedDictionary<string, object?>();
                    if (_parameters != null)
                        foreach (var pair in _parameters) parameters[pair.Key] = pair.Value;
                    if (_previous != null)
                        foreach (var pair in _previous.@params) parameters.TryAdd(pair.Key, pair.Value);
                    _test._subtest = new @case._SubTest(_test, _message, parameters);
                    return null;
                }

                public bool __exit__(object? type, object? exc, object? tb)
                {
                    if (_test._runningResult is not TestResult result || _test._subtest is not @case._SubTest subtest) return false;
                    try
                    {
                        if (exc is KeyboardInterrupt or StopSubtests) return false;
                        if (exc is SkipTest skip)
                        {
                            _test._successful = false;
                            result.addSkip(subtest, Builtins.str(skip));
                        }
                        else if (exc is Exception exception)
                        {
                            _test._successful = false;
                            result.addSubTest(_test, subtest, (exception.GetType(), exception, tb));
                        }
                        else if (_test._successful) result.addSubTest(_test, subtest, null);
                        if (!_test._successful && result.failfast) throw new StopSubtests();
                        return exc != null;
                    }
                    finally
                    {
                        _test._subtest = _previous;
                        _test._successful &= _previousSuccess;
                    }
                }
            }
        }

        internal static class @case
        {
            internal static readonly object NoMessage = new();
            public static void _addSkip(TestResult result, object test, string reason) => result.addSkip(test, reason);

            internal class _SubTest : TestCase
            {
                public readonly TestCase test_case;
                readonly object? _message;
                public readonly OrderedDictionary<string, object?> @params;
                public _SubTest(TestCase test_case, object? message, object @params)
                {
                    this.test_case = test_case;
                    _message = message;
                    this.@params = new OrderedDictionary<string, object?>();
                    if (@params is not IDictionary mapping) throw new InvalidCastException("subtest parameters must be a mapping");
                    foreach (DictionaryEntry pair in mapping)
                        this.@params[Builtins.str(pair.Key)] = pair.Value;
                }
                string Detail
                {
                    get
                    {
                        string text = ReferenceEquals(_message, NoMessage) ? "" : $"[{Builtins.str(_message)}]";
                        if (@params.Count > 0)
                            text += (text.Length > 0 ? " " : "") + "(" + string.Join(", ", @params.Select(pair => $"{pair.Key}={Builtins.repr(pair.Value)}")) + ")";
                        return text.Length == 0 ? "(<subtest>)" : text;
                    }
                }
                public override Type failureException => test_case.failureException;
                public override string id() => test_case.id() + " " + Detail;
                public override string? shortDescription() => test_case.shortDescription();
                public override string ToString() => test_case + " " + Detail;
            }
        }

        internal class TestSuite : IEnumerable<object?>, IPyRepr
        {
            public readonly List<object?> _tests = new();
            public bool _cleanup = true;
            long _removedTests;
            public TestSuite() { }
            public TestSuite(IEnumerable? tests) { if (tests != null) addTests(tests); }
            public virtual void addTest(object? test)
            {
                if (test is Type t && (typeof(TestCase).IsAssignableFrom(t) || typeof(TestSuite).IsAssignableFrom(t)))
                    throw new InvalidCastException("TestCases and TestSuites must be instantiated before passing them to addTest()");
                if (test is not (Delegate or PyFunction or PyBoundMethod or Type) && !Builtins.hasattr(test, "__call__"))
                    throw new InvalidCastException($"{Builtins.repr(test)} is not callable");
                _tests.Add(test);
            }
            public virtual void addTests(IEnumerable tests)
            {
                if (tests is string) throw new InvalidCastException("tests must be an iterable of tests, not a string");
                foreach (var test in tests) addTest(test);
            }
            static long Count(object test) => test switch
            {
                TestCase testcase => testcase.countTestCases(),
                TestSuite suite => suite.countTestCases(),
                _ => Builtins.@int((object)Builtins.__invoke((object?)Builtins.getattr(test, "countTestCases"), [])),
            };
            public virtual long countTestCases() => _removedTests + _tests.Where(t => t != null).Sum(t => Count(t!));
            public virtual void _removeTestAtIndex(long index)
            {
                if (_tests[(int)index] is object test)
                {
                    if (Builtins.hasattr(test, "countTestCases")) _removedTests += Count(test);
                    _tests[(int)index] = null;
                }
            }
            public virtual TestResult run(TestResult result, bool debug = false)
            {
                for (int i = 0; i < _tests.Count && !result.shouldStop; i++)
                {
                    var test = _tests[i];
                    if (debug) Builtins.__invoke((object?)Builtins.getattr(test, "debug"), []);
                    else Builtins.__invoke(test, [result]);
                    if (_cleanup) _removeTestAtIndex(i);
                }
                return result;
            }
            public TestResult __call__(TestResult result) => run(result);
            public virtual void debug()
            {
                foreach (var test in _tests) Builtins.__invoke((object?)Builtins.getattr(test, "debug"), []);
            }
            public IEnumerator<object?> GetEnumerator() => _tests.GetEnumerator();
            IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
            public virtual string __repr__() => $"<{Builtins.__module_of(GetType())}.{GetType().Name} tests={Builtins.repr(_tests)}>";
            public override string ToString() => __repr__();
        }
    }
}
