// Copyright (c) 2026 The Typhon contributors.
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

using Typhon.Stdlib;
using Xunit;

namespace Typhon.NumPy.Tests;

/// <summary>numpy.matmul / @ semantics: 1-D operands, batches, broadcasting, transposed (copied) inputs.</summary>
public sealed class MatMulApiTests
{
    [Fact]
    public void VectorOperands()
    {
        int k = 37, n = 19;
        var v = Ref.Random(k, 1);
        var b = Ref.Random(k * n, 2);
        var bt = Ref.Transpose(b, k, n); // n x k

        var left = numpy.matmul(Ref.Array(v, k), Ref.Array(b, k, n));
        Assert.Equal(new[] { n }, left.shape);
        Ref.Close(Ref.MatMul(v, b, 1, k, n), Ref.Data(left), 1e-12, 6);

        var right = numpy.matmul(Ref.Array(bt, n, k), Ref.Array(v, k));
        Assert.Equal(new[] { n }, right.shape);
        Ref.Close(Ref.MatMul(bt, v, n, k, 1), Ref.Data(right), 1e-12, 6);

        var dot = numpy.matmul(Ref.Array(v, k), Ref.Array(v, k));
        Assert.Equal(0, dot.ndim);
    }

    [Fact]
    public void TransposedInputs()
    {
        int m = 23, k = 129, n = 17;
        var a = Ref.Random(k * m, 3); // stored k x m, used transposed
        var b = Ref.Random(n * k, 4); // stored n x k, used transposed
        var r = numpy.matmul(Ref.Array(a, k, m).T, Ref.Array(b, n, k).T);
        Assert.Equal(new[] { m, n }, r.shape);
        Ref.Close(Ref.MatMul(Ref.Transpose(a, k, m), Ref.Transpose(b, n, k), m, k, n), Ref.Data(r), 1e-12, 12);
    }

    [Fact]
    public void BatchedSameShape()
    {
        int batch = 5, m = 9, k = 17, n = 3;
        var a = Ref.Random(batch * m * k, 5);
        var b = Ref.Random(batch * k * n, 6);
        var r = numpy.matmul(Ref.Array(a, batch, m, k), Ref.Array(b, batch, k, n));
        Assert.Equal(new[] { batch, m, n }, r.shape);
        var data = Ref.Data(r);
        for (int t = 0; t < batch; t++)
        {
            var want = Ref.MatMul(a[(t * m * k)..((t + 1) * m * k)], b[(t * k * n)..((t + 1) * k * n)], m, k, n);
            Ref.Close(want, data[(t * m * n)..((t + 1) * m * n)], 1e-12, 5);
        }
    }

    [Fact]
    public void BatchedTimesMatrixAndVector()
    {
        int b1 = 2, b2 = 3, m = 4, k = 5, n = 6;
        var a = Ref.Random(b1 * b2 * m * k, 7);
        var w = Ref.Random(k * n, 8);
        var r = numpy.matmul(Ref.Array(a, b1, b2, m, k), Ref.Array(w, k, n));
        Assert.Equal(new[] { b1, b2, m, n }, r.shape);
        Ref.Close(Ref.MatMul(a, w, b1 * b2 * m, k, n), Ref.Data(r), 1e-12, 3);

        var v = Ref.Random(k, 9);
        var rv = numpy.matmul(Ref.Array(a, b1, b2, m, k), Ref.Array(v, k));
        Assert.Equal(new[] { b1, b2, m }, rv.shape);
        Ref.Close(Ref.MatMul(a, v, b1 * b2 * m, k, 1), Ref.Data(rv), 1e-12, 3);

        // 1-D @ batched: the vector is a row of every batch
        var bb = Ref.Random(b1 * k * n, 10);
        var vr = numpy.matmul(Ref.Array(v, k), Ref.Array(bb, b1, k, n));
        Assert.Equal(new[] { b1, n }, vr.shape);
        var data = Ref.Data(vr);
        for (int t = 0; t < b1; t++)
            Ref.Close(Ref.MatMul(v, bb[(t * k * n)..((t + 1) * k * n)], 1, k, n), data[(t * n)..((t + 1) * n)], 1e-12, 3);
    }

    [Fact]
    public void BroadcastBatchDimensions()
    {
        // (2, 1, m, k) @ (3, k, n) -> (2, 3, m, n)
        int m = 7, k = 11, n = 5;
        var a = Ref.Random(2 * m * k, 11);
        var b = Ref.Random(3 * k * n, 12);
        var r = numpy.matmul(Ref.Array(a, 2, 1, m, k), Ref.Array(b, 3, k, n));
        Assert.Equal(new[] { 2, 3, m, n }, r.shape);
        var data = Ref.Data(r);
        for (int i = 0; i < 2; i++)
            for (int j = 0; j < 3; j++)
            {
                var want = Ref.MatMul(a[(i * m * k)..((i + 1) * m * k)], b[(j * k * n)..((j + 1) * k * n)], m, k, n);
                int o = (i * 3 + j) * m * n;
                Ref.Close(want, data[o..(o + m * n)], 1e-12, 4);
            }

        // matrix @ batch: (m, k) @ (3, k, n)
        var w = Ref.Random(m * k, 13);
        var r2 = numpy.matmul(Ref.Array(w, m, k), Ref.Array(b, 3, k, n));
        Assert.Equal(new[] { 3, m, n }, r2.shape);
        var d2 = Ref.Data(r2);
        for (int j = 0; j < 3; j++)
            Ref.Close(Ref.MatMul(w, b[(j * k * n)..((j + 1) * k * n)], m, k, n), d2[(j * m * n)..((j + 1) * m * n)], 1e-12, 4);
    }

    [Fact]
    public void MismatchedShapesRaiseValueError()
    {
        Assert.Throws<ArgumentException>(() => numpy.matmul(Ref.Array(new double[6], 2, 3), Ref.Array(new double[8], 2, 4)));
        Assert.Throws<ArgumentException>(() => numpy.matmul(Ref.Array(new double[12], 2, 2, 3), Ref.Array(new double[27], 3, 3, 3)));
    }
}
