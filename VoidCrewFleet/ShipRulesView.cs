using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.UIElements;

namespace VoidCrewFleet
{
    /// <summary>
    /// The SHIP RULES tab of the Fleet Battle lobby screen (PvpTeamScreen). The host picks which MK tiers, categories and
    /// single options a CUSTOM loadout may use, and how many starting alloys it may set; everyone else sees the same page
    /// and can't change it. The categories and options are Ship Customizer's own (its names, codes and tiers), so the tab
    /// only shows with a Customizer that lists them. Built like the loadout terminal: three sections with big headers over
    /// its decorative lines, a list like its LOADOUTS list, rows like its RESOURCES list, and its small buttons.
    /// </summary>
    internal static class ShipRulesView
    {
        private sealed class Option
        {
            public string Code;
            public string Category;
            public int Tier;
            public string Name;
        }

        private static readonly Button[] TierButtons = new Button[3];
        private static List<Option> _options = new List<Option>();
        private static List<string> _categories = new List<string>();
        private static float _optionsRead = -100f;

        private static VisualElement _view;
        private static Button _alloysButton;
        private static Button _clearButton;
        private static ScrollView _categoryList;
        private static ScrollView _itemList;
        private static Label _itemsTitle;
        private static Label _note;
        private static string _selected;

        // What the tab shows, as last drawn: it is drawn again only when one of these changed (or Redraw asks).
        private static bool _drawn;
        private static string _drawnJson;
        private static bool _drawnEdit;
        private static bool _drawnChoosing;
        private static string _drawnSelected;
        private static int _drawnOptions;

        /// <summary>Whether the tab can show: the Customizer lists what the rules can restrict.</summary>
        public static bool Available
        {
            get { return CustomizerLink.HasRules; }
        }

        public static VisualElement Build()
        {
            // As tall as the lobby screen's teams, which it replaces: both fill the screen's height.
            _view = Ui.HorizontalRow();
            _view.style.alignItems = Align.Stretch;
            _view.style.justifyContent = Justify.SpaceBetween;
            _view.style.flexGrow = 1;
            _view.style.minHeight = 0;

            Label header;
            VisualElement content;
            VisualElement left = Ui.GameSection("ALLOWED TIERS", Ui.SharedWidth, out header, out content);
            content.Add(Note("Weapons, modules and items of these MK tiers may be fitted. The fabricator may start at the highest one."));
            VisualElement tiers = Ui.HorizontalRow();
            tiers.style.marginTop = 10;
            for (int i = 0; i < 3; i++)
            {
                int tier = i + 1;
                TierButtons[i] = Ui.GameButton(ShipRules.TierName(tier), () => ToggleTier(tier), "metem-button-size__small");
                TierButtons[i].style.flexGrow = 1;
                TierButtons[i].style.paddingLeft = 4;
                TierButtons[i].style.paddingRight = 4;
                Ui.Margin(TierButtons[i], 0, i < 2 ? 8 : 0, 0, 0);
                tiers.Add(TierButtons[i]);
            }
            content.Add(tiers);

            Label alloys = Ui.GameLabel("STARTING ALLOYS", "ui-text-size__mediumPlus", "ui-text-color__white", "ui-text-dropshadow");
            alloys.style.unityFontStyleAndWeight = FontStyle.Bold;
            alloys.style.marginTop = 24;
            content.Add(alloys);
            content.Add(Note("The most starting alloys a CUSTOM loadout may set."));
            _alloysButton = Ui.GameButton("", CycleAlloys, "metem-button-size__small");
            Ui.Margin(_alloysButton, 10, 0, 0, 0);
            content.Add(_alloysButton);

            var spacer = new VisualElement { pickingMode = PickingMode.Ignore };
            spacer.style.flexGrow = 1;
            content.Add(spacer);
            _note = Note("");
            _note.style.marginBottom = 8;
            content.Add(_note);
            _clearButton = Ui.GameButton("CLEAR ALL RULES", Clear, "metem-button-size__small");
            Ui.Margin(_clearButton, 0, 0, 0, 0);
            content.Add(_clearButton);
            _view.Add(left);

            VisualElement middle = Ui.GameSection("CATEGORIES", Ui.SharedWidth, out header, out content);
            content.Add(Note("Pick one to list its options. Its button blocks the whole category."));
            _categoryList = new ScrollView(ScrollViewMode.Vertical);
            _categoryList.style.flexGrow = 1;
            _categoryList.style.marginTop = 8;
            content.Add(_categoryList);
            _view.Add(middle);

            VisualElement right = Ui.GameSection("", Ui.SharedWidth, out _itemsTitle, out content);
            content.Add(Note("Click an option to ban it, and again to allow it."));
            _itemList = new ScrollView(ScrollViewMode.Vertical);
            _itemList.style.flexGrow = 1;
            _itemList.style.marginTop = 8;
            content.Add(_itemList);
            _view.Add(right);
            PvpTeamScreen.SpaceSections(_view);

            _drawn = false;
            return _view;
        }

        private static Label Note(string text)
        {
            Label note = Ui.GameLabel(text, "ui-text-size__medium", "ui-text-dropshadow");
            note.style.fontSize = 18;
            note.style.color = Ui.GameBrightGrey;
            note.style.whiteSpace = WhiteSpace.Normal;
            return note;
        }

        // ---------------------------------------------------------------- the Customizer's options

        private static void ReadOptions()
        {
            if (_options.Count > 0 || Time.unscaledTime - _optionsRead < 2f) return;
            _optionsRead = Time.unscaledTime;
            var options = new List<Option>();
            var categories = new List<string>();
            foreach (string line in CustomizerLink.GetRuleOptions())
            {
                if (string.IsNullOrEmpty(line)) continue;
                string[] parts = line.Split(new[] { '|' }, 4);
                if (parts.Length < 4 || parts[0].Length == 0) continue;
                int tier;
                int.TryParse(parts[2], out tier);
                var option = new Option { Code = parts[0].Trim().ToLowerInvariant(), Category = parts[1].Trim().ToUpperInvariant(), Tier = tier, Name = parts[3].Trim() };
                options.Add(option);
                if (!categories.Contains(option.Category)) categories.Add(option.Category);
            }
            _options = options;
            _categories = categories;
            if (options.Count > 0)
            {
                _drawn = false;
                Log.Info("Ship rules: Ship Customizer lists " + options.Count + " options in " + categories.Count + " categories.");
            }
        }

        // ---------------------------------------------------------------- updating

        /// <summary>A few times a second while the lobby screen shows this tab.</summary>
        public static void Refresh()
        {
            if (_view == null) return;
            ReadOptions();
            // The rules' JSON as the room (or the host's pending change) holds it: the same string until the rules change.
            string json = ShipRules.ShownJson;
            bool edit = ShipRules.CanEdit;
            bool choosing = PvpTeams.HubChoosing;
            if (_selected == null && _categories.Count > 0) _selected = _categories[0];
            if (_drawn && ReferenceEquals(json, _drawnJson) && edit == _drawnEdit && choosing == _drawnChoosing && _selected == _drawnSelected &&
                _options.Count == _drawnOptions) return;
            _drawn = true;
            _drawnJson = json;
            _drawnEdit = edit;
            _drawnChoosing = choosing;
            _drawnSelected = _selected;
            _drawnOptions = _options.Count;
            ShipRules.Rules rules = ShipRules.Shown;
            try
            {
                PickingMode controls = edit ? PickingMode.Position : PickingMode.Ignore;
                for (int i = 0; i < 3; i++)
                {
                    TierButtons[i].EnableInClassList("metem-button-selected", rules.TierAllowed(i + 1));
                    TierButtons[i].pickingMode = controls;
                }
                _alloysButton.text = ShipRules.AlloysText(rules.MaxAlloys);
                _alloysButton.EnableInClassList("metem-button-selected", rules.MaxAlloys != int.MaxValue);
                _alloysButton.pickingMode = controls;
                _clearButton.style.display = edit ? DisplayStyle.Flex : DisplayStyle.None;
                _note.text = edit
                    ? "Rules apply to CUSTOM loadouts (Ship Customizer). The game's own loadouts are unchanged."
                    : (choosing ? "Only the host can change the rules." : "The rules are fixed once the teams are confirmed.");

                _categoryList.Clear();
                if (_categories.Count == 0) _categoryList.Add(Note("Waiting for Ship Customizer's list of options..."));
                foreach (string category in _categories) _categoryList.Add(CategoryRow(category, rules));

                _itemList.Clear();
                _itemsTitle.text = _selected ?? "OPTIONS";
                foreach (Option option in _options.Where(o => o.Category == _selected)) _itemList.Add(OptionRow(option, rules));
            }
            catch (Exception e)
            {
                Log.Failure("Updating the ship rules tab", e);
            }
        }

        /// <summary>
        /// A category, like a row of the terminal's LOADOUTS list: the one whose options are listed is filled orange, and
        /// the row under the pointer white. Its button blocks or allows the whole category.
        /// </summary>
        private static VisualElement CategoryRow(string category, ShipRules.Rules rules)
        {
            bool allowed = rules.CategoryAllowed(category);
            bool picked = category == _selected;
            int banned = _options.Count(o => o.Category == category && !rules.ItemAllowed(o.Code));

            var row = new VisualElement();
            row.style.flexDirection = FlexDirection.Row;
            row.style.alignItems = Align.Center;
            row.style.justifyContent = Justify.SpaceBetween;
            row.style.flexShrink = 0;
            row.style.minHeight = 40;
            Ui.Padding(row, 3, 10);
            Label name = Ui.GameLabel(category + (banned > 0 ? "  (" + banned + " BANNED)" : ""), "ui-text-size__medium");
            name.style.fontSize = 21;
            name.style.unityFontStyleAndWeight = FontStyle.Bold;
            name.style.flexShrink = 1;
            row.Add(name);
            Button toggle = Ui.GameButton(allowed ? "ALLOWED" : "BLOCKED", () => ToggleCategory(category), "metem-button-size__small");
            toggle.pickingMode = ShipRules.CanEdit ? PickingMode.Position : PickingMode.Ignore;
            toggle.style.height = 34;
            toggle.style.fontSize = 16;
            toggle.style.paddingLeft = 12;
            toggle.style.paddingRight = 12;
            toggle.style.paddingTop = 0;
            toggle.style.paddingBottom = 0;
            toggle.style.flexShrink = 0;
            row.Add(toggle);

            Color background = picked ? Ui.GameOrange : Color.clear;
            Color text = picked ? Color.black : (allowed ? Color.white : Ui.GameLightGrey);
            Color toggleTint = allowed ? Color.white : Ui.Warning;
            row.style.backgroundColor = background;
            name.style.color = text;
            toggle.style.unityBackgroundImageTintColor = toggleTint;
            row.RegisterCallback<PointerEnterEvent>(e =>
            {
                if (picked) return;
                row.style.backgroundColor = Color.white;
                name.style.color = Color.black;
                toggle.style.unityBackgroundImageTintColor = Color.black;
                toggle.style.color = Color.black;
            });
            row.RegisterCallback<PointerLeaveEvent>(e =>
            {
                row.style.backgroundColor = background;
                name.style.color = text;
                toggle.style.unityBackgroundImageTintColor = toggleTint;
                toggle.style.color = new StyleColor(StyleKeyword.Null);
            });
            Ui.Click(row, () =>
            {
                _selected = category;
                Redraw();
            });
            return row;
        }

        /// <summary>One option, like a row of the terminal's RESOURCES list: its name, and its tier or why the rules refuse it.</summary>
        private static VisualElement OptionRow(Option option, ShipRules.Rules rules)
        {
            // Ship Customizer bans a weapon's crate along with the weapon (the same GUID), so it can't be built from one.
            bool crateOfBanned = option.Code.StartsWith("c:", StringComparison.Ordinal) && !rules.ItemAllowed("w:" + option.Code.Substring(2));
            bool banned = !rules.ItemAllowed(option.Code) || crateOfBanned;
            bool tierOff = !rules.TierAllowed(option.Tier);
            bool categoryOff = !rules.CategoryAllowed(option.Category);
            VisualElement row = Ui.GameListRow();
            Label name = Ui.GameLabel(option.Name, "ui-text-size__medium");
            name.style.fontSize = 19;
            name.style.flexShrink = 1;
            name.style.whiteSpace = WhiteSpace.Normal;
            name.style.color = banned || tierOff || categoryOff ? Ui.GameLightGrey : Color.white;
            row.Add(name);
            string state = crateOfBanned ? "WEAPON BANNED" : banned ? "BANNED" : tierOff ? "TIER OFF" : categoryOff ? "BLOCKED" : (option.Tier > 0 ? ShipRules.TierName(option.Tier) : "");
            Label tag = Ui.GameLabel(state, "ui-text-size__small");
            tag.style.fontSize = 16;
            tag.style.flexShrink = 0;
            tag.style.unityFontStyleAndWeight = FontStyle.Bold;
            tag.style.color = banned ? Ui.Warning : (tierOff || categoryOff) ? Ui.GameLightGrey : Ui.GameBrightGrey;
            tag.style.marginLeft = 8;
            row.Add(tag);
            if (!ShipRules.CanEdit) return row;
            Ui.Hover(row, Ui.GameDarkGrey, new Color(0.27f, 0.27f, 0.27f, 1f));
            Ui.Click(row, () =>
            {
                if (crateOfBanned) PvpTeamScreen.Flash("Its weapon is banned, which bans its crate too: allow the weapon in WEAPONS.");
                else ToggleBan(option.Code);
            });
            return row;
        }

        // ---------------------------------------------------------------- the host's changes

        /// <summary>Redraws the tab now, instead of on the next tick.</summary>
        private static void Redraw()
        {
            _drawn = false;
            Refresh();
        }

        private static bool MayEdit()
        {
            if (ShipRules.CanEdit) return true;
            PvpTeamScreen.Flash(PvpTeams.HubChoosing ? "Only the host can change the ship rules." : "The ship rules are fixed once the teams are confirmed.");
            return false;
        }

        private static void ToggleTier(int tier)
        {
            if (!MayEdit()) return;
            ShipRules.Change(rules =>
            {
                string digit = tier.ToString();
                if (!rules.TierAllowed(tier)) rules.Tiers += digit;
                else if (rules.Tiers.Length > 1) rules.Tiers = rules.Tiers.Replace(digit, "");
                else PvpTeamScreen.Flash("At least one tier stays allowed.");
            });
            Redraw();
        }

        private static void CycleAlloys()
        {
            if (!MayEdit()) return;
            ShipRules.Change(rules =>
            {
                int index = Array.IndexOf(ShipRules.AlloyChoices, rules.MaxAlloys);
                rules.MaxAlloys = ShipRules.AlloyChoices[(index + 1) % ShipRules.AlloyChoices.Length];
            });
            Redraw();
        }

        private static void ToggleCategory(string category)
        {
            _selected = category;
            if (MayEdit())
            {
                ShipRules.Change(rules =>
                {
                    if (rules.BlockedCategories.Contains(category)) rules.BlockedCategories.Remove(category);
                    else rules.BlockedCategories.Add(category);
                });
            }
            Redraw();
        }

        private static void ToggleBan(string code)
        {
            if (!MayEdit()) return;
            ShipRules.Change(rules =>
            {
                if (rules.Banned.Contains(code)) rules.Banned.Remove(code);
                else rules.Banned.Add(code);
            });
            Redraw();
        }

        private static void Clear()
        {
            if (!MayEdit()) return;
            ShipRules.Change(rules =>
            {
                rules.Tiers = "123";
                rules.BlockedCategories.Clear();
                rules.Banned.Clear();
                rules.MaxAlloys = int.MaxValue;
            });
            Redraw();
        }
    }
}
