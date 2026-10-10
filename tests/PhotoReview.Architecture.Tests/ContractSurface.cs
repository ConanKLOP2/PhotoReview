using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;

namespace PhotoReview.Architecture.Tests;

/// <summary>
/// L-CONTRACT (NO-WPF-EXEC-PLAN mục 3.3, 5): dựng "chữ ký" văn bản của các kiểu hợp đồng bằng reflection trên assembly đã
/// build - kiểu, assembly, base/interface, mọi thành viên public/protected/internal khai báo trực tiếp (kể cả nullability,
/// tên phần tử tuple, giá trị mặc định, in/out/ref, static abstract, init). Thành viên do trình biên dịch sinh
/// ([CompilerGenerated], ví dụ Equals/Deconstruct của record) và private bị bỏ qua.
/// </summary>
internal static class ContractSurface
{
    private const BindingFlags Declared =
        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

    private static readonly Dictionary<Type, string> Aliases = new()
    {
        [typeof(void)] = "void", [typeof(bool)] = "bool", [typeof(byte)] = "byte", [typeof(sbyte)] = "sbyte",
        [typeof(short)] = "short", [typeof(ushort)] = "ushort", [typeof(int)] = "int", [typeof(uint)] = "uint",
        [typeof(long)] = "long", [typeof(ulong)] = "ulong", [typeof(float)] = "float", [typeof(double)] = "double",
        [typeof(decimal)] = "decimal", [typeof(char)] = "char", [typeof(string)] = "string", [typeof(object)] = "object",
        [typeof(nint)] = "nint", [typeof(nuint)] = "nuint",
    };

    /// <summary>Toàn bộ bề mặt: một khối mỗi kiểu, theo thứ tự hợp đồng rồi tên kiểu; dòng kết thúc bằng '\n'.</summary>
    public static string Render(IEnumerable<(string Id, Type[] Types)> contracts, string header)
    {
        var sb = new StringBuilder();
        foreach (var line in header.Split('\n')) sb.Append("# ").Append(line.TrimEnd('\r')).Append('\n');
        foreach (var (id, types) in contracts)
        {
            foreach (var type in types.OrderBy(t => t.FullName, StringComparer.Ordinal))
            {
                sb.Append('\n');
                foreach (var line in RenderType(id, type)) sb.Append(line).Append('\n');
            }
        }
        return sb.ToString();
    }

    public static IEnumerable<string> RenderType(string contractId, Type type)
    {
        yield return $"[{contractId}] {TypeHeader(type)}";
        foreach (var line in Members(type)) yield return "  " + line;
    }

    private static string TypeHeader(Type type)
    {
        var parts = new List<string> { Visibility(type) };
        string kind;
        if (type.IsEnum)
        {
            kind = (type.IsDefined(typeof(FlagsAttribute)) ? "[Flags] " : string.Empty) + "enum";
        }
        else if (type.IsInterface)
        {
            kind = "interface";
        }
        else if (type.IsValueType)
        {
            kind = (type.IsDefined(typeof(IsReadOnlyAttribute)) ? "readonly " : string.Empty) + (IsRecord(type) ? "record struct" : "struct");
        }
        else if (type.IsAbstract && type.IsSealed)
        {
            kind = "static class";
        }
        else
        {
            kind = (type.IsSealed ? "sealed " : type.IsAbstract ? "abstract " : string.Empty) + (IsRecord(type) ? "record" : "class");
        }
        parts.Add(kind);
        parts.Add(type.FullName!);

        var bases = new List<string>();
        if (type.IsEnum)
        {
            var underlying = Enum.GetUnderlyingType(type);
            if (underlying != typeof(int)) bases.Add(Format(underlying, null, null));
        }
        else
        {
            if (type.BaseType is { } baseType && baseType != typeof(object) && baseType != typeof(ValueType))
                bases.Add(Format(baseType, null, null));
            bases.AddRange(type.GetInterfaces().Select(i => Format(i, null, null)).OrderBy(n => n, StringComparer.Ordinal));
        }

        var header = string.Join(' ', parts);
        if (bases.Count > 0) header += " : " + string.Join(", ", bases);
        return header + $"  (assembly {type.Assembly.GetName().Name})";
    }

    private static List<string> Members(Type type)
    {
        var nullability = new NullabilityInfoContext();
        var lines = new List<string>();

        if (type.IsEnum)
        {
            // Theo giá trị rồi tên: bí danh (Return = Enter = 6) có thứ tự xác định.
            foreach (var field in type.GetFields(BindingFlags.Public | BindingFlags.Static)
                         .Select(f => (f.Name, Value: Convert.ToInt64(f.GetRawConstantValue(), CultureInfo.InvariantCulture)))
                         .OrderBy(f => f.Value).ThenBy(f => f.Name, StringComparer.Ordinal))
            {
                lines.Add($"{field.Name} = {field.Value.ToString(CultureInfo.InvariantCulture)}");
            }
            return lines;
        }

        foreach (var field in type.GetFields(Declared).Where(f => IsSurface(f) && !f.IsSpecialName))
        {
            var prefix = field.IsLiteral ? "const" : (field.IsStatic ? "static " : string.Empty) + (field.IsInitOnly ? "readonly" : "field");
            var text = $"field {Visibility(field)} {prefix} {Format(field.FieldType, nullability.Create(field), TupleNames(field))} {field.Name}";
            if (field.IsLiteral) text += " = " + FormatConstant(field.GetRawConstantValue(), field.FieldType);
            lines.Add(text);
        }

        foreach (var ctor in type.GetConstructors(Declared).Where(IsSurface))
        {
            lines.Add($"ctor {Visibility(ctor)}{(ctor.IsStatic ? " static" : string.Empty)} ({Parameters(ctor, nullability)})");
        }

        foreach (var property in type.GetProperties(Declared))
        {
            var getter = property.GetMethod;
            var setter = property.SetMethod;
            var visibleGetter = getter is not null && IsVisible(getter) ? getter : null;
            var visibleSetter = setter is not null && IsVisible(setter) ? setter : null;
            var any = visibleGetter ?? visibleSetter;
            if (any is null || property.IsDefined(typeof(CompilerGeneratedAttribute))) continue;

            var accessors = new List<string>();
            if (visibleGetter is not null) accessors.Add(AccessorVisibility(any, visibleGetter) + "get;");
            if (visibleSetter is not null)
            {
                var isInit = visibleSetter.ReturnParameter.GetRequiredCustomModifiers().Contains(typeof(IsExternalInit));
                accessors.Add(AccessorVisibility(any, visibleSetter) + (isInit ? "init;" : "set;"));
            }

            var indexParameters = property.GetIndexParameters();
            var name = indexParameters.Length == 0 ? property.Name : $"this[{string.Join(", ", indexParameters.Select(p => FormatParameter(p, nullability)))}]";
            lines.Add($"property {Visibility(any)}{Modifiers(any, type)} {Format(property.PropertyType, nullability.Create(property), TupleNames(property))} {name} {{ {string.Join(' ', accessors)} }}");
        }

        foreach (var evt in type.GetEvents(Declared))
        {
            var add = evt.AddMethod;
            if (add is null || !IsVisible(add) || evt.IsDefined(typeof(CompilerGeneratedAttribute))) continue;
            lines.Add($"event {Visibility(add)}{Modifiers(add, type)} {Format(evt.EventHandlerType!, nullability.Create(evt), null)} {evt.Name}");
        }

        foreach (var method in type.GetMethods(Declared).Where(m => IsSurface(m) && (!m.IsSpecialName || m.Name.StartsWith("op_", StringComparison.Ordinal))))
        {
            var generic = method.IsGenericMethodDefinition ? $"<{string.Join(", ", method.GetGenericArguments().Select(a => a.Name))}>" : string.Empty;
            var returnType = Format(method.ReturnType, nullability.Create(method.ReturnParameter), TupleNames(method.ReturnParameter));
            var extension = method.IsDefined(typeof(ExtensionAttribute)) ? "this " : string.Empty;
            lines.Add($"method {Visibility(method)}{Modifiers(method, type)} {returnType} {method.Name}{generic}({extension}{Parameters(method, nullability)})");
        }

        // Thứ tự theo chữ để file duyệt không phụ thuộc thứ tự metadata.
        lines.Sort(StringComparer.Ordinal);
        return lines;
    }

    private static bool IsRecord(Type type) =>
        type.GetMethod("PrintMembers", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public, [typeof(StringBuilder)]) is { } m
        && m.IsDefined(typeof(CompilerGeneratedAttribute));

    // Accessor của auto-property/event mang [CompilerGenerated] nhưng thuộc bề mặt: chỉ xét mức truy cập.
    private static bool IsVisible(MethodBase method) => !method.IsPrivate && !method.IsFamilyAndAssembly;

    private static bool IsSurface(MemberInfo member) =>
        !member.IsDefined(typeof(CompilerGeneratedAttribute)) && member switch
        {
            MethodBase m => !m.IsPrivate && !m.IsFamilyAndAssembly,
            FieldInfo f => !f.IsPrivate && !f.IsFamilyAndAssembly,
            _ => true,
        };

    private static string Visibility(Type type) =>
        type.IsPublic || type.IsNestedPublic ? "public" : type.IsNestedFamily ? "protected" : type.IsNestedFamORAssem ? "protected internal" : "internal";

    private static string Visibility(MethodBase method) =>
        method.IsPublic ? "public" : method.IsFamilyOrAssembly ? "protected internal" : method.IsFamily ? "protected" : "internal";

    private static string Visibility(FieldInfo field) =>
        field.IsPublic ? "public" : field.IsFamilyOrAssembly ? "protected internal" : field.IsFamily ? "protected" : "internal";

    private static string AccessorVisibility(MethodInfo propertyLevel, MethodInfo accessor) =>
        Visibility(accessor) == Visibility(propertyLevel) ? string.Empty : Visibility(accessor) + " ";

    private static string Modifiers(MethodInfo method, Type declaring)
    {
        if (method.IsStatic) return method.IsAbstract ? " static abstract" : method.IsVirtual ? " static virtual" : " static";
        if (declaring.IsInterface) return method.IsAbstract ? string.Empty : " (default)";
        if (method.IsAbstract) return " abstract";
        if (method.IsVirtual && !method.IsFinal) return method.GetBaseDefinition() != method ? " override" : " virtual";
        return string.Empty;
    }

    private static string Parameters(MethodBase method, NullabilityInfoContext nullability) =>
        string.Join(", ", method.GetParameters().Select(p => FormatParameter(p, nullability)));

    private static string FormatParameter(ParameterInfo parameter, NullabilityInfoContext nullability)
    {
        var type = parameter.ParameterType;
        var modifier = string.Empty;
        if (type.IsByRef)
        {
            modifier = parameter.IsOut ? "out " : parameter.IsIn || parameter.IsDefined(typeof(IsReadOnlyAttribute)) ? "in " : "ref ";
            type = type.GetElementType()!;
        }
        if (parameter.IsDefined(typeof(ParamArrayAttribute))) modifier = "params " + modifier;

        var text = $"{modifier}{Format(type, nullability.Create(parameter), TupleNames(parameter))} {parameter.Name}";
        if (parameter.HasDefaultValue) text += " = " + FormatDefault(parameter.DefaultValue, type);
        return text;
    }

    private static Queue<string?>? TupleNames(ICustomAttributeProvider provider) =>
        provider.GetCustomAttributes(typeof(TupleElementNamesAttribute), false).OfType<TupleElementNamesAttribute>().FirstOrDefault() is { } names
            ? new Queue<string?>(names.TransformNames)
            : null;

    private static string Format(Type type, NullabilityInfo? nullability, Queue<string?>? tupleNames)
    {
        if (type.IsByRef) return Format(type.GetElementType()!, nullability, tupleNames) + "&";
        if (type.IsPointer) return Format(type.GetElementType()!, null, tupleNames) + "*";
        if (type.IsArray) return Format(type.GetElementType()!, nullability?.ElementType, tupleNames) + "[]" + Suffix(nullability);
        if (type.IsGenericParameter) return type.Name + Suffix(nullability);

        if (Nullable.GetUnderlyingType(type) is { } underlying) return Format(underlying, null, tupleNames) + "?";

        if (Aliases.TryGetValue(type, out var alias)) return alias + Suffix(nullability);

        if (type.IsGenericType)
        {
            var arguments = type.GetGenericArguments();
            if (type.FullName?.StartsWith("System.ValueTuple`", StringComparison.Ordinal) == true)
            {
                var names = arguments.Select(_ => tupleNames is { Count: > 0 } ? tupleNames.Dequeue() : null).ToArray();
                var elements = arguments.Select((a, i) =>
                    Format(a, nullability?.GenericTypeArguments.ElementAtOrDefault(i), tupleNames) + (names[i] is { } n ? " " + n : string.Empty));
                return $"({string.Join(", ", elements)})";
            }

            var definition = type.GetGenericTypeDefinition().FullName!;
            var name = definition[..definition.IndexOf('`', StringComparison.Ordinal)];
            var formatted = arguments.Select((a, i) => Format(a, nullability?.GenericTypeArguments.ElementAtOrDefault(i), tupleNames));
            return $"{name}<{string.Join(", ", formatted)}>" + Suffix(nullability);
        }

        return (type.FullName ?? type.Name).Replace('+', '.') + Suffix(nullability);
    }

    private static string Suffix(NullabilityInfo? nullability) =>
        nullability is { ReadState: NullabilityState.Nullable } && !nullability.Type.IsValueType ? "?" : string.Empty;

    private static string FormatDefault(object? value, Type type)
    {
        if (value is null) return type.IsValueType && Nullable.GetUnderlyingType(type) is null ? "default" : "null";
        return FormatConstant(value, type);
    }

    private static string FormatConstant(object? value, Type type) => value switch
    {
        null => "null",
        string s => "\"" + s + "\"",
        bool b => b ? "true" : "false",
        float f => f.ToString("R", CultureInfo.InvariantCulture) + "f",
        double d => d.ToString("R", CultureInfo.InvariantCulture),
        _ when (Nullable.GetUnderlyingType(type) ?? type).IsEnum =>
            (Nullable.GetUnderlyingType(type) ?? type).Name + "." + Enum.ToObject(Nullable.GetUnderlyingType(type) ?? type, value),
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? string.Empty,
    };
}
