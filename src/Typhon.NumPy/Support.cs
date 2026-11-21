// Copyright (c) 2026 The Typhon contributors.
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

using System;
using System.Collections.Generic;
using System.Globalization;

namespace Typhon.Stdlib;

/// <summary>
/// A numpy "shape-like" argument. Python ints and int tuples convert to it implicitly, so a single
/// parameter of this type accepts <c>np.zeros(3)</c>, <c>np.zeros((3, 3))</c>, <c>np.zeros(a.shape)</c>
/// and <c>np.zeros([2, 3])</c> (List&lt;int&gt;).
/// </summary>
public readonly struct NumPyShape
{
    private readonly int[] _dims;

    public NumPyShape(params int[] dims) => _dims = dims ?? Array.Empty<int>();

    internal int[] Dims => _dims ?? Array.Empty<int>();

    public static implicit operator NumPyShape(int n) => new(new[] { n });
    public static implicit operator NumPyShape(long n) => new(new[] { checked((int)n) });
    public static implicit operator NumPyShape(ValueTuple<int> t) => new(new[] { t.Item1 });
    public static implicit operator NumPyShape((int, int) t) => new(new[] { t.Item1, t.Item2 });
    public static implicit operator NumPyShape((int, int, int) t) => new(new[] { t.Item1, t.Item2, t.Item3 });
    public static implicit operator NumPyShape((int, int, int, int) t) => new(new[] { t.Item1, t.Item2, t.Item3, t.Item4 });
    public static implicit operator NumPyShape(int[] dims) => new((int[])(dims ?? Array.Empty<int>()).Clone());
    public static implicit operator NumPyShape(List<int> dims) => new((dims ?? new List<int>()).ToArray());
    // Typhon's int is a 64-bit long: shapes written as Python ints/tuples arrive as longs
    public static implicit operator NumPyShape(ValueTuple<long> t) => new(new[] { checked((int)t.Item1) });
    public static implicit operator NumPyShape((long, long) t) => new(new[] { checked((int)t.Item1), checked((int)t.Item2) });
    public static implicit operator NumPyShape((long, long, long) t) => new(new[] { checked((int)t.Item1), checked((int)t.Item2), checked((int)t.Item3) });
    public static implicit operator NumPyShape((long, long, long, long) t) => new(new[] { checked((int)t.Item1), checked((int)t.Item2), checked((int)t.Item3), checked((int)t.Item4) });
    public static implicit operator NumPyShape(long[] dims) => new(Array.ConvertAll(dims ?? Array.Empty<long>(), d => checked((int)d)));
    public static implicit operator NumPyShape(List<long> dims) => new((dims ?? new List<long>()).ConvertAll(d => checked((int)d)).ToArray());

    public override string ToString() => Err.ShapeStr(Dims);
}

/// <summary>Performance knobs for the numpy runtime (not part of the numpy API).</summary>
public static class NumPyRuntime
{
    /// <summary>Maximum worker threads for large element-wise ops and matmul. 1 disables parallelism.
    /// Defaults to the TYPHON_NUMPY_THREADS environment variable, else Environment.ProcessorCount.</summary>
    public static int MaxDegreeOfParallelism { get; set; } = DefaultThreads();

    /// <summary>Element count from which element-wise ops are split across threads.</summary>
    public static int ParallelThreshold { get; set; } = 1 << 17;

    /// <summary>n*k*m from which matmul (and the matrix products inside linalg) is split across threads.</summary>
    public static long MatMulParallelThreshold { get; set; } = 1L << 19;

    private static int DefaultThreads()
    {
        var env = Environment.GetEnvironmentVariable("TYPHON_NUMPY_THREADS");
        if (!string.IsNullOrEmpty(env) && int.TryParse(env, NumberStyles.Integer, CultureInfo.InvariantCulture, out int n) && n > 0)
            return n;
        return Environment.ProcessorCount;
    }
}

/// <summary>Display/semantic kind of an array. Storage is always float64.</summary>
internal enum NdKind : byte
{
    Float = 0,
    Bool = 1,
}

/// <summary>Python-style exceptions mapped onto .NET exception types.</summary>
internal static class Err
{
    /// <summary>Python ValueError.</summary>
    internal static ArgumentException Value(string msg) => new(msg);

    /// <summary>Python IndexError.</summary>
    internal static IndexOutOfRangeException Index(string msg) => new(msg);

    /// <summary>Python TypeError.</summary>
    internal static InvalidOperationException Type(string msg) => new(msg);

    internal static string ShapeStr(int[] s)
    {
        if (s.Length == 1) return "(" + s[0].ToString(CultureInfo.InvariantCulture) + ",)";
        var parts = new string[s.Length];
        for (int i = 0; i < s.Length; i++) parts[i] = s[i].ToString(CultureInfo.InvariantCulture);
        return "(" + string.Join(", ", parts) + ")";
    }
}
