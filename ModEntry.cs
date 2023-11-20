using HarmonyLib;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewModdingAPI;
using StardewValley;
using StardewValley.GameData.Characters;
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;

namespace ichortower.NPCGeometry
{
    internal sealed class ModEntry : Mod
    {
        public static IMonitor MONITOR;
        public static IModHelper HELPER;
        public static string Prefix = "ichortower.NPCGeometry";


        public override void Entry(IModHelper helper)
        {
            ModEntry.MONITOR = this.Monitor;
            ModEntry.HELPER = helper;
            var harmony = new Harmony(this.ModManifest.UniqueID);
            harmony.Patch(
                original: typeof(NPC).GetMethod("reloadSprite",
                    BindingFlags.Instance | BindingFlags.Public),
                postfix: new HarmonyMethod(typeof(ModEntry),
                    "NPC_reloadSprite__Postfix")
            );
            harmony.Patch(
                original: typeof(Character).GetMethod("DrawShadow",
                    BindingFlags.Instance | BindingFlags.Public),
                transpiler: new HarmonyMethod(typeof(ModEntry),
                    "Character_DrawShadow__Transpiler")
            );
            harmony.Patch(
                original: typeof(NPC).GetMethod("draw",
                    BindingFlags.Instance | BindingFlags.Public,
                    new Type[]{typeof(SpriteBatch), typeof(float)}),
                transpiler: new HarmonyMethod(typeof(ModEntry),
                    "NPC_draw__Transpiler")
            );
            helper.ConsoleCommands.Add("pxpos", "\nDumps values of Position and getLocalPosition for named NPC.", this.pxpos);
        }

        public void pxpos(string command, string[] args)
        {
            if (args.Length < 1) {
                this.Monitor.Log($"Usage: pxpos <name>", LogLevel.Warn);
                return;
            }
            NPC who = Game1.getCharacterFromName(args[0]);
            if (who is null) {
                this.Monitor.Log($"Couldn't find NPC called '{args[0]}'", LogLevel.Warn);
                return;
            }
            Vector2 p = who.Position;
            this.Monitor.Log($"{args[0]}.Position: ({p.X}, {p.Y})", LogLevel.Info);
            Vector2 localp = who.getLocalPosition(Game1.viewport);
            this.Monitor.Log($"{args[0]} getLocalPosition: ({localp.X}, {localp.Y})", LogLevel.Info);
        }

        /*
         * I couldn't get ldfld to work on CharacterData::CustomFields, so the
         * custom field check got offloaded to C#.
         */
        public static string GetCustomFieldValue(Character who, string key)
        {
            string val;
            if ((who as NPC)?.GetData()?.CustomFields?
                    .TryGetValue(key, out val) == true) {
                return val;
            }
            return null;
        }

        /*
         * Likewise, this helper function offloads a bunch of locals and so on
         * into C#.
         */
        public static bool TryParseBreatheRect(string val,
                ref Microsoft.Xna.Framework.Rectangle rect)
        {
            string[] split = val.Split("/");
            int x, y, width, height;
            if (split.Length < 4) {
                return false;
            }
            if (!int.TryParse(split[0], out x)) {
                return false;
            }
            if (!int.TryParse(split[1], out y)) {
                return false;
            }
            if (!int.TryParse(split[2], out width)) {
                return false;
            }
            if (!int.TryParse(split[3], out height)) {
                return false;
            }
            rect.X = x;
            rect.Y = y;
            rect.Width = width;
            rect.Height = height;
            return true;
        }

        public static IEnumerable<CodeInstruction> Character_DrawShadow__Transpiler(
                IEnumerable<CodeInstruction> instructions,
                ILGenerator generator,
                MethodBase original)
        {
            LocalBuilder shadowScale = generator.DeclareLocal(typeof(float));
            LocalBuilder stringVal = generator.DeclareLocal(typeof(string));
            Label startOfOriginalCode = generator.DefineLabel();
            var codes = new List<CodeInstruction>(instructions);
            codes[0].labels.Add(startOfOriginalCode);
            /*
             * first, use the ShadowScale custom field to set our float.
             * it should end up as 1.0 if the field isn't found.
             * this chunk goes before the rest of the function.
             */
            var fieldChecker = new List<CodeInstruction>(){
                new(OpCodes.Ldc_R4, 1.0f),
                new(OpCodes.Stloc, shadowScale),
                new(OpCodes.Ldarg_0),
                new(OpCodes.Ldstr, Prefix + "/ShadowScale"),
                new(OpCodes.Call, typeof(ModEntry).GetMethod("GetCustomFieldValue",
                        BindingFlags.Public | BindingFlags.Static)),
                new(OpCodes.Stloc, stringVal),
                new(OpCodes.Ldloc, stringVal),
                new(OpCodes.Brfalse, startOfOriginalCode),
                new(OpCodes.Ldloc, stringVal),
                new(OpCodes.Ldloca, shadowScale),
                new(OpCodes.Call, typeof(System.Single).GetMethod("TryParse",
                        BindingFlags.Public | BindingFlags.Static,
                        new Type[]{typeof(string), typeof(float).MakeByRefType()})),
                new(OpCodes.Pop),
            };
            codes.InsertRange(0, fieldChecker);
            /*
             * now, we search for the setup for the draw call. we want to
             * multiply the shadow's scale parameter by our float.
             * this looks for the next callvirt after the 40f. may be safer
             * if we check for the get_Value on the NetFloat?
             */
            int target = -1;
            bool forty = false;
            for (int i = 0; i < codes.Count - 1; ++i) {
                if (codes[i].opcode == OpCodes.Ldc_R4 && (float)codes[i].operand == 40f) {
                    forty = true;
                }
                if (forty && codes[i].opcode == OpCodes.Callvirt) {
                    target = i+1;
                    break;
                }
            }
            var extraMul = new List<CodeInstruction>(){
                new(OpCodes.Ldloc, shadowScale),
                new(OpCodes.Mul),
            };
            codes.InsertRange(target, extraMul);
            return codes;
        }


        public static IEnumerable<CodeInstruction> NPC_draw__Transpiler(
                IEnumerable<CodeInstruction> instructions,
                ILGenerator generator,
                MethodBase original)
        {
            LocalBuilder emoteHeight = generator.DeclareLocal(typeof(int));
            LocalBuilder emoteStringVal = generator.DeclareLocal(typeof(string));
            Label noHeightField = generator.DefineLabel();
            Label foundHeightField = generator.DefineLabel();
            //Label noBreatheRectField = generator.DefineLabel();
            //Label foundBreatheRectField = generator.DefineLabel();
            var codes = new List<CodeInstruction>(instructions);

            /* TODO: breathe rect here */

            /*
             * Inject the patch for custom emote height.
             * We want to start right after loading the constant 32 near the
             * end.
             */
            int heightTarget = -1;
            var heightInjection = new List<CodeInstruction>(){
                //new(OpCodes.Shl),
                new(OpCodes.Ldarg_0),
                new(OpCodes.Ldstr, Prefix + "/EmoteHeight"),
                new(OpCodes.Call, typeof(ModEntry).GetMethod("GetCustomFieldValue",
                        BindingFlags.Public | BindingFlags.Static)),
                new(OpCodes.Stloc, emoteStringVal),
                new(OpCodes.Ldloc, emoteStringVal),
                new(OpCodes.Brfalse, noHeightField),
                new(OpCodes.Ldloc, emoteStringVal),
                new(OpCodes.Ldloca, emoteHeight),
                new(OpCodes.Call, typeof(System.Int32).GetMethod("TryParse",
                        BindingFlags.Public | BindingFlags.Static,
                        new Type[]{typeof(string), typeof(int).MakeByRefType()})),
                new(OpCodes.Brfalse, noHeightField),
                new(OpCodes.Ldloc, emoteHeight),
                new(OpCodes.Ldc_I4_S, (SByte)4),
                new(OpCodes.Sub),
                new(OpCodes.Br_S, foundHeightField),
            };
            for (int i = codes.Count - 5; i >= 0; --i) {
                if (codes[i].opcode == OpCodes.Ldc_I4_S && codes[i].operand.Equals((SByte)32)) {
                    heightTarget = i+1;
                    codes[i+1].labels.Add(noHeightField);
                    codes[i+4].labels.Add(foundHeightField);
                    break;
                }
            }
            codes.InsertRange(heightTarget, heightInjection);
            return codes;
        }


        public static void NPC_reloadSprite__Postfix(NPC __instance)
        {
            var data = __instance.GetData();
            if (data is null) {
                return;
            }

            if (data.CustomFields?.TryGetValue(Prefix + "/Scale", out var val) == true) {
                if (float.TryParse(val, out var f)) {
                    __instance.Scale = f;
                }
            }
        }
    }
}
