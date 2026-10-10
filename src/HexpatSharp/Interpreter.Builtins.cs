using System.Globalization;
using System.Text;

namespace HexpatSharp;

internal sealed partial class Interpreter
{
    private Value CallBuiltin(string name, List<Value> a, Scope scope, Expr? at)
    {
        Expr where = at ?? new LiteralExpr(Value.Void);

        Value Arg(int index)
        {
            if (index >= a.Count) throw Fail($"{name} needs more arguments", where);
            return Resolve(a[index], where);
        }
        string Text(int index) => ToText(Arg(index));
        Int128 Int(int index) => ToInteger(Arg(index));
        double Dbl(int index) => ToDouble(Arg(index));
        Endian EndianArg(int index) => a.Count > index && ToInteger(Arg(index)) == 1 ? Endian.Big : a.Count > index ? Endian.Little : _options.DefaultEndian;
        string Format(int firstArg) => StdLib.Format(Text(firstArg), a.Skip(firstArg + 1).Select(v => Resolve(v, where)).ToList(), FormatForText);

        switch (name)
        {
            // ---- messages ----
            case "std::print" or "std::println":
                _result.Log.Add(Format(0));
                return Value.Void;
            case "std::warning":
                _result.Log.Add("warning: " + Format(0));
                return Value.Void;
            case "std::error":
                throw Fail(Format(0), where);
            case "std::assert":
                if (!Truthy(Arg(0), where)) throw Fail("assertion failed: " + (a.Count > 1 ? Text(1) : ""), where);
                return Value.Void;
            case "std::assert_warn":
                if (!Truthy(Arg(0), where)) _result.Log.Add("warning: " + (a.Count > 1 ? Text(1) : "assertion failed"));
                return Value.Void;
            case "std::format":
                return Value.FromString(Format(0));

            // ---- std::mem ----
            case "std::mem::eof":
                return Value.FromBool(_cursor >= (ulong)_main.Data.LongLength);
            case "std::mem::size":
                return Value.FromUnsigned((ulong)_main.Data.LongLength);
            case "std::mem::base_address":
                return Value.FromUnsigned(0);
            case "std::mem::current_bit_offset":
                return Value.FromUnsigned(0);
            case "std::mem::read_unsigned":
                return Value.FromUnsigned(_section.ReadUnsigned((ulong)Int(0), (int)Int(1), EndianArg(2)));
            case "std::mem::read_signed":
                return Value.FromInt(SignExtend((Int128)_section.ReadUnsigned((ulong)Int(0), (int)Int(1), EndianArg(2)), (int)Int(1)));
            case "std::mem::read_string":
            {
                ulong offset = (ulong)Int(0), size = (ulong)Int(1);
                if (!_section.InRange(offset, size)) throw Fail("reading past the end of the data", where);
                return Value.FromString(Encoding.Latin1.GetString(_section.Data, (int)offset, (int)size));
            }
            case "std::mem::find_sequence_in_range":
                return FindInRange(Int(0), (ulong)Int(1), (ulong)Int(2), BytesOf(a.Skip(3).ToList(), where));
            case "std::mem::find_string_in_range":
                return FindInRange(Int(0), (ulong)Int(1), (ulong)Int(2), Encoding.Latin1.GetBytes(Text(3)));
            case "std::mem::align_to":
            {
                Int128 alignment = Int(0), value = Int(1);
                if (alignment <= 0) throw Fail("the alignment must be positive", where);
                return Value.FromUnsigned((UInt128)((value + alignment - 1) / alignment * alignment));
            }
            case "std::mem::create_section":
            {
                int id = _nextSectionId++;
                _sections[id] = new Section(id, Text(0), []);
                return Value.FromSection(id);
            }
            case "std::mem::delete_section":
                _sections.Remove(SectionOf(Arg(0), where).Id);
                return Value.Void;
            case "std::mem::get_section_size":
                return Value.FromUnsigned((ulong)SectionOf(Arg(0), where).Data.LongLength);
            case "std::mem::set_section_size":
            {
                Section section = SectionOf(Arg(0), where);
                byte[] resized = new byte[(int)Int(1)];
                Array.Copy(section.Data, resized, Math.Min(section.Data.Length, resized.Length));
                section.Data = resized;
                return Value.Void;
            }
            case "std::mem::copy_value_to_section":
            {
                Value source = a[0];
                Section destination = SectionOf(Arg(1), where);
                ulong offset = (ulong)Int(2);
                byte[] bytes = BytesOfValue(source, where);
                if (offset + (ulong)bytes.Length > (ulong)destination.Data.Length)
                    throw Fail("the value does not fit in the section; set its size first", where);
                bytes.CopyTo(destination.Data, (int)offset);
                return Value.Void;
            }
            case "std::mem::copy_section_to_section":
            {
                Section from = SectionOf(Arg(0), where), to = SectionOf(Arg(2), where);
                ulong fromOffset = (ulong)Int(1), toOffset = (ulong)Int(3), size = (ulong)Int(4);
                if (!from.InRange(fromOffset, size) || !to.InRange(toOffset, size)) throw Fail("copying past the end of a section", where);
                Array.Copy(from.Data, (long)fromOffset, to.Data, (long)toOffset, (long)size);
                return Value.Void;
            }

            // ---- std::core ----
            case "std::core::member_count":
                return Value.FromUnsigned((ulong)(a[0].Pattern?.Children.Count ?? (a[0].Pattern is ArrayPattern { IsScalar: true } arr ? (int)arr.Count : 0)));
            case "std::core::has_member":
                return Value.FromBool(a[0].Pattern switch
                {
                    StructPattern s => s.Find(Text(1)) is not null || s.Locals.ContainsKey(Text(1)),
                    BitfieldPattern b => b.ByName.ContainsKey(Text(1)),
                    _ => false,
                });
            case "std::core::array_index":
                return Value.FromInt(0);
            case "std::core::set_endian":
                _endian = Int(0) == 1 ? Endian.Big : Endian.Little;
                return Value.Void;
            case "std::core::get_endian":
                return Value.FromInt(_endian == Endian.Big ? 1 : 0);
            case "std::core::formatted_value":
                return Value.FromString(a[0].Pattern is { } fp ? FormatPattern(fp) : ToText(Arg(0)));
            case "std::core::has_attribute":
                return Value.FromBool(a[0].Pattern?.Attributes.Any(x => x.Name == Text(1)) == true);
            case "std::core::get_attribute_argument":
                return a[0].Pattern is { } ap && ap.Attributes.FirstOrDefault(x => x.Name == Text(1)) is { } attribute && (int)Int(2) < attribute.Args.Count
                    ? Evaluate(attribute.Args[(int)Int(2)], scope)
                    : Value.FromString("");
            case "std::core::is_valid_enum":
                return Value.FromBool(a[0].Pattern is EnumPattern ep && ep.Def.NameOf(ToInteger(ValueOf(ep))) is not null);
            case "std::core::set_bitfield_order" or "std::core::get_bitfield_order":
                return Value.FromInt(0);

            // ---- std::string ----
            case "std::string::length":
                return Value.FromUnsigned((ulong)Text(0).Length);
            case "std::string::at":
            {
                string s = Text(0);
                Int128 i = Int(1);
                if (i < 0) i += s.Length;
                if (i < 0 || i >= s.Length) throw Fail("index outside the string", where);
                return Value.FromChar(s[(int)i]);
            }
            case "std::string::substr":
            {
                string s = Text(0);
                int pos = (int)Int(1);
                int length = (int)Int(2);
                if (pos < 0 || pos > s.Length) throw Fail("position outside the string", where);
                return Value.FromString(s.Substring(pos, Math.Min(length, s.Length - pos)));
            }
            case "std::string::find":
                return Value.FromInt(Text(0).IndexOf(Text(1), a.Count > 2 ? (int)Int(2) : 0, StringComparison.Ordinal));
            case "std::string::contains":
                return Value.FromBool(Text(0).Contains(Text(1), StringComparison.Ordinal));
            case "std::string::starts_with":
                return Value.FromBool(Text(0).StartsWith(Text(1), StringComparison.Ordinal));
            case "std::string::ends_with":
                return Value.FromBool(Text(0).EndsWith(Text(1), StringComparison.Ordinal));
            case "std::string::to_upper":
                return Value.FromString(Text(0).ToUpperInvariant());
            case "std::string::to_lower":
                return Value.FromString(Text(0).ToLowerInvariant());
            case "std::string::replace":
                return Value.FromString(Text(0).Replace(Text(1), Text(2), StringComparison.Ordinal));
            case "std::string::reverse":
            {
                char[] chars = Text(0).ToCharArray();
                Array.Reverse(chars);
                return Value.FromString(new string(chars));
            }
            case "std::string::trim":
                return Value.FromString(Text(0).Trim());
            case "std::string::parse_int":
            {
                string s = Text(0).Trim();
                int radix = a.Count > 1 ? (int)Int(1) : 10;
                try
                {
                    return Value.FromInt(Convert.ToInt64(s.StartsWith("0x", StringComparison.OrdinalIgnoreCase) && radix == 16 ? s[2..] : s, radix));
                }
                catch (Exception e) when (e is FormatException or OverflowException or ArgumentException)
                {
                    throw Fail($"'{s}' is not a number in base {radix}", where);
                }
            }
            case "std::string::parse_float":
                return double.TryParse(Text(0), NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed)
                    ? Value.FromFloat(parsed)
                    : throw Fail($"'{Text(0)}' is not a number", where);

            // ---- std::math ----
            case "std::math::floor": return Value.FromFloat(Math.Floor(Dbl(0)));
            case "std::math::ceil": return Value.FromFloat(Math.Ceiling(Dbl(0)));
            case "std::math::round": return Value.FromFloat(Math.Round(Dbl(0), MidpointRounding.AwayFromZero));
            case "std::math::trunc": return Value.FromFloat(Math.Truncate(Dbl(0)));
            case "std::math::abs": return Arg(0).Kind == ValueKind.Float ? Value.FromFloat(Math.Abs(Dbl(0))) : Value.FromInt(Int128.Abs(Int(0)));
            case "std::math::min": return Compare(Arg(0), Arg(1)) <= 0 ? Arg(0) : Arg(1);
            case "std::math::max": return Compare(Arg(0), Arg(1)) >= 0 ? Arg(0) : Arg(1);
            case "std::math::clamp": return Compare(Arg(0), Arg(1)) < 0 ? Arg(1) : Compare(Arg(0), Arg(2)) > 0 ? Arg(2) : Arg(0);
            case "std::math::pow": return Value.FromFloat(Math.Pow(Dbl(0), Dbl(1)));
            case "std::math::sqrt": return Value.FromFloat(Math.Sqrt(Dbl(0)));
            case "std::math::log": return Value.FromFloat(Math.Log(Dbl(0)));
            case "std::math::log2": return Value.FromFloat(Math.Log2(Dbl(0)));
            case "std::math::log10": return Value.FromFloat(Math.Log10(Dbl(0)));
            case "std::math::exp": return Value.FromFloat(Math.Exp(Dbl(0)));
            case "std::math::sin": return Value.FromFloat(Math.Sin(Dbl(0)));
            case "std::math::cos": return Value.FromFloat(Math.Cos(Dbl(0)));
            case "std::math::tan": return Value.FromFloat(Math.Tan(Dbl(0)));

            case "std::sys::is_big_endian":
                return Value.FromBool(false);
        }
        throw Fail($"'{name}' is not supported", where);
    }

    private Value FindInRange(Int128 occurrence, ulong start, ulong end, byte[] needle)
    {
        Int128 found = 0;
        end = Math.Min(end, (ulong)_section.Data.LongLength);
        if (needle.Length == 0) return Value.FromInt(-1);
        for (ulong at = start; at + (ulong)needle.Length <= end; at++)
        {
            if (_section.Data.AsSpan((int)at, needle.Length).SequenceEqual(needle))
            {
                if (found == occurrence) return Value.FromUnsigned(at);
                found++;
            }
        }
        return Value.FromInt(-1);
    }

    private byte[] BytesOf(List<Value> values, Expr at)
    {
        List<byte> bytes = [];
        foreach (Value value in values)
        {
            Value v = Resolve(value, at);
            if (v.Kind == ValueKind.List) bytes.AddRange(BytesOf(v.Items!, at));
            else if (v.Kind == ValueKind.String) bytes.AddRange(Encoding.Latin1.GetBytes(v.Text));
            else bytes.Add((byte)ToInteger(v));
        }
        return [.. bytes];
    }

    private byte[] BytesOfValue(Value value, Expr at)
    {
        if (value.Kind == ValueKind.Pattern)
        {
            Pattern p = value.Pattern!;
            if (!p.Source.InRange(p.Offset, p.Size)) throw Fail("the value is outside the data", at);
            return p.Source.Data.AsSpan((int)p.Offset, (int)p.Size).ToArray();
        }
        return BytesOf([value], at);
    }

    // ---- showing values as text ----

    private string FormatForText(Value value)
    {
        if (value.Kind == ValueKind.Pattern) return FormatPattern(value.Pattern!);
        return ToText(value);
    }

    /// <summary>The value of a pattern as the pattern viewer shows it: its [[format]] function if it has one, otherwise by type.</summary>
    public string FormatPattern(Pattern pattern)
    {
        if (pattern.Format is not null)
        {
            Value formatted = CallNamedFunction(pattern.Format, [Value.FromPattern(pattern)], _global, null);
            return ToText(formatted);
        }
        return DefaultFormat(pattern);
    }

    public string DefaultFormat(Pattern pattern)
    {
        switch (pattern)
        {
            case ScalarPattern scalar:
                return FormatScalar(scalar);
            case EnumPattern en:
            {
                Int128 number = ToInteger(ScalarValue(en.Underlying));
                string? entry = en.Def.NameOf(number);
                return entry is null ? $"{number} (unknown)" : $"{entry} ({number})";
            }
            case BitfieldFieldPattern field:
            {
                Value value = BitfieldFieldValue(field);
                if (field.Enum is not null)
                {
                    string? entry = field.Enum.NameOf(value.Integer);
                    return entry is null ? $"{value} (unknown)" : $"{entry} ({value})";
                }
                return value.Kind == ValueKind.Boolean ? (value.Integer != 0 ? "true" : "false") : value.ToString();
            }
            case ArrayPattern { ScalarElement: ScalarKind.Char or ScalarKind.Char16 } chars:
                return ArrayText(chars);
            case ArrayPattern { IsScalar: true } numbers:
            {
                StringBuilder text = new("[");
                for (ulong i = 0; i < numbers.Count && i < 16; i++)
                {
                    if (i > 0) text.Append(", ");
                    text.Append(FormatScalar(numbers.ScalarAt(i)));
                }
                if (numbers.Count > 16) text.Append($", ... {numbers.Count} in all");
                return text.Append(']').ToString();
            }
            case PointerPattern pointer:
                return "*0x" + pointer.Address.ToString("X");
            default:
                return "";
        }
    }

    private string FormatScalar(ScalarPattern scalar)
    {
        Value value = ScalarValue(scalar);
        switch (scalar.Kind)
        {
            case ScalarKind.Bool:
                return value.Integer != 0 ? "true" : "false";
            case ScalarKind.Char or ScalarKind.Char16:
            {
                int code = (int)value.Integer;
                return code is >= 32 and < 127 || code > 160 ? ((char)code).ToString() : $"'\\x{code:X2}'";
            }
            case ScalarKind.Float:
                return scalar.Size == 4
                    ? ((float)value.Float).ToString("R", CultureInfo.InvariantCulture)
                    : value.Float.ToString("R", CultureInfo.InvariantCulture);
            default:
                return value.ToString();
        }
    }
}
