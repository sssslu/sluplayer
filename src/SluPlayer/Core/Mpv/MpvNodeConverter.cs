using System.Runtime.InteropServices;

namespace SluPlayer.Core.Mpv;

/// <summary>Converts mpv_node trees into plain .NET values.</summary>
/// <remarks>
/// String -> string, Flag -> bool, Int64 -> long, Double -> double, NodeArray -> List&lt;object?&gt;,
/// NodeMap -> Dictionary&lt;string, object?&gt;, ByteArray -> byte[], None -> null.
/// </remarks>
internal static unsafe class MpvNodeConverter
{
    public static object? ToObject(MpvNode* node)
    {
        switch (node->Format)
        {
            case MpvFormat.None:
                return null;
            case MpvFormat.String:
            case MpvFormat.OsdString:
                return node->String == 0 ? null : Marshal.PtrToStringUTF8(node->String);
            case MpvFormat.Flag:
                return node->Flag != 0;
            case MpvFormat.Int64:
                return node->Int64;
            case MpvFormat.Double:
                return node->Double;
            case MpvFormat.NodeArray:
            {
                var list = (MpvNodeList*)node->List;
                if (list == null) return new List<object?>();
                var values = (MpvNode*)list->Values;
                var result = new List<object?>(list->Num);
                for (int i = 0; i < list->Num; i++)
                    result.Add(ToObject(&values[i]));
                return result;
            }
            case MpvFormat.NodeMap:
            {
                var list = (MpvNodeList*)node->List;
                var result = new Dictionary<string, object?>(StringComparer.Ordinal);
                if (list == null) return result;
                var values = (MpvNode*)list->Values;
                var keys = (nint*)list->Keys;
                for (int i = 0; i < list->Num; i++)
                {
                    var key = Marshal.PtrToStringUTF8(keys[i]) ?? string.Empty;
                    result[key] = ToObject(&values[i]);
                }
                return result;
            }
            case MpvFormat.ByteArray:
            {
                var ba = (MpvByteArray*)node->ByteArray;
                if (ba == null || ba->Size == 0) return Array.Empty<byte>();
                var bytes = new byte[(int)ba->Size];
                Marshal.Copy(ba->Data, bytes, 0, bytes.Length);
                return bytes;
            }
            default:
                return null;
        }
    }

    /// <summary>Reads the value pointed to by a property-change/get-property data pointer.</summary>
    public static object? ReadData(MpvFormat format, nint data)
    {
        if (data == 0) return null;
        switch (format)
        {
            case MpvFormat.None:
                return null;
            case MpvFormat.String:
            case MpvFormat.OsdString:
            {
                var str = *(nint*)data;
                return str == 0 ? null : Marshal.PtrToStringUTF8(str);
            }
            case MpvFormat.Flag:
                return *(int*)data != 0;
            case MpvFormat.Int64:
                return *(long*)data;
            case MpvFormat.Double:
                return *(double*)data;
            case MpvFormat.Node:
                return ToObject((MpvNode*)data);
            default:
                return null;
        }
    }
}

/// <summary>Typed accessors for converted node maps.</summary>
public static class MpvNodeExtensions
{
    public static string? GetString(this IReadOnlyDictionary<string, object?> map, string key)
        => map.TryGetValue(key, out var v) ? v as string ?? v?.ToString() : null;

    public static long? GetLong(this IReadOnlyDictionary<string, object?> map, string key)
        => map.TryGetValue(key, out var v) ? v switch
        {
            long l => l,
            double d => (long)d,
            bool b => b ? 1 : 0,
            string s when long.TryParse(s, out var p) => p,
            _ => null,
        } : null;

    public static double? GetDouble(this IReadOnlyDictionary<string, object?> map, string key)
        => map.TryGetValue(key, out var v) ? v switch
        {
            double d => d,
            long l => l,
            string s when double.TryParse(s, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var p) => p,
            _ => null,
        } : null;

    public static bool GetBool(this IReadOnlyDictionary<string, object?> map, string key)
        => map.TryGetValue(key, out var v) && v switch
        {
            bool b => b,
            long l => l != 0,
            string s => s is "yes" or "true",
            _ => false,
        };
}
