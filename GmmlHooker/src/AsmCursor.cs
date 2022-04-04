using System.Diagnostics.CodeAnalysis;

using UndertaleModLib;
using UndertaleModLib.Decompiler;
using UndertaleModLib.Models;

namespace GmmlHooker;

// totally not stolen from MonoMod
public class AsmCursor {
    public int index {
        get => _index;
        set {
            if(value < 0 || value > _code.Instructions.Count) return;
            _index = value;
        }
    }

    private int _index;

    private readonly UndertaleData _data;
    private readonly UndertaleCode _code;
    private readonly Dictionary<string, UndertaleVariable> _locals;
    private readonly Dictionary<string, UndertaleInstruction> _labels = new();
    private readonly Dictionary<UndertaleInstruction, string> _labelTargets = new();

    public AsmCursor(UndertaleData data, UndertaleCode code, UndertaleCodeLocals locals) {
        _data = data;
        _code = code;
        _locals = Hooker.GetLocalVars(data, locals);
    }

    public UndertaleInstruction GetCurrent() => _code.Instructions[index];

    public void Emit(UndertaleInstruction instruction) {
        _code.Instructions.Insert(index, instruction);
        _code.UpdateAddresses();
        index++;
    }

    public void Emit(string source) => Emit(Assemble(source));

    public void Replace(UndertaleInstruction instruction) {
        _code.Instructions[index] = instruction;
        _code.UpdateAddresses();
        index++;
    }

    public void Replace(string source) => Replace(Assemble(source));

    public void DefineLabel(string name) => _labels.Add(name, GetCurrent());

    public void GotoFirst(string match) => GotoFirst(Assemble(match));
    public void GotoLast(string match) => GotoLast(Assemble(match));
    public void GotoNext(string match) => GotoNext(Assemble(match));
    public void GotoPrev(string match) => GotoPrev(Assemble(match));

    public void GotoFirst(UndertaleInstruction match) => GotoFirst(instruction => instruction.Match(match));
    public void GotoLast(UndertaleInstruction match) => GotoLast(instruction => instruction.Match(match));
    public void GotoNext(UndertaleInstruction match) => GotoNext(instruction => instruction.Match(match));
    public void GotoPrev(UndertaleInstruction match) => GotoPrev(instruction => instruction.Match(match));

    public void GotoFirst(Predicate<UndertaleInstruction> match) => index = _code.Instructions.FindIndex(match);
    public void GotoLast(Predicate<UndertaleInstruction> match) => index = _code.Instructions.FindLastIndex(match);
    public void GotoNext(Predicate<UndertaleInstruction> match) => index = _code.Instructions.FindIndex(index, match);
    public void GotoPrev(Predicate<UndertaleInstruction> match) =>
        index = _code.Instructions.FindLastIndex(0, index - 1, match);

    public void Finish() {
        _code.UpdateAddresses();
        foreach((UndertaleInstruction? instruction, string? label) in _labelTargets)
            instruction.JumpOffset = (int)_labels[label].Address - (int)instruction.Address;
    }

    private UndertaleInstruction Assemble(string source) {
        UndertaleInstruction instruction = Assembler.AssembleOne(source, _data.Functions, _data.Variables,
            _data.Strings, _locals, out string? label, _data);

        if(label is not null)
            _labelTargets.Add(instruction, label);

        return instruction;
    }
}