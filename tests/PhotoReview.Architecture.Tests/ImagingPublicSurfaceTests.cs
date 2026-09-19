using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Xunit;

namespace PhotoReview.Architecture.Tests;

public sealed class ImagingPublicSurfaceTests
{
    [Fact(DisplayName = "Rule 3: Public types in Imaging.Caching and Imaging.Preload do not expose System.Windows.Media types (K-1)")]
    public void Imaging_CachingAndPreload_PublicMembers_DoNotExpose_SystemWindowsMediaTypes()
    {
        var assembly = typeof(PhotoReview.Imaging.Decoding.IDecodedImage).Assembly;
        var targetNamespaces = new[]
        {
            "PhotoReview.Imaging.Caching",
            "PhotoReview.Imaging.Preload"
        };

        var publicTypes = assembly.GetExportedTypes()
            .Where(t => targetNamespaces.Any(ns => t.Namespace == ns || (t.Namespace != null && t.Namespace.StartsWith(ns + ".", StringComparison.Ordinal))))
            .ToList();

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
            $"Found K-1 violations (System.Windows.Media types exposed on public surface):\n{string.Join("\n", violations)}");
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

        return type.FullName != null && type.FullName.StartsWith("System.Windows.Media", StringComparison.Ordinal);
    }
}
