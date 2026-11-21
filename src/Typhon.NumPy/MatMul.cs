// Copyright (c) 2026 The Typhon contributors.
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

using System;
using System.Buffers;
using System.Diagnostics;
using System.Numerics.Tensors;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.Arm;
using System.Runtime.Intrinsics.X86;
using System.Threading.Tasks;

namespace Typhon.Stdlib;

/// <summary>
/// Dense float64 GEMM: C = alpha * A * B (+ C), the structure of GotoBLAS / BLIS.
/// <list type="bullet">
/// <item>B is packed per KC x NC panel into NR-column slivers and A per KC-deep panel into MR-row slivers, both
/// contiguous in the order the micro-kernel reads them (zero-padded at the edges). Packing reads any strides, so
/// transposed operands and sub-matrices (the trailing updates of LU, Cholesky, QR) need no copy.</item>
/// <item>The micro-kernel keeps a 6 x 8 block of C in 12 Vector256 registers (24 Vector128 registers on Arm64) and
/// does one broadcast and two FMAs per element of A: 12 FMAs per 2 loads of B.</item>
/// <item>Blocking: an MC x KC block of packed A stays in L2 while the micro-kernel walks the NR slivers of packed B
/// (one sliver, KC x NR, stays in L1); the packed B panel lives in L3.</item>
/// <item>Threads: the C tiles of one panel (MC rows x a range of slivers) are independent tasks. Every element of C
/// is summed in the same order whatever the thread count, so results do not depend on it.</item>
/// </list>
/// </summary>
internal static class Gemm
{
    internal const int MR = 6, NR = 8;
    internal const int MC = 72;   // multiple of MR; MC x KC doubles of packed A = 144 KB (L2)
    internal const int KC = 256;  // KC x NR doubles of packed B = 16 KB (L1); measured best of 128..512 on AVX2
    internal const int NC = 4080; // multiple of NR; KC x NC doubles of packed B = 8 MB (L3)

    /// <summary>Below this m*n*k the plain triple loop wins (no packing).</summary>
    internal const long SmallWork = 8 * 8 * 8;

    /// <summary>m*n*k per thread at least (a thread costs a few microseconds to start and join).</summary>
    internal const long WorkPerThread = 64 * 64 * 64;

    internal enum KernelKind { Scalar, Vector128, Vector256 }

    /// <summary>The micro-kernel in use; tests switch it to exercise every path on one machine.</summary>
    internal static KernelKind Kind = DefaultKind();

    private static KernelKind DefaultKind()
    {
        if (Vector256.IsHardwareAccelerated) return KernelKind.Vector256;
        if (Vector128.IsHardwareAccelerated) return KernelKind.Vector128;
        return KernelKind.Scalar;
    }

    /// <summary>
    /// C[m x n] (row-major, row stride ldc) = alpha * A[m x k] * B[k x n] (+ C when accumulate). A and B are read
    /// through element strides (row stride, column stride) from an offset, so any layout or sub-matrix works.
    /// Without accumulate, C's previous contents are ignored (it may be uninitialized memory).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    internal static void Run(int m, int n, int k, double alpha,
        double[] a, int aOff, int rsa, int csa,
        double[] b, int bOff, int rsb, int csb,
        double[] c, int cOff, int ldc, bool accumulate, int threads = 0)
    {
        if (m <= 0 || n <= 0) return;
        if (k <= 0 || alpha == 0)
        {
            if (!accumulate)
                for (int i = 0; i < m; i++) c.AsSpan(cOff + i * ldc, n).Clear();
            return;
        }
        if (threads <= 0) threads = NumPyRuntime.MaxDegreeOfParallelism;
        long work = (long)m * n * k;
        if (work <= SmallWork || Kind == KernelKind.Scalar)
        {
            Small(m, n, k, alpha, a, aOff, rsa, csa, b, bOff, rsb, csb, c, cOff, ldc, accumulate);
            return;
        }
        // Threads only pay off from ~80^3 multiply-adds; up to there, one more thread per 64^3 of work.
        if (work < NumPyRuntime.MatMulParallelThreshold) threads = 1;
        else threads = (int)Math.Clamp(work / WorkPerThread, 1, threads);

        int mPad = (m + MR - 1) / MR * MR;
        int ncMax = Math.Min(NC, (n + NR - 1) / NR * NR);
        int kcMax = Math.Min(KC, k);
        double[] ap = ArrayPool<double>.Shared.Rent(mPad * kcMax);
        double[] bp = ArrayPool<double>.Shared.Rent(ncMax * kcMax);
        try
        {
            for (int jc = 0; jc < n; jc += NC)
            {
                int nc = Math.Min(NC, n - jc);
                for (int pc = 0; pc < k; pc += KC)
                {
                    int kc = Math.Min(KC, k - pc);
                    bool acc = accumulate || pc > 0;
                    Panel(m, nc, kc, alpha, a, aOff + pc * csa, rsa, csa, b, bOff + pc * rsb + jc * csb, rsb, csb,
                        c, cOff + jc, ldc, acc, ap, bp, threads);
                }
            }
        }
        finally
        {
            ArrayPool<double>.Shared.Return(ap);
            ArrayPool<double>.Shared.Return(bp);
        }
    }

    /// <summary>One KC-deep panel: pack A (m x kc) and B (kc x nc), then all C tiles.</summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static void Panel(int m, int nc, int kc, double alpha,
        double[] a, int aOff, int rsa, int csa, double[] b, int bOff, int rsb, int csb,
        double[] c, int cOff, int ldc, bool acc, double[] ap, double[] bp, int threads)
    {
        int rowBlocks = (m + MC - 1) / MC;
        int slivers = (nc + NR - 1) / NR;

        int colChunks = 1;
        if (threads > 1)
        {
            int target = threads * 3;
            if (rowBlocks < target) colChunks = (target + rowBlocks - 1) / rowBlocks;
            colChunks = Math.Clamp(colChunks, 1, Math.Max(1, slivers / 4)); // at least 4 slivers (32 columns) per task
        }
        int perChunk = (slivers + colChunks - 1) / colChunks;
        colChunks = (slivers + perChunk - 1) / perChunk;
        int tasks = rowBlocks * colChunks;

        // Pack: A row blocks and B chunks of 16 slivers, as one list of independent jobs.
        const int BPackSlivers = 16;
        int bJobs = (slivers + BPackSlivers - 1) / BPackSlivers;
        if (threads > 1 && rowBlocks + bJobs > 1)
        {
            Parallel.For(0, rowBlocks + bJobs, new ParallelOptions { MaxDegreeOfParallelism = threads }, t =>
            {
                if (t < rowBlocks) PackA(a, aOff, rsa, csa, alpha, t * MC, Math.Min(MC, m - t * MC), kc, ap);
                else
                {
                    int s0 = (t - rowBlocks) * BPackSlivers;
                    PackB(b, bOff, rsb, csb, s0, Math.Min(slivers, s0 + BPackSlivers), nc, kc, bp);
                }
            });
        }
        else
        {
            for (int t = 0; t < rowBlocks; t++) PackA(a, aOff, rsa, csa, alpha, t * MC, Math.Min(MC, m - t * MC), kc, ap);
            PackB(b, bOff, rsb, csb, 0, slivers, nc, kc, bp);
        }

        if (threads > 1 && tasks > 1)
        {
            Parallel.For(0, tasks, new ParallelOptions { MaxDegreeOfParallelism = threads }, t =>
            {
                int rb = t % rowBlocks, cc = t / rowBlocks;
                int i0 = rb * MC;
                Macro(Math.Min(MC, m - i0), nc, kc, ap, i0 * kc, bp, cc * perChunk, Math.Min(slivers, (cc + 1) * perChunk),
                    c, cOff + i0 * ldc, ldc, acc);
            });
        }
        else
        {
            for (int rb = 0; rb < rowBlocks; rb++)
            {
                int i0 = rb * MC;
                Macro(Math.Min(MC, m - i0), nc, kc, ap, i0 * kc, bp, 0, slivers, c, cOff + i0 * ldc, ldc, acc);
            }
        }
    }

    /// <summary>
    /// Packs rows [i0, i0 + mc) of the kc-deep A panel into MR-row slivers: element (i, p) of sliver s lands at
    /// ap[(i0 + s*MR) * kc + p*MR + i]. Rows past mc are zero.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static void PackA(double[] a, int aOff, int rsa, int csa, double alpha, int i0, int mc, int kc, double[] ap)
    {
        ref double dst0 = ref MemoryMarshal.GetArrayDataReference(ap);
        for (int s = 0; s < mc; s += MR)
        {
            int mr = Math.Min(MR, mc - s);
            ref double dst = ref Unsafe.Add(ref dst0, (nint)(i0 + s) * kc);
            if (csa == 1 && mr == MR && Avx.IsSupported)
            {
                PackA6x4(a, aOff + (i0 + s) * rsa, rsa, alpha, kc, ref dst);
            }
            else if (csa == 1)
            {
                for (int i = 0; i < mr; i++)
                {
                    ReadOnlySpan<double> row = a.AsSpan(aOff + (i0 + s + i) * rsa, kc);
                    ref double d = ref Unsafe.Add(ref dst, i);
                    if (alpha == 1.0)
                        for (int p = 0; p < row.Length; p++) Unsafe.Add(ref d, p * MR) = row[p];
                    else
                        for (int p = 0; p < row.Length; p++) Unsafe.Add(ref d, p * MR) = alpha * row[p];
                }
            }
            else
            {
                for (int i = 0; i < mr; i++)
                {
                    int src = aOff + (i0 + s + i) * rsa;
                    ref double d = ref Unsafe.Add(ref dst, i);
                    for (int p = 0; p < kc; p++) Unsafe.Add(ref d, p * MR) = alpha * a[src + p * csa];
                }
            }
            for (int i = mr; i < MR; i++)
            {
                ref double d = ref Unsafe.Add(ref dst, i);
                for (int p = 0; p < kc; p++) Unsafe.Add(ref d, p * MR) = 0.0;
            }
        }
    }

    /// <summary>
    /// One full 6-row sliver of a row-major A: 4 k steps at a time, the 6 x 4 block is transposed in registers
    /// (a 4 x 4 and a 2 x 4 part) and written as 4 contiguous groups of 6.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static void PackA6x4(double[] a, int off, int rsa, double alpha, int kc, ref double dst)
    {
        Debug.Assert(off + 5 * rsa + kc <= a.Length);
        ref double r0 = ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(a), off);
        ref double r1 = ref Unsafe.Add(ref r0, rsa);
        ref double r2 = ref Unsafe.Add(ref r1, rsa);
        ref double r3 = ref Unsafe.Add(ref r2, rsa);
        ref double r4 = ref Unsafe.Add(ref r3, rsa);
        ref double r5 = ref Unsafe.Add(ref r4, rsa);
        var va = Vector256.Create(alpha);
        int p = 0;
        for (; p + 3 < kc; p += 4)
        {
            nuint up = (nuint)p;
            var x0 = Vector256.LoadUnsafe(ref r0, up) * va;
            var x1 = Vector256.LoadUnsafe(ref r1, up) * va;
            var x2 = Vector256.LoadUnsafe(ref r2, up) * va;
            var x3 = Vector256.LoadUnsafe(ref r3, up) * va;
            var x4 = Vector256.LoadUnsafe(ref r4, up) * va;
            var x5 = Vector256.LoadUnsafe(ref r5, up) * va;
            var t0 = Avx.UnpackLow(x0, x1);  // a0 b0 a2 b2
            var t1 = Avx.UnpackHigh(x0, x1); // a1 b1 a3 b3
            var t2 = Avx.UnpackLow(x2, x3);
            var t3 = Avx.UnpackHigh(x2, x3);
            var u0 = Avx.UnpackLow(x4, x5);  // e0 f0 e2 f2
            var u1 = Avx.UnpackHigh(x4, x5); // e1 f1 e3 f3
            ref double d = ref Unsafe.Add(ref dst, p * MR);
            Avx.Permute2x128(t0, t2, 0x20).StoreUnsafe(ref d);
            u0.GetLower().StoreUnsafe(ref d, 4);
            Avx.Permute2x128(t1, t3, 0x20).StoreUnsafe(ref d, 6);
            u1.GetLower().StoreUnsafe(ref d, 10);
            Avx.Permute2x128(t0, t2, 0x31).StoreUnsafe(ref d, 12);
            u0.GetUpper().StoreUnsafe(ref d, 16);
            Avx.Permute2x128(t1, t3, 0x31).StoreUnsafe(ref d, 18);
            u1.GetUpper().StoreUnsafe(ref d, 22);
        }
        for (; p < kc; p++)
        {
            ref double d = ref Unsafe.Add(ref dst, p * MR);
            d = alpha * Unsafe.Add(ref r0, p);
            Unsafe.Add(ref d, 1) = alpha * Unsafe.Add(ref r1, p);
            Unsafe.Add(ref d, 2) = alpha * Unsafe.Add(ref r2, p);
            Unsafe.Add(ref d, 3) = alpha * Unsafe.Add(ref r3, p);
            Unsafe.Add(ref d, 4) = alpha * Unsafe.Add(ref r4, p);
            Unsafe.Add(ref d, 5) = alpha * Unsafe.Add(ref r5, p);
        }
    }

    /// <summary>
    /// Packs slivers [s0, s1) (NR columns each) of the kc x nc B panel: element (p, j) of sliver s lands at
    /// bp[s*NR*kc + p*NR + j]. Columns past nc are zero.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static void PackB(double[] b, int bOff, int rsb, int csb, int s0, int s1, int nc, int kc, double[] bp)
    {
        ref double dst0 = ref MemoryMarshal.GetArrayDataReference(bp);
        if (csb == 1)
        {
            // Row-major B: walk the rows of the panel, so each read is a contiguous run of (full) slivers.
            int full = Math.Min(s1, nc / NR);
            if (full > s0)
            {
                ref double src0 = ref MemoryMarshal.GetArrayDataReference(b);
                Debug.Assert(bOff + (kc - 1) * rsb + full * NR <= b.Length);
                nint sliverSize = (nint)NR * kc;
                for (int p = 0; p < kc; p++)
                {
                    ref double src = ref Unsafe.Add(ref src0, (nint)bOff + (nint)p * rsb + s0 * NR);
                    ref double d = ref Unsafe.Add(ref dst0, s0 * sliverSize + p * NR);
                    for (int s = s0; s < full; s++)
                    {
                        if (Vector256.IsHardwareAccelerated)
                        {
                            Vector256.LoadUnsafe(ref src).StoreUnsafe(ref d);
                            Vector256.LoadUnsafe(ref src, 4).StoreUnsafe(ref d, 4);
                        }
                        else
                        {
                            for (int j = 0; j < NR; j++) Unsafe.Add(ref d, j) = Unsafe.Add(ref src, j);
                        }
                        src = ref Unsafe.Add(ref src, NR);
                        d = ref Unsafe.Add(ref d, sliverSize);
                    }
                }
                s0 = full;
            }
        }
        for (int s = s0; s < s1; s++)
        {
            int j0 = s * NR;
            int nr = Math.Min(NR, nc - j0);
            ref double dst = ref Unsafe.Add(ref dst0, (nint)s * NR * kc);
            if (rsb == 1 && csb != 1)
            {
                // B is transposed (column-major): read each column contiguously.
                for (int j = 0; j < NR; j++)
                {
                    ref double d = ref Unsafe.Add(ref dst, j);
                    if (j < nr)
                    {
                        ReadOnlySpan<double> col = b.AsSpan(bOff + (j0 + j) * csb, kc);
                        for (int p = 0; p < col.Length; p++) Unsafe.Add(ref d, p * NR) = col[p];
                    }
                    else
                        for (int p = 0; p < kc; p++) Unsafe.Add(ref d, p * NR) = 0.0;
                }
            }
            else
            {
                for (int p = 0; p < kc; p++)
                {
                    int src = bOff + p * rsb + j0 * csb;
                    ref double d = ref Unsafe.Add(ref dst, p * NR);
                    int j = 0;
                    for (; j < nr; j++) Unsafe.Add(ref d, j) = b[src + j * csb];
                    for (; j < NR; j++) Unsafe.Add(ref d, j) = 0.0;
                }
            }
        }
    }

    /// <summary>C tiles for rows [0, mc) of one packed A block and slivers [s0, s1) of packed B.</summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static void Macro(int mc, int nc, int kc, double[] ap, int apOff, double[] bp, int s0, int s1,
        double[] c, int cOff, int ldc, bool acc)
    {
        ref double a0 = ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(ap), apOff);
        ref double b0 = ref MemoryMarshal.GetArrayDataReference(bp);
        ref double c0 = ref MemoryMarshal.GetArrayDataReference(c);
        Span<double> tile = stackalloc double[MR * NR];
        bool v256 = Kind == KernelKind.Vector256;
        // While one sliver of B is in use, the next one (kc cache lines) is fetched into L2, a share per micro-tile.
        int linesPerTile = (kc + (mc + MR - 1) / MR - 1) / ((mc + MR - 1) / MR);
        for (int s = s0; s < s1; s++)
        {
            int j = s * NR;
            int nr = Math.Min(NR, nc - j);
            ref double bs = ref Unsafe.Add(ref b0, (nint)s * NR * kc);
            int line = s + 1 < s1 ? 0 : kc;
            for (int i = 0; i < mc; i += MR)
            {
                for (int end = Math.Min(kc, line + linesPerTile); line < end; line++)
                    PrefetchL2(ref bs, (NR * kc) + line * NR);
                int mr = Math.Min(MR, mc - i);
                ref double asl = ref Unsafe.Add(ref a0, (nint)i * kc);
                if (mr == MR && nr == NR)
                {
                    Debug.Assert(cOff + (i + MR - 1) * ldc + j + NR <= c.Length);
                    ref double ct = ref Unsafe.Add(ref c0, (nint)cOff + (nint)i * ldc + j);
                    if (v256) Kernel256(kc, ref asl, ref bs, ref ct, ldc, acc);
                    else Kernel128(kc, ref asl, ref bs, ref ct, ldc, acc);
                }
                else
                {
                    ref double tt = ref MemoryMarshal.GetReference(tile);
                    if (v256) Kernel256(kc, ref asl, ref bs, ref tt, NR, false);
                    else Kernel128(kc, ref asl, ref bs, ref tt, NR, false);
                    for (int ii = 0; ii < mr; ii++)
                    {
                        int row = cOff + (i + ii) * ldc + j;
                        for (int jj = 0; jj < nr; jj++)
                            c[row + jj] = acc ? c[row + jj] + tile[ii * NR + jj] : tile[ii * NR + jj];
                    }
                }
            }
        }
    }

    /// <summary>
    /// Starts loading the 6 x 8 C tile (two cache lines per row, it need not be aligned) so the loads overlap the
    /// k loop. Without it, rows of C a power of two apart (n = 512, 1024, ...) miss in L1 at the end of every
    /// micro-kernel call. A prefetch never faults, so a stale address after the GC moved the array is harmless.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static unsafe void PrefetchTile(ref double c, nint ldc)
    {
        if (!Sse.IsSupported) return;
        double* p = (double*)Unsafe.AsPointer(ref c);
        for (int i = 0; i < MR; i++, p += ldc)
        {
            Sse.Prefetch0(p);
            Sse.Prefetch0(p + NR - 1);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static unsafe void Prefetch(ref double r, int offset)
    {
        if (Sse.IsSupported) Sse.Prefetch0((double*)Unsafe.AsPointer(ref r) + offset);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static unsafe void PrefetchL2(ref double r, int offset)
    {
        if (Sse.IsSupported) Sse.Prefetch1((double*)Unsafe.AsPointer(ref r) + offset);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector256<double> Fma256(Vector256<double> a, Vector256<double> b, Vector256<double> c) =>
        Fma.IsSupported ? Fma.MultiplyAdd(a, b, c) : a * b + c;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<double> Fma128(Vector128<double> a, Vector128<double> b, Vector128<double> c) =>
        AdvSimd.Arm64.IsSupported ? AdvSimd.Arm64.FusedMultiplyAdd(c, a, b)
        : Fma.IsSupported ? Fma.MultiplyAdd(a, b, c) : a * b + c;

    /// <summary>
    /// C[6 x 8] (row stride ldc) (+)= sum over p of a[p*6 + i] * b[p*8 + j]: 12 Vector256 accumulators.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static void Kernel256(int kc, ref double a, ref double b, ref double c, nint ldc, bool acc)
    {
        PrefetchTile(ref c, ldc);
        Vector256<double> c00 = default, c01 = default, c10 = default, c11 = default, c20 = default, c21 = default;
        Vector256<double> c30 = default, c31 = default, c40 = default, c41 = default, c50 = default, c51 = default;
        int p = 0;
        for (; p + 1 < kc; p += 2)
        {
            // two k steps per iteration: fewer loop instructions between the 24 FMAs
            Prefetch(ref a, 8 * MR); // packed A streams from L2: fetch 8 steps ahead
            var b0 = Vector256.LoadUnsafe(ref b);
            var b1 = Vector256.LoadUnsafe(ref b, 4);
            var x = Vector256.Create(a);
            c00 = Fma256(x, b0, c00); c01 = Fma256(x, b1, c01);
            x = Vector256.Create(Unsafe.Add(ref a, 1));
            c10 = Fma256(x, b0, c10); c11 = Fma256(x, b1, c11);
            x = Vector256.Create(Unsafe.Add(ref a, 2));
            c20 = Fma256(x, b0, c20); c21 = Fma256(x, b1, c21);
            x = Vector256.Create(Unsafe.Add(ref a, 3));
            c30 = Fma256(x, b0, c30); c31 = Fma256(x, b1, c31);
            x = Vector256.Create(Unsafe.Add(ref a, 4));
            c40 = Fma256(x, b0, c40); c41 = Fma256(x, b1, c41);
            x = Vector256.Create(Unsafe.Add(ref a, 5));
            c50 = Fma256(x, b0, c50); c51 = Fma256(x, b1, c51);
            b0 = Vector256.LoadUnsafe(ref b, 8);
            b1 = Vector256.LoadUnsafe(ref b, 12);
            x = Vector256.Create(Unsafe.Add(ref a, 6));
            c00 = Fma256(x, b0, c00); c01 = Fma256(x, b1, c01);
            x = Vector256.Create(Unsafe.Add(ref a, 7));
            c10 = Fma256(x, b0, c10); c11 = Fma256(x, b1, c11);
            x = Vector256.Create(Unsafe.Add(ref a, 8));
            c20 = Fma256(x, b0, c20); c21 = Fma256(x, b1, c21);
            x = Vector256.Create(Unsafe.Add(ref a, 9));
            c30 = Fma256(x, b0, c30); c31 = Fma256(x, b1, c31);
            x = Vector256.Create(Unsafe.Add(ref a, 10));
            c40 = Fma256(x, b0, c40); c41 = Fma256(x, b1, c41);
            x = Vector256.Create(Unsafe.Add(ref a, 11));
            c50 = Fma256(x, b0, c50); c51 = Fma256(x, b1, c51);
            a = ref Unsafe.Add(ref a, 2 * MR);
            b = ref Unsafe.Add(ref b, 2 * NR);
        }
        if (p < kc)
        {
            var b0 = Vector256.LoadUnsafe(ref b);
            var b1 = Vector256.LoadUnsafe(ref b, 4);
            var x = Vector256.Create(a);
            c00 = Fma256(x, b0, c00); c01 = Fma256(x, b1, c01);
            x = Vector256.Create(Unsafe.Add(ref a, 1));
            c10 = Fma256(x, b0, c10); c11 = Fma256(x, b1, c11);
            x = Vector256.Create(Unsafe.Add(ref a, 2));
            c20 = Fma256(x, b0, c20); c21 = Fma256(x, b1, c21);
            x = Vector256.Create(Unsafe.Add(ref a, 3));
            c30 = Fma256(x, b0, c30); c31 = Fma256(x, b1, c31);
            x = Vector256.Create(Unsafe.Add(ref a, 4));
            c40 = Fma256(x, b0, c40); c41 = Fma256(x, b1, c41);
            x = Vector256.Create(Unsafe.Add(ref a, 5));
            c50 = Fma256(x, b0, c50); c51 = Fma256(x, b1, c51);
        }
        ref double r0 = ref c;
        ref double r1 = ref Unsafe.Add(ref c, ldc);
        ref double r2 = ref Unsafe.Add(ref c, 2 * ldc);
        ref double r3 = ref Unsafe.Add(ref c, 3 * ldc);
        ref double r4 = ref Unsafe.Add(ref c, 4 * ldc);
        ref double r5 = ref Unsafe.Add(ref c, 5 * ldc);
        if (acc)
        {
            c00 += Vector256.LoadUnsafe(ref r0); c01 += Vector256.LoadUnsafe(ref r0, 4);
            c10 += Vector256.LoadUnsafe(ref r1); c11 += Vector256.LoadUnsafe(ref r1, 4);
            c20 += Vector256.LoadUnsafe(ref r2); c21 += Vector256.LoadUnsafe(ref r2, 4);
            c30 += Vector256.LoadUnsafe(ref r3); c31 += Vector256.LoadUnsafe(ref r3, 4);
            c40 += Vector256.LoadUnsafe(ref r4); c41 += Vector256.LoadUnsafe(ref r4, 4);
            c50 += Vector256.LoadUnsafe(ref r5); c51 += Vector256.LoadUnsafe(ref r5, 4);
        }
        c00.StoreUnsafe(ref r0); c01.StoreUnsafe(ref r0, 4);
        c10.StoreUnsafe(ref r1); c11.StoreUnsafe(ref r1, 4);
        c20.StoreUnsafe(ref r2); c21.StoreUnsafe(ref r2, 4);
        c30.StoreUnsafe(ref r3); c31.StoreUnsafe(ref r3, 4);
        c40.StoreUnsafe(ref r4); c41.StoreUnsafe(ref r4, 4);
        c50.StoreUnsafe(ref r5); c51.StoreUnsafe(ref r5, 4);
    }

    /// <summary>
    /// The 6 x 8 micro-kernel with Vector128. Arm64 NEON has 32 registers, so all 24 accumulators fit; x64 without AVX
    /// (16 xmm registers, e.g. a Native AOT build for the default baseline ISA) computes the tile as two 6 x 4 halves.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static void Kernel128(int kc, ref double a, ref double b, ref double c, nint ldc, bool acc)
    {
        if (!AdvSimd.Arm64.IsSupported)
        {
            PrefetchTile(ref c, ldc);
            Kernel128Half(kc, ref a, ref b, ref c, ldc, acc);
            Kernel128Half(kc, ref a, ref Unsafe.Add(ref b, 4), ref Unsafe.Add(ref c, 4), ldc, acc);
            return;
        }
        PrefetchTile(ref c, ldc);
        Vector128<double> c00 = default, c01 = default, c02 = default, c03 = default;
        Vector128<double> c10 = default, c11 = default, c12 = default, c13 = default;
        Vector128<double> c20 = default, c21 = default, c22 = default, c23 = default;
        Vector128<double> c30 = default, c31 = default, c32 = default, c33 = default;
        Vector128<double> c40 = default, c41 = default, c42 = default, c43 = default;
        Vector128<double> c50 = default, c51 = default, c52 = default, c53 = default;
        for (int p = 0; p < kc; p++)
        {
            var b0 = Vector128.LoadUnsafe(ref b);
            var b1 = Vector128.LoadUnsafe(ref b, 2);
            var b2 = Vector128.LoadUnsafe(ref b, 4);
            var b3 = Vector128.LoadUnsafe(ref b, 6);
            var x = Vector128.Create(a);
            c00 = Fma128(x, b0, c00); c01 = Fma128(x, b1, c01); c02 = Fma128(x, b2, c02); c03 = Fma128(x, b3, c03);
            x = Vector128.Create(Unsafe.Add(ref a, 1));
            c10 = Fma128(x, b0, c10); c11 = Fma128(x, b1, c11); c12 = Fma128(x, b2, c12); c13 = Fma128(x, b3, c13);
            x = Vector128.Create(Unsafe.Add(ref a, 2));
            c20 = Fma128(x, b0, c20); c21 = Fma128(x, b1, c21); c22 = Fma128(x, b2, c22); c23 = Fma128(x, b3, c23);
            x = Vector128.Create(Unsafe.Add(ref a, 3));
            c30 = Fma128(x, b0, c30); c31 = Fma128(x, b1, c31); c32 = Fma128(x, b2, c32); c33 = Fma128(x, b3, c33);
            x = Vector128.Create(Unsafe.Add(ref a, 4));
            c40 = Fma128(x, b0, c40); c41 = Fma128(x, b1, c41); c42 = Fma128(x, b2, c42); c43 = Fma128(x, b3, c43);
            x = Vector128.Create(Unsafe.Add(ref a, 5));
            c50 = Fma128(x, b0, c50); c51 = Fma128(x, b1, c51); c52 = Fma128(x, b2, c52); c53 = Fma128(x, b3, c53);
            a = ref Unsafe.Add(ref a, MR);
            b = ref Unsafe.Add(ref b, NR);
        }
        Store128(ref c, c00, c01, c02, c03, acc);
        Store128(ref Unsafe.Add(ref c, ldc), c10, c11, c12, c13, acc);
        Store128(ref Unsafe.Add(ref c, 2 * ldc), c20, c21, c22, c23, acc);
        Store128(ref Unsafe.Add(ref c, 3 * ldc), c30, c31, c32, c33, acc);
        Store128(ref Unsafe.Add(ref c, 4 * ldc), c40, c41, c42, c43, acc);
        Store128(ref Unsafe.Add(ref c, 5 * ldc), c50, c51, c52, c53, acc);
    }

    /// <summary>Columns 0..3 of a 6 x 8 tile (b and c already offset to the half): 12 Vector128 accumulators.</summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static void Kernel128Half(int kc, ref double a, ref double b, ref double c, nint ldc, bool acc)
    {
        Vector128<double> c00 = default, c01 = default, c10 = default, c11 = default, c20 = default, c21 = default;
        Vector128<double> c30 = default, c31 = default, c40 = default, c41 = default, c50 = default, c51 = default;
        for (int p = 0; p < kc; p++)
        {
            var b0 = Vector128.LoadUnsafe(ref b);
            var b1 = Vector128.LoadUnsafe(ref b, 2);
            var x = Vector128.Create(a);
            c00 = Fma128(x, b0, c00); c01 = Fma128(x, b1, c01);
            x = Vector128.Create(Unsafe.Add(ref a, 1));
            c10 = Fma128(x, b0, c10); c11 = Fma128(x, b1, c11);
            x = Vector128.Create(Unsafe.Add(ref a, 2));
            c20 = Fma128(x, b0, c20); c21 = Fma128(x, b1, c21);
            x = Vector128.Create(Unsafe.Add(ref a, 3));
            c30 = Fma128(x, b0, c30); c31 = Fma128(x, b1, c31);
            x = Vector128.Create(Unsafe.Add(ref a, 4));
            c40 = Fma128(x, b0, c40); c41 = Fma128(x, b1, c41);
            x = Vector128.Create(Unsafe.Add(ref a, 5));
            c50 = Fma128(x, b0, c50); c51 = Fma128(x, b1, c51);
            a = ref Unsafe.Add(ref a, MR);
            b = ref Unsafe.Add(ref b, NR);
        }
        Store128Half(ref c, c00, c01, acc);
        Store128Half(ref Unsafe.Add(ref c, ldc), c10, c11, acc);
        Store128Half(ref Unsafe.Add(ref c, 2 * ldc), c20, c21, acc);
        Store128Half(ref Unsafe.Add(ref c, 3 * ldc), c30, c31, acc);
        Store128Half(ref Unsafe.Add(ref c, 4 * ldc), c40, c41, acc);
        Store128Half(ref Unsafe.Add(ref c, 5 * ldc), c50, c51, acc);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Store128Half(ref double r, Vector128<double> v0, Vector128<double> v1, bool acc)
    {
        if (acc)
        {
            v0 += Vector128.LoadUnsafe(ref r);
            v1 += Vector128.LoadUnsafe(ref r, 2);
        }
        v0.StoreUnsafe(ref r);
        v1.StoreUnsafe(ref r, 2);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Store128(ref double r, Vector128<double> v0, Vector128<double> v1, Vector128<double> v2, Vector128<double> v3, bool acc)
    {
        if (acc)
        {
            v0 += Vector128.LoadUnsafe(ref r); v1 += Vector128.LoadUnsafe(ref r, 2);
            v2 += Vector128.LoadUnsafe(ref r, 4); v3 += Vector128.LoadUnsafe(ref r, 6);
        }
        v0.StoreUnsafe(ref r); v1.StoreUnsafe(ref r, 2); v2.StoreUnsafe(ref r, 4); v3.StoreUnsafe(ref r, 6);
    }

    /// <summary>Tiny products (and machines without SIMD): one plain dot product per element of C.</summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static void Small(int m, int n, int k, double alpha,
        double[] a, int aOff, int rsa, int csa, double[] b, int bOff, int rsb, int csb,
        double[] c, int cOff, int ldc, bool accumulate)
    {
        for (int i = 0; i < m; i++)
        {
            int ai = aOff + i * rsa;
            int ci = cOff + i * ldc;
            for (int j = 0; j < n; j++)
            {
                int bj = bOff + j * csb;
                double s = 0;
                for (int p = 0; p < k; p++) s += a[ai + p * csa] * b[bj + p * rsb];
                s *= alpha;
                c[ci + j] = accumulate ? c[ci + j] + s : s;
            }
        }
    }
}

/// <summary>Matrix products on contiguous row-major float64 arrays (numpy.matmul / dot).</summary>
internal static class MatMulKernels
{
    /// <summary>C (n x m) = A (n x k) * B (k x m); all row-major.</summary>
    internal static double[] MatMul(double[] A, double[] B, int n, int k, int m)
    {
        long cells = (long)n * m;
        if (cells > Array.MaxLength) throw new OutOfMemoryException("array is too big");
        var C = k == 0 ? new double[cells] : Kernels.Alloc((int)cells);
        MatMulInto(A, 0, B, 0, C, 0, n, k, m);
        return C;
    }

    /// <summary>C[cOff..] (n x m) = A[aOff..] (n x k) * B[bOff..] (k x m), contiguous row-major blocks.</summary>
    internal static void MatMulInto(double[] A, int aOff, double[] B, int bOff, double[] C, int cOff, int n, int k, int m, int threads = 0)
    {
        if (n == 0 || m == 0) return;
        if (m == 1 && n > 1)
        {
            MatVecInto(A, aOff, B, bOff, C, cOff, n, k, threads);
            return;
        }
        if (n == 1 && m > 1)
        {
            VecMatInto(A, aOff, B, bOff, C, cOff, k, m, threads);
            return;
        }
        Gemm.Run(n, m, k, 1.0, A, aOff, k, 1, B, bOff, m, 1, C, cOff, m, false, threads);
    }

    /// <summary>y (n) = A (n x k) * x (k).</summary>
    internal static double[] MatVec(double[] A, double[] x, int n, int k)
    {
        var y = Kernels.Alloc(n);
        MatVecInto(A, 0, x, 0, y, 0, n, k);
        return y;
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static void MatVecInto(double[] A, int aOff, double[] x, int xOff, double[] y, int yOff, int n, int k, int threads = 0)
    {
        var xs = new ReadOnlyMemory<double>(x, xOff, k);
        if (threads == 1 || (long)n * k < NumPyRuntime.ParallelThreshold * 4L)
        {
            for (int i = 0; i < n; i++) y[yOff + i] = TensorPrimitives.Dot(A.AsSpan(aOff + i * k, k), xs.Span);
            return;
        }
        Kernels.ChunkedItems(n, k, (i0, cnt) =>
        {
            var xspan = xs.Span;
            for (int i = i0; i < i0 + cnt; i++) y[yOff + i] = TensorPrimitives.Dot(A.AsSpan(aOff + i * k, k), xspan);
        });
    }

    /// <summary>y (m) = x (k) * B (k x m).</summary>
    internal static double[] VecMat(double[] x, double[] B, int k, int m)
    {
        var y = Kernels.Alloc(m);
        VecMatInto(x, 0, B, 0, y, 0, k, m);
        return y;
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static void VecMatInto(double[] x, int xOff, double[] B, int bOff, double[] y, int yOff, int k, int m, int threads = 0)
    {
        y.AsSpan(yOff, m).Clear();
        // Column chunks of y are independent; each walks all k rows of B (axpy).
        const int Chunk = 2048;
        int chunks = (m + Chunk - 1) / Chunk;
        void Body(int c0, int cnt)
        {
            for (int c = c0; c < c0 + cnt; c++)
            {
                int j0 = c * Chunk, len = Math.Min(Chunk, m - j0);
                var ys = y.AsSpan(yOff + j0, len);
                for (int p = 0; p < k; p++) TensorPrimitives.MultiplyAdd(B.AsSpan(bOff + p * m + j0, len), x[xOff + p], ys, ys);
            }
        }
        if (threads == 1 || chunks < 2) Body(0, chunks);
        else Kernels.ChunkedItems(chunks, (long)k * Chunk, Body);
    }
}
