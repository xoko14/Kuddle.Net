using System;
using System.Diagnostics.CodeAnalysis;
using Kuddle.AST;

namespace Kuddle.Extensions;

public static class KdlValueExtensions
{
    extension(KdlValue value)
    {
        public bool IsNull => value is KdlNull;
        public bool IsNumber => value is KdlNumber;
        public bool IsString => value is KdlString;
        public bool IsBool => value is KdlBool;

        public bool TryGetInt(out int res) => TryConvert(value, n => n.ToInt32(), out res);

        public bool TryGetLong(out long res) => TryConvert(value, n => n.ToInt64(), out res);

        public bool TryGetDouble(out double res) => TryConvert(value, n => n.ToDouble(), out res);

        public bool TryGetDecimal(out decimal res) =>
            TryConvert(value, n => n.ToDecimal(), out res);

        public bool TryGetBool(out bool res)
        {
            res = default;
            if (value is KdlBool b)
            {
                res = b.Value;
                return true;
            }
            return false;
        }

        public bool TryGetString([NotNullWhen(true)] out string? res)
        {
            res = (value as KdlString)?.Value;
            return res != null;
        }

        public bool TryGetUuid(out Guid res) => Guid.TryParse((value as KdlString)?.Value, out res);

        public bool TryGetDateTime(out DateTimeOffset res) =>
            DateTimeOffset.TryParse((value as KdlString)?.Value, out res);

        public bool TryGetDateOnly(out DateOnly res) =>
            DateOnly.TryParse((value as KdlString)?.Value, out res);

        public bool TryGetTimeOnly(out TimeOnly res) =>
            TimeOnly.TryParse((value as KdlString)?.Value, out res);

        public bool TryGetTimeSpan(out TimeSpan res) =>
            TimeSpan.TryParse((value as KdlString)?.Value, out res);

    }

    private static bool TryConvert<T>(KdlValue val, Func<KdlNumber, T> converter, out T result)
    {
        if (val is KdlNumber num)
        {
            try
            {
                result = converter(num);
                return true;
            }
            catch
            {
                // Ignore parsing/overflow errors
            }
        }
        result = default!;
        return false;
    }
}
