# HexpatSharp

An interpreter for [ImHex's pattern language](https://docs.werwolv.net/pattern-language/) (`.hexpat`). It lays structs, unions, enums,
bitfields, arrays and pointers over binary data and gives back the tree of what it found.

```csharp
string pattern = File.ReadAllText("tscb.hexpat");
byte[] data = File.ReadAllBytes("GerudoQuickSandA.tscb");

EvaluationResult result = Hexpat.Evaluate(pattern, data);

result.Patterns;      // the variables placed at the top level, with their children
result.Error;         // why evaluation stopped early ("12:5: ..."), or null; what was placed before it is kept
result.Log;           // what std::print and std::assert_warn wrote
string json = result.ToJson();   // { "ast": [ { name, type, start_offset, size, value?, children } ], "log": [...], "evaluationError": ... }
```

`EvaluationOptions` sets the pattern, array and nesting limits (`#pragma pattern_limit`, `array_limit` and `eval_depth` raise them), a step
limit against loops that never end, the default endianness, and how `#include "file"` is resolved. The standard library is built in, so
`#include <std/...>` does nothing.

## What it understands

- **Types:** `u8` to `u128`, `s8` to `s128`, `float`, `double`, `char`, `char16`, `bool`; `struct`, `union` (with inheritance and templates, type and
  value parameters), `enum` (with ranges), `bitfield` (with `if`, typed fields, `padding`, and `bitfield_order`), `using` aliases and forward declarations,
  `namespace`, `be` / `le`, `padding[n]`, unnamed placements like `std::mem::AlignTo<4>;`.
- **Placement:** `T x;`, `T x @ offset;`, `T x[n]`, `T x[while(cond)]`, unsized `T x[]` (to an all-zero element), pointers (`T *p : u32`, `[[pointer_base]]`),
  `in section` placement, `$`, and assigning to `$`.
- **Statements and expressions:** `if` / `else`, `while`, `for` (commas or semicolons), `match` (with `|` alternatives, ranges and `_`), `break`,
  `continue`, `return`, `fn` with defaults and variadics, C operator precedence, `?:`, `sizeof`, `addressof`, `typenameof`, `this`, `parent`.
- **Attributes:** `inline`, `hidden`, `name`, `comment`, `color`, `format`, `transform`, `bitfield_order`, `pointer_base` take effect; the rest are
  kept for `std::core::has_attribute`.
- **Preprocessor:** `#define` (without arguments), `#ifdef`, `#ifndef`, `#else`, `#endif`, `#include`, `#pragma`.
- **Standard library:** `std::print`, `format` (libfmt style), `assert`, `error`, `std::mem` (`read_unsigned`, `read_signed`, `read_string`,
  `find_sequence_in_range`, `align_to`, sections), `std::core` (`member_count`, `has_member`, `formatted_value`, ...), `std::string`, `std::math`, and the
  types `AlignTo`, `Bytes`, `NullString`, `SizedString`, `NullablePtr`.

## Build

```bash
dotnet build HexpatSharp.sln -c Release
dotnet test tests/HexpatSharp.Tests
```

## Licence

AGPL-3.0-or-later. See [license.md](license.md).
