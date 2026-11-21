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
        var rc = wt != null ? new double[n] : null;
        var rs = wt != null ? new double[n] : null;
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
                    if (wt != null) { rc[i] = c; rs[i] = s; }
                }
                // this sweep's rotations m-1 .. i+1, in that order, applied to rows of wt chunk by chunk
                if (wt != null && i + 1 <= m - 1) ApplyRotations(wt, n, i + 1, m, rc, rs, descending: true, negate: false);
                if (r == 0 && i >= l) continue;
                d[l] -= p;
                e[l] = g;
                e[m] = 0;
            } while (m != l);
        }
        return true;
    }

    // ================================================================== singular value decomposition

    /// <summary>
    /// SVD a = U diag(s) Vt of the m x n matrix a (row-major), s descending. Thin (U m x k, Vt k x n, k = min(m, n))
    /// or full (U m x m, Vt n x n); with vectors = false only s. Golub-Kahan-Reinsch: QR (the rest works on the
    /// k x k triangle), Householder bidiagonalization, then implicit-shift QR sweeps on the bidiagonal; each sweep's
    /// rotations are applied to U^T and V^T afterwards in column chunks (LAPACK's dlasr idea: cache-friendly and
    /// threaded, with the same result on any number of threads). Returns false if the sweeps do not converge.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    internal static bool Gesvd(double[] a, int m, int n, bool vectors, bool full, out double[] s, out double[] u, out double[] vt)
    {
        if (m < n)
        {
            // a^T (n x m, tall) = U' S V'^T  =>  a = V' S U'^T: U = (V'^T)^T (m x m), Vt = U'^T
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

        // a = Q R; r = R (k x k)
        var f = (double[])a.Clone();
        var tau = new double[k];
        Geqrf(f, n, m, n, tau);
        var r = new double[k * k];
        for (int i = 0; i < k; i++) Array.Copy(f, i * n + i, r, i * k + i, k - i);

        // R = U1 B V1^T, B upper bidiagonal (d, e)
        var d = new double[k];
        var e = new double[k];
        var tauq = new double[k];
        var taup = new double[k];
        Bidiagonalize(r, k, d, e, tauq, taup);

        double[] ut = null, vtr = null; // (U1 Ub)^T and (V1 Vb)^T: rotations act on their rows
        if (vectors)
        {
            ut = Transpose(Orgqr(r, k, k, k, tauq, k), k, k);
            vtr = new double[k * k];
            vtr[0] = 1.0;
            if (k > 1)
            {
                int mm = k - 1;
                var g = new double[mm * mm];
                for (int c = 0; c < mm; c++)
                    for (int rr = c + 1; rr < mm; rr++) g[rr * mm + c] = r[c * k + 1 + rr];
                var q1 = Orgqr(g, mm, mm, mm, taup, mm); // V1 = diag(1, q1), V1^T = diag(1, q1^T)
                for (int i = 0; i < mm; i++)
                    for (int j = 0; j < mm; j++) vtr[(i + 1) * k + j + 1] = q1[j * mm + i];
            }
        }

        if (!BidiagonalQr(d, e, k, ut, vtr)) return false;

        // non-negative singular values, descending
        for (int i = 0; i < k; i++)
            if (d[i] < 0)
            {
                d[i] = -d[i];
                if (vtr != null) for (int j = 0; j < k; j++) vtr[i * k + j] = -vtr[i * k + j];
            }
        var order = new int[k];
        for (int i = 0; i < k; i++) order[i] = i;
        Array.Sort(order, (x, y) => d[y] != d[x] ? d[y].CompareTo(d[x]) : x.CompareTo(y));
        for (int i = 0; i < k; i++) s[i] = d[order[i]];
        if (!vectors) return true;

        vt = new double[k * n];
        var urt = new double[k * k]; // rows: left singular vectors of R, sorted
        for (int i = 0; i < k; i++)
        {
            Array.Copy(vtr, order[i] * k, vt, i * n, k);
            Array.Copy(ut, order[i] * k, urt, i * k, k);
        }

        int qc = full ? m : k;
        var q = Orgqr(f, n, m, k, tau, qc); // m x qc
        u = new double[m * qc];
        // U[:, :k] = Q[:, :k] U_R (U_R = urt^T); U[:, k:] = Q[:, k:]
        Gemm.Run(m, k, k, 1.0, q, 0, qc, 1, urt, 0, 1, k, u, 0, qc, accumulate: false);
        for (int i = 0; i < m; i++)
            for (int c = k; c < qc; c++) u[i * qc + c] = q[i * qc + c];
        return true;
    }

    /// <summary>
    /// Householder bidiagonalization (dgebd2) of the dense n x n matrix r: d the diagonal, e the superdiagonal. The
    /// left vectors (columns, v[0] = 1 implied) are left below the diagonal with tauq, the right vectors (rows) right
    /// of the superdiagonal with taup. Both updates of the trailing block are row operations (contiguous).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static void Bidiagonalize(double[] r, int n, double[] d, double[] e, double[] tauq, double[] taup)
    {
        ref double r0 = ref MemoryMarshal.GetArrayDataReference(r);
        var col = new double[n];
        var w = new double[n];
        ref double c0 = ref MemoryMarshal.GetArrayDataReference(col);
        ref double w0 = ref MemoryMarshal.GetArrayDataReference(w);
        for (int i = 0; i < n; i++)
        {
            // left reflector: column i, rows i..n-1
            int len = n - i;
            for (int t = 0; t < len; t++) col[t] = r[(i + t) * n + i];
            double tq = Larfg(ref c0, len);
            d[i] = col[0];
            tauq[i] = tq;
            for (int t = 1; t < len; t++) r[(i + t) * n + i] = col[t];
            r[i * n + i] = col[0];
            int nc = n - i - 1;
            if (tq != 0 && nc > 0)
            {
                col[0] = 1.0;
                // w = v^T R[i:, i+1:], then R[i:, i+1:] -= tq v w^T
                Array.Clear(w, 0, nc);
                for (int t = 0; t < len; t++)
                    AxpyNeg(-col[t], ref Unsafe.Add(ref r0, (nint)(i + t) * n + i + 1), ref w0, nc);
                for (int t = 0; t < len; t++)
                    AxpyNeg(tq * col[t], ref w0, ref Unsafe.Add(ref r0, (nint)(i + t) * n + i + 1), nc);
            }
            if (nc == 0) { e[i] = 0; taup[i] = 0; continue; }

            // right reflector: row i, columns i+1..n-1
            ref double x = ref Unsafe.Add(ref r0, (nint)i * n + i + 1);
            double tp = Larfg(ref x, nc);
            e[i] = x;
            taup[i] = tp;
            if (tp != 0)
            {
                x = 1.0;
                for (int t = i + 1; t < n; t++)
                {
                    ref double row = ref Unsafe.Add(ref r0, (nint)t * n + i + 1);
                    AxpyNeg(tp * Dot(ref row, ref x, nc), ref x, ref row, nc);
                }
                x = e[i];
            }
        }
    }

    /// <summary>
    /// Singular values of the upper bidiagonal (d, e[0..n-1)) by implicit-shift QR (Golub-Kahan steps with the Wilkinson
    /// shift, Golub &amp; Van Loan 8.6), in place in d (signs may be negative). Rotations of columns k, k+1 go to rows k,
    /// k+1 of vt, rotations of rows to rows of ut (both optional).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static bool BidiagonalQr(double[] d, double[] e, int n, double[] ut, double[] vt)
    {
        const double Eps = 2.220446049250313e-16;
        double anorm = 0;
        for (int i = 0; i < n; i++) anorm = Math.Max(anorm, Math.Abs(d[i]) + (i < n - 1 ? Math.Abs(e[i]) : 0));
        if (anorm == 0) return true;
        // scale to avoid overflow/underflow in the squared shifts
        double scale = 1.0 / anorm;
        for (int i = 0; i < n; i++) { d[i] *= scale; e[i] *= scale; }
        bool vectors = ut != null;
        var rc = vectors ? new double[n] : null; // one sweep's right rotations (c, s), left rotations (c, s)
        var rs = vectors ? new double[n] : null;
        var lc = vectors ? new double[n] : null;
        var ls = vectors ? new double[n] : null;
        double tiny = Eps; // |d| below this (relative to the norm, now 1) counts as zero
        long maxIter = 75L * n * n + 100;
        long iter = 0;
        int h = n - 1;
        while (h > 0)
        {
            for (int i = 0; i < h; i++)
                if (Math.Abs(e[i]) <= Eps * (Math.Abs(d[i]) + Math.Abs(d[i + 1]))) e[i] = 0;
            if (e[h - 1] == 0) { h--; continue; }
            int l = h - 1;
            while (l > 0 && e[l - 1] != 0) l--;
            if (++iter > maxIter) return false;

            // a zero on the diagonal splits the block: chase its superdiagonal element out
            int z = -1;
            for (int i = l; i <= h; i++)
                if (Math.Abs(d[i]) <= tiny) { z = i; break; }
            if (z >= 0)
            {
                d[z] = 0;
                if (z < h)
                {
                    // rows (j, z), j = z+1..h: zero B[z, j]
                    double fz = e[z];
                    e[z] = 0;
                    for (int j = z + 1; j <= h && fz != 0; j++)
                    {
                        double rr = double.Hypot(d[j], fz), c = d[j] / rr, sn = fz / rr;
                        d[j] = rr;
                        if (j < h) { fz = -sn * e[j]; e[j] = c * e[j]; }
                        if (vectors) Rotate(ref ut[j * n], ref ut[z * n], n, c, -sn);
                    }
                }
                else
                {
                    // d[h] = 0: columns (j, h), j = h-1..l: zero B[j, h]
                    double fz = e[h - 1];
                    e[h - 1] = 0;
                    for (int j = h - 1; j >= l && fz != 0; j--)
                    {
                        double rr = double.Hypot(d[j], fz), c = d[j] / rr, sn = fz / rr;
                        d[j] = rr;
                        if (j > l) { fz = -sn * e[j - 1]; e[j - 1] = c * e[j - 1]; }
                        if (vectors) Rotate(ref vt[j * n], ref vt[h * n], n, c, -sn);
                    }
                }
                continue;
            }

            // Wilkinson shift from the trailing 2 x 2 of B^T B
            double t11 = d[h - 1] * d[h - 1] + (h - 1 > l ? e[h - 2] * e[h - 2] : 0);
            double t22 = d[h] * d[h] + e[h - 1] * e[h - 1];
            double t12 = d[h - 1] * e[h - 1];
            double delta = 0.5 * (t11 - t22);
            double den = delta + Math.CopySign(double.Hypot(delta, t12), delta);
            double mu = den == 0 ? t22 : t22 - t12 * t12 / den;

            double y = d[l] * d[l] - mu, zz = d[l] * e[l];
            for (int kk = l; kk < h; kk++)
            {
                // columns kk, kk+1
                double rr = double.Hypot(y, zz);
                double c = rr == 0 ? 1 : y / rr, sn = rr == 0 ? 0 : zz / rr;
                if (kk > l) e[kk - 1] = rr;
                y = c * d[kk] + sn * e[kk];
                e[kk] = -sn * d[kk] + c * e[kk];
                zz = sn * d[kk + 1];
                d[kk + 1] = c * d[kk + 1];
                if (vectors) { rc[kk] = c; rs[kk] = sn; }
                // rows kk, kk+1
                rr = double.Hypot(y, zz);
                c = rr == 0 ? 1 : y / rr;
                sn = rr == 0 ? 0 : zz / rr;
                d[kk] = rr;
                y = c * e[kk] + sn * d[kk + 1];
                d[kk + 1] = -sn * e[kk] + c * d[kk + 1];
                if (kk < h - 1)
                {
                    zz = sn * e[kk + 1];
                    e[kk + 1] = c * e[kk + 1];
                }
                if (vectors) { lc[kk] = c; ls[kk] = sn; }
            }
            e[h - 1] = y;
            if (vectors)
            {
                ApplyRotations(vt, n, l, h, rc, rs, descending: false, negate: true);
                ApplyRotations(ut, n, l, h, lc, ls, descending: false, negate: true);
            }
        }
        for (int i = 0; i < n; i++) d[i] *= anorm;
        return true;
    }

    /// <summary>
    /// A sequence of plane rotations kk = lo..hi-1 (or hi-1..lo when descending), each on rows kk and kk+1 of the
    /// n x n matrix m: (x, y) := (c x - s y, s x + c y), with s negated when negate. Done per chunk of columns, so the
    /// rows of a chunk stay in L1 across the whole sequence (LAPACK's dlasr); chunks are independent (threads).
    /// </summary>
    private static void ApplyRotations(double[] m, int n, int lo, int hi, double[] c, double[] s, bool descending, bool negate)
    {
        const int Chunk = 128;
        int chunks = (n + Chunk - 1) / Chunk;
        if (chunks > 1 && NumPyRuntime.MaxDegreeOfParallelism > 1 && (long)(hi - lo) * n >= 1 << 17)
            System.Threading.Tasks.Parallel.For(0, chunks, new System.Threading.Tasks.ParallelOptions { MaxDegreeOfParallelism = NumPyRuntime.MaxDegreeOfParallelism },
                ch => RotationsOnChunk(m, n, lo, hi, c, s, descending, negate, ch * Chunk, Math.Min(Chunk, n - ch * Chunk)));
        else
            for (int ch = 0; ch < chunks; ch++) RotationsOnChunk(m, n, lo, hi, c, s, descending, negate, ch * Chunk, Math.Min(Chunk, n - ch * Chunk));
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static void RotationsOnChunk(double[] m, int n, int lo, int hi, double[] c, double[] s, bool descending, bool negate, int j0, int len)
    {
        ref double m0 = ref MemoryMarshal.GetArrayDataReference(m);
        double sign = negate ? -1.0 : 1.0;
        for (int t = 0; t < hi - lo; t++)
        {
            int kk = descending ? hi - 1 - t : lo + t;
            ref double x = ref Unsafe.Add(ref m0, (nint)kk * n + j0);
            Rotate(ref x, ref Unsafe.Add(ref x, n), len, c[kk], sign * s[kk]);
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
