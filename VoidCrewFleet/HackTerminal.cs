using System;
using System.Reflection;
using CG;
using CG.Client.UserData;
using CG.Game.Player;
using CG.Network;
using CG.Objects;
using CG.Ship.Hull;
using CG.Ship.Object;
using Client.Player.Interactions;
using Photon.Pun;
using ResourceAssets;
using RuntimeAssets;
using UI.LoadoutTerminal;
using UnityEngine;

namespace VoidCrewFleet
{
    /// <summary>
    /// The PvP item "Airlock Hacking Terminal": the Data Shard's model under an item GUID of its own. Every client
    /// registers it at start-up, the way the game registers the items of its own mod bundles, and the fabricators of both
    /// ships offer it in PvP. Printing, the network spawn, carrying, storing and the end tally are the game's own code. This
    /// class adds the item, its fabricator entry and a model of it for the panel; <see cref="PvpHack"/> does the hacking.
    /// </summary>
    internal static class HackTerminal
    {
        public const string Feature = "PvP hacking terminal";
        public const int DefaultCost = 10;

        /// <summary>The item's GUID, the same on every client (same family as the plugin's other GUIDs).</summary>
        public static readonly GUIDUnion Guid = new GUIDUnion("f1ee7c0de5f1ee7c0de5f1ee7c0de5b2");
        /// <summary>The model: Data Shard: Minefield (an inert little cartridge until a shard reader takes it).</summary>
        private static readonly GUIDUnion ModelGuid = new GUIDUnion("d2427729f742ec04d98aa89b0575e9e0");

        private const string Header = "AIRLOCK HACKING TERMINAL";
        private const string Body = "PvP: plug it into the control panel outside an enemy airlock (hold Fire on the panel). After a one-minute hack, " +
                                    "your whole team can open that ship's doors for five minutes, except its cockpit. Their crew can pull it out while it hacks.";

        private static readonly MethodInfo AddPrintableMethod = GameMember.Method(typeof(CraftData), "AddCraftableAsPrintable");
        private static readonly MethodInfo SetupCategoriesMethod = GameMember.Method(typeof(CraftData), "SetupCategories");

        private static GameObject _template;
        private static int _cost = -1;
        private static bool _attempted;

        public static bool Registered { get; private set; }

        public static bool Is(CarryableObject carryable)
        {
            return carryable != null && carryable.assetGuid == Guid;
        }

        /// <summary>The hacking terminal with this photon view ID (the same on every client), or null.</summary>
        public static CarryableObject Find(int view)
        {
            PhotonView found = view > 0 ? PhotonView.Find(view) : null;
            CarryableObject item = found != null ? found.GetComponent<CarryableObject>() : null;
            return Is(item) ? item : null;
        }

        /// <summary>What the local player holds, when it is a hacking terminal.</summary>
        public static CarryableObject LocalHeld
        {
            get
            {
                LocalPlayer me = LocalPlayer.Instance;
                CarryableObject held = me != null && me.Carrier != null ? me.Payload : null;
                return Is(held) ? held : null;
            }
        }

        private static CloneStarObjectContainer Objects
        {
            get { return ResourceAssetContainer<CloneStarObjectContainer, AbstractCloneStarObject, CloneStarObjectDef>.Instance; }
        }

        // ---------------------------------------------------------------- the item

        /// <summary>Once the game has loaded (main menu), on every client, before any room can be joined.</summary>
        public static void EnsureRegistered()
        {
            if (_attempted || !FleetRuntime.GameReady) return;
            _attempted = true;
            if (!Patcher.IsReady(Feature)) return;
            try
            {
                if (Objects.HasItem(Guid))
                {
                    Registered = true;
                    return;
                }
                CarryableDef modelDef = ResourceAssetContainer<CarryableContainer, CarryableObject, CarryableDef>.Instance.GetAssetDefById(ModelGuid, false);
                CarryableObject model = modelDef != null ? modelDef.Asset : null;
                if (model == null)
                {
                    Log.Warn("Hacking terminal: the game's Data Shard wasn't found, so there is no hacking terminal.");
                    return;
                }

                // The new item's prefab: an inactive copy of the shard's (inactive, so none of its own code ever runs on it).
                GameObject source = model.gameObject;
                bool wasActive = source.activeSelf;
                if (wasActive) source.SetActive(false);
                try
                {
                    _template = UnityEngine.Object.Instantiate(source);
                }
                finally
                {
                    if (wasActive) source.SetActive(true);
                }
                UnityEngine.Object.DontDestroyOnLoad(_template);
                _template.name = "VCF_HackTerminal";
                _template.GetComponent<CarryableObject>().ContainerGuid = Guid;

                // Effect None: never part of the game's check of which mod assets the host and a joining player have (Fleet
                // itself is required in Fleet rooms, and players with Fleet can still join rooms without it). No info: not
                // listed among the mod assets in the main menu.
                if (!RuntimeAssetsRegister.Instance.HasAsset(Guid)) RuntimeAssetsRegister.Instance.RegisterAsset(Guid, _template, SessionModificationEffect.None, null);

                IResourceAssetContextInfo modelInfo = Objects.GetContext(ModelGuid);
                RuntimeAssetConverter.RegisterCarryableToContainers(Guid, Header, Body, modelInfo != null ? modelInfo.Icon : null);
                SetCost(DefaultCost);
                ResourceAssetContainer<CarryableContainer, CarryableObject, CarryableDef>.Instance.GetAssetDefById(Guid)
                    .SetPurchaseCategory(PurchasableItemSubCategory.Resources_Support);
                UnlockItemDef unlock = ResourceAssetContainer<UnlockContainer, UnityEngine.Object, UnlockItemDef>.Instance.GetAssetDefById(Guid);
                // Unlocked for the session only: never written into anyone's profile.
                unlock.UnlockOptions = new UnlockOptions { UnlockCriteria = UnlockCriteriaType.Session };
                unlock.rarity = RarityType.Epic;
                var context = Objects.GetContext(Guid) as ContextInfo;
                if (context != null) context.hideFlags |= HideFlags.DontUnloadUnusedAsset;

                Registered = true;
                Log.Info("The Airlock Hacking Terminal is ready (PvP fabricators offer it).");
                Describe();
            }
            catch (Exception e)
            {
                Log.Failure("Adding the hacking terminal item", e);
            }
        }

        /// <summary>The fabricator price; the host's, from the fleet plan, so every client's fabricator agrees.</summary>
        private static void SetCost(int cost)
        {
            if (cost == _cost) return;
            ResourceAssetContainer<CraftingDataContainer, UnityEngine.Object, CraftableItemDef>.Instance.GetAssetDefById(Guid).SetCraftingRules(new CraftingRules
            {
                CraftingMethod = CraftMethod.Resource,
                Creation = new ResourceCurrency(ResourceType.Alloys, cost),
                // Not recyclable: it can't be farmed, or learned as a blueprint.
                CanBeRecycled = false,
                Recycle = new ResourceCurrency(ResourceType.Alloys, 0)
            });
            _cost = cost;
        }

        private static void Describe()
        {
            try
            {
                var components = _template.GetComponentsInChildren<Component>(true);
                var names = new System.Collections.Generic.List<string>();
                foreach (Component component in components)
                    if (component != null) names.Add(component.gameObject.name + ":" + component.GetType().Name);
                Log.Verbose("Hacking terminal model: " + string.Join(", ", names.ToArray()));
            }
            catch
            {
                // Diagnostics only.
            }
        }

        // ---------------------------------------------------------------- the fabricator entry

        /// <summary>Each fabricator terminal's shop (both ships, on every client, the host's included) offers it in PvP.</summary>
        [GamePatch(Feature, "FabricatorData", ".ctor", PatchKind.Postfix)]
        private static void FabricatorDataPostfix(FabricatorData __instance)
        {
            if (!Registered || !FleetSession.IsPvP || __instance == null || __instance.CraftingData == null) return;
            try
            {
                FleetPlan plan = FleetSession.Current;
                SetCost(plan != null ? Mathf.Clamp(plan.HackCost, 1, 60) : DefaultCost);
                CraftData shop = __instance.CraftingData;
                if (!shop.Craftables.ContainsKey(Guid)) shop.TryAddItem(Guid);
                PurchasableItem item;
                if (!shop.Craftables.TryGetValue(Guid, out item)) item = AddDirectly(shop);
                if (item == null) return;
                // An entry of the shop, not a blueprint someone found.
                item.SharedSessionUnlocked = false;
                item.IsUnlocked = true;
                Log.InfoOnce("hack shop", "PvP fabricators offer the Airlock Hacking Terminal for " + item.CraftingRules.Creation.Amount + " alloys.");
            }
            catch (Exception e)
            {
                Log.Failure("Adding the hacking terminal to a fabricator", e);
            }
        }

        /// <summary>A fabricator that only offers its own list: add the entry the way the game adds its own.</summary>
        private static PurchasableItem AddDirectly(CraftData shop)
        {
            if (AddPrintableMethod == null || SetupCategoriesMethod == null) return null;
            CraftableItemDef def = ResourceAssetContainer<CraftingDataContainer, UnityEngine.Object, CraftableItemDef>.Instance.GetAssetDefById(Guid, false);
            if (def == null) return null;
            object[] args = { def, -1, false };
            var item = AddPrintableMethod.Invoke(shop, args) as PurchasableItem;
            SetupCategoriesMethod.Invoke(shop, null);
            if (shop.OnShopUpdated != null) shop.OnShopUpdated();
            return item;
        }

        /// <summary>It is not a real data shard: no socket takes it except storage shelves.</summary>
        [GamePatch(Feature, "CG.Ship.Hull.CarryablesSocket", "DoesAccept", PatchKind.Postfix)]
        private static void DoesAcceptPostfix(CarryablesSocket __instance, CarryableObject carryable, ref bool __result)
        {
            try
            {
                if (__result && Is(carryable) && __instance.GetComponentInParent<CarryablesShelf>() == null) __result = false;
            }
            catch (Exception e)
            {
                Log.Failure("Keeping the hacking terminal out of sockets", e);
            }
        }

        /// <summary>The Nano Copy Matrix can't duplicate it.</summary>
        [GamePatch(Feature, "CG.Ship.Object.InteractiveCarryableItemCopier", "GetGuidOfCopyableCarryable", PatchKind.Postfix)]
        private static void CopyPostfix(CarryableObject targetCarryable, ref GUIDUnion __result)
        {
            try
            {
                if (Is(targetCarryable)) __result = GUIDUnion.Empty();
            }
            catch (Exception e)
            {
                Log.Failure("Keeping the hacking terminal from being copied", e);
            }
        }

        // ---------------------------------------------------------------- using it up

        /// <summary>Its owner (the player carrying it): put it down and destroy it, as the game does with a used health pack.</summary>
        public static void Consume(CarryableObject charge)
        {
            if (charge == null || charge.photonView == null || !charge.photonView.IsMine) return;
            if (charge.Carrier != null) charge.Carrier.TryReleaseCarryable().Then((Action)charge.Kill);
            else charge.Kill();
        }

        /// <summary>Anyone (the host, for a plugged-in terminal): destroy a charge, through its owner's game when it's someone else's.</summary>
        public static void Destroy(CarryableObject charge)
        {
            if (charge == null || charge.photonView == null) return;
            if (charge.photonView.IsMine) ObjectFactory.DestroyCloneStarObject(charge);
            else charge.Destroy();
        }

        // ---------------------------------------------------------------- its look on a door

        /// <summary>
        /// A local copy of the item's model (meshes only: no physics, no network), to put on a panel: its flat side lies on
        /// the root's XY plane, its back at the root, facing the root's forward (out of the panel).
        /// </summary>
        public static GameObject BuildModel()
        {
            var root = new GameObject("VCF_HackTerminalModel");
            if (_template == null) return root;
            var pivot = new GameObject("Pivot");
            pivot.transform.SetParent(root.transform, false);
            Transform origin = _template.transform;
            var parts = new System.Collections.Generic.List<GameObject>();
            foreach (Renderer renderer in _template.GetComponentsInChildren<Renderer>(true))
            {
                Mesh mesh = null;
                var skinned = renderer as SkinnedMeshRenderer;
                if (skinned != null) mesh = skinned.sharedMesh;
                else if (renderer is MeshRenderer)
                {
                    MeshFilter filter = renderer.GetComponent<MeshFilter>();
                    mesh = filter != null ? filter.sharedMesh : null;
                }
                if (mesh == null) continue;
                var part = new GameObject(renderer.name);
                part.layer = renderer.gameObject.layer;
                part.transform.SetParent(pivot.transform, false);
                parts.Add(part);
                part.transform.localPosition = origin.InverseTransformPoint(renderer.transform.position);
                part.transform.localRotation = Quaternion.Inverse(origin.rotation) * renderer.transform.rotation;
                part.transform.localScale = renderer.transform.lossyScale;
                part.AddComponent<MeshFilter>().sharedMesh = mesh;
                MeshRenderer copy = part.AddComponent<MeshRenderer>();
                copy.sharedMaterials = renderer.sharedMaterials;
                copy.renderingLayerMask = renderer.renderingLayerMask;
            }
            // Its bounds in its own space: turn the thinnest axis to forward, centre it, and set its back on the panel.
            var local = new Bounds();
            bool first = true;
            foreach (GameObject part in parts)
            {
                Mesh mesh = part.GetComponent<MeshFilter>().sharedMesh;
                Bounds b = mesh.bounds;
                foreach (Vector3 corner in Corners(b))
                {
                    Vector3 p = part.transform.localPosition + part.transform.localRotation * Vector3.Scale(corner, part.transform.localScale);
                    if (first)
                    {
                        local = new Bounds(p, Vector3.zero);
                        first = false;
                    }
                    else local.Encapsulate(p);
                }
            }
            if (!first)
            {
                Vector3 size = local.size;
                Vector3 thin = size.x <= size.y && size.x <= size.z ? Vector3.right : (size.y <= size.z ? Vector3.up : Vector3.forward);
                float thickness = Vector3.Dot(size, thin);
                Quaternion turn = Quaternion.FromToRotation(thin, Vector3.forward);
                pivot.transform.localRotation = turn;
                pivot.transform.localPosition = -(turn * local.center) + Vector3.forward * (thickness * 0.5f);
            }
            return root;
        }

        private static System.Collections.Generic.IEnumerable<Vector3> Corners(Bounds b)
        {
            for (int i = 0; i < 8; i++)
                yield return b.center + Vector3.Scale(b.extents, new Vector3((i & 1) == 0 ? -1 : 1, (i & 2) == 0 ? -1 : 1, (i & 4) == 0 ? -1 : 1));
        }
    }
}
