// Copyright (c) 2026 The Typhon contributors.
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

namespace Typhon.Stdlib;

internal static partial class Lapack
{
    // ================================================================== symmetric eigenproblem (syev)

    /// <summary>
    /// Eigenvalues (ascending, into w) and optionally eigenvectors (columns of the n x n row-major z) of the symmetric
    /// matrix a, of which only the lower (or upper) triangle is read. Householder tridiagonalization (dsytd2), the
    /// orthogonal factor from <see cref="Orgqr"/>, then implicit QL with shifts on the tridiagonal matrix, applying
    /// the rotations to rows of Q^T (contiguous) rather than columns of Q. Returns false if QL does not converge.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    internal static bool Syev(double[] a, int n, bool lower, double[] w, double[] z)
    {
        if (n == 0) return true;
        var s = new double[n * n];
        for (int i = 0; i < n; i++)
            for (int j = 0; j < n; j++)
                s[i * n + j] = (i >= j) == lower ? a[i * n + j] : a[j * n + i];

        var d = new double[n];
        var e = new double[n]; // e[i] couples rows i and i + 1; e[n - 1] = 0
        var tau = new double[Math.Max(1, n - 1)];
        Tridiagonalize(s, n, d, e, tau);

        double[] wt = null; // Q^T, then (Q Z_tri)^T: its rows become the eigenvectors
        if (z != null)
        {
            wt = new double[n * n];
            wt[0] = 1.0;
            if (n > 1)
            {
                // Q = diag(1, Q'), Q' = H_0...H_{n-2} on indices 1..n-1, v_c stored right of the diagonal in row c of s
                int m = n - 1;
                var f = new double[m * m];
                for (int c = 0; c < m; c++)
                    for (int r = c + 1; r < m; r++) f[r * m + c] = s[c * n + 1 + r];
                var q = Orgqr(f, m, m, m, tau, m);
                for (int r = 0; r < m; r++)
                    for (int c = 0; c < m; c++) wt[(c + 1) * n + r + 1] = q[r * m + c];
            }
        }

        if (!TridiagonalQl(d, e, n, wt)) return false;

        var order = new int[n];
        for (int i = 0; i < n; i++) order[i] = i;
        Array.Sort((double[])d.Clone(), order);
        for (int i = 0; i < n; i++) w[i] = d[order[i]];
        if (z != null)
            for (int c = 0; c < n; c++)
            {
                int src = order[c] * n;
                for (int r = 0; r < n; r++) z[r * n + c] = wt[src + r];
            }
        return true;
    }

    /// <summary>
    /// Householder reduction of the full symmetric matrix s to tridiagonal form (diagonal d, off-diagonal e). Step k
    /// zeroes row/column k beyond the subdiagonal; v_k (v_k[0] = 1 implied) is left in s[k, k + 2 ..], tau[k] its scalar.
    /// The trailing block gets the symmetric rank-2 update A -= v w^T + w v^T row by row.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static void Tridiagonalize(double[] s, int n, double[] d, double[] e, double[] tau)
    {
        ref double s0 = ref MemoryMarshal.GetArrayDataReference(s);
        var p = new double[n];
        ref double p0 = ref MemoryMarshal.GetArrayDataReference(p);
        for (int k = 0; k < n - 1; k++)
        {
            d[k] = s[k * n + k];
            int len = n - k - 1;
            ref double x = ref Unsafe.Add(ref s0, (nint)k * n + k + 1); // row k right of the diagonal = column k below it
            double t = Larfg(ref x, len);
            e[k] = x;
            tau[k] = t;
            if (t == 0) continue;
            double beta = x;
            x = 1.0;
            // p = t * A22 v
            for (int i = 0; i < len; i++)
                Unsafe.Add(ref p0, i) = t * Dot(ref Unsafe.Add(ref s0, (nint)(k + 1 + i) * n + k + 1), ref x, len);
            // w = p - (t/2)(p . v) v, kept in p
            double alpha = -0.5 * t * Dot(ref p0, ref x, len);
            AxpyNeg(-alpha, ref x, ref p0, len);
            // A22 -= v w^T + w v^T
            for (int i = 0; i < len; i++)
            {
                ref double row = ref Unsafe.Add(ref s0, (nint)(k + 1 + i) * n + k + 1);
                AxpyNeg(Unsafe.Add(ref x, i), ref p0, ref row, len);
                AxpyNeg(Unsafe.Add(ref p0, i), ref x, ref row, len);
            }
            x = beta;
        }
        d[n - 1] = s[(n - 1) * n + n - 1];
        e[n - 1] = 0;
    }

    /// <summary>
    /// Eigenvalues of the symmetric tridiagonal matrix (d, e) by implicit QL with shifts (the tql2 / tqli scheme),
    /// in place in d. When wt is given, every plane rotation of rows i and i + 1 is applied to its rows i and i + 1.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static bool TridiagonalQl(double[] d, double[] e, int n, double[] wt)
    {
        for (int l = 0; l < n; l++)
        {
            int iter = 0, m;
            do
            {
                for (m = l; m < n - 1; m++)
                {
                    double dd = Math.Abs(d[m]) + Math.Abs(d[m + 1]);
                    if (Math.Abs(e[m]) + dd == dd) break;
                }
                if (m == l) break;
                if (iter++ == 60) return false;
                double g = (d[l + 1] - d[l]) / (2.0 * e[l]);
                double r = double.Hypot(g, 1.0);
                g = d[m] - d[l] + e[l] / (g + Math.CopySign(r, g));
                double s = 1.0, c = 1.0, p = 0.0;
                int i;
                for (i = m - 1; i >= l; i--)
                {
                    double f = s * e[i], b = c * e[i];
                    e[i + 1] = r = double.Hypot(f, g);
                    if (r == 0)
                    {
                        d[i + 1] -= p;
                        e[m] = 0;
                        break;
                    }
                    s = f / r;
                    c = g / r;
                    g = d[i + 1] - p;
                    r = (d[i] - g) * s + 2.0 * c * b;
                    p = s * r;
                    d[i + 1] = g + p;
                    g = c * r - b;
                    if (wt != null) RotateRows(wt, n, i, c, s);
                }
                if (r == 0 && i >= l) continue;
                d[l] -= p;
                e[l] = g;
                e[m] = 0;
            } while (m != l);
        }
        return true;
    }

    /// <summary>Rows i and i + 1 of the n x n matrix: (x, y) := (c x - s y, s x + c y).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void RotateRows(double[] wt, int n, int i, double c, double s)
    {
        ref double x = ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(wt), (nint)i * n);
        Rotate(ref x, ref Unsafe.Add(ref x, n), n, c, s);
    }

    // ================================================================== singular value decomposition

    /// <summary>
    /// SVD a = U diag(s) Vt of the m x n matrix a (row-major), s descending. Thin (U m x k, Vt k x n, k = min(m, n))
    /// or full (U m x m, Vt n x n); with vectors = false only s. Method: QR (so the iteration works on a k x k
    /// triangle), then one-sided Jacobi (Hestenes) on its columns, which is accurate even for tiny singular values.
    /// Pairs are visited in round-robin order: each step rotates k/2 disjoint pairs (threaded for large k), and the
    /// result does not depend on the thread count. Returns false if the sweeps do not converge.
    /// </summary>
    internal static bool Gesvd(double[] a, int m, int n, bool vectors, bool full, out double[] s, out double[] u, out double[] vt)
    {
        if (m < n)
        {
            // a^T (n x m, tall) = U' S V'^T  =>  a = V' S U'^T: U = V'^T^T (m x m), Vt = U'^T
            bool ok = Gesvd(Transpose(a, m, n), n, m, vectors, full, out s, out var u2, out var vt2);
            u = vt = null;
            if (!ok || !vectors) return ok;
            u = Transpose(vt2, m, m);
            vt = Transpose(u2, n, u2.Length / n);
            return true;
        }

        int k = n;
        s = new double[k];
        u = vt = null;
        if (k == 0)
        {
            if (vectors)
            {
                u = full ? Identity(m) : new double[0];
                vt = new double[0];
            }
            return true;
        }

        // a = Q R, R k x k upper triangular
        var f = (double[])a.Clone();
        var tau = new double[k];
        Geqrf(f, n, m, n, tau);
        // g = R^T: row j of g is column j of R
        var g = new double[k * k];
        for (int i = 0; i < k; i++)
            for (int j = i; j < k; j++) g[j * k + i] = f[i * n + j];
        double[] v = vectors ? Identity(k) : null; // V^T: rows are the right singular vectors

        if (!Jacobi(g, v, k)) return false;

        var norms = new double[k];
        for (int j = 0; j < k; j++) norms[j] = Math.Sqrt(Dot(ref g[j * k], ref g[j * k], k));
        var order = new int[k];
        for (int j = 0; j < k; j++) order[j] = j;
        Array.Sort(order, (x, y) => norms[y].CompareTo(norms[x]) != 0 ? norms[y].CompareTo(norms[x]) : x.CompareTo(y));
        for (int j = 0; j < k; j++) s[j] = norms[order[j]];
        if (!vectors) return true;

        // U_r (k x k): column j = g_order[j] / s_j (Jacobi leaves the rows orthogonal relative to their norms, so this
        // holds for tiny s_j too), completed to an orthonormal basis where s_j is zero
        var ur = new double[k * k];
        int rank = 0;
        for (int j = 0; j < k; j++)
        {
            if (s[j] < SafeMin) break;
            int src = order[j] * k;
            for (int r = 0; r < k; r++) ur[r * k + j] = g[src + r] / s[j];
            rank++;
        }
        CompleteBasis(ur, k, rank);

        vt = new double[k * n];
        for (int j = 0; j < k; j++) Array.Copy(v, order[j] * k, vt, j * n, k);

        int qc = full ? m : k;
        var q = Orgqr(f, n, m, k, tau, qc); // m x qc
        u = new double[m * qc];
        // U[:, :k] = Q[:, :k] U_r; U[:, k:] = Q[:, k:]
        Gemm.Run(m, k, k, 1.0, q, 0, qc, 1, ur, 0, k, 1, u, 0, qc, accumulate: false);
        for (int r = 0; r < m; r++)
            for (int c = k; c < qc; c++) u[r * qc + c] = q[r * qc + c];
        return true;
    }

    /// <summary>
    /// One-sided Jacobi on the rows of g (k x k): rotates pairs of rows until all are orthogonal, applying the same
    /// rotations to the rows of v (when given).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static bool Jacobi(double[] g, double[] v, int k)
    {
        int players = k + (k & 1); // round-robin over an even count (k odd: one idle row per step)
        var pos = new int[players];
        for (int i = 0; i < players; i++) pos[i] = i;
        var sq = new double[k];
        bool parallel = k >= 192 && NumPyRuntime.MaxDegreeOfParallelism > 1;
        double Tol = Math.Sqrt(k) * 2.220446049250313e-16; // dgesvj's convergence test: |g_i . g_j| <= sqrt(k) eps |g_i| |g_j|
        for (int sweep = 0; sweep < 60; sweep++)
        {
            for (int j = 0; j < k; j++) sq[j] = Dot(ref g[j * k], ref g[j * k], k);
            int rotations = 0;
            for (int step = 0; step < players - 1; step++)
            {
                int pairs = players / 2;
                if (parallel)
                {
                    int count = 0;
                    System.Threading.Tasks.Parallel.For(0, pairs, new System.Threading.Tasks.ParallelOptions { MaxDegreeOfParallelism = NumPyRuntime.MaxDegreeOfParallelism },
                        () => 0, (p, _, local) => local + JacobiPair(g, v, k, sq, pos[p], pos[players - 1 - p], Tol),
                        local => System.Threading.Interlocked.Add(ref count, local));
                    rotations += count;
                }
                else
                {
                    for (int p = 0; p < pairs; p++) rotations += JacobiPair(g, v, k, sq, pos[p], pos[players - 1 - p], Tol);
                }
                // circle method: keep pos[0], rotate the rest by one
                int last = pos[players - 1];
                for (int i = players - 1; i > 1; i--) pos[i] = pos[i - 1];
                pos[1] = last;
            }
            if (rotations == 0) return true;
        }
        return false;
    }

    /// <summary>Orthogonalizes rows i and j of g (one Jacobi rotation); returns 1 if it rotated, 0 if they were orthogonal.</summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static int JacobiPair(double[] g, double[] v, int k, double[] sq, int i, int j, double tol)
    {
        if (i >= k || j >= k) return 0;
        if (i > j) (i, j) = (j, i);
        double alpha = sq[i], beta = sq[j];
        if (alpha == 0 || beta == 0) return 0;
        ref double gi = ref g[i * k];
        ref double gj = ref g[j * k];
        double gamma = Dot(ref gi, ref gj, k);
        if (Math.Abs(gamma) <= tol * Math.Sqrt(alpha) * Math.Sqrt(beta)) return 0;
        double zeta = (beta - alpha) / (2.0 * gamma);
        double t = Math.CopySign(1.0, zeta) / (Math.Abs(zeta) + Math.Sqrt(1.0 + zeta * zeta));
        double c = 1.0 / Math.Sqrt(1.0 + t * t), sn = c * t;
        Rotate(ref gi, ref gj, k, c, sn);
        if (v != null) Rotate(ref v[i * k], ref v[j * k], k, c, sn);
        sq[i] = alpha - t * gamma;
        sq[j] = beta + t * gamma;
        return 1;
    }

    /// <summary>Columns [rank, k) of the k x k matrix ur := an orthonormal completion of its first rank columns.</summary>
    private static void CompleteBasis(double[] ur, int k, int rank)
    {
        var col = new double[k];
        int next = 0;
        for (int j = rank; j < k; j++)
        {
            for (; next < k; next++)
            {
                Array.Clear(col);
                col[next] = 1.0;
                for (int pass = 0; pass < 2; pass++)
                    for (int c = 0; c < j; c++)
                    {
                        double d = 0;
                        for (int r = 0; r < k; r++) d += ur[r * k + c] * col[r];
                        for (int r = 0; r < k; r++) col[r] -= d * ur[r * k + c];
                    }
                double nrm = Math.Sqrt(Dot(ref col[0], ref col[0], k));
                if (nrm > 0.5)
                {
                    for (int r = 0; r < k; r++) ur[r * k + j] = col[r] / nrm;
                    next++;
                    break;
                }
            }
        }
    }

    private static double[] Identity(int n)
    {
        var e = new double[n * n];
        for (int i = 0; i < n; i++) e[i * n + i] = 1.0;
        return e;
    }

    private static double[] Transpose(double[] a, int rows, int cols)
    {
        var t = new double[rows * cols];
        for (int i = 0; i < rows; i++)
            for (int j = 0; j < cols; j++) t[j * rows + i] = a[i * cols + j];
        return t;
    }

    /// <summary>(x, y) := (c x - s y, s x + c y) over n elements.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Rotate(ref double x, ref double y, int n, double c, double s)
    {
        int k = 0;
        if (Vector256.IsHardwareAccelerated)
        {
            var vc = Vector256.Create(c);
            var vs = Vector256.Create(s);
            for (; k + 4 <= n; k += 4)
            {
                var vx = Vector256.LoadUnsafe(ref x, (nuint)k);
                var vy = Vector256.LoadUnsafe(ref y, (nuint)k);
                (vc * vx - vs * vy).StoreUnsafe(ref x, (nuint)k);
                (vs * vx + vc * vy).StoreUnsafe(ref y, (nuint)k);
            }
        }
        else if (Vector128.IsHardwareAccelerated)
        {
            var vc = Vector128.Create(c);
            var vs = Vector128.Create(s);
            for (; k + 2 <= n; k += 2)
            {
                var vx = Vector128.LoadUnsafe(ref x, (nuint)k);
                var vy = Vector128.LoadUnsafe(ref y, (nuint)k);
                (vc * vx - vs * vy).StoreUnsafe(ref x, (nuint)k);
                (vs * vx + vc * vy).StoreUnsafe(ref y, (nuint)k);
            }
        }
        for (; k < n; k++)
        {
            double vx = Unsafe.Add(ref x, k), vy = Unsafe.Add(ref y, k);
            Unsafe.Add(ref x, k) = c * vx - s * vy;
            Unsafe.Add(ref y, k) = s * vx + c * vy;
        }
    }
}
