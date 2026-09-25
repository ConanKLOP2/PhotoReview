using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;

namespace PhotoReview.Localization.Generator;

/// <summary>
/// Generates <c>TrKeys</c> (one const per key) and <c>Tr</c> (one typed member per key or plural group) in
/// <c>PhotoReview.Core.Localization</c> from the English catalog passed as an AdditionalFile named <c>en.json</c>.
/// A key that does not exist in English is therefore a compile error at every call site.
/// </summary>
[Generator(LanguageNames.CSharp)]
public sealed class TrGenerator : IIncrementalGenerator
{
    private const string CatalogFileName = "en.json";
    private const string Namespace = "PhotoReview.Core.Localization";
    private const string OneSuffix = ".one";
    private const string OtherSuffix = ".other";
    private const string CountName = "count";

    // Members a static class inherits from object: a generated member with one of these names would hide it.
    private static readonly HashSet<string> s_reserved = new(StringComparer.Ordinal)
    {
        "Tr", "TrKeys", "Equals", "ReferenceEquals", "GetHashCode", "GetType", "ToString", "MemberwiseClone", "Finalize",
    };

    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var catalogs = context.AdditionalTextsProvider
            .Where(static f => string.Equals(Path.GetFileName(f.Path), CatalogFileName, StringComparison.OrdinalIgnoreCase))
            .Select(static (f, ct) => new CatalogInput(f.Path, f.GetText(ct)?.ToString()));

        context.RegisterSourceOutput(catalogs, static (spc, input) => Execute(spc, input));
    }

    private static void Execute(SourceProductionContext context, CatalogInput input)
    {
        var text = input.Content ?? string.Empty;
        var source = SourceText.From(text);
        Location At(int start, int length)
        {
            start = Math.Max(0, Math.Min(start, text.Length));
            length = Math.Max(0, Math.Min(length, text.Length - start));
            var span = new TextSpan(start, length);
            return Location.Create(input.Path, span, source.Lines.GetLinePositionSpan(span));
        }

        if (input.Content is null)
        {
            context.ReportDiagnostic(Diagnostic.Create(LocDiagnostics.InvalidJson, At(0, 0), "file cannot be read"));
            return;
        }

        List<JsonEntry> entries;
        try
        {
            entries = FlatJsonReader.Read(text);
        }
        catch (FlatJsonException ex)
        {
            context.ReportDiagnostic(Diagnostic.Create(LocDiagnostics.InvalidJson, At(ex.Position, 1), ex.Message));
            return;
        }

        // 1. Valid, unique keys (first occurrence wins; the runtime would take the last, so a duplicate is an error).
        var keys = new List<KeyInfo>();
        var byKey = new Dictionary<string, KeyInfo>(StringComparer.Ordinal);
        foreach (var entry in entries)
        {
            var location = At(entry.KeyStart, entry.KeyLength);
            if (!IsValidKey(entry.Key))
            {
                context.ReportDiagnostic(Diagnostic.Create(LocDiagnostics.InvalidKey, location, entry.Key));
                continue;
            }
            if (byKey.ContainsKey(entry.Key))
            {
                context.ReportDiagnostic(Diagnostic.Create(LocDiagnostics.DuplicateKey, location, entry.Key));
                continue;
            }
            var placeholders = TryGetPlaceholders(entry.Value);
            if (placeholders is null)
            {
                context.ReportDiagnostic(Diagnostic.Create(LocDiagnostics.InvalidPlaceholder, location, entry.Key));
            }
            var info = new KeyInfo(entry.Key, entry.Value, ToIdentifier(entry.Key), placeholders ?? new List<string>(), location,
                placeholders is not null);
            keys.Add(info);
            byKey.Add(entry.Key, info);
        }

        // 2. TrKeys: one const per key.
        var keyConsts = new List<KeyInfo>();
        var constOwners = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var key in keys)
        {
            if (TryClaim(context, constOwners, key.Identifier, key.Key, key.Location, "TrKeys")) keyConsts.Add(key);
        }

        // 3. Tr: plural groups (base.one / base.other) become one method; every other key one member.
        var members = new List<TrMember>();
        var memberOwners = new Dictionary<string, string>(StringComparer.Ordinal);
        var pluralDone = new HashSet<string>(StringComparer.Ordinal);
        foreach (var key in keys)
        {
            var pluralBase = GetPluralBase(key.Key);
            if (pluralBase is null)
            {
                if (!key.IsValidText) continue;
                if (TryClaim(context, memberOwners, key.Identifier, key.Key, key.Location, "Tr"))
                {
                    members.Add(new TrMember(key.Identifier, key, null, null, null, key.Placeholders));
                }
                continue;
            }

            if (!pluralDone.Add(pluralBase)) continue;
            byKey.TryGetValue(pluralBase + OneSuffix, out var one);
            byKey.TryGetValue(pluralBase + OtherSuffix, out var other);
            if (other is null)
            {
                context.ReportDiagnostic(Diagnostic.Create(LocDiagnostics.PluralWithoutOther, key.Location, pluralBase));
                continue;
            }
            if ((one is not null && !one.IsValidText) || !other.IsValidText) continue;

            var parameters = new List<string>();
            foreach (var name in other.Placeholders.Concat(one?.Placeholders ?? Enumerable.Empty<string>()))
            {
                if (!string.Equals(name, CountName, StringComparison.Ordinal) && !parameters.Contains(name)) parameters.Add(name);
            }
            var identifier = ToIdentifier(pluralBase);
            if (TryClaim(context, memberOwners, identifier, pluralBase, key.Location, "Tr"))
            {
                members.Add(new TrMember(identifier, null, pluralBase, one, other, parameters));
            }
        }

        context.AddSource("Tr.g.cs", SourceText.From(Render(keyConsts, members), Encoding.UTF8));
    }

    private static bool TryClaim(SourceProductionContext context, Dictionary<string, string> owners, string identifier, string key,
        Location location, string typeName)
    {
        if (owners.TryGetValue(identifier, out var owner))
        {
            context.ReportDiagnostic(Diagnostic.Create(LocDiagnostics.IdentifierCollision, location, key, typeName + "." + identifier,
                "'" + owner + "'"));
            return false;
        }
        if (s_reserved.Contains(identifier))
        {
            context.ReportDiagnostic(Diagnostic.Create(LocDiagnostics.IdentifierCollision, location, key, typeName + "." + identifier,
                "a built-in member"));
            return false;
        }
        owners.Add(identifier, key);
        return true;
    }

    private static string Render(List<KeyInfo> keyConsts, List<TrMember> members)
    {
        var sb = new StringBuilder();
        sb.Append("// <auto-generated/>\n");
        sb.Append("// Generated by PhotoReview.Localization.Generator from Localization/Languages/en.json. Do not edit.\n");
        sb.Append("#nullable enable\n\n");
        sb.Append("namespace ").Append(Namespace).Append(";\n\n");

        sb.Append("/// <summary>Catalog keys of the English source catalog (en.json).</summary>\n");
        sb.Append("[global::System.CodeDom.Compiler.GeneratedCode(\"PhotoReview.Localization.Generator\", \"1.0\")]\n");
        sb.Append("public static partial class TrKeys\n{\n");
        foreach (var key in keyConsts)
        {
            sb.Append("    public const string ").Append(key.Identifier).Append(" = ").Append(Literal(key.Key)).Append(";\n");
        }
        sb.Append("}\n\n");

        sb.Append("/// <summary>Typed access to UI text in the current language (<see cref=\"Localizer.Current\"/>).</summary>\n");
        sb.Append("[global::System.CodeDom.Compiler.GeneratedCode(\"PhotoReview.Localization.Generator\", \"1.0\")]\n");
        sb.Append("public static partial class Tr\n{\n");
        var first = true;
        foreach (var member in members)
        {
            if (!first) sb.Append('\n');
            first = false;
            if (member.PluralBase is null) RenderSimple(sb, member);
            else RenderPlural(sb, member);
        }
        sb.Append("}\n");
        return sb.ToString();
    }

    private static void RenderSimple(StringBuilder sb, TrMember member)
    {
        var key = member.Key!;
        AppendSummary(sb, "English: \"" + key.Text + "\"");
        if (member.Parameters.Count == 0)
        {
            sb.Append("    public static string ").Append(member.Identifier)
                .Append(" => Localizer.Current.Get(TrKeys.").Append(key.Identifier).Append(");\n");
            return;
        }
        sb.Append("    public static string ").Append(member.Identifier).Append('(');
        AppendParameters(sb, member.Parameters, leadingComma: false);
        sb.Append(") =>\n        Localizer.Current.Format(TrKeys.").Append(key.Identifier);
        AppendArgs(sb, member.Parameters);
        sb.Append(");\n");
    }

    private static void RenderPlural(StringBuilder sb, TrMember member)
    {
        var summary = member.One is null
            ? "English: \"" + member.Other!.Text + "\""
            : "English: \"" + member.One.Text + "\" / \"" + member.Other!.Text + "\"";
        AppendSummary(sb, summary);
        sb.Append("    public static string ").Append(member.Identifier).Append("(long count");
        AppendParameters(sb, member.Parameters, leadingComma: true);
        sb.Append(") =>\n        Localizer.Current.FormatPlural(").Append(Literal(member.PluralBase!))
            .Append(", count, new LocArg(\"count\", count)");
        AppendArgs(sb, member.Parameters);
        sb.Append(");\n");
    }

    private static void AppendSummary(StringBuilder sb, string text) =>
        sb.Append("    /// <summary>").Append(XmlEscape(text)).Append("</summary>\n");

    private static void AppendParameters(StringBuilder sb, List<string> names, bool leadingComma)
    {
        for (var i = 0; i < names.Count; i++)
        {
            if (i > 0 || leadingComma) sb.Append(", ");
            sb.Append("object? ").Append(EscapeIdentifier(names[i]));
        }
    }

    private static void AppendArgs(StringBuilder sb, List<string> names)
    {
        foreach (var name in names)
        {
            sb.Append(", new LocArg(").Append(Literal(name)).Append(", ").Append(EscapeIdentifier(name)).Append(')');
        }
    }

    private static string Literal(string value) => SymbolDisplay.FormatLiteral(value, quote: true);

    private static string EscapeIdentifier(string name) =>
        SyntaxFacts.GetKeywordKind(name) != SyntaxKind.None ? "@" + name : name;

    private static string XmlEscape(string text)
    {
        var sb = new StringBuilder(text.Length + 8);
        foreach (var c in text)
        {
            switch (c)
            {
                case '&': sb.Append("&amp;"); break;
                case '<': sb.Append("&lt;"); break;
                case '>': sb.Append("&gt;"); break;
                case '\r': break;
                case '\n': sb.Append("\\n"); break;
                default:
                    if (c < ' ' || c is (char)0x85 or (char)0x2028 or (char)0x2029) sb.Append(' '); // U+0085/2028/2029 are C# line terminators and would end the /// comment
                    else sb.Append(c);
                    break;
            }
        }
        return sb.ToString();
    }

    private static string? GetPluralBase(string key)
    {
        if (key.EndsWith(OneSuffix, StringComparison.Ordinal) && key.Length > OneSuffix.Length)
        {
            return key.Substring(0, key.Length - OneSuffix.Length);
        }
        if (key.EndsWith(OtherSuffix, StringComparison.Ordinal) && key.Length > OtherSuffix.Length)
        {
            return key.Substring(0, key.Length - OtherSuffix.Length);
        }
        return null;
    }

    internal static bool IsValidKey(string key)
    {
        if (key.Length == 0 || !IsAsciiLetter(key[0]) || key[key.Length - 1] == '.') return false;
        for (var i = 0; i < key.Length; i++)
        {
            var c = key[i];
            if (c == '.')
            {
                if (key[i - 1] == '.') return false;
                if (!IsAsciiLetter(key[i + 1]) && !IsAsciiDigit(key[i + 1])) return false;
            }
            else if (!IsAsciiLetter(c) && !IsAsciiDigit(c))
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>"status.batchDone" → "StatusBatchDone": split on '.', upper-case each segment's first letter.</summary>
    internal static string ToIdentifier(string key)
    {
        var sb = new StringBuilder(key.Length);
        foreach (var segment in key.Split('.'))
        {
            if (segment.Length == 0) continue;
            sb.Append(char.ToUpperInvariant(segment[0])).Append(segment, 1, segment.Length - 1);
        }
        return sb.ToString();
    }

    /// <summary>Placeholder names in order of first appearance (deduplicated); null when the text is invalid.
    /// Same grammar as <c>LocTemplate.TryParse</c> in PhotoReview.Core.</summary>
    internal static List<string>? TryGetPlaceholders(string text)
    {
        var names = new List<string>();
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '{')
            {
                if (i + 1 < text.Length && text[i + 1] == '{')
                {
                    i++;
                    continue;
                }
                var end = text.IndexOf('}', i + 1);
                if (end < 0) return null;
                var name = text.Substring(i + 1, end - i - 1);
                if (!IsValidPlaceholderName(name)) return null;
                if (!names.Contains(name)) names.Add(name);
                i = end;
            }
            else if (c == '}')
            {
                if (i + 1 < text.Length && text[i + 1] == '}')
                {
                    i++;
                    continue;
                }
                return null;
            }
        }
        return names;
    }

    private static bool IsValidPlaceholderName(string name)
    {
        if (name.Length == 0 || !IsAsciiLetter(name[0])) return false;
        foreach (var c in name)
        {
            if (!IsAsciiLetter(c) && !IsAsciiDigit(c)) return false;
        }
        return true;
    }

    private static bool IsAsciiLetter(char c) => (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z');

    private static bool IsAsciiDigit(char c) => c >= '0' && c <= '9';

    /// <summary>Pipeline value: compared by path and content so unchanged catalogs are not regenerated.</summary>
    private sealed class CatalogInput : IEquatable<CatalogInput>
    {
        public CatalogInput(string path, string? content)
        {
            Path = path;
            Content = content;
        }

        public string Path { get; }

        public string? Content { get; }

        public bool Equals(CatalogInput? other) =>
            other is not null
            && string.Equals(Path, other.Path, StringComparison.Ordinal)
            && string.Equals(Content, other.Content, StringComparison.Ordinal);

        public override bool Equals(object? obj) => Equals(obj as CatalogInput);

        public override int GetHashCode() =>
            StringComparer.Ordinal.GetHashCode(Path) ^ (Content is null ? 0 : StringComparer.Ordinal.GetHashCode(Content));
    }

    private sealed class KeyInfo
    {
        public KeyInfo(string key, string text, string identifier, List<string> placeholders, Location location, bool isValidText)
        {
            Key = key;
            Text = text;
            Identifier = identifier;
            Placeholders = placeholders;
            Location = location;
            IsValidText = isValidText;
        }

        public string Key { get; }

        public string Text { get; }

        public string Identifier { get; }

        public List<string> Placeholders { get; }

        public Location Location { get; }

        public bool IsValidText { get; }
    }

    private sealed class TrMember
    {
        public TrMember(string identifier, KeyInfo? key, string? pluralBase, KeyInfo? one, KeyInfo? other, List<string> parameters)
        {
            Identifier = identifier;
            Key = key;
            PluralBase = pluralBase;
            One = one;
            Other = other;
            Parameters = parameters;
        }

        public string Identifier { get; }

        /// <summary>The key of a simple member; null for a plural group.</summary>
        public KeyInfo? Key { get; }

        public string? PluralBase { get; }

        public KeyInfo? One { get; }

        public KeyInfo? Other { get; }

        public List<string> Parameters { get; }
    }
}
