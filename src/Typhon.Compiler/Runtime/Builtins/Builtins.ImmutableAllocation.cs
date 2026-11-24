// Copyright (c) 2026 The Typhon contributors.
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

#nullable enable

namespace Typhon.Runtime
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Numerics;

    internal static partial class Builtins
    {
        static readonly object OmittedAllocationArgument = new();

        static string AllocationArgumentType(object? value) => value == null ? "NoneType" : PyTypeName(value.GetType());

        static Type AllocationClass(Type owner, object?[] args)
        {
            if (args.Length != 0 && args[0] is Type subtype && owner.IsAssignableFrom(subtype)) return subtype;
            string name = PyTypeName(owner);
            if (args.Length == 0) throw new InvalidCastException($"{name}.__new__(): not enough arguments");
            if (args[0] is not Type cls)
                throw new InvalidCastException($"{name}.__new__(X): X is not a type object ({AllocationArgumentType(args[0])})");
            string requested = PyTypeName(cls);
            throw new InvalidCastException($"{name}.__new__({requested}): {requested} is not a subtype of {name}");
        }

        public static bool __bool_new([PyPackedArguments] object?[] args, [PyKeywordArguments] IDictionary<string, object?> keywords)
        {
            AllocationClass(typeof(bool), args);
            return ConstructBool(args.AsSpan(1), keywords);
        }

        public static double __float_new([PyPackedArguments] object?[] args, [PyKeywordArguments] IDictionary<string, object?> keywords)
        {
            AllocationClass(typeof(double), args);
            return ConstructFloat(args.AsSpan(1), keywords);
        }

        public static Complex __complex_new([PyPackedArguments] object?[] args, [PyKeywordArguments] IDictionary<string, object?> keywords)
        {
            AllocationClass(typeof(Complex), args);
            return ConstructComplex(args[1..], keywords);
        }

        public static PyBytes __bytes_new([PyPackedArguments] object?[] args, [PyKeywordArguments] IDictionary<string, object?> keywords)
        {
            Type cls = AllocationClass(typeof(PyBytes), args);
            return PyBytes.Allocate(cls, ConstructBytes(args[1..], keywords));
        }

        static void CheckUnaryAllocation(string name, int count, IDictionary<string, object?>? keywords)
        {
            if (keywords?.Count > 0) throw new InvalidCastException($"{name}() takes no keyword arguments");
            if (count > 1) throw new InvalidCastException($"{name}() takes at most 1 argument ({count} given)");
        }

        static bool ConstructBool(ReadOnlySpan<object?> args, IDictionary<string, object?>? keywords)
        {
            CheckUnaryAllocation("bool", args.Length, keywords);
            return args.Length != 0 && @bool(args[0]);
        }

        static string? AllocationString(object? value) => value switch
        {
            string text => text,
            PyString text => text.Text,
            PyStrEnum member => member.value,
            _ => null,
        };

        static double? ConvertRealNumber(object? value)
        {
            if (value is double or float or decimal) return RealToDouble(value);
            if (value != null && TryGetAttr(value, "__float__", out var convert))
                return (object?)__invoke(convert, []) is double result ? result :
                    throw new InvalidCastException("__float__ returned a non-float");
            return IndexValueOrNull(IntegerValue(value)) is object integer ? RealToDouble(integer) : null;
        }

        static double ConstructFloat(ReadOnlySpan<object?> args, IDictionary<string, object?>? keywords)
        {
            CheckUnaryAllocation("float", args.Length, keywords);
            if (args.Length == 0) return 0.0;
            object? source = args[0];
            if (AllocationString(source) is string text)
            {
                string literal = NormalizeDecimalLiteral(text.Trim());
                if (!ComplexText.FloatLiteral.IsMatch(literal))
                    throw new ArgumentException($"could not convert string to float: {repr(text)}");
                return @float(literal);
            }
            if (source is byte[] managedBytes) return @float(bytes(managedBytes));
            if (source is PyBytes or PyByteArray or global::memoryview) return @float(source);
            return ConvertRealNumber(source) ??
                throw new InvalidCastException($"float() argument must be a string or a real number, not '{AllocationArgumentType(source)}'");
        }

        static Complex ConstructComplex(object?[] args, IDictionary<string, object?>? keywords)
        {
            var values = PyClosure.BindArguments("complex", ["real", "imag"],
                [OmittedAllocationArgument, OmittedAllocationArgument], 2, 0,
                args, keywords, varargs: false, kwargs: false, out _);
            object? real = values[0], imag = values[1];
            bool hasReal = !ReferenceEquals(real, OmittedAllocationArgument);
            bool hasImag = !ReferenceEquals(imag, OmittedAllocationArgument);
            bool singlePositional = args.Length == 1 && !hasImag;
            if (singlePositional && AllocationString(real) is string text) return complex(text);

            Complex realValue = Complex.Zero;
            bool complexReal = false;
            if (hasReal)
            {
                if (real is Complex number)
                {
                    realValue = number;
                    complexReal = true;
                }
                else if (real != null && TryGetAttr(real, "__complex__", out var convert))
                {
                    realValue = (object?)__invoke(convert, []) is Complex converted ? converted :
                        throw new InvalidCastException("__complex__ returned a non-complex");
                    complexReal = true;
                }
                else if (ConvertRealNumber(real) is double numericReal) realValue = complex(numericReal);
                else
                    throw new InvalidCastException(singlePositional
                        ? $"complex() argument must be a string or a number, not {AllocationArgumentType(real)}"
                        : $"complex() argument 'real' must be a real number, not {AllocationArgumentType(real)}");
            }
            if (complexReal && !singlePositional) WarnComplexOption("real", real);
            if (!hasImag) return realValue;
            if (imag is Complex imaginary)
            {
                WarnComplexOption("imag", imag);
                return complex(realValue.Real - imaginary.Imaginary, complexReal ? realValue.Imaginary + imaginary.Real : imaginary.Real);
            }
            if (ConvertRealNumber(imag) is not double numericImag)
                throw new InvalidCastException($"complex() argument 'imag' must be a real number, not {AllocationArgumentType(imag)}");
            return complex(realValue.Real, complexReal ? realValue.Imaginary + numericImag : numericImag);
        }

        static void WarnComplexOption(string parameter, object? value) => Typhon.Stdlib.warnings.Warn(
            $"complex() argument '{parameter}' must be a real number, not {AllocationArgumentType(value)}",
            typeof(DeprecationWarning), 1);

        static PyBytes ConstructBytes(object?[] args, IDictionary<string, object?>? keywords)
        {
            var values = PyClosure.BindArguments("bytes", ["source", "encoding", "errors"],
                [OmittedAllocationArgument, OmittedAllocationArgument, OmittedAllocationArgument], 3, 0,
                args, keywords, varargs: false, kwargs: false, out _);
            string? Option(int index, string name)
            {
                if (ReferenceEquals(values[index], OmittedAllocationArgument)) return null;
                if (AllocationString(values[index]) is string text) return text;
                string type = values[index] == null ? "None" : AllocationArgumentType(values[index]);
                throw new InvalidCastException($"bytes() argument '{name}' must be str, not {type}");
            }

            object? source = values[0];
            string? encoding = Option(1, "encoding"), errors = Option(2, "errors");
            if (AllocationString(source) is string literal)
                return encoding != null ? bytes(literal, encoding, errors ?? "strict") :
                    throw new InvalidCastException("string argument without an encoding");
            if (encoding != null) throw new InvalidCastException("encoding without a string argument");
            if (errors != null) throw new InvalidCastException("errors without a string argument");
            if (ReferenceEquals(source, OmittedAllocationArgument)) return bytes();
            if (source != null && TryGetAttr(source, "__bytes__", out var convert))
                return (object?)__invoke(convert, []) is PyBytes converted ? converted :
                    throw new InvalidCastException("__bytes__ returned a non-bytes");
            if (source is PyBytes existing)
                return existing.GetType() == typeof(PyBytes) ? existing : new PyBytes(existing.Data);
            if (IndexValueOrNull(IntegerValue(source)) is object integer)
                return bytes(checked((long)__bigint(integer)));
            if (source == null) throw new InvalidCastException("cannot convert 'NoneType' object to bytes");
            if (source is PyByteArray or byte[] or global::memoryview or Typhon.Stdlib._pickle.PickleBuffer)
                return bytes(source);
            return new PyBytes(__iter_dyn(source).Select(item => ByteOps.Byte((object)__index_value(item))).ToArray());
        }
    }
}
