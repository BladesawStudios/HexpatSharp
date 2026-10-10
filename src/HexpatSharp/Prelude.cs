namespace HexpatSharp;

/// <summary>The types of ImHex's standard library, written in the pattern language itself. The functions are built in (<see cref="StdLib"/>).</summary>
internal static class Prelude
{
    public const string Source = """
        namespace std::mem {
            struct AlignTo<auto Alignment> {
                padding[(Alignment - ($ % Alignment)) % Alignment];
            } [[hidden]];

            struct Bytes<auto Size> {
                u8 bytes[Size];
            };
        }

        namespace std::string {
            struct NullString {
                char string[];
            };

            struct NullString16 {
                char16 string[];
            };

            struct SizedString<SizeType> {
                SizeType size;
                char string[size];
            };

            struct SizedString16<SizeType> {
                SizeType size;
                char16 string[size];
            };
        }

        namespace std::ptr {
            struct NullablePtr<T, PtrType> {
                PtrType address;
                if (address != 0) {
                    T *value : PtrType @ $ - sizeof(PtrType);
                }
            };
        }
        """;
}
