// Copyright (c) 2026 The Typhon contributors.
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

using Typhon.Stdlib;
using Xunit;

namespace Typhon.NumPy.Tests;

/// <summary>Householder QR: reconstruction, orthogonality, shape and NumPy's (LAPACK's) sign convention.</summary>
public sealed class QrTests
{
    private static void CheckQr(double[] a, int m, int n, bool complete, string what)
    {
        var (q, r) = numpy.linalg.qr(Ref.Array(a, m, n), complete ? "complete" : "reduced");
        int k = Math.Min(m, n), qc = complete ? m : k;
        Assert.Equal(new[] { m, qc }, q.shape);
        Assert.Equal(new[] { complete ? m : k, n }, r.shape);
        var qd = Ref.Data(q);
        var rd = Ref.Data(r);
        for (int i = 0; i < r.shape[0]; i++)
            for (int j = 0; j < Math.Min(i, n); j++) Assert.Equal(0.0, rd[i * n + j]);
        // Q^T Q = I
        var qtq = Ref.MatMul(Ref.Transpose(qd, m, qc), qd, qc, m, qc);
        var eye = new double[qc * qc];
        for (int i = 0; i < qc; i++) eye[i * qc + i] = 1;
        Ref.Close(eye, qtq, 1e-13, 1.0, what + " Q^T Q");
        // Q R = A
        double scale = 0;
        foreach (double v in a) scale = Math.Max(scale, Math.Abs(v));
        Ref.Close(a, Ref.MatMul(qd, rd, m, qc, n), 1e-13 * Math.Max(m, n), scale, what + " QR");
    }

    [Fact]
    public void ShapesAndReconstruction()
    {
        foreach (int threads in new[] { 1, 6 })
        {
            using var _ = new Knobs(Gemm.Kind, threads, parallelThreshold: 0);
            foreach (var (m, n) in new[] { (1, 1), (3, 3), (5, 2), (2, 5), (31, 31), (33, 32), (64, 64), (100, 37), (37, 100), (129, 129), (200, 70) })
            {
                var a = Ref.Random(m * n, m * 7 + n);
                CheckQr(a, m, n, false, $"reduced {m}x{n} threads={threads}");
                CheckQr(a, m, n, true, $"complete {m}x{n} threads={threads}");
            }
        }
    }

    [Fact]
    public void MatchesNumPySigns()
    {
        var (q, r) = numpy.linalg.qr(Ref.Array(new[] { 12.0, -51, 4, 6, 167, -68, -4, 24, -41 }, 3, 3));
        Ref.Close(new[] { -0.85714286, 0.39428571, 0.33142857, -0.42857143, -0.90285714, -0.03428571, 0.28571429, -0.17142857, 0.94285714 },
            Ref.Data(q), 1e-8, 1, "Q");
        Ref.Close(new[] { -14.0, -21, 14, 0, -175, 70, 0, 0, -35 }, Ref.Data(r), 1e-12, 1, "R");

        var (q2, r2) = numpy.linalg.qr(Ref.Array(new[] { 1.0, 2, 3, 4, 5, 6, 7, 8.5 }, 4, 2), "complete");
        Ref.Close(new[] { -0.10910895, -0.90291513, -0.21137666, -0.3579936, -0.32732684, -0.35696645, 0.57072231, 0.66310492,
                          -0.54554473, 0.18898224, 0.52846832, -0.62240493, -0.76376262, 0.14698618, -0.59187598, 0.21152907 },
            Ref.Data(q2), 1e-8, 1, "complete Q");
        Ref.Close(new[] { -9.16515139, -11.29277582, 0, -0.85042006, 0, 0, 0, 0 }, Ref.Data(r2), 1e-8, 1, "complete R");

        var (q3, r3) = numpy.linalg.qr(Ref.Array(new[] { 2.0, -1, 0, 3, 1, 1, 1, 1 }, 2, 4));
        Ref.Close(new[] { -0.89442719, -0.4472136, -0.4472136, 0.89442719 }, Ref.Data(q3), 1e-8, 1, "wide Q");
        Ref.Close(new[] { -2.23606798, 0.4472136, -0.4472136, -3.13049517, 0, 1.34164079, 0.89442719, -0.4472136 }, Ref.Data(r3), 1e-8, 1, "wide R");
    }

    [Fact]
    public void RankDeficientAndZeroColumns()
    {
        // a zero column gives tau = 0 (H = I); a repeated column gives a zero on R's diagonal
        int m = 40, n = 6;
        var a = Ref.Random(m * n, 9);
        for (int i = 0; i < m; i++) { a[i * n + 2] = 0; a[i * n + 4] = a[i * n + 1]; }
        CheckQr(a, m, n, false, "rank deficient");
    }
}
