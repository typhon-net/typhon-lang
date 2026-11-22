// Copyright (c) 2026 The Typhon contributors.
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

#nullable enable
#pragma warning disable

namespace Typhon.Runtime
{
    using System;

    internal enum PyCallableKind { Ordinary, Generator, Coroutine, AsyncGenerator }

    /// <summary>The original location of a compiled module or function, available without a debugger.</summary>
    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Method | AttributeTargets.Property, Inherited = false)]
    internal sealed class PySourceAttribute : Attribute
    {
        internal string Filename { get; }
        internal string Module { get; }
        internal PyCallableKind Kind { get; }
        internal string? DocString { get; }
        internal int Line { get; }
        internal string? PythonName { get; }

        public PySourceAttribute(string filename, string module, PyCallableKind kind = PyCallableKind.Ordinary,
            string? doc = null, int line = 0) : this(filename, module, kind, doc, line, null) { }

        public PySourceAttribute(string filename, string module, PyCallableKind kind, string? doc, int line,
            string? pythonName)
        {
            Filename = filename;
            Module = module;
            Kind = kind;
            DocString = doc;
            Line = line;
            PythonName = pythonName;
        }
    }
}

namespace Typhon.Stdlib
{
    using System;
    using System.Collections;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using System.Reflection;
    using System.Runtime.CompilerServices;
    using System.Threading.Tasks;
    using Typhon.Runtime;

    internal static partial class inspect
    {
        static readonly object Missing = new();
        static readonly ConditionalWeakTable<object, object> CoroutineMarkers = new();
        static readonly object Marker = new();
        const BindingFlags PublicMembers = BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static;
        const BindingFlags DeclaredMembers = PublicMembers | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;

        public static bool ismodule(object? @object) => @object is PyModule;
        public static bool isclass(object? @object) => @object is Type;
        public static bool isframe(object? @object) => @object is sys_.frame;
        public static bool iscode(object? @object) => @object is sys_.code;
        public static bool istraceback(object? @object) => @object is types.TracebackType;
        public static bool isabstract(object? @object) => @object is Type { IsAbstract: true, IsSealed: false };
        public static bool isfunction(object? @object) => @object switch
        {
            PyFunction => true,
            PyBoundMethod { Target: Type } method => DefinedInPython(MethodOf(method)?.DeclaringType),
            Delegate function => DefinedInPython(function.Method.DeclaringType),
            _ => false,
        };

        public static bool ismethod(object? @object) => @object is types.MethodType ||
            @object is PyBoundMethod method && method.Target is not Type && DefinedInPython(MethodOf(method)?.DeclaringType);

        public static bool ismethoddescriptor(object? @object)
        {
            if (@object is MethodInfo { IsStatic: false } or PyBoundMethod { Target: Type })
                return MethodOf(@object) is { IsStatic: false } method && !DefinedInPython(method.DeclaringType);
            if (@object == null || isclass(@object) || isfunction(@object) || ismethod(@object)) return false;
            var type = @object.GetType();
            return HasDescriptorMethod(type, "__get__") && !HasDescriptorMethod(type, "__set__") && !HasDescriptorMethod(type, "__delete__");
        }

        static bool HasDescriptorMethod(Type type, string name) =>
            type.GetMethods(BindingFlags.Public | BindingFlags.Instance).Any(method => method.Name == name);

        public static bool ismethodwrapper(object? @object) =>
            @object is PyBoundMethod method && method.Target is not Type && IsDunder(method.Name) &&
            MethodOf(method) is { } member && !DefinedInPython(member.DeclaringType);

        public static bool isbuiltin(object? @object) => @object switch
        {
            PyBoundMethod method => MethodOf(method) is { } member && !DefinedInPython(member.DeclaringType) &&
                !ismethoddescriptor(method) && !ismethodwrapper(method),
            Delegate function => !DefinedInPython(function.Method.DeclaringType),
            MethodInfo method => method.IsStatic && !DefinedInPython(method.DeclaringType),
            _ => false,
        };

        public static bool isroutine(object? @object) =>
            isfunction(@object) || ismethod(@object) || isbuiltin(@object) || ismethoddescriptor(@object) || ismethodwrapper(@object);

        public static bool isdatadescriptor(object? @object) =>
            @object is PropertyInfo || @object is FieldInfo { IsStatic: false } || @object != null && !isclass(@object) &&
            (HasDescriptorMethod(@object.GetType(), "__set__") || HasDescriptorMethod(@object.GetType(), "__delete__"));

        public static bool isgetsetdescriptor(object? @object) =>
            @object is PropertyInfo property && !DefinedInPython(property.DeclaringType);
        public static bool ismemberdescriptor(object? @object) =>
            @object is FieldInfo { IsStatic: false } field && !DefinedInPython(field.DeclaringType);

        internal static bool IsGeneratorType(Type type) => typeof(IPyGenerator).IsAssignableFrom(type) ||
            typeof(IEnumerator).IsAssignableFrom(type) && type.IsDefined(typeof(CompilerGeneratedAttribute)) && DefinedInPython(type);

        public static bool isgenerator(object? @object) => @object != null && IsGeneratorType(@object.GetType());
        public static bool iscoroutine(object? @object) => @object is Task;
        internal static bool IsAsyncGeneratorType(Type type) => typeof(IPyAsyncGenerator).IsAssignableFrom(type) ||
            type.IsDefined(typeof(CompilerGeneratedAttribute)) && DefinedInPython(type) &&
            type.GetInterfaces().Any(t => t.IsGenericType && t.GetGenericTypeDefinition() == typeof(IAsyncEnumerator<>));

        public static bool isasyncgen(object? @object) => @object != null && IsAsyncGeneratorType(@object.GetType());

        public static bool isawaitable(object? @object) =>
            @object?.GetType().GetMethod("GetAwaiter", Type.EmptyTypes) != null ||
            @object?.GetType().GetMethod("__await__", Type.EmptyTypes) != null;

        public static bool isgeneratorfunction(object? @object) =>
            CallableKind(@object) == PyCallableKind.Generator;
        public static bool isasyncgenfunction(object? @object) =>
            CallableKind(@object) == PyCallableKind.AsyncGenerator;
        public static bool iscoroutinefunction(object? @object) =>
            CallableKind(@object) == PyCallableKind.Coroutine ||
            @object != null && CoroutineMarkers.TryGetValue(MethodOf(@object) ?? @object, out _);

        public static T markcoroutinefunction<T>(T func) where T : class
        {
            CoroutineMarkers.GetValue((object?)MethodOf(func) ?? func, _ => Marker);
            return func;
        }

        static PyCallableKind CallableKind(object? value)
        {
            if (value is PyFunction f && f.GetType().GetCustomAttribute<PySourceAttribute>() is { } source)
                return source.Kind;
            var method = MethodOf(value);
            if (method == null) return PyCallableKind.Ordinary;
            if (method.IsDefined(typeof(AsyncIteratorStateMachineAttribute))) return PyCallableKind.AsyncGenerator;
            if (method.IsDefined(typeof(IteratorStateMachineAttribute))) return PyCallableKind.Generator;
            if (method.IsDefined(typeof(AsyncStateMachineAttribute))) return PyCallableKind.Coroutine;
            return PyCallableKind.Ordinary;
        }

        static bool IsDunder(string name) => name.Length > 4 &&
            name.StartsWith("__", StringComparison.Ordinal) && name.EndsWith("__", StringComparison.Ordinal);

        static MethodInfo? MethodOf(object? value) => value switch
        {
            MethodInfo method => method,
            Delegate function => function.Method,
            PyBoundMethod method => (method.Target as Type ?? method.Target.GetType())
                .GetMethods(PublicMembers | BindingFlags.NonPublic)
                .FirstOrDefault(member => member.Name == method.Name &&
                    (method.Target is Type || !member.IsStatic)),
            _ => null,
        };

        static Type? SourceType(object? value) => value switch
        {
            PyModule module => module.SourceType,
            Type type => type,
            PyFunction function => function.GetType(),
            _ => MethodOf(value)?.DeclaringType,
        };

        static PySourceAttribute? Source(Type? type)
        {
            for (; type != null; type = type.DeclaringType)
                if (type.GetCustomAttribute<PySourceAttribute>() is { } source) return source;
            return null;
        }

        static bool DefinedInPython(Type? type) => Source(type) != null;

        static Type? ModuleType(Type? type)
        {
            while (type?.DeclaringType != null) type = type.DeclaringType;
            return type;
        }

        static object? ModuleByName(string name, Type? type = null)
        {
            if (sys_.modules.TryGetValue(name, out var value) && value != null &&
                !(name == "__main__" && type != null && value is PyModule { SourceType: null })) return value;
            if (type == null) return null;
            var module = PyModule.Of(type, name);
            sys_.modules[name] = module;
            return module;
        }

        public static dynamic? getmodule(object? @object, string? _filename = null)
        {
            if (@object is PyModule module) return module;
            if (@object is functools.cached_property cached && cached.__module__ is string cachedModule) return ModuleByName(cachedModule);
            if (@object is builtins.MethodDescriptor descriptor) return ModuleByName(descriptor.__module__);
            if (@object is types.TracebackType traceback) @object = traceback.tb_frame;
            if (@object is sys_.frame frame && frame.f_globals["__name__"] is string frameModule)
                return ModuleByName(frameModule);
            Type? type = SourceType(@object);
            var source = Source(type);
            if (@object is PyFunction function)
                return ModuleByName(function.__module__, source?.Module == function.__module__ ? ModuleType(type) : null);
            if (source != null) return ModuleByName(source.Module, ModuleType(type));
            if (ModuleType(type) is { Namespace: "Typhon.Stdlib" } stdlib)
                return ModuleByName(stdlib.Name.TrimEnd('_'), stdlib);
            if (@object is Type || isbuiltin(@object) || ismethoddescriptor(@object) || ismethodwrapper(@object))
                return ModuleByName("builtins");
            string? filename = _filename ?? (@object as sys_.code)?.co_filename;
            if (!string.IsNullOrEmpty(filename))
                foreach (object? entry in sys_.modules.Values)
                    if (entry is PyModule candidate && Source(candidate.SourceType)?.Filename == filename) return candidate;
            return null;
        }

        public static string getfile(object? @object)
        {
            if (@object is types.TracebackType traceback) @object = traceback.tb_frame;
            if (@object is sys_.frame frame) return frame.f_code.co_filename;
            if (@object is sys_.code code) return code.co_filename;
            if (Source(SourceType(@object)) is { } source) return source.Filename;
            if (@object is PyFunction function && getmodule(function) is PyModule module &&
                Source(module.SourceType) is { } moduleSource) return moduleSource.Filename;
            if (@object is Type || @object is PyModule)
                throw new InvalidCastException($"{Builtins.repr(@object)} is built in");
            throw new InvalidCastException($"source location unavailable for {(@object == null ? "NoneType" : Builtins.PyTypeName(@object.GetType()))}");
        }

        public static string? getsourcefile(object? @object)
        {
            string filename = getfile(@object);
            if (filename.EndsWith(".pyc", StringComparison.Ordinal)) filename = filename[..^1];
            if (filename.EndsWith(".so", StringComparison.Ordinal) || filename.EndsWith(".pyd", StringComparison.Ordinal) ||
                filename.EndsWith(".fwork", StringComparison.Ordinal)) return null;
            return File.Exists(filename) || linecache.cache.ContainsKey(filename) ? filename : null;
        }

        public static string getabsfile(object? @object, string? _filename = null)
        {
            string path = System.IO.Path.GetFullPath(_filename ?? getsourcefile(@object) ?? getfile(@object));
            return OperatingSystem.IsWindows() ? path.ToLowerInvariant() : path;
        }

        public static string? getmodulename(object path)
        {
            string filename = System.IO.Path.GetFileName(os_.__path(path));
            foreach (string suffix in importlib.machinery.SOURCE_SUFFIXES.Concat(importlib.machinery.BYTECODE_SUFFIXES))
                if (filename.EndsWith(suffix, StringComparison.Ordinal)) return filename[..^suffix.Length];
            if (filename.EndsWith(".ty", StringComparison.Ordinal)) return filename[..^3];
            if (!OperatingSystem.IsWindows() && filename.EndsWith(".so", StringComparison.Ordinal))
            {
                int suffix = filename.IndexOf(".cpython-", StringComparison.Ordinal);
                if (suffix < 0 && filename.EndsWith(".abi3.so", StringComparison.Ordinal)) suffix = filename.Length - 8;
                return suffix < 0 ? filename[..^3] : filename[..suffix];
            }
            if (OperatingSystem.IsWindows() && filename.EndsWith(".pyd", StringComparison.Ordinal))
            {
                int suffix = filename.IndexOf(".cp", StringComparison.Ordinal);
                return suffix < 0 ? filename[..^4] : filename[..suffix];
            }
            return null;
        }

        public static dynamic? unwrap(object? func, Func<dynamic, bool>? stop = null)
        {
            var visited = new HashSet<object>(ReferenceEqualityComparer.Instance);
            while (func != null && (stop == null || !stop(func)))
            {
                object? wrapped = Builtins.getattr(func, "__wrapped__", Missing);
                if (ReferenceEquals(wrapped, Missing)) break;
                if (!visited.Add(func) || visited.Count >= 1000)
                    throw new ArgumentException($"wrapper loop when unwrapping {Builtins.repr(func)}");
                func = wrapped;
            }
            return func;
        }

        public static string cleandoc(string doc)
        {
            var lines = Builtins.expandtabs(doc).Split('\n');
            lines[0] = lines[0].TrimStart(' ');
            int margin = int.MaxValue;
            for (int i = 1; i < lines.Length; i++)
            {
                int content = lines[i].TrimStart(' ').Length;
                if (content != 0) margin = Math.Min(margin, lines[i].Length - content);
            }
            if (margin != int.MaxValue)
                for (int i = 1; i < lines.Length; i++) lines[i] = lines[i][Math.Min(margin, lines[i].Length)..];
            int start = 0, end = lines.Length;
            while (start < end && lines[start].Length == 0) start++;
            while (start < end && lines[end - 1].Length == 0) end--;
            return string.Join("\n", lines, start, end - start);
        }

        public static string? getdoc(object? @object, bool fallback_to_class_doc = true, bool inherit_class_doc = true)
        {
            object? doc = Builtins.getattr(@object, "__doc__", null);
            if (doc is string text) return cleandoc(text);
            Type? type = @object is PyModule module ? module.SourceType : @object as Type;
            if (type == null && fallback_to_class_doc) type = @object?.GetType();
            for (; type != null; type = inherit_class_doc ? type.BaseType : null)
                if (type.GetCustomAttribute<PySourceAttribute>()?.DocString is string classDoc) return cleandoc(classDoc);
            return null;
        }

        public static List<(string, dynamic?)> getmembers(object @object, Func<dynamic, bool>? predicate = null)
        {
            var result = new List<(string, dynamic?)>();
            foreach (string name in Builtins.dir(@object))
            {
                object? value = Builtins.getattr(@object, name, Missing);
                if (!ReferenceEquals(value, Missing) && (predicate == null || predicate(value!)))
                    result.Add((name, value));
            }
            return result;
        }

        public static dynamic? getattr_static(object? obj, string attr) =>
            StaticAttribute(obj, attr, Missing);

        public static dynamic? getattr_static(object? obj, string attr, object? @default) =>
            StaticAttribute(obj, attr, @default);

        static object? StaticAttribute(object? obj, string attr, object? fallback)
        {
            bool instance = obj is not (Type or PyModule);
            Type? type = obj switch
            {
                Type cls => cls,
                PyModule module => module.SourceType,
                _ => obj?.GetType(),
            };
            if (obj is PyFunction function && function._attrs != null && function._attrs.TryGetValue(attr, out var saved))
                return saved;
            if (instance && obj != null && PyAttrDict.TryGetExtra(obj, attr, out var extra)) return extra;
            for (; type != null; type = type.BaseType)
            {
                if (type.IsDefined(typeof(PySourceAttribute)) && PyClassDescriptors.TryGetDeclaredDescriptor(type, attr, out var descriptor))
                    return descriptor;
                if (type.GetField(attr, DeclaredMembers) is { } field && (field.IsStatic || instance))
                    return field.GetValue(field.IsStatic ? null : obj);
                if (type.GetProperty(attr, DeclaredMembers) is { } property)
                {
                    if (property.GetCustomAttribute<PyClassSlotAttribute>() is { } slot)
                    {
                        if (PyClassDescriptors.TryReadSlot(property, slot, out var classValue)) return classValue;
                        continue;
                    }
                    var backing = type.GetField("<" + attr + ">k__BackingField", DeclaredMembers);
                    if (backing != null && (backing.IsStatic || instance)) return backing.GetValue(backing.IsStatic ? null : obj);
                    return property;
                }
                if (!instance && type.GetNestedType(attr, BindingFlags.Public | BindingFlags.NonPublic) is { } nested)
                    return nested;
                if (type.GetMethods(DeclaredMembers).Any(method => method.Name == attr))
                    return new PyBoundMethod(type, attr, includeNonPublic: true);
            }
            if (!ReferenceEquals(fallback, Missing)) return fallback;
            throw new MissingMemberException(attr);
        }

        /// <summary>Bind a descriptor found by static lookup using the instance's runtime class.</summary>
        public static dynamic? _descriptor_get(object? descriptor, object? instance)
        {
            if (descriptor is null or Type) return descriptor;
            if (descriptor is PyFunction function)
                return instance == null ? function : new types.MethodType(function, instance);
            if (descriptor is PyBoundMethod method && method.Target is Type declaration && instance != null &&
                declaration.IsInstanceOfType(instance))
            {
                // A CLR member returned by static lookup carries its declaring type until bound.
                var flags = PublicMembers | (method.IncludesNonPublic ? BindingFlags.NonPublic : 0);
                var candidates = declaration.GetMethods(flags).Where(member => member.Name == method.Name).ToArray();
                if (candidates.Length != 0 && candidates.All(member => !member.IsStatic))
                    return new PyBoundMethod(instance, method.Name, includeNonPublic: method.IncludesNonPublic);
            }

            object? getter = Builtins.getattr(descriptor.GetType(), "__get__", Missing);
            return ReferenceEquals(getter, Missing) ? descriptor :
                Builtins.__invoke(getter, [descriptor, instance, Builtins.__type_of(instance)]);
        }

        public static sys_.frame currentframe() => sys_._getframe();
    }
}
