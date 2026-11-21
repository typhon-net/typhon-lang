// Copyright (c) 2026 The Typhon contributors.
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

using System;
using System.Numerics.Tensors;

namespace Typhon.Stdlib;

public static partial class numpy
{
    /// <summary>numpy.typing: annotations only (NDArray[...] is an ndarray, see the Typhon compiler's MapType).</summary>
    public static class typing { }

    /// <summary>
    /// numpy.linalg (subset, 2-D float64): norm, det, slogdet, inv, solve, cholesky, matrix_power.
    /// Factorizations are LAPACK's algorithms (recursive LU with partial pivoting, Cholesky) on top of a blocked,
    /// threaded matrix product; see Lapack and Gemm.
    /// </summary>
    public static class linalg
    {
        /// <summary>numpy.linalg.LinAlgError (a ValueError, as in NumPy): singular or not positive-definite matrices.</summary>
        public sealed class LinAlgError : ArgumentException
        {
            public LinAlgError(string message) : base(message) { }
        }

        /// <summary>Frobenius norm of a matrix / 2-norm of a vector (of the flattened array): sqrt(x . x), as NumPy.</summary>
        public static double norm(ndarray x) => Lapack.Norm2(x._data);

        /// <summary>Vector 2-norms along an axis.</summary>
        public static ndarray norm(ndarray x, int axis)
        {
            var sq = x * x;
            return sqrt(sq.sum(axis));
        }

        /// <summary>Determinant: LU with partial pivoting, then sign * exp(sum log|u_ii|) like NumPy.</summary>
        public static double det(ndarray a)
        {
            var (sign, logdet) = slogdet(a);
            return sign * Math.Exp(logdet);
        }

        /// <summary>Sign and natural log of the absolute value of the determinant (sign 0 and -inf when singular).</summary>
        public static (double sign, double logabsdet) slogdet(ndarray a)
        {
            int n = SquareSize(a);
            if (n == 0) return (1.0, 0.0);
            var lu = (double[])a._data.Clone();
            var piv = new int[n];
            if (Lapack.Getrf(lu, n, n, piv) != 0) return (0.0, double.NegativeInfinity);
            double sign = 1.0, logdet = 0.0;
            for (int i = 0; i < n; i++)
            {
                if (piv[i] != i) sign = -sign;
                double d = lu[i * n + i];
                if (d < 0) { sign = -sign; d = -d; }
                logdet += Math.Log(d);
            }
            return (sign, logdet);
        }

        /// <summary>Matrix inverse: LU, then the two triangular solves on the (permuted) identity, as numpy (gesv).</summary>
        public static ndarray inv(ndarray a)
        {
            int n = SquareSize(a);
            var lu = Factor(a, out var piv);
            var x = new double[n * n];
            for (int i = 0; i < n; i++) x[i * n + i] = 1.0;
            Lapack.ApplyPivots(piv, n, x, n, n);
            Lapack.TrsmLowerUnit(n, n, lu, 0, n, x, 0, n);
            Lapack.TrsmUpper(n, n, lu, 0, n, x, 0, n);
            return new ndarray(x, new[] { n, n });
        }

        /// <summary>Solve a @ x = b for x (b: vector or matrix).</summary>
        public static ndarray solve(ndarray a, ndarray b)
        {
            int n = SquareSize(a);
            int nrhs;
            if (b.ndim == 1 || b.ndim == 2)
            {
                if (b._shape[0] != n) throw Err.Value("solve: Input operand 1 has a mismatch in its core dimension 0");
                nrhs = b.ndim == 1 ? 1 : b._shape[1];
            }
            else throw new NotSupportedException("solve: b must be 1-D or 2-D");

            var lu = Factor(a, out var piv);
            var x = (double[])b._data.Clone();
            Lapack.ApplyPivots(piv, n, x, nrhs, nrhs);
            if (nrhs == 1)
            {
                Lapack.TrsvLowerUnit(n, lu, n, x);
                Lapack.TrsvUpper(n, lu, n, x);
            }
            else
            {
                Lapack.TrsmLowerUnit(n, nrhs, lu, 0, n, x, 0, nrhs);
                Lapack.TrsmUpper(n, nrhs, lu, 0, n, x, 0, nrhs);
            }
            return new ndarray(x, b.ndim == 1 ? new[] { n } : new[] { n, nrhs });
        }

        /// <summary>
        /// Cholesky factor of a symmetric positive-definite matrix: L (lower) with a = L @ L.T, or U = L.T with upper=True.
        /// Only the lower triangle of a is read, as in NumPy.
        /// </summary>
        public static ndarray cholesky(ndarray a, bool upper = false)
        {
            int n = SquareSize(a);
            var l = (double[])a._data.Clone();
            if (Lapack.Potrf(l, 0, n, n) != 0) throw new LinAlgError("Matrix is not positive definite");
            for (int i = 0; i < n; i++) l.AsSpan(i * n + i + 1, n - i - 1).Clear();
            var r = new ndarray(l, new[] { n, n });
            return upper ? r.T : r;
        }

        /// <summary>
        /// QR decomposition a = Q @ R by Householder reflections (LAPACK's dgeqrf + dorgqr, so the signs match NumPy's).
        /// mode "reduced" (default): Q m x k, R k x n with k = min(m, n); "complete": Q m x m, R m x n.
        /// </summary>
        public static (ndarray Q, ndarray R) qr(ndarray a, string mode = "reduced")
        {
            if (a.ndim != 2) throw new NotSupportedException("qr: only 2-D arrays are supported");
            bool complete = mode switch
            {
                "reduced" => false,
                "complete" => true,
                "r" or "raw" => throw new NotSupportedException($"qr: mode '{mode}' is not supported; use np.linalg.qr(a)[1] for R"),
                _ => throw Err.Value($"Unrecognized mode '{mode}'"),
            };
            int m = a._shape[0], n = a._shape[1], k = Math.Min(m, n);
            var f = (double[])a._data.Clone();
            var tau = new double[k];
            Lapack.Geqrf(f, n, m, n, tau);
            int rows = complete ? m : k;
            var r = new double[rows * n];
            for (int i = 0; i < k; i++) Array.Copy(f, i * n + i, r, i * n + i, n - i);
            var q = Lapack.Orgqr(f, n, m, k, tau, complete ? m : k);
            return (new ndarray(q, new[] { m, complete ? m : k }), new ndarray(r, new[] { rows, n }));
        }

        /// <summary>
        /// Eigenvalues (ascending) and eigenvectors (columns) of a symmetric matrix; only the UPLO ("L" or "U") triangle
        /// is read. Householder tridiagonalization + implicit QL; an eigenvector's sign may differ from NumPy's.
        /// </summary>
        public static (ndarray eigenvalues, ndarray eigenvectors) eigh(ndarray a, string UPLO = "L")
        {
            int n = SquareSize(a);
            var w = new double[n];
            var z = new double[n * n];
            if (!Lapack.Syev(a._data, n, Lower(UPLO), w, z)) throw new LinAlgError("Eigenvalues did not converge");
            return (new ndarray(w, new[] { n }), new ndarray(z, new[] { n, n }));
        }

        /// <summary>Eigenvalues (ascending) of a symmetric matrix; only the UPLO ("L" or "U") triangle is read.</summary>
        public static ndarray eigvalsh(ndarray a, string UPLO = "L")
        {
            int n = SquareSize(a);
            var w = new double[n];
            if (!Lapack.Syev(a._data, n, Lower(UPLO), w, null)) throw new LinAlgError("Eigenvalues did not converge");
            return new ndarray(w, new[] { n });
        }

        /// <summary>
        /// Singular value decomposition a = U @ diag(S) @ Vh, S descending. full_matrices: U m x m and Vh n x n, else
        /// U m x k and Vh k x n (k = min(m, n)). QR + one-sided Jacobi; singular vectors' signs may differ from NumPy's.
        /// For the singular values alone use svdvals (compute_uv=False is not supported).
        /// </summary>
        public static (ndarray U, ndarray S, ndarray Vh) svd(ndarray a, bool full_matrices = true)
        {
            var (m, n) = Shape2(a, "svd");
            if (!Lapack.Gesvd(a._data, m, n, true, full_matrices, out var s, out var u, out var vt))
                throw new LinAlgError("SVD did not converge");
            int k = Math.Min(m, n);
            return (new ndarray(u, new[] { m, full_matrices ? m : k }), new ndarray(s, new[] { k }),
                    new ndarray(vt, new[] { full_matrices ? n : k, n }));
        }

        /// <summary>Singular values, descending (numpy.linalg.svdvals).</summary>
        public static ndarray svdvals(ndarray a)
        {
            var (m, n) = Shape2(a, "svdvals");
            if (!Lapack.Gesvd(a._data, m, n, false, false, out var s, out _, out _)) throw new LinAlgError("SVD did not converge");
            return new ndarray(s, new[] { s.Length });
        }

        /// <summary>
        /// Least-squares solution of a @ x = b via the SVD, as NumPy (gelsd): returns (x, residuals, rank, s).
        /// Singular values at or below rcond * max(s) count as zero; rcond defaults to eps * max(m, n).
        /// residuals are the squared 2-norms of the columns of b - a @ x when rank == n and m > n, else empty.
        /// </summary>
        public static (ndarray x, ndarray residuals, int rank, ndarray s) lstsq(ndarray a, ndarray b, double? rcond = null)
        {
            var (m, n) = Shape2(a, "lstsq");
            if (b.ndim < 1 || b.ndim > 2 || b._shape[0] != m)
                throw Err.Value("lstsq: Incompatible dimensions");
            int nrhs = b.ndim == 1 ? 1 : b._shape[1];
            if (!Lapack.Gesvd(a._data, m, n, true, false, out var s, out var u, out var vt)) throw new LinAlgError("SVD did not converge");
            int k = s.Length;
            double cut = (rcond ?? 2.220446049250313e-16 * Math.Max(m, n)) * (k > 0 ? s[0] : 0);
            int rank = 0;
            while (rank < k && s[rank] > cut) rank++;
            // x = V[:, :rank] diag(1/s) U[:, :rank]^T b
            var utb = new double[rank * nrhs];
            Gemm.Run(rank, nrhs, m, 1.0, u, 0, 1, k, b._data, 0, nrhs, 1, utb, 0, nrhs, accumulate: false);
            for (int i = 0; i < rank; i++)
                for (int j = 0; j < nrhs; j++) utb[i * nrhs + j] /= s[i];
            var x = new double[n * nrhs];
            Gemm.Run(n, nrhs, rank, 1.0, vt, 0, 1, n, utb, 0, nrhs, 1, x, 0, nrhs, accumulate: false);
            double[] res = Array.Empty<double>();
            if (rank == n && m > n)
            {
                var r = (double[])b._data.Clone();
                Gemm.Run(m, nrhs, n, -1.0, a._data, 0, n, 1, x, 0, nrhs, 1, r, 0, nrhs, accumulate: true);
                res = new double[nrhs];
                for (int i = 0; i < m; i++)
                    for (int j = 0; j < nrhs; j++) res[j] += r[i * nrhs + j] * r[i * nrhs + j];
            }
            return (new ndarray(x, b.ndim == 1 ? new[] { n } : new[] { n, nrhs }), new ndarray(res, new[] { res.Length }),
                    rank, new ndarray(s, new[] { k }));
        }

        /// <summary>Moore-Penrose pseudo-inverse via the SVD; singular values at or below rcond * max(s) are dropped.</summary>
        public static ndarray pinv(ndarray a, double rcond = 1e-15)
        {
            var (m, n) = Shape2(a, "pinv");
            if (!Lapack.Gesvd(a._data, m, n, true, false, out var s, out var u, out var vt)) throw new LinAlgError("SVD did not converge");
            int k = s.Length;
            double cut = rcond * (k > 0 ? s[0] : 0);
            // pinv = V diag(1/s) U^T  (n x m): scale the rows of Vh, then (Vh^T)(U^T)
            var vs = (double[])vt.Clone();
            for (int i = 0; i < k; i++)
            {
                double inv = s[i] > cut ? 1.0 / s[i] : 0.0;
                for (int j = 0; j < n; j++) vs[i * n + j] *= inv;
            }
            var r = new double[n * m];
            Gemm.Run(n, m, k, 1.0, vs, 0, 1, n, u, 0, 1, k, r, 0, m, accumulate: false);
            return new ndarray(r, new[] { n, m });
        }

        /// <summary>Rank: the number of singular values above tol (default max(s) * max(m, n) * eps).</summary>
        public static int matrix_rank(ndarray A, double? tol = null)
        {
            if (A.ndim < 2) return A._data.Length > 0 && Array.Exists(A._data, v => v != 0) ? 1 : 0;
            var s = svdvals(A)._data;
            if (s.Length == 0) return 0;
            double t = tol ?? s[0] * Math.Max(A._shape[0], A._shape[1]) * 2.220446049250313e-16;
            int r = 0;
            foreach (double v in s) if (v > t) r++;
            return r;
        }

        /// <summary>2-norm condition number: largest / smallest singular value.</summary>
        public static double cond(ndarray x)
        {
            var s = svdvals(x)._data;
            return s.Length == 0 ? double.NaN : s[0] / s[^1];
        }

        private static (int m, int n) Shape2(ndarray a, string what)
        {
            if (a.ndim != 2) throw new NotSupportedException($"{what}: only 2-D arrays are supported");
            return (a._shape[0], a._shape[1]);
        }

        private static bool Lower(string uplo) => uplo switch
        {
            "L" or "l" => true,
            "U" or "u" => false,
            _ => throw Err.Value("UPLO argument must be 'L' or 'U'"),
        };

        /// <summary>a raised to an integer power (repeated squaring; negative uses inv).</summary>
        public static ndarray matrix_power(ndarray a, int n)
        {
            int size = SquareSize(a);
            if (n == 0) return eye(size);
            var b = n < 0 ? inv(a) : a.copy();
            int p = Math.Abs(n);
            ndarray result = null;
            while (p > 0)
            {
                if ((p & 1) != 0) result = result == null ? b : matmul(result, b);
                p >>= 1;
                if (p > 0) b = matmul(b, b);
            }
            return result;
        }

        private static int SquareSize(ndarray a)
        {
            if (a.ndim != 2 || a._shape[0] != a._shape[1])
                throw Err.Value($"Last 2 dimensions of the array must be square (got shape {Err.ShapeStr(a._shape)})");
            return a._shape[0];
        }

        /// <summary>LU factors of a copy of the square matrix a; LinAlgError when it is singular.</summary>
        private static double[] Factor(ndarray a, out int[] piv)
        {
            int n = a._shape[0];
            var lu = (double[])a._data.Clone();
            piv = new int[n];
            if (Lapack.Getrf(lu, n, n, piv) != 0) throw new LinAlgError("Singular matrix");
            return lu;
        }
    }
}
