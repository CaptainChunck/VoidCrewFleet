using System;
using System.Reflection;
using BepInEx.Bootstrap;
using ResourceAssets;

namespace VoidCrewFleet
{
    /// <summary>
    /// The optional link to Void Crew Ship Customizer, so ships 2-6 can use its CUSTOM loadout. Everything goes through
    /// reflection on the Customizer's public ShipCustomizerApi, found only when that plugin is loaded: Fleet never
    /// references or needs it. Without it (or with a version older than the API) every call answers "not CUSTOM".
    /// Ships are numbered 1-6 here, as in the API. Ship 1 is the game's own selection (the Customizer handles its CUSTOM by
    /// itself). Ship 2 works with any Customizer that has the API (its ship-2 members); ships 3-6 need its per-ship members
    /// (Ship Customizer 0.9.0).
    /// </summary>
    internal static class CustomizerLink
    {
        public const string PluginGuid = "com.captainchunck.voidcrew.shipcustomizer";
        private const string ApiTypeName = "VoidCrewShipCustomizer.ShipCustomizerApi";
        private const int ApiVersion = 1;

        private static bool _looked;
        private static bool _installed;
        private static MethodInfo _getDef, _getStandIn, _setTerminalShip, _checkShip2, _setShip2, _buildShip2, _getShip2Alloys, _getRuleOptions;
        private static MethodInfo _checkShip, _setShip, _buildShip, _getShipAlloys, _getDefaultLoadout;

        /// <summary>Whether the Customizer is installed at all (even one without the API).</summary>
        public static bool Installed
        {
            get
            {
                Look();
                return _installed;
            }
        }

        /// <summary>Whether the Customizer's API for a second ship is there.</summary>
        public static bool Linked
        {
            get
            {
                Look();
                return _getDef != null;
            }
        }

        /// <summary>Looked up on first use, never from Awake: the Customizer loads after Fleet.</summary>
        private static void Look()
        {
            if (_looked || !FleetRuntime.GameReady) return;
            _looked = true;
            try
            {
                BepInEx.PluginInfo info;
                if (!Chainloader.PluginInfos.TryGetValue(PluginGuid, out info) || info == null)
                {
                    Log.Verbose("Ship Customizer is not installed.");
                    return;
                }
                _installed = true;
                Type api = FindApi(info);
                FieldInfo version = api != null ? api.GetField("ApiVersion", BindingFlags.Public | BindingFlags.Static) : null;
                if (version == null || (int)version.GetValue(null) < ApiVersion)
                {
                    Log.Info("Ship Customizer is installed without its API for a second ship: ship 2 can be a copy of ship 1 (CUSTOM included), " +
                             "but can't have a CUSTOM loadout of its own until the Customizer is updated.");
                    return;
                }
                MethodInfo getDef = api.GetMethod("GetDef", new[] { typeof(GUIDUnion) });
                _getStandIn = api.GetMethod("GetStandIn", new[] { typeof(GUIDUnion) });
                _setTerminalShip = api.GetMethod("SetTerminalShip", new[] { typeof(int) });
                _checkShip2 = api.GetMethod("CheckShip2", new[] { typeof(GUIDUnion), typeof(int) });
                _setShip2 = api.GetMethod("SetShip2", new[] { typeof(GUIDUnion) });
                _buildShip2 = api.GetMethod("BuildShip2", new[] { typeof(GUIDUnion) });
                // Optional (Ship Customizer 0.5.1+): ship 2's own CUSTOM starting alloys.
                _getShip2Alloys = api.GetMethod("GetShip2Alloys", new[] { typeof(GUIDUnion) });
                // Optional (Ship Customizer 0.7.0+): what Fleet Battle's ship rules can restrict.
                _getRuleOptions = api.GetMethod("GetRuleOptions", Type.EmptyTypes);
                if (_getRuleOptions != null && _getRuleOptions.ReturnType != typeof(string[])) _getRuleOptions = null;
                if (_getShip2Alloys != null && _getShip2Alloys.ReturnType != typeof(int)) _getShip2Alloys = null;
                if (getDef == null || _getStandIn == null || _setTerminalShip == null || _checkShip2 == null || _setShip2 == null || _buildShip2 == null)
                {
                    Log.Warn("Ship Customizer's API is missing members, so ship 2 can't use CUSTOM of its own.");
                    return;
                }
                // Optional (Ship Customizer 0.9.0+): every ship 2-6 gets CUSTOM of its own, and a ship type's default loadout.
                _checkShip = api.GetMethod("CheckShip", new[] { typeof(int), typeof(GUIDUnion), typeof(int) });
                _setShip = api.GetMethod("SetShip", new[] { typeof(int), typeof(GUIDUnion) });
                _buildShip = api.GetMethod("BuildShip", new[] { typeof(int), typeof(GUIDUnion) });
                _getShipAlloys = api.GetMethod("GetShipAlloys", new[] { typeof(int), typeof(GUIDUnion) });
                _getDefaultLoadout = api.GetMethod("GetDefaultLoadout", new[] { typeof(GUIDUnion) });
                if (_checkShip != null && _checkShip.ReturnType != typeof(string)) _checkShip = null;
                if (_buildShip != null && _buildShip.ReturnType != typeof(ShipLoadout)) _buildShip = null;
                if (_getShipAlloys != null && _getShipAlloys.ReturnType != typeof(int)) _getShipAlloys = null;
                if (_getDefaultLoadout != null && _getDefaultLoadout.ReturnType != typeof(ShipLoadoutDataDef)) _getDefaultLoadout = null;
                if (_checkShip == null || _setShip == null || _buildShip == null)
                {
                    _checkShip = null;
                    _setShip = null;
                    _buildShip = null;
                }
                _getDef = getDef;
                ListenForCancels(api);
                Log.Info(PerShip
                    ? "Ship Customizer found: every ship can use a CUSTOM loadout of its own."
                    : "Ship Customizer found: ship 2 can use its own CUSTOM loadout. Ships 3-6 need Ship Customizer 0.9.0 for CUSTOM of their own.");
            }
            catch (Exception e)
            {
                _getDef = null;
                Log.Failure("Linking to Ship Customizer", e);
            }
        }

        /// <summary>
        /// The Customizer's API type. Not through its plugin object: the game can destroy BepInEx's object (and with it the
        /// plugin's component) while the plugin's code keeps running, so look in its assembly by name.
        /// </summary>
        private static Type FindApi(BepInEx.PluginInfo info)
        {
            if (!ReferenceEquals(info.Instance, null))
            {
                Type type = info.Instance.GetType().Assembly.GetType(ApiTypeName, false);
                if (type != null) return type;
            }
            foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type type;
                try
                {
                    type = assembly.GetType(ApiTypeName, false);
                }
                catch
                {
                    continue;
                }
                if (type != null) return type;
            }
            return null;
        }

        /// <summary>The Customizer's CUSTOM definition behind a GUID, or null.</summary>
        public static ShipLoadoutDataDef GetDef(GUIDUnion loadout)
        {
            return loadout.IsNull ? null : Call(() => _getDef, loadout) as ShipLoadoutDataDef;
        }

        public static bool IsCustom(GUIDUnion loadout)
        {
            return GetDef(loadout) != null;
        }

        /// <summary>The game loadout a CUSTOM loadout is built on, or null.</summary>
        public static ShipLoadoutDataDef GetStandIn(GUIDUnion loadout)
        {
            return Call(() => _getStandIn, loadout) as ShipLoadoutDataDef;
        }

        /// <summary>Whether the Customizer has its per-ship members (0.9.0+): every ship 2-6 can have CUSTOM of its own.</summary>
        public static bool PerShip
        {
            get { return Linked && _setShip != null; }
        }

        /// <summary>Whether ship n (1-6) can have a CUSTOM loadout of its own with the Customizer installed here.</summary>
        public static bool CanCustomize(int ship)
        {
            return ship == 1 || (Linked && (ship == 2 || PerShip));
        }

        /// <summary>Which ship (1-6) the loadout terminal sets up: the Customizer's CUSTOM shows and edits that ship's layout.</summary>
        public static void SetTerminalShip(int ship)
        {
            // An older Customizer knows ships 1 and 2 only (it takes any other number for ship 1).
            Call(() => _setTerminalShip, ship);
        }

        /// <summary>Host: null when this player may give ship n (2-6) this CUSTOM loadout now; otherwise why not.</summary>
        public static string CheckShip(int ship, GUIDUnion loadout, int actor)
        {
            if (!Linked) return "Ship Customizer is not available.";
            if (!CanCustomize(ship)) return "CUSTOM on " + HubFleet.ShipLabel(ship - 1) + " needs Ship Customizer 0.9.0 or later on the host's PC.";
            try
            {
                if (PerShip) return _checkShip.Invoke(null, new object[] { ship, loadout, actor }) as string;
                return _checkShip2.Invoke(null, new object[] { loadout, actor }) as string;
            }
            catch (Exception e)
            {
                Log.Failure("Calling Ship Customizer (CheckShip)", e);
                return "CUSTOM could not be checked (see the BepInEx log).";
            }
        }

        /// <summary>Host: ship n (2-6) now uses this loadout (a game loadout or empty: no CUSTOM); the Customizer shares the host's layout.</summary>
        public static void SetShip(int ship, GUIDUnion loadout)
        {
            if (PerShip) Call(() => _setShip, ship, loadout);
            else if (ship == 2) Call(() => _setShip2, loadout);
        }

        /// <summary>Host, at mission start: ship n's CUSTOM ShipLoadout, or null when it can't be used (the Customizer says why).</summary>
        public static ShipLoadout BuildShip(int ship, GUIDUnion loadout)
        {
            if (PerShip) return Call(() => _buildShip, ship, loadout) as ShipLoadout;
            return ship == 2 ? Call(() => _buildShip2, loadout) as ShipLoadout : null;
        }

        /// <summary>Host, at mission start: ship n's CUSTOM starting alloys, or -1 for the game's own amount (or no such API).</summary>
        public static int GetShipAlloys(int ship, GUIDUnion loadout)
        {
            object value = PerShip && _getShipAlloys != null
                ? Call(() => _getShipAlloys, ship, loadout)
                : (ship == 2 ? Call(() => _getShip2Alloys, loadout) : null);
            return value is int ? (int)value : -1;
        }

        /// <summary>A ship type's default loadout as the Customizer sees it (0.9.0+), or null.</summary>
        public static ShipLoadoutDataDef GetDefaultLoadout(GUIDUnion type)
        {
            return _getDefaultLoadout != null ? Call(() => _getDefaultLoadout, type) as ShipLoadoutDataDef : null;
        }

        /// <summary>
        /// When someone without the Customizer joins, it switches a ship's CUSTOM off and says so: per ship (0.9.0+), or ship 2
        /// only. Either way the host gives that ship CUSTOM's stand-in.
        /// </summary>
        private static void ListenForCancels(Type api)
        {
            try
            {
                EventInfo perShip = api.GetEvent("ShipCustomCancelled", BindingFlags.Public | BindingFlags.Static);
                if (perShip != null && perShip.EventHandlerType == typeof(Action<int>))
                {
                    perShip.AddEventHandler(null, (Action<int>)HubFleet.ShipCustomCancelled);
                    return;
                }
                EventInfo cancelled = api.GetEvent("Ship2CustomCancelled", BindingFlags.Public | BindingFlags.Static);
                if (cancelled != null && cancelled.EventHandlerType == typeof(Action)) cancelled.AddEventHandler(null, (Action)(() => HubFleet.ShipCustomCancelled(2)));
            }
            catch (Exception e)
            {
                Log.Failure("Listening to Ship Customizer's CUSTOM notices", e);
            }
        }

        /// <summary>Whether the Customizer can follow Fleet Battle's ship rules (it lists what they can restrict).</summary>
        public static bool HasRules
        {
            get { return Linked && _getRuleOptions != null; }
        }

        /// <summary>Every option a CUSTOM loadout can use, as "code|category|tier|name"; empty without the API or before the hub loads.</summary>
        public static string[] GetRuleOptions()
        {
            return Call(() => _getRuleOptions) as string[] ?? new string[0];
        }

        /// <summary>Calls an API member; the member is read after linking, so the very first call works too.</summary>
        private static object Call(Func<MethodInfo> member, params object[] args)
        {
            if (!Linked) return null;
            MethodInfo method = member();
            if (method == null) return null;
            try
            {
                return method.Invoke(null, args);
            }
            catch (Exception e)
            {
                Log.Failure("Calling Ship Customizer (" + method.Name + ")", e);
                return null;
            }
        }
    }
}
