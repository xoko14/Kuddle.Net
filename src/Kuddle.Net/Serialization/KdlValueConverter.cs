using System;
using Kuddle.AST;
using Kuddle.Extensions;
using Kuddle.Parser;

namespace Kuddle.Serialization;

internal static class KdlValueConverter
{
    public static bool TryFromKdl(KdlValue kdlValue, Type targetType, out object? value)
    {
        value = default;
        if (kdlValue is KdlNull)
            return IsNullable(targetType);

        var underlying = Nullable.GetUnderlyingType(targetType) ?? targetType;

        // 1. Strings & Enums
        if (underlying == typeof(string))
            return kdlValue.TryGetString(out var s) && (value = s) is not null;
        if (underlying.IsEnum)
        {
            return kdlValue.TryGetString(out var s)
                && Enum.TryParse(underlying, s, true, out value);
        }

        // 2. Numerics
        if (underlying == typeof(int))
            return kdlValue.TryGetInt(out var i) && (value = i) is not null;
        if (underlying == typeof(long))
            return kdlValue.TryGetLong(out var l) && (value = l) is not null;
        if (underlying == typeof(short))
            return kdlValue.TryGetInt(out var sh) && (value = (short)sh) is not null;
        if (underlying == typeof(double))
            return kdlValue.TryGetDouble(out var d) && (value = d) is not null;
        if (underlying == typeof(decimal))
            return kdlValue.TryGetDecimal(out var m) && (value = m) is not null;
        if (underlying == typeof(float))
            return kdlValue.TryGetDouble(out var f) && (value = (float)f) is not null;
        if (underlying == typeof(bool))
            return kdlValue.TryGetBool(out var b) && (value = b) is not null;
        if (underlying == typeof(byte))
            return kdlValue.TryGetInt(out var by) && (value = (byte)by) is not null;

        // 3. Temporal & Specialized
        if (underlying == typeof(Guid))
            return kdlValue.TryGetUuid(out var g) && (value = g) is not null;
        if (underlying == typeof(DateTimeOffset))
            return kdlValue.TryGetDateTime(out var dto) && (value = dto) is not null;
        if (underlying == typeof(DateTime))
            return kdlValue.TryGetDateTime(out var dt) && (value = dt.DateTime) is not null;
        if (underlying == typeof(DateOnly))
            return kdlValue.TryGetDateOnly(out var do1) && (value = do1) is not null;
        if (underlying == typeof(TimeOnly))
            return kdlValue.TryGetTimeOnly(out var to1) && (value = to1) is not null;
        if (underlying == typeof(TimeSpan))
            return kdlValue.TryGetTimeSpan(out var ts) && (value = ts) is not null;
        
        //4. AST
        if (underlying == typeof(KdlValue))
        {
            value = kdlValue;
            return true;
        }


        return false;
    }

    public static bool TryToKdl(object? input, out KdlValue kdlValue, string? typeAnnotation = null)
    {
        if (input is null)
        {
            kdlValue = KdlValue.Null;
            return true;
        }

        kdlValue = input switch
        {
            string s => KdlValue.From(s),
            int i => KdlValue.From(i),
            long l => KdlValue.From(l),
            short sh => KdlValue.From(sh),
            sbyte sb => KdlValue.From(sb),
            uint ui => KdlValue.From(ui),
            ulong ul => KdlValue.From((long)ul),
            ushort us => KdlValue.From(us),
            byte by => KdlValue.From(by),
            double d => KdlValue.From(d),
            float f => KdlValue.From((double)f),
            decimal m => KdlValue.From(m),
            bool b => KdlValue.From(b),
            Guid uuid => KdlValue.From(uuid),
            DateTime dt => KdlValue.From(dt),
            DateTimeOffset dto => KdlValue.From(dto),
            DateOnly d => KdlValue.From(d),
            TimeOnly t => KdlValue.From(t),
            TimeSpan ts => KdlValue.From(ts),
            Enum e => KdlValue.From(e),
            KdlValue kv => kv,
            _ => null!,
        };

        if (kdlValue is not null && typeAnnotation is not null)
            kdlValue = kdlValue with { TypeAnnotation = typeAnnotation };

        return kdlValue is not null;
    }

    /// <summary>
    /// Converts a KDL value to a CLR type, throwing on failure.
    /// </summary>
    public static object FromKdlOrThrow(
        KdlValue kdlValue,
        Type targetType,
        string context,
        string? expectedTypeAnnotation = null
    )
    {
        var finalTargetType = targetType;

        if (targetType == typeof(object))
        {
            finalTargetType =
                CharacterSets.GetClrType(expectedTypeAnnotation ?? kdlValue.TypeAnnotation)
                ?? targetType;
        }

        if (!TryFromKdl(kdlValue, finalTargetType, out var result))
        {
            throw new KuddleSerializationException(
                $"Cannot convert KDL value '{kdlValue}' to {targetType.Name}. {context}"
            );
        }
        return result ?? throw new KuddleSerializationException("Conversion resulted in null.");
    }

    /// <summary>
    /// Converts a CLR value to a KDL value, throwing on failure.
    /// </summary>
    public static KdlValue ToKdlOrThrow(object? input, string? typeAnnotation = null)
    {
        if (!TryToKdl(input, out var kdlValue, typeAnnotation))
        {
            var typeName = input?.GetType().Name ?? "null";
            throw new KuddleSerializationException(
                $"Cannot convert CLR value of type '{typeName}' to KDL."
            );
        }
        return kdlValue;
    }

    private static bool IsNullable(Type type) =>
        !type.IsValueType || Nullable.GetUnderlyingType(type) != null;
}
