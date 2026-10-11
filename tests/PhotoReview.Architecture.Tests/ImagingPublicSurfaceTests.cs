using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace PhotoReview.Architecture.Tests;

public sealed class ImagingPublicSurfaceTests
{
    /// <summary>
    /// WP-06: every assembly of the Imaging family is WPF-free, so no public member of any exported type (in any namespace, not
    /// only Caching/Preload as before) may expose a <c>System.Windows.*</c> type. The WPF types live in PhotoReview.Imaging.Wpf.
    /// </summary>
    public static TheoryData<string> ImagingAssemblies() => new()
    {
        typeof(PhotoReview.Imaging.Decoding.IDecodedImage).Assembly.GetName().Name!,
        typeof(PhotoReview.Imaging.Raw.RawFormat).Assembly.GetName().Name!,
        typeof(PhotoReview.Imaging.LibRaw.LibRawAvailability).Assembly.GetName().Name!,
        typeof(PhotoReview.Imaging.TurboJpeg.TurboJpegDecoder).Assembly.GetName().Name!,
    };

    [Theory(DisplayName = "Rule 3: Public types of Imaging, Imaging.Raw, Imaging.LibRaw and Imaging.TurboJpeg do not expose System.Windows types (K-1, WP-06)")]
    [MemberData(nameof(ImagingAssemblies))]
    public void ImagingFamily_PublicMembers_DoNotExpose_SystemWindowsTypes(string assemblyName)
    {
        var assembly = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == assemblyName)
            ?? Assembly.Load(assemblyName);
        var publicTypes = assembly.GetExportedTypes().ToList();

        Assert.NotEmpty(publicTypes);

        var violations = new List<string>();

        foreach (var type in publicTypes)
        {
            // Check public properties
            foreach (var prop in type.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
            {
                if (IsForbiddenMediaType(prop.PropertyType))
                {
                    violations.Add($"{type.FullName}.{prop.Name} (property of type {prop.PropertyType})");
                }
            }

            // Check public methods
            foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
            {
                if (method.IsSpecialName) continue; // Property getters/setters & event add/remove are checked via properties/events

                if (IsForbiddenMediaType(method.ReturnType))
                {
                    violations.Add($"{type.FullName}.{method.Name} (return type {method.ReturnType})");
                }

                foreach (var param in method.GetParameters())
                {
                    if (IsForbiddenMediaType(param.ParameterType))
                    {
                        violations.Add($"{type.FullName}.{method.Name} (parameter '{param.Name}' of type {param.ParameterType})");
                    }
                }
            }

            // Check public constructors
            foreach (var ctor in type.GetConstructors(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            {
                foreach (var param in ctor.GetParameters())
                {
                    if (IsForbiddenMediaType(param.ParameterType))
                    {
                        violations.Add($"{type.FullName}..ctor (parameter '{param.Name}' of type {param.ParameterType})");
                    }
                }
            }

            // Check public fields
            foreach (var field in type.GetFields(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
            {
                if (IsForbiddenMediaType(field.FieldType))
                {
                    violations.Add($"{type.FullName}.{field.Name} (field of type {field.FieldType})");
                }
            }

            // Check public events
            foreach (var evt in type.GetEvents(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
            {
                if (evt.EventHandlerType != null && IsForbiddenMediaType(evt.EventHandlerType))
                {
                    violations.Add($"{type.FullName}.{evt.Name} (event of type {evt.EventHandlerType})");
                }
            }
        }

        Assert.True(
            violations.Count == 0,
            $"Found K-1 violations (System.Windows types exposed on public surface of {assemblyName}):\n{string.Join("\n", violations)}");
    }

    private static bool IsForbiddenMediaType(Type? type)
    {
        if (type == null) return false;

        if (type.IsGenericType)
        {
            if (type.GetGenericArguments().Any(IsForbiddenMediaType)) return true;
        }

        if (type.HasElementType)
        {
            return IsForbiddenMediaType(type.GetElementType());
        }

        return type.FullName != null && type.FullName.StartsWith("System.Windows", StringComparison.Ordinal);
    }
}
