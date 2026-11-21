// Copyright (c) 2026 The Typhon contributors.
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
using System.Threading.Tasks;

namespace Typhon.Stdlib;

/// <summary>
/// LAPACK-style factorizations on row-major float64 matrices (element (i, j) of a matrix at off + i*ld + j).
/// They are recursive, so most of the work is in a few large matrix products (<see cref="Gemm"/>, threaded);
/// what remains is narrow panels and small triangles, done with vector loops.
/// </summary>
internal static class Lapack
{
    /// <summary>Panels this narrow are factored column by column (8 doubles = two Vector256 per row).</summary>
    private const int LuBase = 8;

    /// <summary>Triangles this small are solved with row operations instead of recursing further.</summary>
    private const int TrsmBase = 32;

    /// <summary>Columns of the right-hand side per independent chunk in the small triangular solves.</summary>
    private const int TrsmChunk = 256;

    /// <summary>LAPACK's sfmin (the smallest normal double): from here on 1/x is finite, so x can be inverted once.</summary>
    private const double SafeMin = 2.2250738585072014E-308;

    // ================================================================== LU (getrf)

    /// <summary>
    /// In-place LU with partial pivoting of the n x n matrix a (row stride lda): P*A = L*U, L unit lower and U upper
    /// stored over A. ipiv[i] is the row that row i was swapped with at step i (LAPACK's convention, 0-based).
    /// Returns 0, or i + 1 when U[i, i] is exactly zero (the factorization still completes, like LAPACK's).
    /// </summary>
    internal static int Getrf(double[] a, int lda, int n, int[] ipiv)
    {
        int info = 0;
        if (n > 0) GetrfRec(a, lda, n, 0, n, ipiv, ref info);
        return info;
    }

    /// <summary>
    /// Factors the panel of rows [r0, n) and columns [r0, r0 + w) (recursive LU, Toledo / LAPACK dgetrf2): the left
    /// half, then the top right block by a triangular solve, the bottom right block by a matrix product, then the
    /// bottom right block itself. Row swaps exchange whole rows of the matrix, so they reach every column.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static void GetrfRec(double[] a, int lda, int n, int r0, int w, int[] ipiv, ref int info)
    {
        if (w <= LuBase)
        {
            if (w == LuBase && Vector256.IsHardwareAccelerated) GetrfPanel8(a, lda, n, r0, ipiv, ref info);
            else GetrfPanel(a, lda, n, r0, w, ipiv, ref info);
            return;
        }
        int w1 = Math.Max(LuBase, (w / 2) & ~(LuBase - 1)); // multiples of 8: the leaves are 8 wide
        int w2 = w - w1;
        GetrfRec(a, lda, n, r0, w1, ipiv, ref info);
        int d = r0 * lda + r0;
        TrsmLowerUnit(w1, w2, a, d, lda, a, d + w1, lda);
        int m2 = n - r0 - w1;
        if (m2 > 0)
            Gemm.Run(m2, w2, w1, -1.0, a, d + w1 * lda, lda, 1, a, d + w1, lda, 1, a, d + w1 * lda + w1, lda, accumulate: true);
        GetrfRec(a, lda, n, r0 + w1, w2, ipiv, ref info);
    }

    /// <summary>Unblocked LU of the panel rows [r0, n) x columns [r0, r0 + w) (any width, scalar).</summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static void GetrfPanel(double[] a, int lda, int n, int r0, int w, int[] ipiv, ref int info)
    {
        for (int j = 0; j < w; j++)
        {
            int row = r0 + j, col = r0 + j;
            int p = Pivot(a, lda, n, row, col);
            ipiv[row] = p;
            double piv = a[p * lda + col];
            if (piv == 0)
            {
                if (info == 0) info = row + 1;
                continue;
            }
            if (p != row) SwapRows(a, lda, n, p, row);
            bool recip = Math.Abs(piv) >= SafeMin;
            double r = 1.0 / piv;
            int c1 = r0 + w;
            for (int i = row + 1; i < n; i++)
            {
                int ri = i * lda;
                double l = recip ? a[ri + col] * r : a[ri + col] / piv;
                a[ri + col] = l;
                if (l == 0) continue;
                int rp = row * lda;
                for (int q = col + 1; q < c1; q++) a[ri + q] -= l * a[rp + q];
            }
        }
    }

    /// <summary>
    /// <see cref="GetrfPanel"/> for an 8-wide panel: each row's panel segment is two Vector256, so the rank-1
    /// update of a row is two FMAs (the pivot row is masked to the columns right of the current one).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static void GetrfPanel8(double[] a, int lda, int n, int r0, int[] ipiv, ref int info)
    {
        Debug.Assert(r0 + LuBase <= n && (long)(n - 1) * lda + r0 + LuBase <= a.Length);
        ref double a0 = ref MemoryMarshal.GetArrayDataReference(a);
        var lane = Vector256.Create(0.0, 1.0, 2.0, 3.0);
        for (int j = 0; j < LuBase; j++)
        {
            int row = r0 + j, col = r0 + j;
            int p = Pivot(a, lda, n, row, col);
            ipiv[row] = p;
            double piv = a[p * lda + col];
            if (piv == 0)
            {
                if (info == 0) info = row + 1;
                continue;
            }
            if (p != row) SwapRows(a, lda, n, p, row);
            bool recip = Math.Abs(piv) >= SafeMin;
            double r = 1.0 / piv;
            ref double prow = ref Unsafe.Add(ref a0, (nint)row * lda + r0);
            var jj = Vector256.Create((double)j);
            var u0 = Vector256.ConditionalSelect(Vector256.GreaterThan(lane, jj), Vector256.LoadUnsafe(ref prow), Vector256<double>.Zero);
            var u1 = Vector256.ConditionalSelect(Vector256.GreaterThan(lane + Vector256.Create(4.0), jj), Vector256.LoadUnsafe(ref prow, 4), Vector256<double>.Zero);
            for (int i = row + 1; i < n; i++)
            {
                ref double ai = ref Unsafe.Add(ref a0, (nint)i * lda + r0);
                double l = recip ? Unsafe.Add(ref ai, j) * r : Unsafe.Add(ref ai, j) / piv;
                if (l != 0)
                {
                    var vl = Vector256.Create(l);
                    var v0 = Vector256.LoadUnsafe(ref ai);
                    var v1 = Vector256.LoadUnsafe(ref ai, 4);
                    if (Fma.IsSupported)
                    {
                        v0 = Fma.MultiplyAddNegated(vl, u0, v0);
                        v1 = Fma.MultiplyAddNegated(vl, u1, v1);
                    }
                    else
                    {
                        v0 -= vl * u0;
                        v1 -= vl * u1;
                    }
                    v0.StoreUnsafe(ref ai);
                    v1.StoreUnsafe(ref ai, 4);
                }
                Unsafe.Add(ref ai, j) = l;
            }
        }
    }

    /// <summary>Row index in [row, n) of the first largest |a[i, col]| (LAPACK's idamax).</summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static int Pivot(double[] a, int lda, int n, int row, int col)
    {
        int p = row;
        double max = Math.Abs(a[row * lda + col]);
        for (int i = row + 1; i < n; i++)
        {
            double v = Math.Abs(a[i * lda + col]);
            if (v > max) { max = v; p = i; }
        }
        return p;
    }

    /// <summary>Exchanges rows i and j (the first `cols` elements of each).</summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    internal static void SwapRows(double[] a, int lda, int cols, int i, int j)
    {
        if (i == j || cols == 0) return;
        ref double x = ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(a), (nint)i * lda);
        ref double y = ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(a), (nint)j * lda);
        Debug.Assert((long)Math.Max(i, j) * lda + cols <= a.Length);
        int k = 0;
        if (Vector256.IsHardwareAccelerated)
        {
            for (; k + 4 <= cols; k += 4)
            {
                var t = Vector256.LoadUnsafe(ref x, (nuint)k);
                Vector256.LoadUnsafe(ref y, (nuint)k).StoreUnsafe(ref x, (nuint)k);
                t.StoreUnsafe(ref y, (nuint)k);
            }
        }
        for (; k < cols; k++) (Unsafe.Add(ref x, k), Unsafe.Add(ref y, k)) = (Unsafe.Add(ref y, k), Unsafe.Add(ref x, k));
    }

    /// <summary>Applies the row interchanges of <see cref="Getrf"/> to the m x cols matrix b (P*b).</summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    internal static void ApplyPivots(int[] ipiv, int n, double[] b, int ldb, int cols)
    {
        for (int i = 0; i < n; i++)
            if (ipiv[i] != i) SwapRows(b, ldb, cols, i, ipiv[i]);
    }

    // ================================================================== triangular solves (trsm / trsv)

    /// <summary>B (m x nrhs) := L^-1 * B, L the unit lower triangle of the m x m block at l[lOff] (diagonal not read).</summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    internal static void TrsmLowerUnit(int m, int nrhs, double[] l, int lOff, int ldl, double[] b, int bOff, int ldb)
    {
        if (m <= 0 || nrhs <= 0) return;
        if (m <= TrsmBase)
        {
            if (!Wide(m, nrhs)) TrsmLowerUnitBase(m, l, lOff, ldl, b, bOff, ldb, 0, nrhs);
            else ForChunks(nrhs, (j0, len) => TrsmLowerUnitBase(m, l, lOff, ldl, b, bOff, ldb, j0, len));
            return;
        }
        int m1 = Half(m);
        TrsmLowerUnit(m1, nrhs, l, lOff, ldl, b, bOff, ldb);
        Gemm.Run(m - m1, nrhs, m1, -1.0, l, lOff + m1 * ldl, ldl, 1, b, bOff, ldb, 1, b, bOff + m1 * ldb, ldb, accumulate: true);
        TrsmLowerUnit(m - m1, nrhs, l, lOff + m1 * ldl + m1, ldl, b, bOff + m1 * ldb, ldb);
    }

    /// <summary>Columns [j0, j0 + len) of the small lower solve: row i -= L[i, p] * row p.</summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static void TrsmLowerUnitBase(int m, double[] l, int lOff, int ldl, double[] b, int bOff, int ldb, int j0, int len)
    {
        ref double b0 = ref MemoryMarshal.GetArrayDataReference(b);
        for (int i = 1; i < m; i++)
        {
            ref double bi = ref Unsafe.Add(ref b0, (nint)bOff + (nint)i * ldb + j0);
            for (int p = 0; p < i; p++)
            {
                double lip = l[lOff + i * ldl + p];
                if (lip != 0) AxpyNeg(lip, ref Unsafe.Add(ref b0, (nint)bOff + (nint)p * ldb + j0), ref bi, len);
            }
        }
    }

    /// <summary>B (m x nrhs) := U^-1 * B, U the upper triangle (with its diagonal) of the m x m block at u[uOff].</summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    internal static void TrsmUpper(int m, int nrhs, double[] u, int uOff, int ldu, double[] b, int bOff, int ldb)
    {
        if (m <= 0 || nrhs <= 0) return;
        if (m <= TrsmBase)
        {
            if (!Wide(m, nrhs)) TrsmUpperBase(m, u, uOff, ldu, b, bOff, ldb, 0, nrhs);
            else ForChunks(nrhs, (j0, len) => TrsmUpperBase(m, u, uOff, ldu, b, bOff, ldb, j0, len));
            return;
        }
        int m1 = Half(m), m2 = m - m1;
        TrsmUpper(m2, nrhs, u, uOff + m1 * ldu + m1, ldu, b, bOff + m1 * ldb, ldb);
        Gemm.Run(m1, nrhs, m2, -1.0, u, uOff + m1, ldu, 1, b, bOff + m1 * ldb, ldb, 1, b, bOff, ldb, accumulate: true);
        TrsmUpper(m1, nrhs, u, uOff, ldu, b, bOff, ldb);
    }

    /// <summary>Columns [j0, j0 + len) of the small upper solve, bottom row first.</summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static void TrsmUpperBase(int m, double[] u, int uOff, int ldu, double[] b, int bOff, int ldb, int j0, int len)
    {
        ref double b0 = ref MemoryMarshal.GetArrayDataReference(b);
        for (int i = m - 1; i >= 0; i--)
        {
            ref double bi = ref Unsafe.Add(ref b0, (nint)bOff + (nint)i * ldb + j0);
            for (int p = i + 1; p < m; p++)
            {
                double uip = u[uOff + i * ldu + p];
                if (uip != 0) AxpyNeg(uip, ref Unsafe.Add(ref b0, (nint)bOff + (nint)p * ldb + j0), ref bi, len);
            }
            double d = u[uOff + i * ldu + i];
            for (int j = 0; j < len; j++) Unsafe.Add(ref bi, j) /= d;
        }
    }

    /// <summary>
    /// B (m x n) := B * L^-T, L the lower triangle (with its diagonal) of the n x n block at l[lOff]: solves
    /// X * L^T = B row by row (the off-diagonal blocks of a Cholesky factor).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    internal static void TrsmRightLowerTrans(int m, int n, double[] l, int lOff, int ldl, double[] b, int bOff, int ldb)
    {
        if (m <= 0 || n <= 0) return;
        if (n <= TrsmBase)
        {
            if (!Wide(n, m)) TrsmRightLowerTransBase(n, l, lOff, ldl, b, bOff, ldb, 0, m);
            else ForChunks(m, (i0, cnt) => TrsmRightLowerTransBase(n, l, lOff, ldl, b, bOff, ldb, i0, cnt));
            return;
        }
        int n1 = Half(n), n2 = n - n1;
        TrsmRightLowerTrans(m, n1, l, lOff, ldl, b, bOff, ldb);
        // B2 -= X1 * L21^T; L21 (n2 x n1) read transposed through its strides
        Gemm.Run(m, n2, n1, -1.0, b, bOff, ldb, 1, l, lOff + n1 * ldl, 1, ldl, b, bOff + n1, ldb, accumulate: true);
        TrsmRightLowerTrans(m, n2, l, lOff + n1 * ldl + n1, ldl, b, bOff + n1, ldb);
    }

    /// <summary>Rows [i0, i0 + cnt) of the small right solve: forward substitution with dot products.</summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static void TrsmRightLowerTransBase(int n, double[] l, int lOff, int ldl, double[] b, int bOff, int ldb, int i0, int cnt)
    {
        ref double b0 = ref MemoryMarshal.GetArrayDataReference(b);
        ref double l0 = ref MemoryMarshal.GetArrayDataReference(l);
        for (int i = i0; i < i0 + cnt; i++)
        {
            ref double x = ref Unsafe.Add(ref b0, (nint)bOff + (nint)i * ldb);
            for (int j = 0; j < n; j++)
            {
                ref double lj = ref Unsafe.Add(ref l0, (nint)lOff + (nint)j * ldl);
                Unsafe.Add(ref x, j) = (Unsafe.Add(ref x, j) - Dot(ref lj, ref x, j)) / Unsafe.Add(ref lj, j);
            }
        }
    }

    /// <summary>x := L^-1 x (unit lower) for one right-hand side: a dot product per row.</summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    internal static void TrsvLowerUnit(int n, double[] l, int ldl, double[] x)
    {
        Debug.Assert(x.Length >= n && (long)(n - 1) * ldl + n <= l.Length);
        ref double l0 = ref MemoryMarshal.GetArrayDataReference(l);
        ref double x0 = ref MemoryMarshal.GetArrayDataReference(x);
        for (int i = 1; i < n; i++)
            Unsafe.Add(ref x0, i) -= Dot(ref Unsafe.Add(ref l0, (nint)i * ldl), ref x0, i);
    }

    /// <summary>x := U^-1 x for one right-hand side.</summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    internal static void TrsvUpper(int n, double[] u, int ldu, double[] x)
    {
        Debug.Assert(x.Length >= n && (long)(n - 1) * ldu + n <= u.Length);
        ref double u0 = ref MemoryMarshal.GetArrayDataReference(u);
        ref double x0 = ref MemoryMarshal.GetArrayDataReference(x);
        for (int i = n - 1; i >= 0; i--)
        {
            ref double ui = ref Unsafe.Add(ref u0, (nint)i * ldu);
            double s = Unsafe.Add(ref x0, i) - Dot(ref Unsafe.Add(ref ui, i + 1), ref Unsafe.Add(ref x0, i + 1), n - i - 1);
            Unsafe.Add(ref x0, i) = s / Unsafe.Add(ref ui, i);
        }
    }

    /// <summary>sqrt(x . x) over the whole array.</summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    internal static double Norm2(double[] x) =>
        x.Length == 0 ? 0.0 : Math.Sqrt(Dot(ref MemoryMarshal.GetArrayDataReference(x), ref MemoryMarshal.GetArrayDataReference(x), x.Length));

    /// <summary>Whether a small triangular solve (m x m triangle, `cols` columns) is worth splitting across threads.</summary>
    private static bool Wide(int m, int cols) =>
        cols > TrsmChunk && NumPyRuntime.MaxDegreeOfParallelism > 1 && (long)m * m * cols >= NumPyRuntime.MatMulParallelThreshold;

    /// <summary>Runs body(start, count) over [0, count) in chunks of TrsmChunk, across threads.</summary>
    private static void ForChunks(int count, Action<int, int> body)
    {
        int chunks = (count + TrsmChunk - 1) / TrsmChunk;
        Parallel.For(0, chunks, new ParallelOptions { MaxDegreeOfParallelism = NumPyRuntime.MaxDegreeOfParallelism },
            c => body(c * TrsmChunk, Math.Min(TrsmChunk, count - c * TrsmChunk)));
    }

    /// <summary>y[0..n) -= a * x[0..n).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void AxpyNeg(double a, ref double x, ref double y, int n)
    {
        int j = 0;
        if (Vector256.IsHardwareAccelerated && n >= 4)
        {
            var va = Vector256.Create(a);
            for (; j + 4 <= n; j += 4)
            {
                var vy = Vector256.LoadUnsafe(ref y, (nuint)j);
                var vx = Vector256.LoadUnsafe(ref x, (nuint)j);
                (Fma.IsSupported ? Fma.MultiplyAddNegated(va, vx, vy) : vy - va * vx).StoreUnsafe(ref y, (nuint)j);
            }
        }
        for (; j < n; j++) Unsafe.Add(ref y, j) -= a * Unsafe.Add(ref x, j);
    }

    /// <summary>sum x[i] * y[i] over [0, n), four vector accumulators.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static double Dot(ref double x, ref double y, int n)
    {
        int j = 0;
        double s = 0;
        if (Vector256.IsHardwareAccelerated && n >= 16)
        {
            Vector256<double> s0 = default, s1 = default, s2 = default, s3 = default;
            for (; j + 16 <= n; j += 16)
            {
                nuint u = (nuint)j;
                s0 = Fma256(Vector256.LoadUnsafe(ref x, u), Vector256.LoadUnsafe(ref y, u), s0);
                s1 = Fma256(Vector256.LoadUnsafe(ref x, u + 4), Vector256.LoadUnsafe(ref y, u + 4), s1);
                s2 = Fma256(Vector256.LoadUnsafe(ref x, u + 8), Vector256.LoadUnsafe(ref y, u + 8), s2);
                s3 = Fma256(Vector256.LoadUnsafe(ref x, u + 12), Vector256.LoadUnsafe(ref y, u + 12), s3);
            }
            for (; j + 4 <= n; j += 4)
                s0 = Fma256(Vector256.LoadUnsafe(ref x, (nuint)j), Vector256.LoadUnsafe(ref y, (nuint)j), s0);
            s = Vector256.Sum((s0 + s1) + (s2 + s3));
        }
        for (; j < n; j++) s += Unsafe.Add(ref x, j) * Unsafe.Add(ref y, j);
        return s;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector256<double> Fma256(Vector256<double> a, Vector256<double> b, Vector256<double> c) =>
        Fma.IsSupported ? Fma.MultiplyAdd(a, b, c) : a * b + c;

    /// <summary>The first part of a recursive split: half, rounded to a multiple of 8 (whole micro-kernel rows).</summary>
    private static int Half(int m) => m >= 16 ? (m / 2) & ~7 : m / 2;

    // ================================================================== Cholesky (potrf)

    /// <summary>
    /// In-place Cholesky factorization A = L * L^T of the symmetric n x n matrix at a[off] (only its lower triangle is
    /// read; L overwrites it; the strict upper triangle is left unspecified). Returns 0, or i + 1 when the leading
    /// minor of order i + 1 is not positive definite.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    internal static int Potrf(double[] a, int off, int lda, int n)
    {
        if (n <= TrsmBase)
        {
            Debug.Assert((long)off + (n - 1) * lda + n <= a.Length);
            ref double a0 = ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(a), off);
            for (int j = 0; j < n; j++)
            {
                ref double lj = ref Unsafe.Add(ref a0, (nint)j * lda);
                for (int p = 0; p < j; p++)
                {
                    ref double lp = ref Unsafe.Add(ref a0, (nint)p * lda);
                    Unsafe.Add(ref lj, p) = (Unsafe.Add(ref lj, p) - Dot(ref lj, ref lp, p)) / Unsafe.Add(ref lp, p);
                }
                double d = Unsafe.Add(ref lj, j) - Dot(ref lj, ref lj, j);
                if (!(d > 0)) return j + 1;
                Unsafe.Add(ref lj, j) = Math.Sqrt(d);
            }
            return 0;
        }
        int n1 = Half(n), n2 = n - n1;
        int info = Potrf(a, off, lda, n1);
        if (info != 0) return info;
        int a21 = off + n1 * lda, a22 = a21 + n1;
        TrsmRightLowerTrans(n2, n1, a, off, lda, a, a21, lda);
        SyrkLower(n2, n1, a, a21, lda, a, a22, lda);
        info = Potrf(a, a22, lda, n2);
        return info == 0 ? 0 : info + n1;
    }

    /// <summary>C (n x n, lower triangle) -= A * A^T, A n x k. Diagonal blocks are computed in full (their upper parts are scratch).</summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    internal static void SyrkLower(int n, int k, double[] a, int aOff, int lda, double[] c, int cOff, int ldc)
    {
        if (n <= 0 || k <= 0) return;
        if (n <= 64)
        {
            Gemm.Run(n, n, k, -1.0, a, aOff, lda, 1, a, aOff, 1, lda, c, cOff, ldc, accumulate: true);
            return;
        }
        int n1 = Half(n), n2 = n - n1;
        SyrkLower(n1, k, a, aOff, lda, c, cOff, ldc);
        Gemm.Run(n2, n1, k, -1.0, a, aOff + n1 * lda, lda, 1, a, aOff, 1, lda, c, cOff + n1 * ldc, ldc, accumulate: true);
        SyrkLower(n2, k, a, aOff + n1 * lda, lda, c, cOff + n1 * ldc + n1, ldc);
    }

    // ================================================================== Householder QR (geqrf / orgqr)

    /// <summary>Columns per block of the blocked Householder QR.</summary>
    private const int QrBlock = 32;

    /// <summary>
    /// In-place QR of the m x n matrix a (row stride lda), LAPACK's dgeqrf: R in the upper triangle, the Householder
    /// vectors v_j (v_j[j] = 1 implied) below it, tau[j] their scalars (H_j = I - tau_j v_j v_j^T, A = H_1...H_k R).
    /// Blocked: each 32-column panel is factored on a transposed copy (its columns contiguous), the trailing columns
    /// are updated with the compact WY form I - V T V^T (three matrix products).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    internal static void Geqrf(double[] a, int lda, int m, int n, double[] tau)
    {
        int k = Math.Min(m, n);
        for (int j = 0; j < k; j += QrBlock)
        {
            int jb = Math.Min(QrBlock, k - j), mp = m - j;
            // panel rows [j, m) x columns [j, j + jb), transposed: pt[c * mp + r] = a[j + r, j + c]
            var pt = new double[jb * mp];
            for (int r = 0; r < mp; r++)
                for (int c = 0; c < jb; c++) pt[c * mp + r] = a[(j + r) * lda + j + c];
            Geqr2Transposed(pt, mp, jb, tau, j);
            for (int r = 0; r < mp; r++)
                for (int c = 0; c < jb; c++) a[(j + r) * lda + j + c] = pt[c * mp + r];
            int n2 = n - j - jb;
            if (n2 > 0)
            {
                var vt = VectorsTransposed(pt, mp, jb);
                var t = Larft(vt, mp, jb, tau, j);
                ApplyBlockReflector(vt, t, mp, jb, a, j * lda + j + jb, lda, n2, transpose: true);
            }
        }
    }

    /// <summary>
    /// Unblocked QR (dgeqr2) of a panel stored transposed: column c of the panel is the contiguous row pt[c*mp ..].
    /// Writes beta (R's diagonal) at the diagonal, R above it and v below it, tau[tauOff + c].
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static void Geqr2Transposed(double[] pt, int mp, int w, double[] tau, int tauOff)
    {
        ref double p0 = ref MemoryMarshal.GetArrayDataReference(pt);
        for (int c = 0; c < w && c < mp; c++)
        {
            ref double x = ref Unsafe.Add(ref p0, (nint)c * mp + c); // x[0] = alpha, x[1..len) the rest of the column
            int len = mp - c;
            double t = Larfg(ref x, len);
            tau[tauOff + c] = t;
            if (t == 0) continue;
            // apply H = I - t v v^T to the panel columns right of c (v[0] = 1)
            double beta = x;
            x = 1.0;
            for (int d = c + 1; d < w; d++)
            {
                ref double y = ref Unsafe.Add(ref p0, (nint)d * mp + c);
                double s = t * Dot(ref x, ref y, len);
                AxpyNeg(s, ref x, ref y, len);
            }
            x = beta;
        }
    }

    /// <summary>
    /// LAPACK's dlarfg on x[0..n): alpha = x[0]; returns tau and leaves beta in x[0] and v[1..n) in x[1..n), so that
    /// H [alpha; x] = [beta; 0] with H = I - tau [1; v] [1; v]^T. tau = 0 (H = I) when x[1..n) is zero.
    /// </summary>
    private static double Larfg(ref double x, int n)
    {
        if (n <= 1) return 0.0;
        double alpha = x;
        double xnorm = Nrm2(ref Unsafe.Add(ref x, 1), n - 1);
        if (xnorm == 0) return 0.0;
        double beta = -Math.CopySign(double.Hypot(alpha, xnorm), alpha);
        double t = (beta - alpha) / beta;
        double scale = 1.0 / (alpha - beta);
        for (int i = 1; i < n; i++) Unsafe.Add(ref x, i) *= scale;
        x = beta;
        return t;
    }

    /// <summary>2-norm of x[0..n) without overflow or underflow (dnrm2's guarantee), fast path first.</summary>
    private static double Nrm2(ref double x, int n)
    {
        double ss = Dot(ref x, ref x, n);
        if (ss > 1e-290 && ss < 1e290) return Math.Sqrt(ss);
        double big = 0;
        for (int i = 0; i < n; i++) big = Math.Max(big, Math.Abs(Unsafe.Add(ref x, i)));
        if (big == 0 || double.IsInfinity(big) || double.IsNaN(ss)) return ss == 0 ? 0 : big;
        double s = 0;
        for (int i = 0; i < n; i++)
        {
            double v = Unsafe.Add(ref x, i) / big;
            s += v * v;
        }
        return big * Math.Sqrt(s);
    }

    /// <summary>V^T (w x mp) from a factored transposed panel: unit diagonal, zeros left of it, v right of it.</summary>
    private static double[] VectorsTransposed(double[] pt, int mp, int w)
    {
        var vt = new double[w * mp];
        for (int c = 0; c < w; c++)
        {
            if (c >= mp) break;
            vt[c * mp + c] = 1.0;
            Array.Copy(pt, c * mp + c + 1, vt, c * mp + c + 1, mp - c - 1);
        }
        return vt;
    }

    /// <summary>LAPACK's dlarft (forward, columnwise): the upper triangular T with H_1...H_w = I - V T V^T.</summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static double[] Larft(double[] vt, int mp, int w, double[] tau, int tauOff)
    {
        var t = new double[w * w];
        ref double v0 = ref MemoryMarshal.GetArrayDataReference(vt);
        var col = new double[w];
        for (int i = 0; i < w; i++)
        {
            double ti = tau[tauOff + i];
            if (ti == 0 || i >= mp) continue; // column i of T stays zero
            // col[c] = -tau_i * v_c . v_i for c < i (v_i is zero above row i)
            ref double vi = ref Unsafe.Add(ref v0, (nint)i * mp + i);
            for (int c = 0; c < i; c++) col[c] = -ti * Dot(ref Unsafe.Add(ref v0, (nint)c * mp + i), ref vi, mp - i);
            // T[0:i, i] = T[0:i, 0:i] * col (upper triangular)
            for (int r = 0; r < i; r++)
            {
                double s = 0;
                for (int c = r; c < i; c++) s += t[r * w + c] * col[c];
                t[r * w + i] = s;
            }
            t[i * w + i] = ti;
        }
        return t;
    }

    /// <summary>
    /// C (mp x nc at c[cOff], row stride ldc) := H C (transpose: false) or H^T C (true), H = I - V T V^T, V = vt^T
    /// (mp x w). Three products: W = V^T C, W = T W (or T^T W), C -= V W.
    /// </summary>
    internal static void ApplyBlockReflector(double[] vt, double[] t, int mp, int w, double[] c, int cOff, int ldc, int nc, bool transpose)
    {
        if (nc <= 0 || mp <= 0) return;
        var wk = new double[w * nc];
        Gemm.Run(w, nc, mp, 1.0, vt, 0, mp, 1, c, cOff, ldc, 1, wk, 0, nc, accumulate: false);
        var w2 = new double[w * nc];
        if (transpose) Gemm.Run(w, nc, w, 1.0, t, 0, 1, w, wk, 0, nc, 1, w2, 0, nc, accumulate: false);
        else Gemm.Run(w, nc, w, 1.0, t, 0, w, 1, wk, 0, nc, 1, w2, 0, nc, accumulate: false);
        Gemm.Run(mp, nc, w, -1.0, vt, 0, 1, mp, w2, 0, nc, 1, c, cOff, ldc, accumulate: true);
    }

    /// <summary>
    /// LAPACK's dorgqr: the first nq columns (k &lt;= nq &lt;= m) of Q = H_1...H_k from <see cref="Geqrf"/>'s output,
    /// as an m x nq matrix. Blocks are applied last to first, each to the rows and columns it can reach.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    internal static double[] Orgqr(double[] a, int lda, int m, int k, double[] tau, int nq)
    {
        var q = new double[m * nq];
        for (int i = 0; i < Math.Min(m, nq); i++) q[i * nq + i] = 1.0;
        int last = (k - 1) / QrBlock * QrBlock;
        for (int j = last; j >= 0; j -= QrBlock)
        {
            int jb = Math.Min(QrBlock, k - j), mp = m - j;
            var vt = new double[jb * mp];
            for (int c = 0; c < jb; c++)
            {
                vt[c * mp + c] = 1.0;
                for (int r = c + 1; r < mp; r++) vt[c * mp + r] = a[(j + r) * lda + j + c];
            }
            var t = Larft(vt, mp, jb, tau, j);
            ApplyBlockReflector(vt, t, mp, jb, q, j * nq + j, nq, nq - j, transpose: false);
        }
        return q;
    }
}
