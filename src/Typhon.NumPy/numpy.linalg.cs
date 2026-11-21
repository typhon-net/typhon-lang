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
