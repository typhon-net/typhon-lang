// Copyright (c) 2026 The Typhon contributors.
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

using Typhon.Stdlib;
using Xunit;

namespace Typhon.NumPy.Tests;

/// <summary>LU, triangular solves, Cholesky and the numpy.linalg functions built on them.</summary>
public sealed class LinAlgTests
{
    private static readonly int[] Sizes = { 1, 2, 3, 7, 8, 9, 16, 17, 31, 33, 64, 65, 100, 129, 257 };

    /// <summary>A random matrix with a dominant diagonal (well conditioned, but pivoting still happens).</summary>
    private static double[] System(int n, int seed, double diag = 0.5)
    {
        var a = Ref.Random(n * n, seed);
        for (int i = 0; i < n; i++) a[i * n + i] += diag * (i % 3 == 0 ? -1 : 1);
        return a;
    }

    /// <summary>
    /// Backward-error check of A X = B (A n x n, X and B n x nrhs): every residual |B - A X|[i, j] is below tol times
    /// sum_p |A[i, p] X[p, j]|, which holds for a backward-stable solver whatever the condition number.
    /// </summary>
    private static void Residual(double[] a, double[] x, double[] b, int n, int nrhs, double tol, string what)
    {
        for (int i = 0; i < n; i++)
            for (int j = 0; j < nrhs; j++)
            {
                double s = 0, mag = 0;
                for (int p = 0; p < n; p++)
                {
                    s += a[i * n + p] * x[p * nrhs + j];
                    mag += Math.Abs(a[i * n + p] * x[p * nrhs + j]);
                }
                double r = Math.Abs(b[i * nrhs + j] - s);
                if (r > tol * (mag + Math.Abs(b[i * nrhs + j])))
                    Assert.Fail($"{what}: residual {r:E3} at ({i}, {j}), scale {mag:E3}");
            }
    }

    private static double[] Identity(int n)
    {
        var e = new double[n * n];
        for (int i = 0; i < n; i++) e[i * n + i] = 1;
        return e;
    }

    [Fact]
    public void LuReconstructsThePermutedMatrix()
    {
        foreach (int threads in new[] { 1, 6 })
        {
            using var _ = new Knobs(Gemm.Kind, threads, parallelThreshold: 0);
            foreach (int n in Sizes)
            {
                var a = Ref.Random(n * n, n);
                var lu = (double[])a.Clone();
                var piv = new int[n];
                Assert.Equal(0, Lapack.Getrf(lu, n, n, piv));
                var l = new double[n * n];
                var u = new double[n * n];
                for (int i = 0; i < n; i++)
                    for (int j = 0; j < n; j++)
                    {
                        if (j < i) l[i * n + j] = lu[i * n + j];
                        else u[i * n + j] = lu[i * n + j];
                        if (i == j) l[i * n + j] = 1;
                        if (j < i) Assert.True(Math.Abs(lu[i * n + j]) <= 1 + 1e-15, "partial pivoting keeps |L| <= 1");
                    }
                var pa = (double[])a.Clone();
                Lapack.ApplyPivots(piv, n, pa, n, n);
                Ref.Close(pa, Ref.MatMul(l, u, n, n, n), 1e-13, 1.0, $"LU n={n} threads={threads}");
            }
        }
    }

    [Fact]
    public void SolveAndInverse()
    {
        foreach (int threads in new[] { 1, 6 })
        {
            using var _ = new Knobs(Gemm.Kind, threads, parallelThreshold: 0);
            foreach (int n in Sizes)
            {
                var a = System(n, 10 + n);
                var na = Ref.Array(a, n, n);

                var b = Ref.Random(n, 20 + n);
                var x = Ref.Data(numpy.linalg.solve(na, Ref.Array(b, n)));
                Residual(a, x, b, n, 1, 1e-13, $"solve vector n={n}");

                foreach (int nrhs in new[] { 2, 5, 40 })
                {
                    var bm = Ref.Random(n * nrhs, 30 + n);
                    var xm = Ref.Data(numpy.linalg.solve(na, Ref.Array(bm, n, nrhs)));
                    Residual(a, xm, bm, n, nrhs, 1e-13, $"solve n={n} nrhs={nrhs}");
                }

                var inv = Ref.Data(numpy.linalg.inv(na));
                Residual(a, inv, Identity(n), n, n, 1e-13, $"inv n={n} threads={threads}");
            }
        }
    }

    [Fact]
    public void Determinants()
    {
        // small cases against the Leibniz formula, larger ones against the product of the LU diagonal
        Assert.Equal(-2.0, numpy.linalg.det(Ref.Array(new[] { 1.0, 2, 3, 4 }, 2, 2)), 14);
        Assert.Equal(1.0, numpy.linalg.det(Ref.Array(new[] { 2.0, 0, 0, 0, 0.5, 0, 0, 0, 1 }, 3, 3)), 14);
        var m3 = new[] { 0.0, 2, 1, 3, -1, 4, 5, 6, -2 }; // needs pivoting
        double leibniz = m3[0] * (m3[4] * m3[8] - m3[5] * m3[7]) - m3[1] * (m3[3] * m3[8] - m3[5] * m3[6]) + m3[2] * (m3[3] * m3[7] - m3[4] * m3[6]);
        Assert.Equal(leibniz, numpy.linalg.det(Ref.Array(m3, 3, 3)), 12);

        // det(A B) = det(A) det(B), det(A^-1) = 1/det(A)
        int n = 70;
        var a = System(n, 1, 2.0);
        var b = System(n, 2, 2.0);
        double da = numpy.linalg.det(Ref.Array(a, n, n)), db = numpy.linalg.det(Ref.Array(b, n, n));
        double dab = numpy.linalg.det(Ref.Array(Ref.MatMul(a, b, n, n, n), n, n));
        Assert.True(Math.Abs(dab - da * db) <= 1e-10 * Math.Abs(dab), $"{dab} vs {da * db}");
        double dinv = numpy.linalg.det(numpy.linalg.inv(Ref.Array(a, n, n)));
        Assert.True(Math.Abs(dinv * da - 1) <= 1e-10);

        var (sign, logdet) = numpy.linalg.slogdet(Ref.Array(a, n, n));
        Assert.Equal(Math.Sign(da), sign);
        Assert.Equal(Math.Log(Math.Abs(da)), logdet, 10);
    }

    [Fact]
    public void SingularMatrices()
    {
        var s = Ref.Array(new[] { 1.0, 2, 2, 4 }, 2, 2);
        Assert.Equal(0.0, numpy.linalg.det(s));
        Assert.Equal((0.0, double.NegativeInfinity), numpy.linalg.slogdet(s));
        Assert.Throws<numpy.linalg.LinAlgError>(() => numpy.linalg.inv(s));
        Assert.Throws<numpy.linalg.LinAlgError>(() => numpy.linalg.solve(s, Ref.Array(new[] { 1.0, 2 }, 2)));
        // a zero column in the middle of a larger matrix
        int n = 40;
        var a = Ref.Random(n * n, 3);
        for (int i = 0; i < n; i++) a[i * n + 17] = 0;
        Assert.Equal(0.0, numpy.linalg.det(Ref.Array(a, n, n)));
        Assert.Throws<numpy.linalg.LinAlgError>(() => numpy.linalg.inv(Ref.Array(a, n, n)));
    }

    [Fact]
    public void Cholesky()
    {
        foreach (int threads in new[] { 1, 6 })
        {
            using var _ = new Knobs(Gemm.Kind, threads, parallelThreshold: 0);
            foreach (int n in Sizes)
            {
                var x = Ref.Random(n * n, 40 + n);
                var spd = Ref.MatMul(x, Ref.Transpose(x, n, n), n, n, n);
                for (int i = 0; i < n; i++) spd[i * n + i] += n;
                var input = (double[])spd.Clone();
                for (int i = 0; i < n; i++)
                    for (int j = i + 1; j < n; j++) input[i * n + j] = double.NaN; // upper triangle is not read
                var l = Ref.Data(numpy.linalg.cholesky(Ref.Array(input, n, n)));
                for (int i = 0; i < n; i++)
                    for (int j = i + 1; j < n; j++) Assert.Equal(0.0, l[i * n + j]);
                Ref.Close(spd, Ref.MatMul(l, Ref.Transpose(l, n, n), n, n, n), 1e-13, n, $"cholesky n={n} threads={threads}");
                var u = Ref.Data(numpy.linalg.cholesky(Ref.Array(input, n, n), upper: true));
                Assert.Equal(Ref.Transpose(l, n, n), u);
            }
        }
        Assert.Throws<numpy.linalg.LinAlgError>(() => numpy.linalg.cholesky(Ref.Array(new[] { 1.0, 2, 2, 1 }, 2, 2)));
        var big = Identity(100);
        big[60 * 100 + 60] = -1;
        Assert.Throws<numpy.linalg.LinAlgError>(() => numpy.linalg.cholesky(Ref.Array(big, 100, 100)));
    }

    [Fact]
    public void TriangularSolvesWithStrides()
    {
        // L and U blocks inside a larger matrix, right-hand sides inside a wider one
        int n = 75, nrhs = 300, ld = 90, ldb = 310;
        var big = Ref.Random(ld * ld, 5);
        for (int i = 0; i < n; i++) big[(3 + i) * ld + 2 + i] += 4 * (i % 2 == 0 ? 1 : -1);
        int off = 3 * ld + 2;
        var b = Ref.Random(n * ldb, 6);
        var l = new double[n * n];
        var u = new double[n * n];
        for (int i = 0; i < n; i++)
            for (int j = 0; j < n; j++)
            {
                double v = big[off + i * ld + j];
                if (j < i) l[i * n + j] = v; else u[i * n + j] = v;
                if (i == j) l[i * n + j] = 1;
            }
        double[] Block(double[] wide)
        {
            var r = new double[n * nrhs];
            for (int i = 0; i < n; i++)
                for (int j = 0; j < nrhs; j++) r[i * nrhs + j] = wide[1 + i * ldb + j];
            return r;
        }

        foreach (int threads in new[] { 1, 6 })
        {
            using var _ = new Knobs(Gemm.Kind, threads, parallelThreshold: 0);
            var x = (double[])b.Clone();
            Lapack.TrsmLowerUnit(n, nrhs, big, off, ld, x, 1, ldb);
            var y = Block(x);
            Residual(l, y, Block(b), n, nrhs, 1e-13, $"trsm lower threads={threads}");
            Lapack.TrsmUpper(n, nrhs, big, off, ld, x, 1, ldb);
            Residual(u, Block(x), y, n, nrhs, 1e-13, $"trsm upper threads={threads}");
            // untouched outside the block
            for (int i = 0; i < x.Length; i++)
            {
                int r = (i - 1) / ldb, c = (i - 1) % ldb;
                if (i < 1 || r >= n || c >= nrhs) Assert.Equal(b[i], x[i]);
            }
        }
    }
}
