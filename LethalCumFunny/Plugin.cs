using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using LC_API.Networking;
using LC_API.Networking.Serializers;
using LethalSettings.UI.Components;
using LethalSettings.UI;
using System.Collections;
using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.InputSystem;
using GameNetcodeStuff;
using System.IO;
using System.Reflection;
using UnityEngine.UI;

namespace LethalComFunny
{
    [BepInPlugin(PluginInfo.PLUGIN_GUID, PluginInfo.PLUGIN_NAME, PluginInfo.PLUGIN_VERSION)]
    public class Plugin : BaseUnityPlugin
    {
        private readonly Harmony harmony = new Harmony(PluginInfo.PLUGIN_GUID);

        public static Plugin instance;

        static InputAction action = new InputAction(binding: "<Keyboard>/y");
        static InputAction menuAction = new InputAction(binding: "<Keyboard>/u");
        static StartOfRound gameInstance => StartOfRound.Instance;
        static SelectableLevel currentLevel => gameInstance.currentLevel;

        static bool allowDeadSpawns = false;
        private static bool menuOpen = false;

        internal static new ManualLogSource Logger;

        public static List<Landmine> mines = new List<Landmine>();
        public static AssetBundle assets;
        private static Dictionary<ulong, GameObject> joinedPlayersUI = new Dictionary<ulong, GameObject>();
        private static GameObject playerTogglesContainer = GameObject.Find("Systems/UI/Canvas/LethalComFunny");

        public static MConfig LCFConfig { get; internal set; }


        private void Awake()
        {
            Logger = base.Logger;

            if (instance == null)
            {
                instance = this;
            }
            
            action.performed += ctx => queueSpawnMine();
            action.Enable();
            
            menuAction.performed += ctx => toggleMenu();
            menuAction.Enable();

            Network.RegisterAll();

            LCFConfig = new(Config);
            
            ModMenu.RegisterMod(new ModMenu.ModSettingsConfig
            {
                Name = "Lethal ComFunny",
                Id = PluginInfo.PLUGIN_GUID,
                Version = "1.0.1",
                Description = "Become a funny asset to the company",
                MenuComponents = new MenuComponent[]
                {
                    new ToggleComponent
                    {
                        Text = "Allow dead players to spawn mines?",
                        OnValueChanged = (self, value) => {
                            MConfig.configDeadSpawns.Value = value;
                            if (gameInstance != null && gameInstance.IsHost)
                            {
                                SyncConfig();
                            }
                        },
                        Value = MConfig.configDeadSpawns.Value
                    },
                    new ToggleComponent
                    {
                        Text = "Allow other (non-host) players to spawn mines?",
                        OnValueChanged = (self, value) => {
                            MConfig.configHostOnly.Value = !value;
                        },
                        Value = !MConfig.configHostOnly.Value
                    }
                }
            }, true, true);
            
            string sAssemblyLocation = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);

            assets = AssetBundle.LoadFromFile(Path.Combine(sAssemblyLocation, "lethalcomfunny"));
            if (assets == null)
            {
                Logger.LogError("Failed to load custom assets.");
                return;
            }
            
            CreditedCompany.Plugin.credits.Add("Orangenal - Lethal ComFunny");

            harmony.PatchAll(typeof(Plugin));
            
            Logger.LogInfo($"Plugin {PluginInfo.PLUGIN_GUID} is loaded!");
        }

        [HarmonyPatch(typeof(Landmine), "Detonate")]
        [HarmonyPostfix]
        static void disableHitboxAfterExplosion(ref Landmine __instance)
        {
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
            if (gameInstance.IsHost && !MConfig.configHostOnly.Value)
            {
                spawnMine(message.pos.vector3, sender);
            } 
        }

        public static void spawnMine(Vector3 pos, ulong sender = 0)
        {
            if (joinedPlayersUI[sender] != null)
            {
                if (!joinedPlayersUI[sender].GetComponent<Toggle>().isOn) return;
            }
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

        public void toggleMenu()
        {
            if (!gameInstance.IsHost || gameInstance.allPlayerScripts.Length == 1 || MConfig.configHostOnly.Value) return;
            PlayerControllerB playerController = gameInstance.localPlayerController;

            if (playerController == null) return;

            if (menuOpen)
            {
                menuOpen = false;
                Cursor.visible = false;
                Cursor.lockState = CursorLockMode.Locked;
            }
            else
            {
                menuOpen = true;
                Cursor.lockState = CursorLockMode.None;
            }
            playerController.quickMenuManager.isMenuOpen = menuOpen;
            playerTogglesContainer.SetActive(menuOpen);
        }

        public static void SyncConfig()
        {
            gameInstance.StartCoroutine(SendConfigOnDelay(5f));
        }

        [HarmonyPatch(typeof(GameNetworkManager), "Singleton_OnClientConnectedCallback")]
        [HarmonyPostfix]
        public static void OnPlayerJoin(ref ulong clientId)
        {
            if (gameInstance.IsHost)
            {
                SyncConfig();

                GameObject toggle = Instantiate(assets.LoadAsset<GameObject>("Assets/AllowSpawningToggle.prefab"));

                GameObject topLeftCorner = GameObject.Find("Systems/UI/Canvas/");
                
                if (playerTogglesContainer == null)
                {
                    playerTogglesContainer = new GameObject("LethalComFunny");
                    playerTogglesContainer.transform.SetParent(topLeftCorner.transform, false);
                    playerTogglesContainer.SetActive(menuOpen);
                }

                toggle.transform.SetParent(playerTogglesContainer.transform, false);
                RectTransform rectTransform = toggle.GetComponent<RectTransform>();

                rectTransform.position += new Vector3(0, 0.05f*joinedPlayersUI.Count, 0);

                string name = StartOfRound.Instance.allPlayerScripts[clientId].playerUsername;
                toggle.GetComponentInChildren<Text>().text = name;


                joinedPlayersUI.Add(clientId, toggle);
            }
        }

        [HarmonyPatch(typeof(GameNetworkManager), "Singleton_OnClientDisconnectCallback")]
        [HarmonyPostfix]
        public static void OnPlayerLeave()
        {
            
        }

        static IEnumerator SendConfigOnDelay(float delay)
        {
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
        public static ConfigEntry<bool> configHostOnly;

        public MConfig(ConfigFile cfg)
        {
            configDeadSpawns = cfg.Bind(
                    "General.Toggles",
                    "AllowDeadSpawns",
                    false,
                    "Whether or not to allow dead players to spawn mines"
            );
            configHostOnly = cfg.Bind(
                    "General.Toggles",
                    "OnlyHostAllowed",
                    false,
                    "If set to true, only the host can spawn mines"
            );

        }
    }
}