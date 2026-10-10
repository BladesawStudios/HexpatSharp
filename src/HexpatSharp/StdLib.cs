using System.Globalization;
using System.Text;

namespace HexpatSharp;

/// <summary>The names of the built-in functions and constants (the parts of ImHex's standard library this supports).</summary>
internal static class StdLib
{
    private static readonly HashSet<string> Functions = new(StringComparer.Ordinal)
    {
        "std::print", "std::println", "std::error", "std::warning", "std::assert", "std::assert_warn", "std::format",
        "std::mem::eof", "std::mem::size", "std::mem::base_address", "std::mem::read_unsigned", "std::mem::read_signed",
        "std::mem::read_string", "std::mem::find_sequence_in_range", "std::mem::find_string_in_range", "std::mem::align_to",
        "std::mem::create_section", "std::mem::delete_section", "std::mem::get_section_size", "std::mem::set_section_size",
        "std::mem::copy_value_to_section", "std::mem::copy_section_to_section", "std::mem::current_bit_offset",
        "std::core::member_count", "std::core::has_member", "std::core::array_index", "std::core::set_endian",
        "std::core::get_endian", "std::core::formatted_value", "std::core::has_attribute", "std::core::get_attribute_argument",
        "std::core::is_valid_enum", "std::core::set_bitfield_order", "std::core::get_bitfield_order",
        "std::string::length", "std::string::at", "std::string::substr", "std::string::find", "std::string::contains",
        "std::string::starts_with", "std::string::ends_with", "std::string::to_upper", "std::string::to_lower",
        "std::string::replace", "std::string::parse_int", "std::string::parse_float", "std::string::reverse", "std::string::trim",
        "std::math::floor", "std::math::ceil", "std::math::round", "std::math::trunc", "std::math::abs", "std::math::min",
        "std::math::max", "std::math::pow", "std::math::sqrt", "std::math::log", "std::math::log2", "std::math::log10",
        "std::math::exp", "std::math::sin", "std::math::cos", "std::math::tan", "std::math::clamp",
        "std::sys::is_big_endian",
    };

    public static bool Has(string name) => Functions.Contains(name);

    public static bool TryConstant(string name, out Value value)
    {
        switch (name)
        {
            case "std::mem::Endian::Native" or "std::mem::Endian::Little" or "std::core::Endian::Little":
                value = Value.FromInt(0);
                return true;
            case "std::mem::Endian::Big" or "std::core::Endian::Big":
                value = Value.FromInt(1);
                return true;
            case "std::core::BitfieldOrder::LeastToMostSignificant" or "std::core::BitfieldOrder::RightToLeft":
                value = Value.FromInt(0);
                return true;
            case "std::core::BitfieldOrder::MostToLeastSignificant" or "std::core::BitfieldOrder::LeftToRight":
                value = Value.FromInt(1);
                return true;
        }
        value = Value.Void;
        return false;
    }

    // ---- std::format ----

    /// <summary>Formats like libfmt: <c>{}</c>, <c>{0}</c>, <c>{:#x}</c>, <c>{:08X}</c>, <c>{:>10}</c>, <c>{:.3f}</c>, <c>{{</c> and <c>}}</c>.</summary>
    public static string Format(string format, IReadOnlyList<Value> args, Func<Value, string> text)
    {
        StringBuilder output = new();
        int nextArg = 0;
        for (int i = 0; i < format.Length; i++)
        {
            char c = format[i];
            if (c == '{')
            {
                if (i + 1 < format.Length && format[i + 1] == '{') { output.Append('{'); i++; continue; }
                int close = format.IndexOf('}', i);
                if (close < 0) throw new HexpatException("format string has an unclosed '{'");
                string field = format[(i + 1)..close];
                i = close;

                string index = field, spec = "";
                int colon = field.IndexOf(':');
                if (colon >= 0) { index = field[..colon]; spec = field[(colon + 1)..]; }
                int argIndex = index.Length > 0 ? int.Parse(index, CultureInfo.InvariantCulture) : nextArg++;
                if (argIndex >= args.Count) throw new HexpatException($"the format string uses argument {argIndex} but only {args.Count} were given");
                output.Append(FormatOne(args[argIndex], spec, text));
            }
            else if (c == '}')
            {
                if (i + 1 < format.Length && format[i + 1] == '}') i++;
                output.Append('}');
            }
            else output.Append(c);
        }
        return output.ToString();
    }

    private static string FormatOne(Value value, string spec, Func<Value, string> text)
    {
        char fill = ' ';
        char align = '\0';
        bool alternate = false, zero = false, plus = false;
        int width = 0, precision = -1;
        char type = '\0';

        int p = 0;
        if (spec.Length >= 2 && spec[1] is '<' or '>' or '^') { fill = spec[0]; align = spec[1]; p = 2; }
        else if (spec.Length >= 1 && spec[0] is '<' or '>' or '^') { align = spec[0]; p = 1; }
        if (p < spec.Length && spec[p] is '+' or '-' or ' ') { plus = spec[p] == '+'; p++; }
        if (p < spec.Length && spec[p] == '#') { alternate = true; p++; }
        if (p < spec.Length && spec[p] == '0') { zero = true; p++; }
        int widthStart = p;
        while (p < spec.Length && char.IsAsciiDigit(spec[p])) p++;
        if (p > widthStart) width = int.Parse(spec[widthStart..p], CultureInfo.InvariantCulture);
        if (p < spec.Length && spec[p] == '.')
        {
            p++;
            int precisionStart = p;
            while (p < spec.Length && char.IsAsciiDigit(spec[p])) p++;
            precision = p > precisionStart ? int.Parse(spec[precisionStart..p], CultureInfo.InvariantCulture) : 0;
        }
        if (p < spec.Length) type = spec[p];

        string body;
        bool numeric = value.Kind is ValueKind.Integer or ValueKind.Boolean or ValueKind.Char or ValueKind.Float;
        if (value.Kind == ValueKind.Float && type is '\0' or 'f' or 'F' or 'e' or 'E' or 'g' or 'G')
        {
            body = type switch
            {
                'e' => value.Float.ToString("e" + (precision < 0 ? 6 : precision), CultureInfo.InvariantCulture),
                'E' => value.Float.ToString("E" + (precision < 0 ? 6 : precision), CultureInfo.InvariantCulture),
                'f' or 'F' => value.Float.ToString("F" + (precision < 0 ? 6 : precision), CultureInfo.InvariantCulture),
                _ => precision >= 0 ? value.Float.ToString("F" + precision, CultureInfo.InvariantCulture) : value.Float.ToString("R", CultureInfo.InvariantCulture),
            };
            if (plus && value.Float >= 0) body = "+" + body;
        }
        else if (numeric && type is 'x' or 'X' or 'b' or 'B' or 'o' or 'd' or 'c')
        {
            Int128 number = value.Kind == ValueKind.Float ? (Int128)value.Float : value.Integer;
            bool negative = !value.Unsigned && number < 0;
            UInt128 magnitude = negative ? (UInt128)(-number) : (UInt128)number;
            string digits = type switch
            {
                'x' => magnitude.ToString("x", CultureInfo.InvariantCulture),
                'X' => magnitude.ToString("X", CultureInfo.InvariantCulture),
                'b' or 'B' => ToBase(magnitude, 2),
                'o' => ToBase(magnitude, 8),
                'c' => ((char)(int)number).ToString(),
                _ => magnitude.ToString(CultureInfo.InvariantCulture),
            };
            string prefix = alternate ? type switch { 'x' => "0x", 'X' => "0X", 'b' => "0b", 'B' => "0B", 'o' => "0", _ => "" } : "";
            string sign = negative ? "-" : plus ? "+" : "";
            if (zero && width > 0 && align == '\0')
            {
                int padding = width - sign.Length - prefix.Length - digits.Length;
                if (padding > 0) digits = new string('0', padding) + digits;
            }
            body = sign + prefix + digits;
        }
        else
        {
            body = text(value);
            if (precision >= 0 && body.Length > precision) body = body[..precision];
        }

        if (body.Length < width)
        {
            char effectiveAlign = align != '\0' ? align : numeric && value.Kind != ValueKind.Boolean && value.Kind != ValueKind.Char ? '>' : '<';
            int gap = width - body.Length;
            body = effectiveAlign switch
            {
                '>' => new string(fill, gap) + body,
                '^' => new string(fill, gap / 2) + body + new string(fill, gap - gap / 2),
                _ => body + new string(fill, gap),
            };
        }
        return body;
    }

    private static string ToBase(UInt128 value, int radix)
    {
        if (value == 0) return "0";
        StringBuilder digits = new();
        while (value > 0)
        {
            digits.Insert(0, (char)('0' + (int)(value % (UInt128)radix)));
            value /= (UInt128)radix;
        }
        return digits.ToString();
    }
}
