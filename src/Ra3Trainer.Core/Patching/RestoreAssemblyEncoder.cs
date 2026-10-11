using System.Globalization;
using Iced.Intel;
using static Iced.Intel.AssemblerRegisters;

namespace Ra3Trainer.Core.Patching;

internal static partial class RestoreAssemblyEncoder
{
    public static byte[] Encode(IReadOnlyList<string> assembly)
    {
        var assembler = new Assembler(32);
        foreach (var instruction in assembly)
        {
            try
            {
                Emit(assembler, instruction.Trim());
            }
            catch (IndexOutOfRangeException ex)
            {
                // 各处直接索引 Operands[0]/[1]：清单里某条指令少了操作数时，
                // 原来只抛 IndexOutOfRange，看不出是哪一条。
                throw new InvalidOperationException($"原始汇编 '{instruction.Trim()}' 的操作数与预期不符（缺少操作数）。", ex);
            }
        }

        using var stream = new MemoryStream();
        assembler.Assemble(new StreamCodeWriter(stream), 0);
        return stream.ToArray();
    }

    private static void Emit(Assembler assembler, string instruction)
    {
        var normalized = instruction.ToLowerInvariant();
        if (normalized == "nop")
        {
            assembler.nop();
            return;
        }

        var parts = SplitInstruction(instruction);
        switch (parts.Mnemonic)
        {
            case "lea":
                assembler.lea(Register32(parts.Operands[0]), Memory(parts.Operands[1]));
                return;
            case "pop":
                assembler.pop(Register32(parts.Operands[0]));
                return;
            case "add":
                assembler.add(Register32(parts.Operands[0]), Memory(parts.Operands[1]));
                return;
            case "cmp":
                if (IsMemory(parts.Operands[0]))
                {
                    assembler.cmp(Memory(parts.Operands[0]), Register32(parts.Operands[1]));
                    return;
                }
                if (IsMemory(parts.Operands[1]))
                {
                    assembler.cmp(Register32(parts.Operands[0]), Memory(parts.Operands[1]));
                    return;
                }
                EmitCmpRegisterRegister(assembler, parts.Operands[0], parts.Operands[1]);
                return;
            case "cvttss2si":
                assembler.cvttss2si(Register32(parts.Operands[0]), Memory(parts.Operands[1]));
                return;
            case "fld":
                assembler.fld(DwordMemory(parts.Operands[0]));
                return;
            case "mov":
                EmitMov(assembler, parts.Operands);
                return;
            case "movss":
                EmitMovss(assembler, parts.Operands);
                return;
            case "sub":
                assembler.sub(Register32(parts.Operands[0]), Memory(parts.Operands[1]));
                return;
            case "test":
                assembler.test(Register32(parts.Operands[0]), Register32(parts.Operands[1]));
                return;
            default:
                throw new NotSupportedException($"Unsupported restore instruction '{instruction}'.");
        }
    }

    private static void EmitMov(Assembler assembler, IReadOnlyList<string> operands)
    {
        if (IsMemory(operands[0]))
        {
            assembler.mov(Memory(operands[0]), Register32(operands[1]));
            return;
        }

        if (!IsMemory(operands[1]))
        {
            EmitMovRegisterRegister(assembler, operands[0], operands[1]);
            return;
        }

        assembler.mov(Register32(operands[0]), Memory(operands[1]));
    }

    private static void EmitMovss(Assembler assembler, IReadOnlyList<string> operands)
    {
        if (IsMemory(operands[0]))
        {
            assembler.movss(Memory(operands[0]), RegisterXmm(operands[1]));
            return;
        }

        assembler.movss(RegisterXmm(operands[0]), Memory(operands[1]));
    }

    private static void EmitCmpRegisterRegister(Assembler assembler, string destination, string source)
    {
        // RA3's original bytes use CMP r32,r/m32 (3B /r); Iced's default emits the equivalent 39 /r form.
        var modRm = 0xC0 | (RegisterIndex(destination) << 3) | RegisterIndex(source);
        assembler.db(0x3B, (byte)modRm);
    }

    private static void EmitMovRegisterRegister(Assembler assembler, string destination, string source)
    {
        // RA3's original bytes use MOV r32,r/m32 (8B /r); Iced's default may emit the equivalent 89 /r form.
        var modRm = 0xC0 | (RegisterIndex(destination) << 3) | RegisterIndex(source);
        assembler.db(0x8B, (byte)modRm);
    }

    private static (string Mnemonic, IReadOnlyList<string> Operands) SplitInstruction(string instruction)
    {
        var firstSpace = instruction.IndexOf(' ');
        if (firstSpace < 0)
        {
            return (instruction.ToLowerInvariant(), Array.Empty<string>());
        }

        var mnemonic = instruction[..firstSpace].ToLowerInvariant();
        var operands = instruction[(firstSpace + 1)..]
            .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        return (mnemonic, operands);
    }

    private static bool IsMemory(string operand) => operand.TrimStart().StartsWith("[", StringComparison.Ordinal);

    private static AssemblerMemoryOperand Memory(string operand)
    {
        var body = operand.Trim();
        body = body.Replace("dword ptr ", "", StringComparison.OrdinalIgnoreCase);
        if (!body.StartsWith("[", StringComparison.Ordinal) || !body.EndsWith("]", StringComparison.Ordinal))
        {
            throw new NotSupportedException($"Unsupported memory operand '{operand}'.");
        }

        body = body[1..^1].Trim();
        if (body.Contains('*', StringComparison.Ordinal))
        {
            return IndexedMemory(body);
        }

        var signIndex = body.IndexOfAny(['+', '-']);
        if (signIndex < 0)
        {
            return __[Register32(body)];
        }

        var register = body[..signIndex].Trim();
        var displacement = ParseNumber(body[(signIndex + 1)..]);
        if (body[signIndex] == '-')
        {
            displacement = -displacement;
        }

        return Register32(register) + displacement;
    }

    private static AssemblerMemoryOperand DwordMemory(string operand)
    {
        return __dword_ptr[Memory(operand)];
    }

    private static AssemblerMemoryOperand IndexedMemory(string body)
    {
        var plusIndex = body.IndexOf('+');
        var baseRegister = Register32(body[..plusIndex].Trim());
        var rest = body[(plusIndex + 1)..];
        // 索引部分可能是 "edx*4"，也可能带位移："[ecx+edx*4+8]"。
        // 原来的 Split('*') 会把 "4+8" 整个当比例去 int.Parse ⇒ FormatException（合法操作数被判为不支持）。
        var scaleIndex = rest.IndexOf('*', StringComparison.Ordinal);
        if (scaleIndex < 0)
        {
            throw new NotSupportedException($"Unsupported indexed memory operand '{body}'.");
        }

        var indexRegister = Register32(rest[..scaleIndex].Trim());
        var tail = rest[(scaleIndex + 1)..];
        var displacementIndex = tail.IndexOfAny(['+', '-']);
        var scaleText = displacementIndex < 0 ? tail : tail[..displacementIndex];
        var operand = baseRegister + indexRegister * int.Parse(scaleText.Trim(), CultureInfo.InvariantCulture);
        if (displacementIndex < 0)
        {
            return operand;
        }

        var displacementText = tail[displacementIndex..];
        var displacement = ParseNumber(displacementText[1..]);
        if (displacementText[0] == '-')
        {
            displacement = -displacement;
        }

        return operand + displacement;
    }

    private static AssemblerRegister32 Register32(string name)
    {
        return name.Trim().ToLowerInvariant() switch
        {
            "eax" => eax,
            "ebx" => ebx,
            "ecx" => ecx,
            "edx" => edx,
            "esi" => esi,
            "edi" => edi,
            "esp" => esp,
            "ebp" => ebp,
            _ => throw new NotSupportedException($"Unsupported 32-bit register '{name}'.")
        };
    }

    private static int RegisterIndex(string name)
    {
        return name.Trim().ToLowerInvariant() switch
        {
            "eax" => 0,
            "ecx" => 1,
            "edx" => 2,
            "ebx" => 3,
            "esp" => 4,
            "ebp" => 5,
            "esi" => 6,
            "edi" => 7,
            _ => throw new NotSupportedException($"Unsupported 32-bit register '{name}'.")
        };
    }

    private static AssemblerRegisterXMM RegisterXmm(string name)
    {
        return name.Trim().ToLowerInvariant() switch
        {
            "xmm0" => xmm0,
            // 只有 xmm0 时，任何用到 xmm1..xmm7 的原始汇编都会直接抛 NotSupported（今天清单里恰好只用 xmm0，
            // 但真机反汇编里 movss xmm1,[...] 这类很常见，补齐后至少能正确编码而不是拒绝）。
            "xmm1" => xmm1,
            "xmm2" => xmm2,
            "xmm3" => xmm3,
            "xmm4" => xmm4,
            "xmm5" => xmm5,
            "xmm6" => xmm6,
            "xmm7" => xmm7,
            _ => throw new NotSupportedException($"Unsupported XMM register '{name}'.")
        };
    }

    private static int ParseNumber(string value)
    {
        var normalized = value.Trim();
        return int.Parse(normalized, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
    }
}
