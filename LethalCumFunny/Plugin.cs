using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using GameNetcodeStuff;
using HarmonyLib;
using LC_API.Networking;
using LC_API.Networking.Serializers;
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization.Formatters.Binary;
using System.Threading;
using System.Threading.Tasks;
using Unity.Collections;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.InputSystem;

namespace LethalComFunny
{
    [BepInPlugin(PluginInfo.PLUGIN_GUID, PluginInfo.PLUGIN_NAME, PluginInfo.PLUGIN_VERSION)]
    public class Plugin : BaseUnityPlugin
    {
        private readonly Harmony harmony = new Harmony(PluginInfo.PLUGIN_GUID);

        public static Plugin instance;

        static InputAction action = new InputAction(binding: "<Keyboard>/y");
        static StartOfRound gameInstance => StartOfRound.Instance;
        static SelectableLevel currentLevel => gameInstance.currentLevel;

        static bool allowDeadSpawns = false;

        public static new ManualLogSource Logger;

        static List<Landmine> mines = new List<Landmine>();

        public static MConfig LCFConfig { get; internal set; }


        private void Awake()
        {
            if (instance == null)
            {
                instance = this;
            }

            action.performed += ctx => queueSpawnMine();
            action.Enable();

            Network.RegisterAll();

            LCFConfig = new(Config);

            harmony.PatchAll(typeof(Plugin));

            Logger = BepInEx.Logging.Logger.CreateLogSource(PluginInfo.PLUGIN_GUID);
            Logger.LogInfo($"Plugin {PluginInfo.PLUGIN_GUID} is loaded!");
        }

        [HarmonyPatch(typeof(Landmine), "Detonate")]
        [HarmonyPostfix]
        static void disableHitboxAfterExplosion(ref Landmine __instance)
        {
            // Start a coroutine to wait for 5 seconds before disabling the hitbox
            gameInstance.StartCoroutine(DestroyOnDelay(5f, __instance));
        }

        [HarmonyPatch(typeof(RoundManager), "DespawnPropsAtEndOfRound")]
        [HarmonyPostfix]
        static void byebyeMines()
        {
            foreach (var mine in mines)
            {
                Destroy(mine.gameObject.transform.parent.gameObject);
            }
            mines = [];
        }

        [NetworkMessage("LethalCumFunnyMineDespawn")]
        public static void SpawnHandler(ulong sender, Landmine mine)
        {
            if (gameInstance.IsHost)
                mine.GetComponent<NetworkObject>().Despawn(true);
        }

        static IEnumerator DestroyOnDelay(float delay, Landmine mine)
        {
            // Wait for the specified delay
            mines.Remove(mine);
            mine.gameObject.transform.Find("BlastMark").parent = null;
            yield return new WaitForSeconds(delay);
            if (gameInstance.IsHost)
                Destroy(mine.gameObject.transform.parent.gameObject);
            
        }

        static void queueSpawnMine()
        {
            if (!gameInstance.localPlayerController.isPlayerDead || allowDeadSpawns)
            {
                Camera gameplayCamera;
                gameplayCamera = gameInstance.activeCamera;

                Ray interactRay = new Ray(gameplayCamera.transform.position, gameplayCamera.transform.forward);
                RaycastHit hit;
                if (Physics.Raycast(interactRay, out hit, 20, 268437761))
                {
                    var pos = hit.point;
                    if (gameInstance.IsHost)
                        spawnMine(pos);
                    else
                        Network.Broadcast("LethalCumFunnyMineSpawn", new Vector3SClass() { pos = pos });
                }
            }
        }

        [NetworkMessage("LethalCumFunnyMineSpawn")]
        public static void SpawnHandler(ulong sender, Vector3SClass message)
        {
            if (gameInstance.IsHost)
                spawnMine(message.pos.vector3);
        }

        public static void spawnMine(Vector3 pos)
        {
            foreach (SpawnableMapObject obj in currentLevel.spawnableMapObjects)
            {
                if (obj.prefabToSpawn.GetComponentInChildren<Landmine>() == null) continue;
                var mine = Instantiate(obj.prefabToSpawn, pos, Quaternion.identity);
                mine.transform.position = pos;
                mine.transform.forward = new Vector3(1, 0, 0);
                mine.GetComponent<NetworkObject>().Spawn(true);
                mines.Add(mine.GetComponentInChildren<Landmine>());
                break;
            }
        }

        [HarmonyPatch(typeof(GameNetworkManager), "SteamMatchmaking_OnLobbyMemberJoined")]
        [HarmonyPostfix]
        public static void SyncConfig()
        {
            if (gameInstance.IsHost)
            {
                gameInstance.StartCoroutine(SendConfigOnDelay(5f));
            }
        }

        static IEnumerator SendConfigOnDelay(float delay)
        {
            // Wait for the specified delay
            yield return new WaitForSeconds(delay);
            Logger.LogInfo("Telling clients to set configDeadSpawns to " + MConfig.configDeadSpawns.Value);
            Network.Broadcast("LethalCumFunnySendConfig", new ConfigWrapper() { Value = MConfig.configDeadSpawns.Value });
            allowDeadSpawns = MConfig.configDeadSpawns.Value;
        }

        [NetworkMessage("LethalCumFunnySendConfig")]
        public static void ConfigReceiver(ulong sender, ConfigWrapper configDeadSpawns)
        {
            Logger.LogInfo("Setting allowDeadSpawns to " + configDeadSpawns.Value);
            allowDeadSpawns = configDeadSpawns.Value;
        }

        [NetworkMessage("LethalCumFunnyRequestConfig")]
        public static void ConfigSender(ulong sender)
        {
            if (gameInstance.IsHost)
            {
                Logger.LogInfo("Telling clients to set configDeadSpawns to " + MConfig.configDeadSpawns.Value);
                Network.Broadcast("LethalCumFunnySendConfig", MConfig.configDeadSpawns);
                allowDeadSpawns = MConfig.configDeadSpawns.Value;
            }
        }
    }

    public class Vector3SClass
    {
        public Vector3S pos { get; set; }
    }

    public class ConfigWrapper
    {
        public bool Value { get; set; }
    }

    public class MConfig
    {
        public static ConfigEntry<bool> configDeadSpawns;

        public MConfig(ConfigFile cfg)
        {
            configDeadSpawns = cfg.Bind(
                    "General.Toggles",
                    "AllowDeadSpawns",
                    false,
                    "Whether or not to allow dead players to spawn mines"
            );
        }
    }
}