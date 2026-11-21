// Copyright (c) 2026 The Typhon contributors.
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

using Typhon.Stdlib;
using Xunit;

namespace Typhon.NumPy.Tests;

/// <summary>eigh / eigvalsh, svd / svdvals and what is built on the SVD (lstsq, pinv, matrix_rank, cond).</summary>
public sealed class EigenSvdTests
{
    private static double[] Symmetric(int n, int seed)
    {
        var x = Ref.Random(n * n, seed);
        var s = new double[n * n];
        for (int i = 0; i < n; i++)
            for (int j = 0; j < n; j++) s[i * n + j] = x[i * n + j] + x[j * n + i];
        return s;
    }

    private static void Orthonormal(double[] q, int rows, int cols, double tol, string what)
    {
        var qtq = Ref.MatMul(Ref.Transpose(q, rows, cols), q, cols, rows, cols);
        var eye = new double[cols * cols];
        for (int i = 0; i < cols; i++) eye[i * cols + i] = 1;
        Ref.Close(eye, qtq, tol, 1.0, what);
    }

    [Fact]
    public void EighDecomposesSymmetricMatrices()
    {
        foreach (int n in new[] { 1, 2, 3, 5, 17, 33, 64, 100 })
        {
            var a = Symmetric(n, n);
            var (w, v) = numpy.linalg.eigh(Ref.Array(a, n, n));
            var wd = Ref.Data(w);
            var vd = Ref.Data(v);
            for (int i = 1; i < n; i++) Assert.True(wd[i - 1] <= wd[i], "ascending");
            Orthonormal(vd, n, n, 1e-13, $"eigh V n={n}");
            // A V = V diag(w)
            var av = Ref.MatMul(a, vd, n, n, n);
            var vw = new double[n * n];
            for (int r = 0; r < n; r++)
                for (int c = 0; c < n; c++) vw[r * n + c] = vd[r * n + c] * wd[c];
            Ref.Close(av, vw, 1e-12, n, $"eigh A V n={n}");
            Ref.Close(wd, Ref.Data(numpy.linalg.eigvalsh(Ref.Array(a, n, n))), 1e-12, n, $"eigvalsh n={n}");
        }
        // known spectrum; only the chosen triangle is read
        var m = new[] { 2.0, double.NaN, -1, 2 }; // lower: [[2, .], [-1, 2]] -> eigenvalues 1, 3
        Ref.Close(new[] { 1.0, 3.0 }, Ref.Data(numpy.linalg.eigvalsh(Ref.Array(m, 2, 2))), 1e-15, 1, "2x2 lower");
        var mu = new[] { 2.0, -1, double.NaN, 2 };
        Ref.Close(new[] { 1.0, 3.0 }, Ref.Data(numpy.linalg.eigvalsh(Ref.Array(mu, 2, 2), "U")), 1e-15, 1, "2x2 upper");
        // repeated eigenvalues
        var (w2, v2) = numpy.linalg.eigh(Ref.Array(new double[] { 1, 0, 0, 0, 1, 0, 0, 0, 1 }, 3, 3));
        Assert.Equal(new double[] { 1, 1, 1 }, Ref.Data(w2));
        Orthonormal(Ref.Data(v2), 3, 3, 1e-15, "identity");
    }

    [Fact]
    public void SvdShapesAndReconstruction()
    {
        foreach (int threads in new[] { 1, 6 })
        {
            using var _ = new Knobs(Gemm.Kind, threads, parallelThreshold: 0);
            foreach (var (m, n) in new[] { (1, 1), (3, 3), (5, 2), (2, 5), (17, 9), (9, 17), (64, 64), (100, 40), (40, 100), (200, 200) })
            {
                var a = Ref.Random(m * n, 3 * m + n);
                int k = Math.Min(m, n);
                foreach (bool full in new[] { true, false })
                {
                    var (u, s, vh) = numpy.linalg.svd(Ref.Array(a, m, n), full);
                    int uc = full ? m : k, vr = full ? n : k;
                    Assert.Equal(new[] { m, uc }, u.shape);
                    Assert.Equal(new[] { k }, s.shape);
                    Assert.Equal(new[] { vr, n }, vh.shape);
                    var ud = Ref.Data(u);
                    var sd = Ref.Data(s);
                    var vd = Ref.Data(vh);
                    for (int i = 1; i < k; i++) Assert.True(sd[i - 1] >= sd[i], "descending");
                    Orthonormal(ud, m, uc, 1e-13, $"U {m}x{n} full={full}");
                    Orthonormal(Ref.Transpose(vd, vr, n), n, vr, 1e-13, $"V {m}x{n} full={full}");
                    // U[:, :k] diag(s) Vh[:k] = A
                    var us = new double[m * k];
                    for (int r = 0; r < m; r++)
                        for (int c = 0; c < k; c++) us[r * k + c] = ud[r * uc + c] * sd[c];
                    Ref.Close(a, Ref.MatMul(us, vd[..(k * n)], m, k, n), 1e-13 * Math.Max(m, n), 1.0, $"USV {m}x{n} threads={threads}");
                    Ref.Close(sd, Ref.Data(numpy.linalg.svdvals(Ref.Array(a, m, n))), 1e-13, sd.Length > 0 ? sd[0] : 1, "svdvals");
                }
            }
        }
    }

    [Fact]
    public void SvdOfRankDeficientAndKnownMatrices()
    {
        // rank 1: outer product; the completed singular vectors are still orthonormal
        int m = 6, n = 4;
        var x = new double[] { 1, 2, 3, 4, 5, 6 };
        var y = new double[] { 1, -1, 2, 0.5 };
        var a = new double[m * n];
        for (int i = 0; i < m; i++)
            for (int j = 0; j < n; j++) a[i * n + j] = x[i] * y[j];
        var (u, s, vh) = numpy.linalg.svd(Ref.Array(a, m, n));
        var sd = Ref.Data(s);
        double nx = Math.Sqrt(91), ny = Math.Sqrt(6.25);
        Assert.Equal(nx * ny, sd[0], 12);
        for (int i = 1; i < 4; i++) Assert.True(sd[i] < 1e-14);
        Orthonormal(Ref.Data(u), m, m, 1e-14, "rank-1 U");
        Orthonormal(Ref.Transpose(Ref.Data(vh), n, n), n, n, 1e-14, "rank-1 V");
        Assert.Equal(1, numpy.linalg.matrix_rank(Ref.Array(a, m, n)));
        Assert.Equal(3, numpy.linalg.matrix_rank(Ref.Array(Ref.Random(9, 1), 3, 3)));
        Assert.Equal(0, numpy.linalg.matrix_rank(Ref.Array(new double[4], 2, 2)));

        // tiny singular values keep their relative accuracy: diag(1, 1e-10, 1e-20) rotated
        var (q, _) = numpy.linalg.qr(Ref.Array(Ref.Random(9, 2), 3, 3));
        var qd = Ref.Data(q);
        var d = new double[] { 1, 0, 0, 0, 1e-10, 0, 0, 0, 1e-20 };
        var b = Ref.MatMul(Ref.MatMul(qd, d, 3, 3, 3), Ref.Transpose(qd, 3, 3), 3, 3, 3);
        var sv = Ref.Data(numpy.linalg.svdvals(Ref.Array(b, 3, 3)));
        Assert.Equal(1.0, sv[0], 14);
        Assert.True(Math.Abs(sv[1] / 1e-10 - 1) < 1e-5, $"{sv[1]}");
        Assert.Equal(1e10, numpy.linalg.cond(Ref.Array(new double[] { 1, 0, 0, 1e-10 }, 2, 2)), 3);
    }

    [Fact]
    public void SvdOfAwkwardMatrices()
    {
        void Check(double[] a, int m, int n, string what)
        {
            var (u, s, vh) = numpy.linalg.svd(Ref.Array(a, m, n), false);
            int k = Math.Min(m, n);
            var ud = Ref.Data(u);
            var sd = Ref.Data(s);
            var vd = Ref.Data(vh);
            double scale = sd.Length > 0 ? Math.Max(sd[0], double.Epsilon) : 1;
            for (int i = 0; i < k; i++) Assert.True(sd[i] >= 0 && (i == 0 || sd[i - 1] >= sd[i]), what + " order");
            Orthonormal(ud, m, k, 1e-13, what + " U");
            Orthonormal(Ref.Transpose(vd, k, n), n, k, 1e-13, what + " V");
            var us = new double[m * k];
            for (int r = 0; r < m; r++)
                for (int c = 0; c < k; c++) us[r * k + c] = ud[r * k + c] * sd[c];
            var back = Ref.MatMul(us, vd, m, k, n);
            for (int i = 0; i < a.Length; i++)
                Assert.True(Math.Abs(back[i] - a[i]) <= 1e-13 * Math.Max(m, n) * scale, $"{what}: {back[i]} vs {a[i]}");
        }

        Check(new double[12], 4, 3, "zero matrix");
        var eye = new double[25];
        for (int i = 0; i < 5; i++) eye[i * 5 + i] = 1;
        Check(eye, 5, 5, "identity (repeated singular values)");
        var graded = new double[36];
        for (int i = 0; i < 6; i++)
            for (int j = 0; j < 6; j++) graded[i * 6 + j] = Math.Pow(10, -2 * (i + j)) * (1 + 0.1 * ((i * 7 + j * 3) % 5));
        Check(graded, 6, 6, "graded");
        var zeroRowCol = Ref.Random(7 * 5, 11);
        for (int j = 0; j < 5; j++) zeroRowCol[3 * 5 + j] = 0;
        for (int i = 0; i < 7; i++) zeroRowCol[i * 5 + 2] = 0;
        Check(zeroRowCol, 7, 5, "zero row and column");
        foreach (double big in new[] { 1e200, 1e-200 })
        {
            var a = Ref.Random(6 * 4, 12);
            for (int i = 0; i < a.Length; i++) a[i] *= big;
            Check(a, 6, 4, $"scaled by {big}");
        }
        var bidiag = new double[100];
        for (int i = 0; i < 10; i++) { bidiag[i * 10 + i] = i % 3 == 0 ? 0 : i; if (i < 9) bidiag[i * 10 + i + 1] = 1; }
        Check(bidiag, 10, 10, "bidiagonal with zeros on the diagonal");
    }

    [Fact]
    public void LeastSquaresAndPseudoInverse()
    {
        // overdetermined, full rank: matches the normal equations, residuals as NumPy defines them
        int m = 30, n = 5;
        var a = Ref.Random(m * n, 4);
        var b = Ref.Random(m, 5);
        var (x, res, rank, s) = numpy.linalg.lstsq(Ref.Array(a, m, n), Ref.Array(b, m));
        Assert.Equal(n, rank);
        Assert.Equal(new[] { n }, x.shape);
        Assert.Equal(new[] { 1 }, res.shape);
        Assert.Equal(new[] { n }, s.shape);
        var xd = Ref.Data(x);
        var at = Ref.Transpose(a, m, n);
        var ata = Ref.MatMul(at, a, n, m, n);
        var atb = Ref.MatMul(at, b, n, m, 1);
        var xn = Ref.Data(numpy.linalg.solve(Ref.Array(ata, n, n), Ref.Array(atb, n)));
        Ref.Close(xn, xd, 1e-10, 1.0, "lstsq vs normal equations");
        var r = Ref.MatMul(a, xd, m, n, 1);
        double rr = 0;
        for (int i = 0; i < m; i++) rr += (b[i] - r[i]) * (b[i] - r[i]);
        Assert.Equal(rr, Ref.Data(res)[0], 12);

        // several right-hand sides; underdetermined gives the minimum-norm solution and no residuals
        var bm = Ref.Random(m * 3, 6);
        var (x2, res2, _, _) = numpy.linalg.lstsq(Ref.Array(a, m, n), Ref.Array(bm, m, 3));
        Assert.Equal(new[] { n, 3 }, x2.shape);
        Assert.Equal(new[] { 3 }, res2.shape);
        var w = Ref.Random(3 * 7, 7);
        var c = Ref.Random(3, 8);
        var (x3, res3, rank3, _) = numpy.linalg.lstsq(Ref.Array(w, 3, 7), Ref.Array(c, 3));
        Assert.Equal(3, rank3);
        Assert.Equal(new[] { 0 }, res3.shape);
        var x3d = Ref.Data(x3);
        Ref.Close(c, Ref.MatMul(w, x3d, 3, 7, 1), 1e-12, 1.0, "underdetermined fits exactly");
        var pw = Ref.Data(numpy.linalg.pinv(Ref.Array(w, 3, 7)));
        Ref.Close(Ref.MatMul(pw, c, 7, 3, 1), x3d, 1e-12, 1.0, "minimum norm = pinv b");

        // Moore-Penrose conditions
        var p = Ref.Data(numpy.linalg.pinv(Ref.Array(a, m, n)));
        Ref.Close(a, Ref.MatMul(Ref.MatMul(a, p, m, n, m), a, m, m, n), 1e-12, 1.0, "A A+ A = A");
        Ref.Close(p, Ref.MatMul(Ref.MatMul(p, a, n, m, n), p, n, n, m), 1e-12, 1.0, "A+ A A+ = A+");
    }
}
