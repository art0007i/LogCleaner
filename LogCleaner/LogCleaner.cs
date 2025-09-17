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
using System.Text.RegularExpressions; // NEW: for simple message pattern matching

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

            // NEW: many long “OrderOffset … driven by Target …” warnings originate here
            AccessTools.Method(typeof(FrooxEngine.SyncElement), "BeginModification", new Type[] { typeof(bool) }),
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

        public static MethodInfo FindAsyncBody (MethodInfo mi)
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

            // NEW: targeted filter at UniLog so we can drop or de-stacktrace specific messages
            try
            {
                harmony.PatchAll(typeof(FilteredUniLogPatch));
            }
            catch (Exception e)
            {
                Debug("  error enabling FilteredUniLogPatch");
                Debug(e);
            }
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

        // NEW: Small, surgical filter that
        //  - drops "Running refresh on:" lines completely
        //  - keeps "OrderOffset ... driven by Target ..." but without stack traces
        [HarmonyPatch]
        static class FilteredUniLogPatch
        {
            static readonly Regex[] Drop =
            {
                new(@"^Running refresh on:", RegexOptions.Compiled),
            };

            static readonly Regex[] NoStack =
            {
                new(@"^The OrderOffset .* is currently being driven by Target", RegexOptions.Compiled),
            };

            static bool ShouldDrop(string s) => s != null && Array.Exists(Drop, r => r.IsMatch(s));
            static bool ShouldNoStack(string s) => s != null && Array.Exists(NoStack, r => r.IsMatch(s));

            [HarmonyPrefix]
            [HarmonyPatch(typeof(UniLog), nameof(UniLog.Log), new[] { typeof(string), typeof(bool) })]
            static bool Log_Prefix(ref string message, ref bool stackTrace)
            {
                if (ShouldDrop(message)) return false;
                if (ShouldNoStack(message)) stackTrace = false;
                return true;
            }

            [HarmonyPrefix]
            [HarmonyPatch(typeof(UniLog), nameof(UniLog.Warning), new[] { typeof(string), typeof(bool) })]
            static bool Warning_Prefix(ref string message, ref bool stackTrace)
            {
                if (ShouldDrop(message)) return false;
                if (ShouldNoStack(message)) stackTrace = false;
                return true;
            }

            [HarmonyPrefix]
            [HarmonyPatch(typeof(UniLog), nameof(UniLog.Log), new[] { typeof(object), typeof(bool) })]
            static bool LogObj_Prefix(ref object message, ref bool stackTrace)
            {
                var s = message?.ToString();
                if (ShouldDrop(s)) return false;
                if (ShouldNoStack(s)) stackTrace = false;
                return true;
            }
        }
    }
}
