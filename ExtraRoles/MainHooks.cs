﻿using HarmonyLib;
using Hazel;
using QRCoder;
using QRCoder.Unity;
using System;
using System.Collections.Generic;
using draw = System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using UnhollowerBaseLib;
using UnityEngine;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.IL2CPP;
using BepInEx.IL2CPP.UnityEngine;
using Il2CppDumper;
using InnerNet;
using Steamworks;
using System.CodeDom;
using System.Net;
using UnityEngine.SceneManagement;
using System.Runtime.InteropServices;
using Reactor;
using ExtraRolesMod;

/*
Hex colors for extra roles
Engineer: 972e00
Joker: 838383
Medic: 24b720
Officer: 0028c6

Yes, I know the code is bad. I'm going to clean up the rpc handler soon, and the engineer's sabotage clearing ability doesn't work right now because i'm in the middle of rewriting that section. 
Also, the medic shield doesn't break when the medic dies.

I can comment the code more extensively if you need me to.
*/

namespace ExtraRolesMod
{
    //Class to log who has vented and their recent times. this is used to ratelimit vent entries/exits (fixes desync bug)
    public static class PlayerVentTimeExtension
    {
        public static void SetLastVent(byte player)
        {
            if (MainHooks.allVentTimes.ContainsKey(player))
            {
                MainHooks.allVentTimes[player] = DateTime.UtcNow;
            }
            else
            {
                MainHooks.allVentTimes.Add(player, DateTime.UtcNow);
            }
        }

        public static DateTime GetLastVent(byte player)
        {
            if (MainHooks.allVentTimes.ContainsKey(player))
            {
                return MainHooks.allVentTimes[player];
            }
            else
                return new DateTime(0);
        }
    }

    //body report class for when medic reports a body
    public class BodyReport
    {
        public byte DeathReason { get; set; }
        public PlayerControl Killer { get; set; }
        public PlayerControl Reporter { get; set; }
        public float KillAge { get; set; }

        public static string ParseBodyReport(BodyReport br)
        {
            if (br.KillAge > MainHooks.MedicSettings.medicKillerColorDuration * 1000)
            {
                return $"Body Report: The body is too old to gain information from. (Killed {Math.Round(br.KillAge / 1000)}s ago)";
            }
            else if (br.DeathReason == 3)
            {
                return $"Body Report (Officer): The cause of death appears to be suicide by shooting an Innocent! (Killed {Math.Round(br.KillAge / 1000)}s ago)";

            }
            else if (br.KillAge < MainHooks.MedicSettings.medicKillerNameDuration * 1000)
            {
                return $"Body Report: The murderer appears to be {br.Killer.name}! (Killed {Math.Round(br.KillAge / 1000)}s ago)";
            }
            else
            {
                //TODO (make the type of color be written to chat
                var colors = new Dictionary<byte, string>()
                {
                    {0, "darker"},
                    {1, "darker"},
                    {2, "darker"},
                    {3, "lighter"},
                    {4, "lighter"},
                    {5, "lighter"},
                    {6, "darker"},
                    {7, "lighter"},
                    {8, "darker"},
                    {9, "darker"},
                    {10, "lighter"},
                    {11, "lighter"},
                };
                var typeOfColor = colors[br.Killer.Data.ColorId];
                return $"Body Report: The murder appears to be a {typeOfColor} color. (Killed {Math.Round(br.KillAge / 1000)}s ago)";
            }
        }
    }

    [HarmonyPatch]
    public static class MainHooks
    {
        public static Texture2D rotateTexture(Color[] originalTexture, bool clockwise, int w, int h)
        {
            Color[] original = originalTexture;
            Color[] rotated = new Color[original.Length];

            int iRotated, iOriginal;

            for (int j = 0; j < h; ++j)
            {
                for (int i = 0; i < w; ++i)
                {
                    iRotated = (i + 1) * h - j - 1;
                    iOriginal = clockwise ? original.Length - 1 - (j * w + i) : j * w + i;
                    rotated[iRotated] = original[iOriginal];
                }
            }

            Texture2D rotatedTexture = new Texture2D(h, w, TextureFormat.ARGB32, true);
            rotatedTexture.SetPixels(rotated);
            rotatedTexture.Apply();
            return rotatedTexture;
        }

        public static SpriteRenderer indicatorRenderer;
        //list of all entries/exits of vents for each player and their times (used by VentPlayerExtension)
        public static IDictionary<byte, DateTime> allVentTimes = new Dictionary<byte, DateTime>() { };
        //array of config settings
        public static string[] configSettingsKeys =
        {
            "Show Medic",
            "Show Shielded Player",
            "Murder Attempt Indicator For Shielded Player",
            "Show Officer",
            "Officer Kill Cooldown",
            "Show Engineer",
            "Show Joker",
            "Joker Can Die To Officer",
            "Duration In Which Medic Report Will Contain The Killers Name",
            "Duration In Which Medic Report Will Contain The Killers Color Type"
        };
        //array of all config settings and their values. these will be loaded by a config and sent to all clients
        public static IDictionary<string, byte> configSettings = new Dictionary<string, byte>()
        {
            { "Show Medic", 0 },
            { "Show Shielded Player", 0 },
            { "Murder Attempt Indicator For Shielded Player", 0 }, //TODO
            { "Show Officer", 0 },
            { "Officer Kill Cooldown", 0 },
            { "Show Engineer", 0 },
            { "Show Joker", 0 },
            { "Joker Can Die To Officer", 0 },
            { "Duration In Which Medic Report Will Contain The Killers Name", 0 },
            { "Duration In Which Medic Report Will Contain The Killers Color Type", 0 }
        };
        //rudimentary array to convert a byte setting from config into true/false
        public static bool[] byteBool =
        {
            false,
            true
        };
        //global rng
        public static System.Random rng = new System.Random();
        //the kill button in the bottom right
        public static KillButtonManager KillButton;
        //the id of the targeted player
        public static int KBTarget;
        //distance between the local player and closest player
        public static double DistLocalClosest;
        //shield indicator sprite (placeholder)
        public static GameObject shieldIndicator = null;
        //renderer for the shield indicator
        public static SpriteRenderer shieldRenderer = null;
        //medic settings and values
        public static class MedicSettings
        {
            public static PlayerControl Medic;
            public static PlayerControl Protected;
            public static bool shieldUsed = false;

            public static int medicKillerNameDuration = 0;
            public static int medicKillerColorDuration = 0;
            public static bool showMedic = false;
            public static bool showProtected = false;
            public static bool shieldKillAttemptIndicator = false;
            public static Color medicColor = new Color(36f / 255f, 183f / 255f, 32f / 255f, 1);
            public static Color protectedColor = new Color(0, 1, 1, 1);

            public static void ClearSettings()
            {
                Medic = null;
                Protected = null;
                shieldUsed = false;
            }

            public static void SetConfigSettings()
            {
                showMedic = byteBool[configSettings["Show Medic"]];
                showProtected = byteBool[configSettings["Show Shielded Player"]];
                shieldKillAttemptIndicator = byteBool[configSettings["Murder Attempt Indicator For Shielded Player"]];
                medicKillerNameDuration = configSettings["Duration In Which Medic Report Will Contain The Killers Name"];
                medicKillerColorDuration = configSettings["Duration In Which Medic Report Will Contain The Killers Color Type"];
            }
        }
        //officer settings and values
        public static class OfficerSettings
        {
            public static PlayerControl Officer;

            public static float OfficerCD = 10f;
            public static bool showOfficer = false;
            public static Color officerColor = new Color(0, 40f / 255f, 198f / 255f, 1);
            public static DateTime? lastKilled = null;
            public static bool firstKill = true;
            public static void ClearSettings()
            {
                Officer = null;
                lastKilled = null;
            }

            public static void SetConfigSettings()
            {
                showOfficer = byteBool[configSettings["Show Officer"]];
                OfficerCD = configSettings["Officer Kill Cooldown"];
            }
        }
        //engineer settings and values
        public static class EngineerSettings
        {
            public static PlayerControl Engineer;
            public static bool repairUsed = false;

            public static bool showEngineer = false;
            public static Color engineerColor = new Color(151f / 255f, 46f / 255f, 0, 1);

            public static void ClearSettings()
            {
                Engineer = null;
                repairUsed = false;
            }

            public static void SetConfigSettings()
            {
                showEngineer = byteBool[configSettings["Show Engineer"]];
            }
        }

        //joker settings and values
        public static class JokerSettings
        {
            public static PlayerControl Joker;
            public static bool showJoker = false;
            public static bool jokerCanDieToOfficer = false;
            public static Color jokerColor = new Color(138f / 255f, 138f / 255f, 138f / 255f, 1);

            public static void ClearSettings()
            {
                Joker = null;
            }

            public static void SetConfigSettings()
            {
                showJoker = byteBool[configSettings["Show Joker"]];
                jokerCanDieToOfficer = byteBool[configSettings["Joker Can Die To Officer"]];
            }
        }

        //function called on start of game. write version text on menu
        [HarmonyPatch(typeof(BOCOFLHKCOJ), "Start")]
        public static void Postfix(BOCOFLHKCOJ __instance)
        {
            __instance.text.Text = __instance.text.Text + "   Extra Roles V0.8 Loaded. (https://github.com/NotHunter101/ExtraRolesAmongUs/)";
        }

        [HarmonyPatch(typeof(FFGALNAPKCD), "RpcSetInfected")]
        public static void Postfix(Il2CppReferenceArray<EGLJNOMOGNP.DCJMABDDJCF> JPGEIBIBJPJ)
        {
            ConsoleTools.Error("SETTING INFECTED! IF YOU AREN'T THE HOST, COPY YOUR CONSOLE AND SEND!");
            MedicSettings.ClearSettings();
            OfficerSettings.ClearSettings();
            EngineerSettings.ClearSettings();
            JokerSettings.ClearSettings();
            MedicSettings.SetConfigSettings();
            OfficerSettings.SetConfigSettings();
            EngineerSettings.SetConfigSettings();
            JokerSettings.SetConfigSettings();

            MessageWriter writer = FMLLKEACGIO.Instance.StartRpcImmediately(PlayerControl.LocalPlayer.NetId, (byte)CustomRPC.ResetVaribles, Hazel.SendOption.None, -1);
            ConsoleTools.Info(String.Join(",", configSettings.Values));
            writer.WriteBytesAndSize(configSettings.Values.ToArray<byte>());
            FMLLKEACGIO.Instance.FinishRpcImmediately(writer);

            List<PlayerControl> crewmates = PlayerControl.AllPlayerControls.ToArray().ToList();
            crewmates.RemoveAll(x => x.Data.IsImpostor);

            if (crewmates.Count > 0)
            {
                writer = FMLLKEACGIO.Instance.StartRpcImmediately(PlayerControl.LocalPlayer.NetId, (byte)CustomRPC.SetMedic, Hazel.SendOption.None, -1);
                var MedicRandom = rng.Next(0, crewmates.Count);
                MedicSettings.Medic = crewmates[MedicRandom]; //.Where(x => x.name == "Medic").ToArray()[0];
                crewmates.RemoveAt(MedicRandom);
                byte MedicId = MedicSettings.Medic.PlayerId;

                writer.Write(MedicId);
                FMLLKEACGIO.Instance.FinishRpcImmediately(writer);
            }

            if (crewmates.Count > 0)
            {
                writer = FMLLKEACGIO.Instance.StartRpcImmediately(PlayerControl.LocalPlayer.NetId, (byte)CustomRPC.SetOfficer, Hazel.SendOption.None, -1);

                var OfficerRandom = rng.Next(0, crewmates.Count);
                OfficerSettings.Officer = crewmates[OfficerRandom]; //.Where(x => x.name == "Officer").ToArray()[0];
                crewmates.RemoveAt(OfficerRandom);
                byte OfficerId = OfficerSettings.Officer.PlayerId;

                writer.Write(OfficerId);
                FMLLKEACGIO.Instance.FinishRpcImmediately(writer);
            }

            if (crewmates.Count > 0)
            {
                writer = FMLLKEACGIO.Instance.StartRpcImmediately(PlayerControl.LocalPlayer.NetId, (byte)CustomRPC.SetEngineer, Hazel.SendOption.None, -1);
                var EngineerRandom = rng.Next(0, crewmates.Count);
                EngineerSettings.Engineer = crewmates[EngineerRandom]; //.Where(x => x.name == "Engineer").ToArray()[0];
                crewmates.RemoveAt(EngineerRandom);
                byte EngineerId = EngineerSettings.Engineer.PlayerId;

                writer.Write(EngineerId);
                FMLLKEACGIO.Instance.FinishRpcImmediately(writer);
            }

            if (crewmates.Count > 0)
            {
                writer = FMLLKEACGIO.Instance.StartRpcImmediately(PlayerControl.LocalPlayer.NetId, (byte)CustomRPC.SetJoker, Hazel.SendOption.None, -1);
                var JokerRandom = rng.Next(0, crewmates.Count);
                ConsoleTools.Info(JokerRandom.ToString());
                JokerSettings.Joker = crewmates[JokerRandom]; //.Where(x => x.name == "Joker").ToArray()[0];
                JokerSettings.Joker.myTasks.Clear();
                crewmates.RemoveAt(JokerRandom);
                byte JokerId = JokerSettings.Joker.PlayerId;

                writer.Write(JokerId);
                FMLLKEACGIO.Instance.FinishRpcImmediately(writer);
            }
        }

        //function called when the game starts and impostors are chosen. this is where we choose all the roles and send the packets
        [HarmonyPatch(typeof(FFGALNAPKCD), "RpcSetInfected")]
        public static bool Prefix(Il2CppReferenceArray<EGLJNOMOGNP.DCJMABDDJCF> JPGEIBIBJPJ)
        {
            var debugImpostors = false;
            if (debugImpostors)
            {
                var infected = new byte[] { 0, 0 };

                foreach (PlayerControl player in PlayerControl.AllPlayerControls)
                {
                    if (player.name == "Impostor")
                    {
                        infected[0] = player.PlayerId;
                    }
                    if (player.name == "Pretender")
                    {
                        infected[1] = player.PlayerId;
                    }
                }

                MessageWriter writer = FMLLKEACGIO.Instance.StartRpcImmediately(PlayerControl.LocalPlayer.NetId, (byte)RPC.SetInfected, Hazel.SendOption.None, -1);
                writer.WritePacked((uint)2);
                writer.WriteBytesAndSize(infected);
                FMLLKEACGIO.Instance.FinishRpcImmediately(writer);

                PlayerControl.LocalPlayer.SetInfected(infected);

                return false;
            }
            return true;
        }

        //function that handles all packets from other clients and server. if you need comments to understand this just ask and i'll write them
        //i'll also send the server rpc code if you want
        [HarmonyPatch(typeof(FFGALNAPKCD), "HandleRpc")]
        public static void Postfix(byte HKHMBLJFLMC, MessageReader ALMCIJKELCP)
        {
            switch (HKHMBLJFLMC)
            {
                case (byte)RPC.SetInfected:
                    {
                        ConsoleTools.Info("set infected.");
                        break;
                    }
                case (byte)CustomRPC.ResetVaribles:
                    {
                        var configSettingsValues = ALMCIJKELCP.ReadBytesAndSize().ToArray();
                        for (var i = 0; i < configSettingsValues.Length; i++)
                        {
                            configSettings[configSettingsKeys[i]] = configSettingsValues[i];
                        }
                        ConsoleTools.Info(String.Join(",", configSettings));
                        MedicSettings.ClearSettings();
                        OfficerSettings.ClearSettings();
                        EngineerSettings.ClearSettings();
                        JokerSettings.ClearSettings();
                        MedicSettings.SetConfigSettings();
                        OfficerSettings.SetConfigSettings();
                        EngineerSettings.SetConfigSettings();
                        JokerSettings.SetConfigSettings();

                        break;
                    }
                case (byte)CustomRPC.SetMedic:
                    {
                        ConsoleTools.Info("Medic Set Through RPC!");
                        byte MedicId = ALMCIJKELCP.ReadByte();
                        foreach (PlayerControl player in PlayerControl.AllPlayerControls)
                        {
                            if (player.PlayerId == MedicId)
                            {
                                MedicSettings.Medic = player;
                            }
                        }
                        break;
                    }
                case (byte)CustomRPC.SetProtected:
                    {
                        byte ProtectedId = ALMCIJKELCP.ReadByte();
                        foreach (PlayerControl player in PlayerControl.AllPlayerControls)
                        {
                            if (player.PlayerId == ProtectedId)
                            {
                                MedicSettings.Protected = player;
                            }
                        }
                        break;
                    }
                case (byte)CustomRPC.MedicDead:
                    {
                        MedicSettings.Protected = null;
                        //COLORID: EGLJNOMOGNP.Instance.GetPlayerById(PlayerControl.LocalPlayer.PlayerId).EHAHBDFODKC
                        break;
                    }
                case (byte)CustomRPC.SetOfficer:
                    {
                        ConsoleTools.Info("Officer Set Through RPC!");
                        byte OfficerId = ALMCIJKELCP.ReadByte();
                        foreach (PlayerControl player in PlayerControl.AllPlayerControls)
                        {
                            if (player.PlayerId == OfficerId)
                            {
                                OfficerSettings.Officer = player;
                            }
                        }
                        break;
                    }
                case (byte)CustomRPC.OfficerKill:
                    {
                        PlayerControl killer = PlayerTools.getPlayerById(ALMCIJKELCP.ReadByte());
                        PlayerControl target = PlayerTools.getPlayerById(ALMCIJKELCP.ReadByte());
                        killer.MurderPlayer(target);
                        break;
                    }
                case (byte)CustomRPC.SetEngineer:
                    {
                        ConsoleTools.Info("Engineer Set Through RPC!");
                        byte EngineerId = ALMCIJKELCP.ReadByte();
                        foreach (PlayerControl player in PlayerControl.AllPlayerControls)
                        {
                            if (player.PlayerId == EngineerId)
                            {
                                EngineerSettings.Engineer = player;
                            }
                        }
                        break;
                    }
                case (byte)CustomRPC.SetJoker:
                    {
                        ConsoleTools.Info("Joker Set Through RPC!");
                        byte JokerId = ALMCIJKELCP.ReadByte();
                        foreach (PlayerControl player in PlayerControl.AllPlayerControls)
                        {
                            if (player.PlayerId == JokerId)
                            {
                                JokerSettings.Joker = player;
                            }
                        }
                        break;
                    }
                case (byte)CustomRPC.MedicReport:
                    {
                        ConsoleTools.Info("Body Reported RPC!");
                        byte reporterId = ALMCIJKELCP.ReadByte();
                        byte killerId = ALMCIJKELCP.ReadByte();
                        byte deathReason = ALMCIJKELCP.ReadByte();
                        float killAge = ALMCIJKELCP.ReadSingle();
                        if (reporterId == MedicSettings.Medic.PlayerId)
                        {
                            if (MedicSettings.Medic.PlayerId == PlayerControl.LocalPlayer.PlayerId)
                            {
                                BodyReport br = new BodyReport();
                                br.Killer = PlayerTools.getPlayerById(killerId);
                                br.Reporter = br.Killer = PlayerTools.getPlayerById(killerId);
                                br.KillAge = killAge;
                                br.DeathReason = deathReason;
                                var reportMsg = BodyReport.ParseBodyReport(br);

                                MessageWriter writer = FMLLKEACGIO.Instance.StartRpcImmediately(PlayerControl.LocalPlayer.NetId, (byte)CustomRPC.SendMeAMessage, Hazel.SendOption.None, -1);
                                writer.Write(reportMsg);
                                FMLLKEACGIO.Instance.FinishRpcImmediately(writer);
                            }
                        }
                        break;
                    }
                //player exiled
                case (byte)CustomRPC.JokerWin:
                    {
                        //ConsoleTools.Info("Joker won!");
                        var exiledId = ALMCIJKELCP.ReadByte();
                        if (JokerSettings.Joker != null)
                        {
                            if (exiledId == JokerSettings.Joker.PlayerId)
                            {
                                foreach (PlayerControl player in PlayerControl.AllPlayerControls)
                                {
                                    if (player != JokerSettings.Joker)
                                    {
                                        player.RemoveInfected();
                                        player.Die(DeathReason.Exile);
                                        player.Data.IsDead = true;
                                    }
                                    else
                                    {
                                        player.Revive();
                                        player.Data.IsImpostor = true;
                                    }
                                }
                            }
                        }
                        break;
                    }
                case (byte)CustomRPC.MeetingEnded:
                    {
                        OfficerSettings.lastKilled = DateTime.UtcNow;
                        break;
                    }
            }
        }

        //called in the intro function
        [HarmonyPatch(typeof(PENEIDJGGAF.CKACLKCOJFO), "MoveNext")]
        public static void Postfix(PENEIDJGGAF.CKACLKCOJFO __instance)
        {
            //change the name and titles accordingly
            if (PlayerControl.LocalPlayer == MedicSettings.Medic)
            {
                __instance.__this.Title.Text = "Medic";
                __instance.__this.Title.Color = MedicSettings.medicColor;
                __instance.__this.ImpostorText.Text = "Create a shield to protect a [8DFFFF]Crewmate";
                __instance.__this.BackgroundBar.material.color = MedicSettings.medicColor;
            }
            if (PlayerControl.LocalPlayer == OfficerSettings.Officer)
            {
                __instance.__this.Title.Text = "Officer";
                __instance.__this.Title.Color = OfficerSettings.officerColor;
                __instance.__this.ImpostorText.Text = "Shoot the [FF0000FF]Impostor";
                __instance.__this.BackgroundBar.material.color = OfficerSettings.officerColor;
            }
            if (PlayerControl.LocalPlayer == EngineerSettings.Engineer)
            {
                __instance.__this.Title.Text = "Engineer";
                __instance.__this.Title.Color = EngineerSettings.engineerColor;
                __instance.__this.ImpostorText.Text = "Maintain important systems on the ship";
                __instance.__this.BackgroundBar.material.color = EngineerSettings.engineerColor;
            }
            if (PlayerControl.LocalPlayer == JokerSettings.Joker)
            {
                __instance.__this.Title.Text = "Joker";
                __instance.__this.Title.Color = JokerSettings.jokerColor;
                __instance.__this.ImpostorText.Text = "Get voted off of the ship to win";
                __instance.__this.BackgroundBar.material.color = JokerSettings.jokerColor;
            }
        }

        [HarmonyPatch(typeof(PENEIDJGGAF.CKACLKCOJFO), "MoveNext")]
        public static bool Prefix(PENEIDJGGAF.CKACLKCOJFO __instance)
        {
            if (PlayerControl.LocalPlayer == JokerSettings.Joker)
            {
                var jokerTeam = new Il2CppSystem.Collections.Generic.List<FFGALNAPKCD>();
                jokerTeam.Add(FFGALNAPKCD.LocalPlayer);
                __instance.yourTeam = jokerTeam;
                return true;
            }
            return true;
        }

        [HarmonyPatch(typeof(MLPJGKEACMM), "PerformKill")]
        static bool Prefix(MethodBase __originalMethod)
        {
            if (PlayerControl.LocalPlayer.Data.IsDead)
                return false;
            //code that handles the ability button presses
            if (PlayerControl.LocalPlayer == OfficerSettings.Officer)
            {
                ConsoleTools.Info("Player is Officer.");
                ConsoleTools.Info(KBTarget.ToString());
                var target = PlayerTools.getPlayerById((byte)KBTarget);
                if (KBTarget != -1 && KBTarget != -2)
                {
                    if (PlayerTools.GetOfficerKD() == 0)
                    {
                        ConsoleTools.Info("KillButton has defined Target.");
                        //check if they're shielded by medic
                        if (MedicSettings.Protected != null && target.PlayerId == MedicSettings.Protected.PlayerId)
                        {
                            ConsoleTools.Info("The target is Protected.");
                            //officer suicide packet
                            MessageWriter writer = FMLLKEACGIO.Instance.StartRpcImmediately(PlayerControl.LocalPlayer.NetId, (byte)CustomRPC.OfficerKill, Hazel.SendOption.None, -1);
                            writer.Write(PlayerControl.LocalPlayer.PlayerId);
                            writer.Write(PlayerControl.LocalPlayer.PlayerId);
                            FMLLKEACGIO.Instance.FinishRpcImmediately(writer);
                            PlayerControl.LocalPlayer.MurderPlayer(PlayerControl.LocalPlayer);
                            OfficerSettings.lastKilled = DateTime.UtcNow;
                            return false;
                        }
                        //check if they're joker and the setting is configured
                        else if (JokerSettings.jokerCanDieToOfficer && (JokerSettings.Joker != null && target.PlayerId == JokerSettings.Joker.PlayerId))
                        {
                            ConsoleTools.Info("The target is an Joker. (jokerCanDieToOfficer = 1)");
                            //officer joker murder packet
                            MessageWriter writer = FMLLKEACGIO.Instance.StartRpcImmediately(PlayerControl.LocalPlayer.NetId, (byte)CustomRPC.OfficerKill, Hazel.SendOption.None, -1);
                            writer.Write(PlayerControl.LocalPlayer.PlayerId);
                            writer.Write(target.PlayerId);
                            FMLLKEACGIO.Instance.FinishRpcImmediately(writer);
                            PlayerControl.LocalPlayer.MurderPlayer(target);
                            OfficerSettings.lastKilled = DateTime.UtcNow;
                        }
                        //check if they're an impostor
                        else if (target.Data.IsImpostor)
                        {
                            ConsoleTools.Info("The target is an Impostor.");
                            //officer impostor murder packet
                            MessageWriter writer = FMLLKEACGIO.Instance.StartRpcImmediately(PlayerControl.LocalPlayer.NetId, (byte)CustomRPC.OfficerKill, Hazel.SendOption.None, -1);
                            writer.Write(PlayerControl.LocalPlayer.PlayerId);
                            writer.Write(target.PlayerId);
                            FMLLKEACGIO.Instance.FinishRpcImmediately(writer);
                            PlayerControl.LocalPlayer.MurderPlayer(target);
                            OfficerSettings.lastKilled = DateTime.UtcNow;
                            return false;
                        }
                        //else, they're innocent and not shielded
                        else
                        {
                            ConsoleTools.Info("The target is Innocent.");
                            //officer suicide packet
                            MessageWriter writer = FMLLKEACGIO.Instance.StartRpcImmediately(PlayerControl.LocalPlayer.NetId, (byte)CustomRPC.OfficerKill, Hazel.SendOption.None, -1);
                            writer.Write(PlayerControl.LocalPlayer.PlayerId);
                            writer.Write(PlayerControl.LocalPlayer.PlayerId);
                            FMLLKEACGIO.Instance.FinishRpcImmediately(writer);
                            PlayerControl.LocalPlayer.MurderPlayer(PlayerControl.LocalPlayer);
                            OfficerSettings.lastKilled = DateTime.UtcNow;
                            return false;
                        }
                        return false;
                    }
                    return false;
                }
            }
            //check if they're medic
            else if (PlayerControl.LocalPlayer == MedicSettings.Medic)
            {
                //if the target is defined
                if (KBTarget != -1 && KBTarget != -2)
                {
                    ConsoleTools.Info("Medic Creating Shield");
                    //create a shield for the targeted player
                    MessageWriter writer = FMLLKEACGIO.Instance.StartRpcImmediately(PlayerControl.LocalPlayer.NetId, (byte)CustomRPC.SetProtected, Hazel.SendOption.None, -1);
                    MedicSettings.Protected = PlayerTools.closestPlayer;
                    MedicSettings.shieldUsed = true;
                    byte ProtectedId = MedicSettings.Protected.PlayerId;
                    writer.Write(ProtectedId);
                    FMLLKEACGIO.Instance.FinishRpcImmediately(writer);
                    return false;
                }
                return false;
            }
            //check if they're engineer
            else if (PlayerControl.LocalPlayer == EngineerSettings.Engineer)
            {
                //INFO
                //this code is finished, but not implemented yet. It was working in a previous version, but I rewrote this whole section because of bugs
                if (KBTarget == -2 && EngineerSettings.repairUsed == false)
                {
                    MessageWriter writer = FMLLKEACGIO.Instance.StartRpcImmediately(PlayerControl.LocalPlayer.NetId, (byte)CustomRPC.RepairAllEmergencies, Hazel.SendOption.None, -1);
                    FMLLKEACGIO.Instance.FinishRpcImmediately(writer);
                    EngineerSettings.repairUsed = true;
                    return false;
                }
                return false;
            }
            //finally, check if the target is protected.
            if (MedicSettings.Protected != null && PlayerTools.closestPlayer.PlayerId == MedicSettings.Protected.PlayerId)
            {
                //cancel the kill
                return false;
            }
            //otherwise, continue the murder as normal
            return true;
        }

        //the code that activates venting for engineers
        [HarmonyPatch(typeof(Vent), "CanUse")]
        public static class VentPatch
        {
            public static bool Prefix(Vent __instance, ref float __result, [HarmonyArgument(0)] GameData.PlayerInfo pc, [HarmonyArgument(1)] out bool canUse, [HarmonyArgument(2)] out bool couldUse)
            {
                float num = float.MaxValue;
                PlayerControl localPlayer = pc.Object;
                couldUse = (localPlayer == EngineerSettings.Engineer || localPlayer.Data.IsImpostor);
                canUse = couldUse;
                if ((DateTime.UtcNow - PlayerVentTimeExtension.GetLastVent(pc.Object.PlayerId)).TotalMilliseconds > 1000)
                {
                    num = Vector2.Distance(localPlayer.GetTruePosition(), __instance.transform.position);
                    canUse &= num <= __instance.UsableDistance;
                }
                __result = num;
                return false;
            }
        }

        [HarmonyPatch(typeof(Vent), "Method_38")]
        public static class VentEnterPatch
        {
            public static void Prefix(PlayerControl NMEAPOJFNKA)
            {
                ConsoleTools.Info("ENTER! " + NMEAPOJFNKA.name);
                PlayerVentTimeExtension.SetLastVent(NMEAPOJFNKA.PlayerId);
            }
        }

        [HarmonyPatch(typeof(Vent), "Method_1")]
        public static class VentExitPatch
        {
            public static bool Prefix(PlayerControl NMEAPOJFNKA)
            {
                ConsoleTools.Info("EXIT! " + NMEAPOJFNKA.name);
                PlayerVentTimeExtension.SetLastVent(NMEAPOJFNKA.PlayerId);
                return true;
            }
        }

        [HarmonyPatch(typeof(PlayerControl), nameof(PlayerControl.MurderPlayer))]
        public static class MurderPlayerPatch
        {
            public static bool Prefix(PlayerControl __instance, PlayerControl CAKODNGLPDF)
            {
                if (MainHooks.OfficerSettings.Officer != null)
                {
                    //check if the player is an officer
                    if (PlayerControl.LocalPlayer == MainHooks.OfficerSettings.Officer)
                    {
                        MainHooks.OfficerSettings.firstKill = false;
                        //if so, set them to impostor for one frame so they aren't banned for anti-cheat
                        PlayerControl.LocalPlayer.Data.IsImpostor = true;
                    }
                }
                return true;
            }

            //handle the murder after it's ran
            public static void Postfix(PlayerControl __instance, PlayerControl CAKODNGLPDF)
            {
                if (MainHooks.MedicSettings.Medic != null)
                {
                    if (CAKODNGLPDF.PlayerId == MainHooks.MedicSettings.Medic.PlayerId)
                    {
                        //medic was just killed for sure.
                        MessageWriter writer = FMLLKEACGIO.Instance.StartRpcImmediately(PlayerControl.LocalPlayer.NetId, (byte)CustomRPC.MedicDead, Hazel.SendOption.None, -1);
                        FMLLKEACGIO.Instance.FinishRpcImmediately(writer);
                        //simply set the protected player to null to break the shield
                        MainHooks.MedicSettings.Protected = null;
                    }
                }
                if (MainHooks.OfficerSettings.Officer != null)
                {
                    //check if killer is officer
                    if (PlayerControl.LocalPlayer == MainHooks.OfficerSettings.Officer)
                    {
                        //finally, set them back to normal
                        PlayerControl.LocalPlayer.Data.IsImpostor = false;
                    }
                }
            }
        }

        [HarmonyPatch(typeof(HudManager), nameof(HudManager.Update))]
        public static class UpdatePatch
        {
            public static void Postfix(HudManager __instance)
            {
                //this is the only way I could reliably figure out if the game was started or just in the lobby.
                var gameStarted = true;
                try
                {
                    //this function throws an error in the lobby, so it gets caught and the main update code has it's condition set to false.
                    PlayerTools.closestPlayer = PlayerTools.getClosestPlayer(PlayerControl.LocalPlayer);
                    DistLocalClosest = PlayerTools.getDistBetweenPlayers(PlayerControl.LocalPlayer, PlayerTools.closestPlayer);
                }
                catch
                {
                    //set the main update code's condition to false
                    gameStarted = false;
                }
                if (gameStarted)
                {
                    KillButton = __instance.KillButton;
                    PlayerTools.closestPlayer = PlayerTools.getClosestPlayer(PlayerControl.LocalPlayer);
                    DistLocalClosest = PlayerTools.getDistBetweenPlayers(PlayerControl.LocalPlayer, PlayerTools.closestPlayer);
                    KBTarget = -1;
                    if (JokerSettings.Joker != null && JokerSettings.Joker.myTasks.Count > 0)
                    {
                        while (JokerSettings.Joker.myTasks.Count > 0)
                        {
                            JokerSettings.Joker.RemoveTask(JokerSettings.Joker.myTasks[0]);
                        }
                    }

                    foreach (PlayerControl player in PlayerControl.AllPlayerControls)
                    {
                        player.nameText.Color = Color.white;
                    }

                    if (PlayerControl.LocalPlayer.Data.IsImpostor)
                    {
                        foreach (PlayerControl player in PlayerControl.AllPlayerControls)
                        {
                            if (player.Data.IsImpostor)
                                player.nameText.Color = Color.red;
                        }
                    }
                    if (MedicSettings.Medic != null)
                    {
                        if (MedicSettings.Medic == PlayerControl.LocalPlayer || MedicSettings.showMedic)
                        {
                            MedicSettings.Medic.nameText.Color = MedicSettings.medicColor;
                        }
                    }
                    if (OfficerSettings.Officer != null)
                    {
                        if (OfficerSettings.Officer == PlayerControl.LocalPlayer || OfficerSettings.showOfficer)
                        {
                            OfficerSettings.Officer.nameText.Color = OfficerSettings.officerColor;
                        }
                    }
                    if (EngineerSettings.Engineer != null)
                    {
                        if (EngineerSettings.Engineer == PlayerControl.LocalPlayer || EngineerSettings.showEngineer)
                        {
                            EngineerSettings.Engineer.nameText.Color = EngineerSettings.engineerColor;
                        }
                    }
                    if (JokerSettings.Joker != null)
                    {
                        if (JokerSettings.Joker == PlayerControl.LocalPlayer || JokerSettings.showJoker)
                        {
                            JokerSettings.Joker.nameText.Color = JokerSettings.jokerColor;
                        }
                    }
                    if (MedicSettings.Protected != null)
                    {
                        if (MedicSettings.Protected == PlayerControl.LocalPlayer || MedicSettings.showProtected)
                        {
                            MedicSettings.Protected.nameText.Color = MedicSettings.protectedColor;
                            //broken; doesn't set the colors properly
                            //this changes the player color and visor color. it's a cool effect to make it obvious a player is shielded.
                            //MedicSettings.Protected.SetColor(7);

                            //SKINID: EGLJNOMOGNP.Instance.GetPlayerById(MedicSettings.Medic.PlayerId).AFEJLMBMKCJ
                            //HATID: EGLJNOMOGNP.Instance.GetPlayerById(MedicSettings.Medic.PlayerId).AJIBCNMKNPM
                            //TASKS: EGLJNOMOGNP.Instance.GetPlayerById(MedicSettings.Medic.PlayerId).CJOBIPKGGHC
                        }
                    }

                    if (PlayerControl.LocalPlayer == MedicSettings.Medic)
                    {
                        Texture2D tex = rotateTexture(CustomSpriteArr.shieldImgArr.Reverse().ToArray(), false, 106, 106);
                        tex.Apply(false, false);
                        if (tex != KillButton.renderer.sprite.texture)
                        {
                            KillButton.renderer.sprite = Sprite.Create(tex, new Rect(0, 0, 106, 106), Vector2.one / 2f);
                        }
                        KillButton.gameObject.SetActive(true);
                        KillButton.isActive = true;
                        KillButton.SetCoolDown(0f, PlayerControl.GameOptions.KillCooldown + 15.0f);
                        if (DistLocalClosest < KMOGFLPJLLK.JMLGACIOLIK[PlayerControl.GameOptions.KillDistance] && MedicSettings.shieldUsed == false)
                        {
                            KillButton.SetTarget(PlayerTools.closestPlayer);
                            KBTarget = PlayerTools.closestPlayer.PlayerId;
                        }
                    }
                    if (PlayerControl.LocalPlayer == OfficerSettings.Officer)
                    {
                        KillButton.gameObject.SetActive(true);
                        KillButton.isActive = true;

                        KillButton.SetCoolDown(PlayerTools.GetOfficerKD(), PlayerControl.GameOptions.KillCooldown + 15.0f);
                        if (DistLocalClosest < KMOGFLPJLLK.JMLGACIOLIK[PlayerControl.GameOptions.KillDistance])
                        {
                            KillButton.SetTarget(PlayerTools.closestPlayer);
                            KBTarget = PlayerTools.closestPlayer.PlayerId;
                        }
                    }
                    if (PlayerControl.LocalPlayer == EngineerSettings.Engineer)
                    {
                        Texture2D tex = rotateTexture(CustomSpriteArr.repairImgArr.Reverse().ToArray(), false, 106, 106);
                        tex.Apply(false, false);
                        if (tex != KillButton.renderer.sprite.texture)
                        {
                            KillButton.renderer.sprite = Sprite.Create(tex, new Rect(0, 0, 106, 106), Vector2.one / 2f);
                        }
                        KillButton.gameObject.SetActive(true);
                        KillButton.isActive = true;
                        KillButton.SetCoolDown(0f, PlayerControl.GameOptions.KillCooldown + 15.0f);
                        var allTasks = PlayerControl.LocalPlayer.myTasks;
                        if (allTasks.Count > 0)
                        {
                            var lastTaskType = allTasks.ToArray().Last().TaskType;
                            var sabotageActive = false;
                            if (lastTaskType == TaskTypes.FixLights || lastTaskType == TaskTypes.FixComms || lastTaskType == TaskTypes.ResetReactor || lastTaskType == TaskTypes.ResetSeismic || lastTaskType == TaskTypes.RestoreOxy)
                            {
                                sabotageActive = true;
                            }
                            if (EngineerSettings.repairUsed == false && sabotageActive)
                            {
                                KillButton.SetTarget(PlayerControl.LocalPlayer);
                                KBTarget = -2;
                            }
                        }
                    }
                    if (PlayerControl.LocalPlayer.Data.IsDead)
                    {
                        KillButton.gameObject.SetActive(false);
                        KillButton.isActive = false;
                    }
                }
            }
        }
    }
}