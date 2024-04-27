using Elements.Core;
using FrooxEngine;
using HarmonyLib;
using ResoniteModLoader;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;

namespace LogCleaner
{
	public class LogCleaner : ResoniteMod
	{
		public override string Name => "LogCleaner";
		public override string Author => "art0007i";
		public override string Version => "1.1.0";
		public override string Link => "https://github.com/art0007i/LogCleaner/";

		public static HashSet<MethodInfo> logMethods = new HashSet<MethodInfo>
		{
			typeof(UniLog).GetMethod("Log", new Type[] { typeof(string), typeof(bool) }),
			typeof(UniLog).GetMethod("Log", new Type[] { typeof(object), typeof(bool) }),
			typeof(UniLog).GetMethod("Warning"),
			typeof(UniLog).GetMethod("Error")
		};

		public static Type[] ctxMenuTypes =
		{
			typeof(AssetToolReplacementMode),
			typeof(DevTool.Selection),
			typeof(DevTool.Interaction),
			typeof(LightTool.Mode),
			typeof(ShadowType),
			typeof(MeterTool.Mode),
			typeof(AssetToolReplacementMode),
			typeof(bool),
			typeof(CameraControlTool.CameraMode),
			typeof(Glue.Mode),
			typeof(MicrophoneTool.RecordFormat),
			typeof(MicrophoneTool.RecordMode),
			typeof(MicrophoneTool.DataSource),
			typeof(VolumePlaneMode),
			typeof(ShadowCastMode)
		};

		public static HashSet<MethodInfo> toPatch = new HashSet<MethodInfo>
		{
            // This happens whenever you change name or description of a world (often triggered without interaction)
            AccessTools.Method(typeof(WorldConfiguration), "FieldChanged"),

            // if you want more stack traces to be stopped just add them here!
        };

		public override void OnEngineInit()
		{


			Harmony harmony = new Harmony("me.art0007i.LogCleaner");
			//harmony.PatchAll();
			var GenericToPatch = typeof(RadialMenuItemExtensions).GetMethod(nameof(RadialMenuItemExtensions.AttachOptionDescriptionDriver));
			var patcherMethod = new HarmonyMethod(AccessTools.Method(typeof(ContextMenuOpenPatch), nameof(ContextMenuOpenPatch.Transpiler)));
			foreach (var item in ctxMenuTypes)
			{
				var toPatch = GenericToPatch.MakeGenericMethod(item);
				harmony.Patch(toPatch, transpiler: patcherMethod);
			}

			MethodInfo transpiler = typeof(StackTraceFixerPatch).GetMethod(nameof(StackTraceFixerPatch.Transpiler));

			foreach (MethodInfo method in toPatch)
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

			var clearExpiredOrig = AccessTools.Method(typeof(SkyFrost.Base.ContactData), "ClearExpired");
			var updateStatusOrig = AccessTools.Method(typeof(SkyFrost.Base.ContactData), "UpdateStatus");
			var contactSpamRemoverTranspiler = new HarmonyMethod(AccessTools.Method(typeof(ContactSpamRemoverPatch), nameof(ContactSpamRemoverPatch.Transpiler)));
			harmony.Patch(clearExpiredOrig, transpiler: contactSpamRemoverTranspiler);
			harmony.Patch(updateStatusOrig, transpiler: contactSpamRemoverTranspiler);
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

		class ContextMenuOpenPatch
		{
			public static void SafeSetTarget(ISyncRef driver, ISyncRef target)
			{
				if (!target.IsLinked) driver.Target = target;
			}

			public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> codes)
			{
				foreach (var code in codes)
				{
					if (code.operand is MethodInfo mf && mf.Name == "set_Target")
					{
						yield return new(OpCodes.Call, typeof(ContextMenuOpenPatch).GetMethod(nameof(ContextMenuOpenPatch.SafeSetTarget)));
					}
					else
					{
						yield return code;
					}
				}
			}
		}

		class ContactSpamRemoverPatch
		{
			static List<string> strList = new List<string>
			{
				"Clearing expired status for contact {0}.\nStatus: {1}\nTotal statuses: {2}",
				"Status before clearing: {0}",
				"Status after clearing: {0}",
				"Received status update that's already expired:\n"
			};
			public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
			{
				bool skip = false;
				var codes = new List<CodeInstruction>(instructions);

				for (var i = 0; i < codes.Count; i++)
				{
					//Debug(i.ToString());
					//Debug(codes[i].ToString());
					if (!skip && codes[i].opcode == OpCodes.Ldstr && codes[i].operand is string str && strList.Contains(str))
					{
						Debug("Found contact spam string. Skipping next codes.");
						skip = true;
					}
					if (skip)
					{
						if (codes[i].opcode == OpCodes.Call && logMethods.Contains(codes[i].operand))
						{
							Debug("Found contact spam log method call. Stop skipping codes.");
							skip = false;
						}
						codes[i].opcode = OpCodes.Nop;
					}
				}

				return codes.AsEnumerable();
			}
		}
	}
}