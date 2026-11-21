// Copyright (c) 2026 The Typhon contributors.
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

using Typhon.Stdlib;
using Xunit;

// The tests switch global knobs (micro-kernel, thread count): run them one at a time.
[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace Typhon.NumPy.Tests;

/// <summary>Reference implementations and helpers shared by the kernel tests.</summary>
internal static class Ref
{
    internal static double[] Random(int n, int seed)
    {
        var rng = new Random(seed);
        var r = new double[n];
        for (int i = 0; i < n; i++) r[i] = rng.NextDouble() * 2 - 1;
        return r;
    }

    /// <summary>C (m x n) = A (m x k) * B (k x n), row-major, plain triple loop.</summary>
    internal static double[] MatMul(double[] a, double[] b, int m, int k, int n)
    {
        var c = new double[m * n];
        for (int i = 0; i < m; i++)
            for (int j = 0; j < n; j++)
            {
                double s = 0;
                for (int p = 0; p < k; p++) s += a[i * k + p] * b[p * n + j];
                c[i * n + j] = s;
            }
        return c;
    }

    internal static double[] Transpose(double[] a, int rows, int cols)
    {
        var t = new double[a.Length];
        for (int i = 0; i < rows; i++)
            for (int j = 0; j < cols; j++) t[j * rows + i] = a[i * cols + j];
        return t;
    }

    /// <summary>|x - y| &lt;= tol * (scale + |y|) element-wise, where scale is a typical magnitude of the result.</summary>
    internal static void Close(double[] expected, double[] actual, double tol, double scale = 1.0, string what = "")
    {
        Assert.Equal(expected.Length, actual.Length);
        for (int i = 0; i < expected.Length; i++)
        {
            double e = expected[i], a = actual[i];
            if (Math.Abs(e - a) > tol * (scale + Math.Abs(e)))
                Assert.Fail($"{what}: element {i}: expected {e:R}, got {a:R}");
        }
    }

    internal static numpy.ndarray Array(double[] data, params int[] shape) =>
        numpy.array(data).reshape(new NumPyShape(shape));

    internal static double[] Data(numpy.ndarray a) => a.AsSpan().ToArray();
}

/// <summary>Restores the GEMM micro-kernel and thread count after a test changes them.</summary>
internal sealed class Knobs : IDisposable
{
    private readonly Gemm.KernelKind _kind = Gemm.Kind;
    private readonly int _threads = NumPyRuntime.MaxDegreeOfParallelism;
    private readonly long _threshold = NumPyRuntime.MatMulParallelThreshold;

    internal Knobs(Gemm.KernelKind kind, int threads, long parallelThreshold = -1)
    {
        Gemm.Kind = kind;
        NumPyRuntime.MaxDegreeOfParallelism = threads;
        if (parallelThreshold >= 0) NumPyRuntime.MatMulParallelThreshold = parallelThreshold;
    }

    public void Dispose()
    {
        Gemm.Kind = _kind;
        NumPyRuntime.MaxDegreeOfParallelism = _threads;
        NumPyRuntime.MatMulParallelThreshold = _threshold;
    }
}

public sealed class GemmTests
{
    public static IEnumerable<object[]> Kinds()
    {
        yield return new object[] { "Vector256" };
        yield return new object[] { "Vector128" };
        yield return new object[] { "Scalar" };
    }

    private static readonly int[][] Shapes =
    {
        new[] { 1, 1, 1 }, new[] { 1, 7, 1 }, new[] { 3, 3, 3 }, new[] { 2, 5, 3 }, new[] { 6, 8, 4 },
        new[] { 7, 9, 5 }, new[] { 17, 17, 17 }, new[] { 16, 16, 16 }, new[] { 13, 1, 29 }, new[] { 1, 33, 40 },
        new[] { 64, 64, 64 }, new[] { 65, 63, 66 }, new[] { 73, 81, 257 }, new[] { 129, 129, 129 },
        new[] { 200, 3, 150 }, new[] { 5, 300, 7 }, new[] { 145, 130, 513 },
    };

    private static bool KindAvailable(Gemm.KernelKind kind) =>
        kind != Gemm.KernelKind.Vector256 || System.Runtime.Intrinsics.Vector256.IsHardwareAccelerated;

    [Theory]
    [MemberData(nameof(Kinds))]
    public void MatchesNaiveProductForManyShapes(string kindName)
    {
        var kind = Enum.Parse<Gemm.KernelKind>(kindName);
        if (!KindAvailable(kind)) return;
        foreach (int threads in new[] { 1, 4 })
        {
            using var _ = new Knobs(kind, threads, parallelThreshold: 0);
            foreach (var s in Shapes)
            {
                int m = s[0], k = s[1], n = s[2];
                var a = Ref.Random(m * k, 1 + m);
                var b = Ref.Random(k * n, 2 + n);
                var expected = Ref.MatMul(a, b, m, k, n);
                var actual = MatMulKernels.MatMul(a, b, m, k, n);
                Ref.Close(expected, actual, 1e-12, Math.Sqrt(k), $"{kind} threads={threads} {m}x{k}x{n}");
            }
        }
    }

    [Fact]
    public void ResultDoesNotDependOnThreadCount()
    {
        if (!KindAvailable(Gemm.KernelKind.Vector256)) return;
        int m = 300, k = 700, n = 290;
        var a = Ref.Random(m * k, 3);
        var b = Ref.Random(k * n, 4);
        double[] one, many;
        using (new Knobs(Gemm.KernelKind.Vector256, 1)) one = MatMulKernels.MatMul(a, b, m, k, n);
        using (new Knobs(Gemm.KernelKind.Vector256, 8, parallelThreshold: 0)) many = MatMulKernels.MatMul(a, b, m, k, n);
        Assert.Equal(one, many); // bit for bit
    }

    [Theory]
    [MemberData(nameof(Kinds))]
    public void StridedOperandsAlphaAndAccumulate(string kindName)
    {
        var kind = Enum.Parse<Gemm.KernelKind>(kindName);
        if (!KindAvailable(kind)) return;
        using var _ = new Knobs(kind, 3, parallelThreshold: 0);
        foreach (var s in new[] { new[] { 5, 7, 3 }, new[] { 70, 300, 90 }, new[] { 31, 260, 17 } })
        {
            int m = s[0], k = s[1], n = s[2];
            var a = Ref.Random(m * k, 5);
            var b = Ref.Random(k * n, 6);
            var expected = Ref.MatMul(a, b, m, k, n);

            // A and B given transposed (column-major) through their strides, C a sub-block of a wider matrix.
            var at = Ref.Transpose(a, m, k);
            var bt = Ref.Transpose(b, k, n);
            int ldc = n + 5, cOff = 2 * ldc + 3;
            var c = new double[(m + 3) * ldc];
            var c0 = Ref.Random(c.Length, 7);
            Array.Copy(c0, c, c.Length);
            Gemm.Run(m, n, k, -2.0, at, 0, 1, m, bt, 0, 1, k, c, cOff, ldc, accumulate: true);
            for (int i = 0; i < c.Length; i++)
            {
                int r = (i - cOff) / ldc, col = (i - cOff) % ldc;
                bool inside = i >= cOff && r < m && col >= 0 && col < n;
                double want = inside ? c0[i] - 2.0 * expected[r * n + col] : c0[i];
                Assert.True(Math.Abs(want - c[i]) <= 1e-12 * (Math.Sqrt(k) + Math.Abs(want)), $"{kind} {m}x{k}x{n} at {i}: {want} vs {c[i]}");
            }

            // Overwrite mode ignores what C held before (NaN included).
            var d = new double[m * n];
            Array.Fill(d, double.NaN);
            Gemm.Run(m, n, k, 1.0, a, 0, k, 1, b, 0, n, 1, d, 0, n, accumulate: false);
            Ref.Close(expected, d, 1e-12, Math.Sqrt(k), $"{kind} overwrite {m}x{k}x{n}");
        }
    }

    [Fact]
    public void EmptyInnerDimensionGivesZeros()
    {
        var c = MatMulKernels.MatMul(Array.Empty<double>(), Array.Empty<double>(), 3, 0, 4);
        Assert.Equal(new double[12], c);
    }
}
