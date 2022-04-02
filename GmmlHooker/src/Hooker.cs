﻿using GmmlPatcher;

using UndertaleModLib;
using UndertaleModLib.Decompiler;
using UndertaleModLib.Models;

namespace GmmlHooker;

// ReSharper disable once ClassNeverInstantiated.Global
public class Hooker : IGameMakerMod {
    public void Load(int audioGroup, UndertaleData data, IReadOnlyList<ModMetadata> availableDependencies,
        IEnumerable<ModMetadata> queuedMods) { /* TODO: define hooks in JSON? maybe? */ }

    public static void ReplaceGmlSafe(UndertaleCode code, string gmlCode, UndertaleData data) {
        try {
            code.ReplaceGML(gmlCode, data);
        }
        // UndertaleModLib is trying to write profile cache but fails, we don't care
        catch(Exception) { /* ignored */ }
    }

    public static void AppendGmlSafe(UndertaleCode code, string gmlCode, UndertaleData data) {
        try {
            code.AppendGML(gmlCode, data);
        }
        // UndertaleModLib is trying to write profile cache but fails, we don't care
        catch(Exception) { /* ignored */ }
    }

    public static UndertaleCode CreateCode(UndertaleData data, UndertaleString name, out UndertaleCodeLocals locals) {
        locals = new UndertaleCodeLocals {
            Name = name
        };
        locals.Locals.Add(new UndertaleCodeLocals.LocalVar {
            Name = data.Strings.MakeString("arguments"),
            Index = 2
        });
        data.CodeLocals.Add(locals);

        UndertaleCode mainCode = new() {
            Name = name,
            LocalsCount = 1,
            ArgumentsCount = 0
        };
        data.Code.Add(mainCode);

        return mainCode;
    }

    public static UndertaleScript CreateSimpleScript(UndertaleData data, string name, string code) {
        UndertaleString mainName = data.Strings.MakeString(name);
        UndertaleCode mainCode = CreateCode(data, mainName, out _);

        ReplaceGmlSafe(mainCode, code, data);

        UndertaleScript script = new() {
            Name = mainName,
            Code = mainCode
        };
        data.Scripts.Add(script);

        return script;
    }

    public static UndertaleScript CreateScript(UndertaleData data, string name, string code, ushort argCount) {
        UndertaleString mainName = data.Strings.MakeString(name);
        UndertaleString scriptName = data.Strings.MakeString($"gml_Script_{name}", out int scriptNameIndex);
        UndertaleString globalName = data.Strings.MakeString($"gml_GlobalScript_{name}");

        data.Variables.EnsureDefined(name, UndertaleInstruction.InstanceType.Self, false, data.Strings, data);

        UndertaleFunction scriptFunction = new() {
            Name = scriptName,
            NameStringID = scriptNameIndex,
            Autogenerated = true
        };
        data.Functions.Add(scriptFunction);

        UndertaleCode globalCode = CreateCode(data, globalName, out UndertaleCodeLocals globalLocals);

        UndertaleCode scriptCode = new() {
            Name = scriptName,
            LocalsCount = 0,
            ArgumentsCount = argCount,
            ParentEntry = globalCode
        };
        globalCode.ChildEntries.Add(scriptCode);
        data.Code.Add(scriptCode);

        data.GlobalInitScripts.Add(new UndertaleGlobalInit {
            Code = globalCode
        });

        ReplaceWithScriptDefinition(data, name, code, globalCode, globalLocals, scriptName.Content);

        UndertaleScript script = new() {
            Name = mainName,
            Code = globalCode
        };
        data.Scripts.Add(script);

        UndertaleScript intermediaryScript = new() {
            Name = scriptName,
            Code = scriptCode
        };
        data.Scripts.Add(intermediaryScript);

        return script;
    }

    private static void ReplaceWithScriptDefinition(UndertaleData data, string name, string gmlCode, UndertaleCode code,
        UndertaleCodeLocals locals, string intermediaryName) {
        ReplaceGmlSafe(code, gmlCode, data);
        code.Replace(Assembler.Assemble(@$"
b [func_def]

{code.Disassemble(data.Variables, locals).Replace("\n:[end]", "")}

exit.i

:[func_def]
push.i {intermediaryName}
conv.i.v
pushi.e -1
conv.i.v
call.i method(argc=2)
dup.v 0
pushi.e -1
pop.v.v [stacktop]self.{name}
popz.v

:[end]", data));
    }

    public static UndertaleCode CloneCode(UndertaleData data, string cloneName, UndertaleCode cloning,
        UndertaleCodeLocals cloningLocals, out UndertaleCodeLocals localsClone) {
        UndertaleCode codeClone = new() {
            Name = data.Strings.MakeString(cloneName),
            LocalsCount = cloning.LocalsCount,
            ArgumentsCount = cloning.ArgumentsCount,
            WeirdLocalsFlag = cloning.WeirdLocalsFlag,
            WeirdLocalFlag = cloning.WeirdLocalFlag
        };
        codeClone.Replace(cloning.Instructions);
        data.Code.Add(codeClone);
        data.Scripts.Add(new UndertaleScript {
            Name = codeClone.Name,
            Code = codeClone
        });

        localsClone = new UndertaleCodeLocals {
            Name = codeClone.Name
        };
        foreach(UndertaleCodeLocals.LocalVar localVar in cloningLocals.Locals)
            localsClone.Locals.Add(new UndertaleCodeLocals.LocalVar {
                Name = localVar.Name,
                Index = localVar.Index
            });
        data.CodeLocals.Add(localsClone);

        return codeClone;
    }

    public static void HookCode(UndertaleData data, string code, string hook) {
        UndertaleCode hookedCode = data.Code.ByName(code);
        UndertaleCodeLocals hookedCodeLocals = data.CodeLocals.ByName(code);

        string originalName = $"gmml_{hookedCode.Name.Content}_orig_{Guid.NewGuid().ToString().Replace('-', '_')}";

        CloneCode(data, originalName, hookedCode, hookedCodeLocals, out _);

        ReplaceGmlSafe(hookedCode, hook.Replace("#orig#", $"{originalName}"), data);
    }

    public static void HookScript(UndertaleData data, string script, string hook) {
        UndertaleScript hookedScript = data.Scripts.ByName(script);
        UndertaleCode hookedCode = hookedScript.Code;
        UndertaleCodeLocals hookedCodeLocals = data.CodeLocals.ByName(hookedCode.Name.Content);

        string originalName = $"gmml_{hookedCode.Name.Content}_orig_{Guid.NewGuid().ToString().Replace('-', '_')}";

        UndertaleCode originalCodeClone =
            CloneCode(data, originalName, hookedCode, hookedCodeLocals, out _);

        // remove the function definition stuff
        originalCodeClone.Instructions.RemoveAt(0);
        originalCodeClone.Instructions.RemoveRange(originalCodeClone.Instructions.Count - 10, 10);

        ReplaceWithScriptDefinition(data, script, hook.Replace("#orig#", $"{originalName}"), hookedCode,
            hookedCodeLocals, $"gml_Script_{script}");
    }
}