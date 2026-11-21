// Copyright (c) 2026 The Typhon contributors.
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Numerics;
using System.Numerics.Tensors;
using System.Runtime.CompilerServices;

namespace Typhon.Stdlib;

/// <summary>
/// numpy for Typhon. <c>import numpy as np</c> is emitted as <c>using np = global::Typhon.Stdlib.numpy;</c>.
/// All arrays are float64 (see <see cref="ndarray"/>). Functions that numpy defines as "array_like" take
/// <see cref="ndarray"/>; Python floats, List&lt;T&gt; list literals and double[] convert to it implicitly.
/// Python exceptions map to .NET: ValueError -> ArgumentException, IndexError -> IndexOutOfRangeException,
/// TypeError -> InvalidOperationException.
/// </summary>
public static partial class numpy
{
    // ====================================================================== constants

    public static readonly double pi = Math.PI;
    public static readonly double e = Math.E;
    public static readonly double inf = double.PositiveInfinity;
    public static readonly double nan = double.NaN;
    public static readonly double NaN = double.NaN;
    public static readonly double Inf = double.PositiveInfinity;
    public static readonly double euler_gamma = 0.57721566490153286060651209008240243;

    // dtype tokens: usable as dtype=np.float64 and callable as np.float64(x).
    public static readonly Func<double, double> float64 = x => x;
    public static readonly Func<double, double> float32 = x => (float)x;
    public static readonly Func<double, long> int64 = x => (long)x;
    public static readonly Func<double, int> int32 = x => (int)x;
    public static readonly Func<object, bool> bool_ = x => ToDouble(x) != 0;

    internal enum DtypeCode { Float, Int, Bool }

    internal static DtypeCode DtypeOf(object dtype)
    {
        switch (dtype)
        {
            case null: return DtypeCode.Float;
            case Type t:
                if (t == typeof(bool)) return DtypeCode.Bool;
                if (t == typeof(int) || t == typeof(long) || t == typeof(short) || t == typeof(byte) || t == typeof(sbyte)
                    || t == typeof(uint) || t == typeof(ulong) || t == typeof(ushort) || t == typeof(BigInteger)) return DtypeCode.Int;
                return DtypeCode.Float;
            case string s:
                s = s.ToLowerInvariant();
                if (s is "bool" or "bool_" or "?" or "b1") return DtypeCode.Bool;
                if (s.StartsWith("int", StringComparison.Ordinal) || s.StartsWith("uint", StringComparison.Ordinal) || s is "i" or "i4" or "i8" or "l" or "long") return DtypeCode.Int;
                return DtypeCode.Float;
            default:
                if (ReferenceEquals(dtype, int64) || ReferenceEquals(dtype, int32)) return DtypeCode.Int;
                if (ReferenceEquals(dtype, bool_)) return DtypeCode.Bool;
                return DtypeCode.Float;
        }
    }

    private static ndarray ApplyDtype(ndarray a, object dtype)
    {
        if (dtype == null) return a;
        switch (DtypeOf(dtype))
        {
            case DtypeCode.Int:
                TensorPrimitives.Truncate<double>(a._data, a._data);
                return a.WithKind(NdKind.Float);
            case DtypeCode.Bool:
                for (int i = 0; i < a._data.Length; i++) a._data[i] = a._data[i] != 0 ? 1.0 : 0.0;
                return a.WithKind(NdKind.Bool);
            default:
                return a.WithKind(NdKind.Float);
        }
    }

    // ====================================================================== scalar conversion helpers

    internal static bool TryScalar(object o, out double v)
    {
        switch (o)
        {
            case double d: v = d; return true;
            case int i: v = i; return true;
            case long l: v = l; return true;
            case float f: v = f; return true;
            case bool b: v = b ? 1.0 : 0.0; return true;
            case short s: v = s; return true;
            case byte by: v = by; return true;
            case sbyte sb: v = sb; return true;
            case uint ui: v = ui; return true;
            case ulong ul: v = ul; return true;
            case ushort us: v = us; return true;
            case decimal m: v = (double)m; return true;
            case BigInteger bi: v = (double)bi; return true;
            case ndarray nd when nd._shape.Length == 0: v = nd._data[0]; return true;
            default: v = 0; return false;
        }
    }

    internal static double ToDouble(object o)
    {
        if (TryScalar(o, out double v)) return v;
        if (o is ndarray nd && nd._data.Length == 1) return nd._data[0];
        if (o is string s && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out v)) return v;
        throw Err.Type($"float() argument must be a number, not '{o?.GetType().Name ?? "None"}'");
    }

    // ====================================================================== array construction from Python objects

    internal static ndarray FromDoubles(IEnumerable<double> values)
    {
        double[] d = values is List<double> l ? l.ToArray() : new List<double>(values).ToArray();
        return new ndarray(d, new[] { d.Length });
    }

    internal static ndarray FromInts(IEnumerable<int> values)
    {
        var list = values as ICollection<int> ?? new List<int>(values);
        var d = new double[list.Count];
        int i = 0;
        foreach (int v in list) d[i++] = v;
        return new ndarray(d, new[] { d.Length });
    }

    private static IList AsSequence(object o)
    {
        switch (o)
        {
            case null: return null;
            case string: return null;
            case ndarray: return null;
            case IList l: return l;
            case ITuple t:
                var items = new object[t.Length];
                for (int i = 0; i < t.Length; i++) items[i] = t[i];
                return items;
            case IEnumerable en:
                var list = new List<object>();
                foreach (object x in en) list.Add(x);
                return list;
            default: return null;
        }
    }

    /// <summary>Builds an array from nested sequences (List, arrays, tuples, IEnumerable) of numbers/bools/ndarrays.</summary>
    internal static ndarray FromObject(object obj)
    {
        if (obj is ndarray nd) return nd.copy();
        if (obj is bool b) return new ndarray(new[] { b ? 1.0 : 0.0 }, Array.Empty<int>(), NdKind.Bool);
        if (TryScalar(obj, out double sv)) return ndarray.Scalar0d(sv);
        if (obj is double[] da) return new ndarray((double[])da.Clone(), new[] { da.Length });
        if (obj is List<double> ld) return new ndarray(ld.ToArray(), new[] { ld.Count });
        if (obj is string str) throw Err.Value($"could not convert string to float: '{str}'");

        // Determine shape by following the first element at every depth.
        var shape = new List<int>();
        object cur = obj;
        while (true)
        {
            if (cur is ndarray inner) { shape.AddRange(inner._shape); break; }
            var seq = AsSequence(cur);
            if (seq == null)
            {
                if (cur == null || !TryScalar(cur, out _)) throw Err.Value($"could not convert {(cur == null ? "None" : cur.GetType().Name)} to float");
                break;
            }
            shape.Add(seq.Count);
            if (seq.Count == 0) break;
            cur = seq[0];
        }
        var shp = shape.ToArray();
        var data = new double[Kernels.Prod(shp)];
        int pos = 0;
        bool allBool = data.Length > 0;
        Fill(obj, 0, shp, data, ref pos, ref allBool);
        return new ndarray(data, shp, allBool ? NdKind.Bool : NdKind.Float);
    }

    private static void Fill(object o, int depth, int[] shape, double[] data, ref int pos, ref bool allBool)
    {
        if (o is ndarray nd)
        {
            bool ok = nd._shape.Length == shape.Length - depth;
            for (int i = 0; ok && i < nd._shape.Length; i++) ok = nd._shape[i] == shape[depth + i];
            if (!ok) throw Inhomogeneous(depth);
            Array.Copy(nd._data, 0, data, pos, nd._data.Length);
            pos += nd._data.Length;
            allBool &= nd._kind == NdKind.Bool;
            return;
        }
        if (depth == shape.Length)
        {
            if (o is bool bv) { data[pos++] = bv ? 1.0 : 0.0; return; }
            allBool = false;
            if (!TryScalar(o, out double v)) throw Inhomogeneous(depth);
            data[pos++] = v;
            return;
        }
        var seq = AsSequence(o);
        if (seq == null || seq.Count != shape[depth]) throw Inhomogeneous(depth);
        for (int i = 0; i < seq.Count; i++) Fill(seq[i], depth + 1, shape, data, ref pos, ref allBool);
    }

    private static ArgumentException Inhomogeneous(int depth) =>
        Err.Value($"setting an array element with a sequence. The requested array has an inhomogeneous shape after {depth} dimensions.");

    // ====================================================================== creation

    /// <summary>np.array(object): nested lists/tuples/arrays of numbers -> new float64 array.</summary>
    public static ndarray array(object @object, object dtype = null) => ApplyDtype(FromObject(@object), dtype);
    public static ndarray array(double[] @object, object dtype = null) => ApplyDtype(new ndarray((double[])@object.Clone(), new[] { @object.Length }), dtype);
    public static ndarray array(IEnumerable<double> @object, object dtype = null) => ApplyDtype(FromDoubles(@object), dtype);
    public static ndarray array(IEnumerable<int> @object, object dtype = null) => ApplyDtype(FromInts(@object), dtype);
    public static ndarray array(IEnumerable<long> @object, object dtype = null) => ApplyDtype(FromObject(@object), dtype);

    public static ndarray array(IEnumerable<IEnumerable<double>> @object, object dtype = null)
    {
        var rows = @object as IList<IEnumerable<double>> ?? new List<IEnumerable<double>>(@object);
        int n = rows.Count;
        if (n == 0) return new ndarray(Array.Empty<double>(), new[] { 0 });
        double[][] rr = new double[n][];
        for (int i = 0; i < n; i++)
            rr[i] = rows[i] switch { double[] a => a, List<double> l => l.ToArray(), var other => new List<double>(other).ToArray() };
        int m = rr[0].Length;
        for (int i = 1; i < n; i++) if (rr[i].Length != m) throw Inhomogeneous(1);
        var data = Kernels.Alloc(n * m);
        for (int i = 0; i < n; i++) Array.Copy(rr[i], 0, data, i * m, m);
        return ApplyDtype(new ndarray(data, new[] { n, m }), dtype);
    }

    public static ndarray array(IEnumerable<IEnumerable<int>> @object, object dtype = null) => ApplyDtype(FromObject(@object), dtype);
    public static ndarray array(IEnumerable<IEnumerable<long>> @object, object dtype = null) => ApplyDtype(FromObject(@object), dtype);

    /// <summary>Like array() but returns the argument itself when it already is an ndarray.</summary>
    public static ndarray asarray(object a, object dtype = null)
    {
        if (a is ndarray nd && dtype == null) return nd;
        return array(a, dtype);
    }

    public static ndarray copy(ndarray a) => a.copy();

    public static ndarray zeros(NumPyShape shape, object dtype = null)
    {
        var s = (int[])shape.Dims.Clone();
        return new ndarray(new double[Kernels.Prod(s)], s, DtypeOf(dtype) == DtypeCode.Bool ? NdKind.Bool : NdKind.Float);
    }

    public static ndarray ones(NumPyShape shape, object dtype = null) => full(shape, 1.0, dtype);

    /// <summary>Like zeros (numpy leaves memory uninitialized; we zero it).</summary>
    public static ndarray empty(NumPyShape shape, object dtype = null) => zeros(shape, dtype);

    public static ndarray full(NumPyShape shape, double fill_value, object dtype = null)
    {
        var s = (int[])shape.Dims.Clone();
        var d = Kernels.Alloc(Kernels.Prod(s));
        d.AsSpan().Fill(fill_value);
        return ApplyDtype(new ndarray(d, s), dtype);
    }

    public static ndarray zeros_like(ndarray a, object dtype = null) => zeros(a._shape, dtype);
    public static ndarray ones_like(ndarray a, object dtype = null) => ones(a._shape, dtype);
    public static ndarray empty_like(ndarray a, object dtype = null) => zeros(a._shape, dtype);
    public static ndarray full_like(ndarray a, double fill_value, object dtype = null) => full(a._shape, fill_value, dtype);

    /// <summary>np.arange(stop): 0, 1, ..., stop-1 (as float64).</summary>
    public static ndarray arange(double stop) => arange(0.0, stop, 1.0);

    /// <summary>np.arange(start, stop, step=1).</summary>
    public static ndarray arange(double start, double stop, double step = 1.0)
    {
        if (step == 0) throw Err.Value("Maximum allowed size exceeded");
        double len = Math.Ceiling((stop - start) / step);
        int n = len > 0 ? checked((int)len) : 0;
        var d = Kernels.Alloc(n);
        for (int i = 0; i < n; i++) d[i] = start + i * step;
        return new ndarray(d, new[] { n });
    }

    /// <summary>np.linspace(start, stop, num=50, endpoint=True).</summary>
    public static ndarray linspace(double start, double stop, int num = 50, bool endpoint = true)
    {
        if (num < 0) throw Err.Value($"Number of samples, {num}, must be non-negative.");
        var d = Kernels.Alloc(num);
        int div = endpoint ? num - 1 : num;
        double delta = stop - start;
        if (num > 0)
        {
            if (div > 0)
            {
                double step = delta / div;
                if (step == 0) for (int i = 0; i < num; i++) d[i] = (i / (double)div) * delta + start;
                else for (int i = 0; i < num; i++) d[i] = i * step + start;
            }
            else d[0] = start;
            if (endpoint && num > 1) d[num - 1] = stop;
        }
        return new ndarray(d, new[] { num });
    }

    public static ndarray logspace(double start, double stop, int num = 50, bool endpoint = true, double @base = 10.0)
    {
        var y = linspace(start, stop, num, endpoint);
        TensorPrimitives.Pow(@base, y._data, y._data);
        return y;
    }

    /// <summary>2-D array with ones on the k-th diagonal.</summary>
    public static ndarray eye(int N, int? M = null, int k = 0, object dtype = null)
    {
        int m = M ?? N;
        var d = new double[N * m];
        for (int i = 0; i < N; i++)
        {
            int j = i + k;
            if (j >= 0 && j < m) d[i * m + j] = 1.0;
        }
        return ApplyDtype(new ndarray(d, new[] { N, m }), dtype);
    }

    public static ndarray identity(int n, object dtype = null) => eye(n, dtype: dtype);

    /// <summary>1-D input: 2-D array with v on the k-th diagonal. 2-D input: the k-th diagonal.</summary>
    public static ndarray diag(ndarray v, int k = 0)
    {
        if (v.ndim == 1)
        {
            int n = v._data.Length + Math.Abs(k);
            var d = new double[n * n];
            for (int i = 0; i < v._data.Length; i++)
            {
                int r = k >= 0 ? i : i - k, c = k >= 0 ? i + k : i;
                d[r * n + c] = v._data[i];
            }
            return new ndarray(d, new[] { n, n });
        }
        if (v.ndim == 2)
        {
            int rows = v._shape[0], cols = v._shape[1];
            var list = new List<double>();
            for (int i = 0; i < rows; i++)
            {
                int j = i + k;
                if (j >= 0 && j < cols) list.Add(v._data[i * cols + j]);
            }
            return new ndarray(list.ToArray(), new[] { list.Count });
        }
        throw Err.Value("Input must be 1- or 2-d.");
    }

    public static double trace(ndarray a) => diag(a).sum();

    /// <summary>Coordinate matrices from coordinate vectors (indexing='xy').</summary>
    public static ndarray[] meshgrid(ndarray x, ndarray y)
    {
        int nx = x._data.Length, ny = y._data.Length;
        var X = Kernels.Alloc(nx * ny);
        var Y = Kernels.Alloc(nx * ny);
        for (int i = 0; i < ny; i++)
        {
            x._data.AsSpan().CopyTo(X.AsSpan(i * nx, nx));
            Y.AsSpan(i * nx, nx).Fill(y._data[i]);
        }
        return new[] { new ndarray(X, new[] { ny, nx }), new ndarray(Y, new[] { ny, nx }) };
    }

    // ====================================================================== attributes as functions

    public static int[] shape(ndarray a) => a.shape;
    public static int size(ndarray a) => a.size;
    public static int ndim(ndarray a) => a.ndim;

    // ====================================================================== element-wise math (ufuncs)

    private static ndarray U(ndarray a, UnOp op) => Kernels.Unary(a, op);

    public static ndarray sqrt(ndarray x) => U(x, UnOp.Sqrt);
    public static double sqrt(double x) => Math.Sqrt(x);
    public static ndarray cbrt(ndarray x) => U(x, UnOp.Cbrt);
    public static double cbrt(double x) => Math.Cbrt(x);
    public static ndarray square(ndarray x) => U(x, UnOp.Square);
    public static double square(double x) => x * x;
    public static ndarray exp(ndarray x) => U(x, UnOp.Exp);
    public static double exp(double x) => Math.Exp(x);
    public static ndarray exp2(ndarray x) => U(x, UnOp.Exp2);
    public static double exp2(double x) => Math.Pow(2.0, x);
    public static ndarray expm1(ndarray x) => U(x, UnOp.Expm1);
    public static double expm1(double x) => double.ExpM1(x);
    public static ndarray log(ndarray x) => U(x, UnOp.Log);
    public static double log(double x) => Math.Log(x);
    public static ndarray log2(ndarray x) => U(x, UnOp.Log2);
    public static double log2(double x) => Math.Log2(x);
    public static ndarray log10(ndarray x) => U(x, UnOp.Log10);
    public static double log10(double x) => Math.Log10(x);
    public static ndarray log1p(ndarray x) => U(x, UnOp.Log1p);
    public static double log1p(double x) => double.LogP1(x);
    public static ndarray sin(ndarray x) => U(x, UnOp.Sin);
    public static double sin(double x) => Math.Sin(x);
    public static ndarray cos(ndarray x) => U(x, UnOp.Cos);
    public static double cos(double x) => Math.Cos(x);
    public static ndarray tan(ndarray x) => U(x, UnOp.Tan);
    public static double tan(double x) => Math.Tan(x);
    public static ndarray arcsin(ndarray x) => U(x, UnOp.Arcsin);
    public static double arcsin(double x) => Math.Asin(x);
    public static ndarray arccos(ndarray x) => U(x, UnOp.Arccos);
    public static double arccos(double x) => Math.Acos(x);
    public static ndarray arctan(ndarray x) => U(x, UnOp.Arctan);
    public static double arctan(double x) => Math.Atan(x);
    public static ndarray sinh(ndarray x) => U(x, UnOp.Sinh);
    public static double sinh(double x) => Math.Sinh(x);
    public static ndarray cosh(ndarray x) => U(x, UnOp.Cosh);
    public static double cosh(double x) => Math.Cosh(x);
    public static ndarray tanh(ndarray x) => U(x, UnOp.Tanh);
    public static double tanh(double x) => Math.Tanh(x);
    public static ndarray arcsinh(ndarray x) => U(x, UnOp.Arcsinh);
    public static double arcsinh(double x) => Math.Asinh(x);
    public static ndarray arccosh(ndarray x) => U(x, UnOp.Arccosh);
    public static double arccosh(double x) => Math.Acosh(x);
    public static ndarray arctanh(ndarray x) => U(x, UnOp.Arctanh);
    public static double arctanh(double x) => Math.Atanh(x);
    public static ndarray abs(ndarray x) => U(x, UnOp.Abs);
    public static double abs(double x) => Math.Abs(x);
    public static ndarray absolute(ndarray x) => U(x, UnOp.Abs);
    public static double absolute(double x) => Math.Abs(x);
    public static ndarray fabs(ndarray x) => U(x, UnOp.Abs);
    public static double fabs(double x) => Math.Abs(x);
    public static ndarray sign(ndarray x) => U(x, UnOp.Sign);
    public static double sign(double x) => Kernels.UnaryScalar(UnOp.Sign, x);
    public static ndarray floor(ndarray x) => U(x, UnOp.Floor);
    public static double floor(double x) => Math.Floor(x);
    public static ndarray ceil(ndarray x) => U(x, UnOp.Ceil);
    public static double ceil(double x) => Math.Ceiling(x);
    public static ndarray trunc(ndarray x) => U(x, UnOp.Trunc);
    public static double trunc(double x) => Math.Truncate(x);
    public static ndarray rint(ndarray x) => U(x, UnOp.Rint);
    public static double rint(double x) => Math.Round(x, MidpointRounding.ToEven);
    public static ndarray negative(ndarray x) => U(x, UnOp.Neg);
    public static double negative(double x) => -x;
    public static ndarray positive(ndarray x) => x.copy();
    public static double positive(double x) => x;
    public static ndarray reciprocal(ndarray x) => U(x, UnOp.Reciprocal);
    public static double reciprocal(double x) => 1.0 / x;
    public static ndarray deg2rad(ndarray x) => U(x, UnOp.Deg2Rad);
    public static double deg2rad(double x) => x * (Math.PI / 180.0);
    public static ndarray radians(ndarray x) => U(x, UnOp.Deg2Rad);
    public static double radians(double x) => x * (Math.PI / 180.0);
    public static ndarray rad2deg(ndarray x) => U(x, UnOp.Rad2Deg);
    public static double rad2deg(double x) => x * (180.0 / Math.PI);
    public static ndarray degrees(ndarray x) => U(x, UnOp.Rad2Deg);
    public static double degrees(double x) => x * (180.0 / Math.PI);

    public static ndarray isnan(ndarray x) => U(x, UnOp.IsNaN);
    public static bool isnan(double x) => double.IsNaN(x);
    public static ndarray isinf(ndarray x) => U(x, UnOp.IsInf);
    public static bool isinf(double x) => double.IsInfinity(x);
    public static ndarray isfinite(ndarray x) => U(x, UnOp.IsFinite);
    public static bool isfinite(double x) => double.IsFinite(x);
    public static ndarray logical_not(ndarray x) => U(x, UnOp.Not);
    public static bool logical_not(double x) => x == 0;

    /// <summary>Round half to even to the given number of decimals.</summary>
    public static ndarray round(ndarray a, int decimals = 0)
    {
        var r = Kernels.Alloc(a._data.Length);
        if (decimals == 0) TensorPrimitives.Round(a._data, MidpointRounding.ToEven, r);
        else
        {
            for (int i = 0; i < r.Length; i++) r[i] = RoundScalar(a._data[i], decimals);
        }
        return new ndarray(r, (int[])a._shape.Clone());
    }

    public static double round(double a, int decimals = 0) => decimals == 0 ? Math.Round(a, MidpointRounding.ToEven) : RoundScalar(a, decimals);
    public static ndarray around(ndarray a, int decimals = 0) => round(a, decimals);
    public static ndarray round_(ndarray a, int decimals = 0) => round(a, decimals);

    private static double RoundScalar(double v, int decimals)
    {
        // numpy: rint(x * 10**d) / 10**d
        if (decimals >= 0)
        {
            double f = Math.Pow(10.0, decimals);
            return Math.Round(v * f, MidpointRounding.ToEven) / f;
        }
        double g = Math.Pow(10.0, -decimals);
        return Math.Round(v / g, MidpointRounding.ToEven) * g;
    }

    // ---- binary ufuncs: (ndarray, ndarray) broadcasts; Python floats convert to 0-d arrays.

    private static ndarray B(ndarray a, ndarray b, BinOp op) => Kernels.Binary(a, b, op);

    public static ndarray add(ndarray x1, ndarray x2) => B(x1, x2, BinOp.Add);
    public static double add(double x1, double x2) => x1 + x2;
    public static ndarray subtract(ndarray x1, ndarray x2) => B(x1, x2, BinOp.Sub);
    public static double subtract(double x1, double x2) => x1 - x2;
    public static ndarray multiply(ndarray x1, ndarray x2) => B(x1, x2, BinOp.Mul);
    public static double multiply(double x1, double x2) => x1 * x2;
    public static ndarray divide(ndarray x1, ndarray x2) => B(x1, x2, BinOp.Div);
    public static double divide(double x1, double x2) => x1 / x2;
    public static ndarray true_divide(ndarray x1, ndarray x2) => B(x1, x2, BinOp.Div);
    public static double true_divide(double x1, double x2) => x1 / x2;
    public static ndarray floor_divide(ndarray x1, ndarray x2) => B(x1, x2, BinOp.FloorDiv);
    public static double floor_divide(double x1, double x2) => Math.Floor(x1 / x2);
    public static ndarray mod(ndarray x1, ndarray x2) => B(x1, x2, BinOp.Mod);
    public static double mod(double x1, double x2) => Kernels.PyMod(x1, x2);
    public static ndarray remainder(ndarray x1, ndarray x2) => B(x1, x2, BinOp.Mod);
    public static double remainder(double x1, double x2) => Kernels.PyMod(x1, x2);
    public static ndarray fmod(ndarray x1, ndarray x2)
    {
        var r = B(x1, x2, BinOp.Sub); // shape/broadcast check + allocation
        var a = Kernels.BroadcastTo(x1, r._shape);
        var b = Kernels.BroadcastTo(x2, r._shape);
        for (int i = 0; i < r._data.Length; i++) r._data[i] = a[i] % b[i];
        return r;
    }

    /// <summary>Element-wise power; exponent 2 / 0.5 use multiply / sqrt.</summary>
    public static ndarray power(ndarray x1, ndarray x2) => B(x1, x2, BinOp.Pow);
    public static ndarray power(ndarray x1, double x2) => Kernels.BinaryScalarRight(x1, x2, BinOp.Pow);
    public static double power(double x1, double x2) => Math.Pow(x1, x2);

    /// <summary>Element-wise maximum (NaN propagates).</summary>
    public static ndarray maximum(ndarray x1, ndarray x2) => B(x1, x2, BinOp.Max);
    public static double maximum(double x1, double x2) => Math.Max(x1, x2);
    public static ndarray minimum(ndarray x1, ndarray x2) => B(x1, x2, BinOp.Min);
    public static double minimum(double x1, double x2) => Math.Min(x1, x2);
    public static ndarray fmax(ndarray x1, ndarray x2) => B(x1, x2, BinOp.FMax);
    public static double fmax(double x1, double x2) => double.MaxNumber(x1, x2);
    public static ndarray fmin(ndarray x1, ndarray x2) => B(x1, x2, BinOp.FMin);
    public static double fmin(double x1, double x2) => double.MinNumber(x1, x2);
    public static ndarray arctan2(ndarray x1, ndarray x2) => B(x1, x2, BinOp.Atan2);
    public static double arctan2(double x1, double x2) => Math.Atan2(x1, x2);
    public static ndarray hypot(ndarray x1, ndarray x2) => B(x1, x2, BinOp.Hypot);
    public static double hypot(double x1, double x2) => double.Hypot(x1, x2);

    public static ndarray equal(ndarray x1, ndarray x2) => B(x1, x2, BinOp.Eq);
    public static ndarray not_equal(ndarray x1, ndarray x2) => B(x1, x2, BinOp.Ne);
    public static ndarray less(ndarray x1, ndarray x2) => B(x1, x2, BinOp.Lt);
    public static ndarray less_equal(ndarray x1, ndarray x2) => B(x1, x2, BinOp.Le);
    public static ndarray greater(ndarray x1, ndarray x2) => B(x1, x2, BinOp.Gt);
    public static ndarray greater_equal(ndarray x1, ndarray x2) => B(x1, x2, BinOp.Ge);
    public static ndarray logical_and(ndarray x1, ndarray x2) => B(x1, x2, BinOp.And);
    public static ndarray logical_or(ndarray x1, ndarray x2) => B(x1, x2, BinOp.Or);
    public static ndarray logical_xor(ndarray x1, ndarray x2) => B(x1, x2, BinOp.Xor);

    /// <summary>Limit values to [a_min, a_max].</summary>
    public static ndarray clip(ndarray a, double a_min, double a_max)
    {
        var src = a._data;
        var r = Kernels.Alloc(src.Length);
        if (a_min <= a_max)
            Kernels.Chunked(r.Length, (s, n) => TensorPrimitives.Clamp(src.AsSpan(s, n), a_min, a_max, r.AsSpan(s, n)));
        else
            r.AsSpan().Fill(a_max); // numpy: max wins when min > max
        return new ndarray(r, (int[])a._shape.Clone());
    }

    public static double clip(double a, double a_min, double a_max) => Math.Min(Math.Max(a, a_min), a_max);

    /// <summary>np.where(condition, x, y) with broadcasting.</summary>
    public static ndarray where(ndarray condition, ndarray x, ndarray y)
    {
        var rs = Kernels.BroadcastShapes(Kernels.BroadcastShapes(condition._shape, x._shape), y._shape);
        var c = Kernels.BroadcastTo(condition, rs);
        var xs = Kernels.BroadcastTo(x, rs);
        var ys = Kernels.BroadcastTo(y, rs);
        var r = Kernels.Alloc(c.Length);
        for (int i = 0; i < r.Length; i++) r[i] = c[i] != 0 ? xs[i] : ys[i];
        var kind = x._kind == NdKind.Bool && y._kind == NdKind.Bool ? NdKind.Bool : NdKind.Float;
        return new ndarray(r, rs, kind);
    }

    /// <summary>np.where(condition): indices of non-zero elements, one index array per dimension.</summary>
    public static ndarray[] where(ndarray condition) => nonzero(condition);

    public static ndarray[] nonzero(ndarray a)
    {
        int nd = Math.Max(1, a.ndim);
        var lists = new List<double>[nd];
        for (int d = 0; d < nd; d++) lists[d] = new List<double>();
        var idx = new int[nd];
        for (int i = 0; i < a._data.Length; i++)
        {
            if (a._data[i] != 0)
            {
                int rem = i;
                for (int d = nd - 1; d >= 0; d--)
                {
                    int dim = a.ndim == 0 ? 1 : a._shape[d];
                    idx[d] = rem % dim;
                    rem /= dim;
                }
                for (int d = 0; d < nd; d++) lists[d].Add(idx[d]);
            }
        }
        var res = new ndarray[nd];
        for (int d = 0; d < nd; d++) res[d] = new ndarray(lists[d].ToArray(), new[] { lists[d].Count });
        return res;
    }

    public static int count_nonzero(ndarray a)
    {
        int c = 0;
        foreach (double v in a._data) if (v != 0) c++;
        return c;
    }

    // ====================================================================== reductions
    // Pattern: f(a) -> double; f(a, axis) -> ndarray; f(a, axis: null/int?, keepdims: ...) -> dynamic.

    public static double sum(ndarray a) => a.sum();
    public static ndarray sum(ndarray a, int axis) => a.sum(axis);
    public static dynamic sum(ndarray a, int? axis = null, bool keepdims = false) => a.Reduce(axis, keepdims, RedOp.Sum);

    public static double mean(ndarray a) => a.mean();
    public static ndarray mean(ndarray a, int axis) => a.mean(axis);
    public static dynamic mean(ndarray a, int? axis = null, bool keepdims = false) => a.Reduce(axis, keepdims, RedOp.Mean);

    public static double average(ndarray a) => a.mean();
    public static ndarray average(ndarray a, int axis) => a.mean(axis);

    public static double prod(ndarray a) => a.prod();
    public static ndarray prod(ndarray a, int axis) => a.prod(axis);
    public static dynamic prod(ndarray a, int? axis = null, bool keepdims = false) => a.Reduce(axis, keepdims, RedOp.Prod);

    public static double min(ndarray a) => a.min();
    public static ndarray min(ndarray a, int axis) => a.min(axis);
    public static dynamic min(ndarray a, int? axis = null, bool keepdims = false) => a.Reduce(axis, keepdims, RedOp.Min);
    public static double amin(ndarray a) => a.min();
    public static ndarray amin(ndarray a, int axis) => a.min(axis);

    public static double max(ndarray a) => a.max();
    public static ndarray max(ndarray a, int axis) => a.max(axis);
    public static dynamic max(ndarray a, int? axis = null, bool keepdims = false) => a.Reduce(axis, keepdims, RedOp.Max);
    public static double amax(ndarray a) => a.max();
    public static ndarray amax(ndarray a, int axis) => a.max(axis);

    public static double ptp(ndarray a) => a.max() - a.min();

    public static int argmax(ndarray a) => a.argmax();
    public static ndarray argmax(ndarray a, int axis) => a.argmax(axis);
    public static int argmin(ndarray a) => a.argmin();
    public static ndarray argmin(ndarray a, int axis) => a.argmin(axis);

    public static double std(ndarray a) => a.std();
    public static ndarray std(ndarray a, int axis) => a.std(axis);
    public static dynamic std(ndarray a, int? axis = null, int ddof = 0, bool keepdims = false) => a.VarImpl(axis, ddof, true, keepdims);

    public static double var(ndarray a) => a.var();
    public static ndarray var(ndarray a, int axis) => a.var(axis);
    public static dynamic var(ndarray a, int? axis = null, int ddof = 0, bool keepdims = false) => a.VarImpl(axis, ddof, false, keepdims);

    public static bool any(ndarray a) => a.any();
    public static ndarray any(ndarray a, int axis) => a.any(axis);
    public static bool all(ndarray a) => a.all();
    public static ndarray all(ndarray a, int axis) => a.all(axis);

    public static ndarray cumsum(ndarray a) => a.cumsum();
    public static ndarray cumsum(ndarray a, int axis) => a.cumsum(axis);
    public static ndarray cumprod(ndarray a) => a.cumprod();
    public static ndarray cumprod(ndarray a, int axis) => a.cumprod(axis);

    public static double median(ndarray a) => QuantileSorted(SortedCopy(a._data), 0.5);

    public static ndarray median(ndarray a, int axis) => QuantileAxis(a, axis, 0.5);

    /// <summary>q-th percentile (0..100), linear interpolation.</summary>
    public static double percentile(ndarray a, double q) => QuantileSorted(SortedCopy(a._data), CheckQ(q / 100.0));
    public static ndarray percentile(ndarray a, double q, int axis) => QuantileAxis(a, axis, CheckQ(q / 100.0));
    /// <summary>q-th quantile (0..1), linear interpolation.</summary>
    public static double quantile(ndarray a, double q) => QuantileSorted(SortedCopy(a._data), CheckQ(q));
    public static ndarray quantile(ndarray a, double q, int axis) => QuantileAxis(a, axis, CheckQ(q));

    private static double CheckQ(double q) =>
        q is >= 0 and <= 1 ? q : throw Err.Value("Quantiles must be in the range [0, 1]");

    private static double[] SortedCopy(ReadOnlySpan<double> d)
    {
        var c = d.ToArray();
        c.AsSpan().Sort(ndarray.NaNLastComparison);
        return c;
    }

    private static double QuantileSorted(double[] s, double q)
    {
        if (s.Length == 0) return double.NaN;
        if (double.IsNaN(s[^1])) return double.NaN;
        double pos = q * (s.Length - 1);
        int lo = (int)Math.Floor(pos);
        int hi = Math.Min(lo + 1, s.Length - 1);
        double frac = pos - lo;
        return s[lo] + (s[hi] - s[lo]) * frac;
    }

    private static ndarray QuantileAxis(ndarray a, int axis, double q)
    {
        int ax = Kernels.NormAxis(axis, a.ndim);
        var (outer, n, inner) = Kernels.SplitAxis(a._shape, ax);
        var res = new double[outer * inner];
        var tmp = new double[n];
        for (int o = 0; o < outer; o++)
            for (int j = 0; j < inner; j++)
            {
                for (int k = 0; k < n; k++) tmp[k] = a._data[(o * n + k) * inner + j];
                res[o * inner + j] = QuantileSorted(SortedCopy(tmp), q);
            }
        return new ndarray(res, Kernels.RemoveAxis(a._shape, ax, false));
    }

    // ====================================================================== linear algebra

    /// <summary>
    /// np.dot: 1-D . 1-D -> double; 2-D . 2-D -> matrix product; 2-D . 1-D / 1-D . 2-D -> ndarray;
    /// a scalar operand multiplies element-wise.
    /// </summary>
    public static dynamic dot(ndarray a, ndarray b)
    {
        if (a.ndim == 0 || b.ndim == 0) return a * b;
        if (a.ndim == 1 && b.ndim == 1)
        {
            if (a._data.Length != b._data.Length)
                throw Err.Value($"shapes {Err.ShapeStr(a._shape)} and {Err.ShapeStr(b._shape)} not aligned: {a._data.Length} (dim 0) != {b._data.Length} (dim 0)");
            return TensorPrimitives.Dot<double>(a._data, b._data);
        }
        if (a.ndim <= 2 && b.ndim <= 2) return matmul(a, b);
        throw new NotSupportedException("np.dot for arrays with more than 2 dimensions is not supported; use np.matmul");
    }

    public static double dot(double a, double b) => a * b;

    /// <summary>Flattened inner product.</summary>
    public static double vdot(ndarray a, ndarray b)
    {
        if (a._data.Length != b._data.Length) throw Err.Value("cannot reshape array: vdot requires equal sizes");
        return TensorPrimitives.Dot<double>(a._data, b._data);
    }

    public static dynamic inner(ndarray a, ndarray b)
    {
        if (a.ndim == 1 && b.ndim == 1) return dot(a, b);
        if (b.ndim == 2 && a.ndim <= 2) return matmul(a, b.T);
        return dot(a, b);
    }

    public static ndarray outer(ndarray a, ndarray b)
    {
        int n = a._data.Length, m = b._data.Length;
        var r = Kernels.Alloc(n * m);
        for (int i = 0; i < n; i++) TensorPrimitives.Multiply(b._data, a._data[i], r.AsSpan(i * m, m));
        return new ndarray(r, new[] { n, m });
    }

    /// <summary>
    /// Matrix product (a @ b). 1-D @ 1-D gives a 0-d array; a 1-D operand is a row (left) or column (right) vector
    /// whose axis is dropped from the result; leading (batch) dimensions broadcast.
    /// </summary>
    public static ndarray matmul(ndarray a, ndarray b)
    {
        if (a.ndim == 0 || b.ndim == 0)
            throw Err.Value("matmul: Input operand does not have enough dimensions (scalar operands are not allowed, use '*' instead)");
        if (a.ndim == 1 && b.ndim == 1)
        {
            if (a._data.Length != b._data.Length)
                throw Err.Value($"matmul: Input operand 1 has a mismatch in its core dimension 0, with gufunc signature (n?,k),(k,m?)->(n?,m?) (size {b._data.Length} is different from {a._data.Length})");
            return ndarray.Scalar0d(TensorPrimitives.Dot<double>(a._data, b._data));
        }

        int n = a.ndim == 1 ? 1 : a._shape[^2];
        int k = a._shape[^1];
        int kb = b.ndim == 1 ? b._shape[0] : b._shape[^2];
        int m = b.ndim == 1 ? 1 : b._shape[^1];
        if (k != kb)
            throw Err.Value($"matmul: Input operand 1 has a mismatch in its core dimension 0, with gufunc signature (n?,k),(k,m?)->(n?,m?) (size {kb} is different from {k})");

        if (a.ndim <= 2 && b.ndim <= 2)
        {
            int[] shape2 = a.ndim == 1 ? new[] { m } : b.ndim == 1 ? new[] { n } : new[] { n, m };
            return new ndarray(MatMulKernels.MatMul(a._data, b._data, n, k, m), shape2);
        }

        // Batched: the leading (batch) dimensions broadcast against each other; a 1-D operand has none.
        int[] batchA = a.ndim <= 2 ? Array.Empty<int>() : a._shape[..^2];
        int[] batchB = b.ndim <= 2 ? Array.Empty<int>() : b._shape[..^2];
        int[] batch = Kernels.BroadcastShapes(batchA, batchB);
        var shape = new List<int>(batch);
        if (a.ndim > 1) shape.Add(n);
        if (b.ndim > 1) shape.Add(m);
        int count = Kernels.Prod(batch);
        long cells = (long)count * n * m;
        if (cells > Array.MaxLength) throw new OutOfMemoryException("array is too big");
        var res = k == 0 ? new double[cells] : Kernels.Alloc((int)cells);
        if (cells == 0) return new ndarray(res, shape.ToArray());

        // One b for all batches and a's batches are contiguous: a single tall product (all rows of a) @ b.
        if (Kernels.Prod(batchB) == 1 && Kernels.SameShape(batchA, batch))
        {
            MatMulKernels.MatMulInto(a._data, 0, b._data, 0, res, 0, count * n, k, m);
            return new ndarray(res, shape.ToArray());
        }

        int[] stA = Kernels.PaddedStrides(batchA, batch), stB = Kernels.PaddedStrides(batchB, batch);
        int sizeA = n * k, sizeB = k * m, sizeC = n * m;
        void One(int t, int threads)
        {
            int oa = 0, ob = 0, rem = t;
            for (int d = batch.Length - 1; d >= 0; d--)
            {
                int i = rem % batch[d];
                rem /= batch[d];
                oa += i * stA[d];
                ob += i * stB[d];
            }
            MatMulKernels.MatMulInto(a._data, oa * sizeA, b._data, ob * sizeB, res, t * sizeC, n, k, m, threads);
        }
        long work = (long)n * k * m;
        if (count > 1 && NumPyRuntime.MaxDegreeOfParallelism > 1 && work < NumPyRuntime.MatMulParallelThreshold * 4L)
            Kernels.ChunkedItems(count, work, (t0, cnt) => { for (int t = t0; t < t0 + cnt; t++) One(t, 1); });
        else
            for (int t = 0; t < count; t++) One(t, 0);
        return new ndarray(res, shape.ToArray());
    }

    // ====================================================================== shape manipulation

    public static ndarray transpose(ndarray a) => a.transpose();
    public static ndarray transpose(ndarray a, int[] axes) => a.transpose(axes);
    public static ndarray swapaxes(ndarray a, int axis1, int axis2) => a.swapaxes(axis1, axis2);
    public static ndarray reshape(ndarray a, NumPyShape newshape) => a.reshape(newshape);
    public static ndarray ravel(ndarray a) => a.ravel();
    public static ndarray squeeze(ndarray a) => a.squeeze();

    public static ndarray expand_dims(ndarray a, int axis)
    {
        int nd = a.ndim + 1;
        int ax = axis < 0 ? axis + nd : axis;
        if ((uint)ax >= (uint)nd) throw Err.Value($"axis {axis} is out of bounds for array of dimension {nd}");
        var s = new List<int>(a._shape);
        s.Insert(ax, 1);
        return new ndarray(a._data, s.ToArray(), a._kind);
    }

    public static ndarray atleast_2d(ndarray a) =>
        a.ndim >= 2 ? a : new ndarray(a._data, a.ndim == 0 ? new[] { 1, 1 } : new[] { 1, a._shape[0] }, a._kind);

    private static List<ndarray> ArraySeq(object arrays)
    {
        var list = new List<ndarray>();
        IList seq = arrays is ndarray nd
            ? (nd.ndim == 0 ? throw Err.Value("zero-dimensional arrays cannot be concatenated") : new List<object>(EnumerateRows(nd)))
            : AsSequence(arrays) ?? throw Err.Type("expected a sequence of arrays");
        foreach (object o in seq) list.Add(asarray(o));
        if (list.Count == 0) throw Err.Value("need at least one array to concatenate");
        return list;
    }

    private static IEnumerable<object> EnumerateRows(ndarray nd)
    {
        foreach (object r in nd) yield return r;
    }

    /// <summary>Join a sequence (list or tuple) of arrays along an existing axis.</summary>
    public static ndarray concatenate(object arrays, int axis = 0)
    {
        var list = ArraySeq(arrays);
        int nd = list[0].ndim;
        if (nd == 0) throw Err.Value("zero-dimensional arrays cannot be concatenated");
        int ax = Kernels.NormAxis(axis, nd);
        var shp = (int[])list[0]._shape.Clone();
        int total = 0;
        foreach (var a in list)
        {
            if (a.ndim != nd) throw Err.Value("all the input array dimensions except for the concatenation axis must match exactly");
            for (int d = 0; d < nd; d++)
                if (d != ax && a._shape[d] != shp[d])
                    throw Err.Value($"all the input array dimensions except for the concatenation axis must match exactly, but along dimension {d}, the array at index 0 has size {shp[d]} and the other array has size {a._shape[d]}");
            total += a._shape[ax];
        }
        shp[ax] = total;
        var (outer, _, inner) = Kernels.SplitAxis(shp, ax);
        var res = Kernels.Alloc(Kernels.Prod(shp));
        int pos = 0;
        for (int o = 0; o < outer; o++)
            foreach (var a in list)
            {
                int block = a._shape[ax] * inner;
                Array.Copy(a._data, o * block, res, pos, block);
                pos += block;
            }
        bool allBool = list.TrueForAll(a => a._kind == NdKind.Bool);
        return new ndarray(res, shp, allBool ? NdKind.Bool : NdKind.Float);
    }

    /// <summary>Join arrays of equal shape along a new axis.</summary>
    public static ndarray stack(object arrays, int axis = 0)
    {
        var list = ArraySeq(arrays);
        var first = list[0]._shape;
        foreach (var a in list) if (!Kernels.SameShape(a._shape, first)) throw Err.Value("all input arrays must have the same shape");
        var expanded = new List<object>();
        foreach (var a in list) expanded.Add(expand_dims(a, axis));
        return concatenate(expanded, axis < 0 ? axis + first.Length + 1 : axis);
    }

    public static ndarray vstack(object tup)
    {
        var list = ArraySeq(tup);
        var two = new List<object>();
        foreach (var a in list) two.Add(atleast_2d(a));
        return concatenate(two, 0);
    }

    public static ndarray hstack(object tup)
    {
        var list = ArraySeq(tup);
        bool all1d = list.TrueForAll(a => a.ndim <= 1);
        var items = new List<object>();
        foreach (var a in list) items.Add(a.ndim == 0 ? a.reshape(1) : a);
        return concatenate(items, all1d ? 0 : 1);
    }

    public static ndarray column_stack(object tup)
    {
        var list = ArraySeq(tup);
        var cols = new List<object>();
        foreach (var a in list) cols.Add(a.ndim == 1 ? a.reshape(-1, 1) : a);
        return concatenate(cols, 1);
    }

    /// <summary>Repeat the whole array (1-D / 2-D tiling).</summary>
    public static ndarray tile(ndarray a, int reps)
    {
        int n = a._data.Length;
        var r = Kernels.Alloc(n * reps);
        if (a.ndim == 0)
        {
            r.AsSpan().Fill(a._data[0]);
            return new ndarray(r, new[] { reps }, a._kind);
        }
        // tile along the last axis
        var shp = (int[])a._shape.Clone();
        int last = a._shape[^1];
        int rows = last == 0 ? 0 : n / last;
        shp[^1] = last * reps;
        for (int row = 0; row < rows; row++)
            for (int i = 0; i < reps; i++)
                Array.Copy(a._data, row * last, r, row * last * reps + i * last, last);
        return new ndarray(r, shp, a._kind);
    }

    /// <summary>Repeat each element (flattened) `repeats` times.</summary>
    public static ndarray repeat(ndarray a, int repeats)
    {
        int n = a._data.Length;
        var r = Kernels.Alloc(n * repeats);
        for (int i = 0; i < n; i++) r.AsSpan(i * repeats, repeats).Fill(a._data[i]);
        return new ndarray(r, new[] { n * repeats }, a._kind);
    }

    /// <summary>n-th discrete difference along the last axis.</summary>
    public static ndarray diff(ndarray a, int n = 1)
    {
        var cur = a;
        for (int t = 0; t < n; t++)
        {
            int last = cur._shape[^1];
            if (last == 0) return cur;
            int rows = cur._data.Length / last;
            var r = Kernels.Alloc(rows * (last - 1));
            for (int row = 0; row < rows; row++)
                TensorPrimitives.Subtract(cur._data.AsSpan(row * last + 1, last - 1), cur._data.AsSpan(row * last, last - 1), r.AsSpan(row * (last - 1), last - 1));
            var shp = (int[])cur._shape.Clone();
            shp[^1] = last - 1;
            cur = new ndarray(r, shp);
        }
        return cur;
    }

    // ====================================================================== sorting / searching

    /// <summary>Sorted copy along the last axis (NaNs last).</summary>
    public static ndarray sort(ndarray a)
    {
        var c = a.copy();
        c.sort();
        return c;
    }

    /// <summary>Indices that sort the array along the last axis.</summary>
    public static ndarray argsort(ndarray a)
    {
        if (a.ndim == 0) return new ndarray(new[] { 0.0 }, Array.Empty<int>());
        int n = a._shape[^1];
        var res = Kernels.Alloc(a._data.Length);
        var keys = new double[n];
        var idx = new int[n];
        for (int o = 0; o + n <= a._data.Length && n > 0; o += n)
        {
            Array.Copy(a._data, o, keys, 0, n);
            for (int i = 0; i < n; i++) idx[i] = i;
            Array.Sort(keys, idx, Comparer<double>.Create(ndarray.NaNLastComparison));
            for (int i = 0; i < n; i++) res[o + i] = idx[i];
        }
        return new ndarray(res, (int[])a._shape.Clone());
    }

    /// <summary>Sorted unique values of the flattened array.</summary>
    public static ndarray unique(ndarray a)
    {
        var s = SortedCopy(a._data);
        var list = new List<double>(s.Length);
        for (int i = 0; i < s.Length; i++)
            if (i == 0 || !(s[i] == s[i - 1] || (double.IsNaN(s[i]) && double.IsNaN(s[i - 1])))) list.Add(s[i]);
        return new ndarray(list.ToArray(), new[] { list.Count }, a._kind);
    }

    /// <summary>Indices where elements should be inserted to keep a sorted 1-D array sorted (side='left').</summary>
    public static int searchsorted(ndarray a, double v)
    {
        int lo = 0, hi = a._data.Length;
        while (lo < hi)
        {
            int mid = (lo + hi) >> 1;
            if (a._data[mid] < v) lo = mid + 1; else hi = mid;
        }
        return lo;
    }

    // ====================================================================== comparison helpers

    public static ndarray isclose(ndarray a, ndarray b, double rtol = 1e-05, double atol = 1e-08, bool equal_nan = false)
    {
        var rs = Kernels.BroadcastShapes(a._shape, b._shape);
        var x = Kernels.BroadcastTo(a, rs);
        var y = Kernels.BroadcastTo(b, rs);
        var r = new double[x.Length];
        for (int i = 0; i < r.Length; i++) r[i] = IsClose(x[i], y[i], rtol, atol, equal_nan) ? 1.0 : 0.0;
        return new ndarray(r, rs, NdKind.Bool);
    }

    public static bool isclose(double a, double b, double rtol = 1e-05, double atol = 1e-08, bool equal_nan = false) =>
        IsClose(a, b, rtol, atol, equal_nan);

    private static bool IsClose(double x, double y, double rtol, double atol, bool equalNan)
    {
        if (double.IsNaN(x) || double.IsNaN(y)) return equalNan && double.IsNaN(x) && double.IsNaN(y);
        if (x == y) return true; // handles infinities
        if (double.IsInfinity(x) || double.IsInfinity(y)) return false;
        return Math.Abs(x - y) <= atol + rtol * Math.Abs(y);
    }

    /// <summary>True when all elements are close: |a - b| &lt;= atol + rtol * |b|.</summary>
    public static bool allclose(ndarray a, ndarray b, double rtol = 1e-05, double atol = 1e-08, bool equal_nan = false)
    {
        var rs = Kernels.BroadcastShapes(a._shape, b._shape);
        var x = Kernels.BroadcastTo(a, rs);
        var y = Kernels.BroadcastTo(b, rs);
        for (int i = 0; i < x.Length; i++) if (!IsClose(x[i], y[i], rtol, atol, equal_nan)) return false;
        return true;
    }

    public static bool array_equal(ndarray a1, ndarray a2)
    {
        if (!Kernels.SameShape(a1._shape, a2._shape)) return false;
        for (int i = 0; i < a1._data.Length; i++) if (a1._data[i] != a2._data[i]) return false;
        return true;
    }

    // ====================================================================== printing

    /// <summary>numpy-style str() of an array.</summary>
    public static string array2string(ndarray a) => ArrayPrinter.Str(a);
    public static string array_str(ndarray a) => ArrayPrinter.Str(a);
    public static string array_repr(ndarray a) => ArrayPrinter.Repr(a);
}
