using System;
using BepInEx;
using HarmonyLib;
using UnityEngine;

namespace VoidCrewFleet
{
    /// <summary>
    /// BepInEx entry point. Applies the game patches and starts the runtime object. Each start-up step is guarded: if
    /// one fails on a player's PC (a different BepInEx, Harmony or game version), the error is logged with what failed
    /// and the rest of the plugin still starts.
    /// </summary>
    [BepInPlugin(Guid, Name, Version)]
    // Optional: when Ship Customizer is installed, BepInEx loads it first (Fleet never needs it).
    [BepInDependency(CustomizerLink.PluginGuid, BepInDependency.DependencyFlags.SoftDependency)]
    public class Plugin : BaseUnityPlugin
    {
        public const string Guid = "com.captainchunck.voidcrew.fleet";
        public const string Name = "Void Crew Fleet";
        public const string Version = "0.3.0";

        private GameObject _runtime;
        private bool _running;

        private void Awake()
        {
            Log.Init(Logger);
            try
            {
                Settings.Bind(Config);
            }
            catch (Exception e)
            {
                Log.Error(Name + " " + Version + " could not read its settings file (" + Config.ConfigFilePath + "), so it is not running. " +
                          "Delete that file and start the game again. Details: " + e);
                return;
            }

            if (!Settings.Enabled.Value)
            {
                Log.Info(Name + " " + Version + " is turned off in its config (General > Enabled).");
                return;
            }

            Log.Info(Name + " " + Version + " | Unity " + Application.unityVersion + " | game " + Application.version +
                     " | BepInEx " + typeof(BaseUnityPlugin).Assembly.GetName().Version + ", Harmony " + typeof(Harmony).Assembly.GetName().Version);

            Step("Applying the game patches", () => Patcher.ApplyAll(new Harmony(Guid)));
            Step("Registering with the game's mod list and marking hosted rooms [mod_session]", ModRegistration.Register);
            Step("Listening for fleet messages", Features.Listen);
            Patcher.Report(Features.All);
            _running = Step("Starting the runtime", CreateRuntime);
        }

        private void Update()
        {
            if (_running && _runtime == null)
            {
                Log.Warn("The plugin's runtime object was destroyed by the game; recreating it.");
                _running = Step("Restarting the runtime", CreateRuntime);
            }
        }

        /// <summary>Runs one start-up step; a failure is logged in full and the next steps still run.</summary>
        private static bool Step(string what, Action step)
        {
            try
            {
                step();
                return true;
            }
            catch (Exception e)
            {
                Log.Error(what + " failed, so part of the plugin may not work. Please send BepInEx\\LogOutput.log with a bug report. Details: " + e);
                return false;
            }
        }

        private void CreateRuntime()
        {
            _runtime = new GameObject("VoidCrewFleet");
            DontDestroyOnLoad(_runtime);
            _runtime.hideFlags = HideFlags.HideAndDontSave;
            _runtime.AddComponent<FleetRuntime>();
        }
    }
}
