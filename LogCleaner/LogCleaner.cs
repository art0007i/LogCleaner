using HarmonyLib;
using ResoniteModLoader;
using System;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using System.Collections.Generic;
using FrooxEngine;
using System.Reflection.Emit;
using Elements.Core;
using SkyFrost.Base;
using System.Runtime.CompilerServices;

namespace LogCleaner
{
    public class LogCleaner : ResoniteMod
    {
        public override string Name => "LogCleaner";
        public override string Author => "art0007i";
        public override string Version => "1.1.2";
        public override string Link => "https://github.com/art0007i/LogCleaner/";

        public static HashSet<MethodInfo> logMethods = new HashSet<MethodInfo>
        {
            typeof(UniLog).GetMethod("Log", new Type[] { typeof(string), typeof(bool) }),
            typeof(UniLog).GetMethod("Log", new Type[] { typeof(object), typeof(bool) }),
            typeof(UniLog).GetMethod("Warning"),
            typeof(UniLog).GetMethod("Error")
        };

        // functions in here will still print messages, but not stack traces
        public static HashSet<MethodInfo> removeStackTrace = new HashSet<MethodInfo>
        {
            // This happens whenever you change name or description of a world (often triggered without interaction)
            AccessTools.Method(typeof(WorldConfiguration), "FieldChanged"),
        };

        // functions in here will be completely silenced
        public static HashSet<MethodInfo> removeLog = new HashSet<MethodInfo>
        {
            // Very spammy and doesn't seem like it has any useful info
            AccessTools.Method(typeof(ContactData), "UpdateStatus"),
            AccessTools.Method(typeof(ContactData), "ClearExpired"),
            AccessTools.Method(typeof(UserStatusManager), "SendStatusToUser"),
            FindAsyncBody(AccessTools.Method(typeof(AppHub), "BroadcastStatus")),
        };

        public static MethodInfo FindAsyncBody(MethodInfo mi)
        {
            AsyncStateMachineAttribute asyncAttribute = (AsyncStateMachineAttribute)mi.GetCustomAttribute(typeof(AsyncStateMachineAttribute));
            Type asyncStateMachineType = asyncAttribute.StateMachineType;
            return AccessTools.Method(asyncStateMachineType, nameof(IAsyncStateMachine.MoveNext));
        }

        public override void OnEngineInit()
        {
            Harmony harmony = new Harmony("me.art0007i.LogCleaner");

            Debug("Patching stack trace funcs");
            // Remove stack traces
            MethodInfo transpiler = typeof(StackTraceFixerPatch).GetMethod(nameof(StackTraceFixerPatch.Transpiler));
            foreach (MethodInfo method in removeStackTrace)
            {
                Debug("Patching method " + method);
                try
                {
                    harmony.Patch(method, transpiler: new HarmonyMethod(transpiler));
                }
                catch (Exception e)
                {
                    Debug("  error in patch for " + method);
                    Debug(e);
                }
            }

            Debug("Patching remove log funcs");
            // Remove log methods
            MethodInfo transpiler2 = typeof(RemoveLogMethodPatch).GetMethod(nameof(RemoveLogMethodPatch.Transpiler));
            foreach (MethodInfo method in removeLog)
            {
                Debug("Patching method " + method);
                try
                {
                    harmony.Patch(method, transpiler: new HarmonyMethod(transpiler2));
                }
                catch (Exception e)
                {
                    Debug("  error in patch for " + method);
                    Debug(e);
                }
            }

            // --- Extra silencers ---

            // Drop "Running refresh on: OwnerId: ..., Path: ..." spam.
            // We don't pre-scan IL. Instead, we attach a lightweight transpiler to methods in FrooxEngine
            // that will NO-OP UniLog.Log(...) only if the method actually loads the target literal.
            try
            {
                var feAsm = typeof(ContactData).Assembly; // pass Assembly, not Type
                var dropTranspiler = new HarmonyMethod(typeof(DropSpecificLogPatch).GetMethod(nameof(DropSpecificLogPatch.Transpiler)));

                foreach (var t in AccessTools.GetTypesFromAssembly(feAsm))
                {
                    MethodInfo[] methods;
                    try
                    {
                        methods = t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static);
                    }
                    catch
                    {
                        continue;
                    }

                    foreach (var m in methods)
                    {
                        // Only patch methods that have bodies
                        if (m == null || m.IsAbstract) continue;
                        try
                        {
                            if (m.GetMethodBody() == null) continue;
                        }
                        catch
                        {
                            continue;
                        }

                        try
                        {
                            harmony.Patch(m, transpiler: dropTranspiler);
                        }
                        catch
                        {
                            // best-effort patching; ignore per-method errors
                        }
                    }
                }
                Debug("Applied conditional refresh-spam suppressor to FrooxEngine methods.");
            }
            catch (Exception e)
            {
                Debug("Failed to attach refresh-spam suppressor: " + e);
            }

            // SignalR BroadcastSession spam is already handled by removeLog (BroadcastStatus async body).
        }

        class StackTraceFixerPatch
        {
            public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> codes)
            {
                List<CodeInstruction> instructions = new List<CodeInstruction>(codes);
                for (int i = 0; i < instructions.Count - 1; i++)
                {
                    // detect integer one (aka true) on stack followed by a call
                    if (instructions[i].opcode == OpCodes.Ldc_I4_1
                        && instructions[i + 1].opcode == OpCodes.Call)
                    {
                        // Is it a UniLog method?
                        if (logMethods.Contains(instructions[i + 1].operand))
                        {
                            // Make it not print a stack trace
                            instructions[i].opcode = OpCodes.Ldc_I4_0;

                            Debug($"Found a {instructions[i + 1].operand}");
                        }
                    }
                }
                Debug("Done!");
                return instructions;
            }
        }

        class RemoveLogMethodPatch
        {
            public static void FakeLog(string message, bool stackTrace) { }
            public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> codes)
            {
                var patching = typeof(RemoveLogMethodPatch).GetMethod(nameof(FakeLog));
                foreach (var code in codes)
                {
                    if (logMethods.Contains(code.operand))
                    {
                        yield return new(OpCodes.Call, patching);
                    }
                    else
                    {
                        yield return code;
                    }
                }
            }
        }

        // Conditional drop: only nop-out UniLog.Log(...) when the method pushes a literal that contains "Running refresh on:"
        class DropSpecificLogPatch
        {
            public static void FakeLog(string message, bool stackTrace) { }

            public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
            {
                var list = new List<CodeInstruction>(instructions);
                var fake = typeof(DropSpecificLogPatch).GetMethod(nameof(FakeLog));

                bool suppressInThisMethod = false;
                for (int i = 0; i < list.Count; i++)
                {
                    var ci = list[i];

                    // If this method loads our literal anywhere, enable suppression for this method
                    if (ci.opcode == OpCodes.Ldstr && ci.operand is string s && s.Contains("Running refresh on:"))
                    {
                        suppressInThisMethod = true;
                    }

                    if (suppressInThisMethod && logMethods.Contains(ci.operand))
                    {
                        // replace UniLog.Log(...) call with no-op
                        yield return new CodeInstruction(OpCodes.Call, fake);
                    }
                    else
                    {
                        yield return ci;
                    }
                }
            }
        }
    }
}
