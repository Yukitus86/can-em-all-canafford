using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using HarmonyLib;
using Il2Cpp;
using Il2CppTMPro;
using MelonLoader;
using MelonLoader.Utils;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;

[assembly: MelonInfo(typeof(CanAfford.CanAffordMod), "CanAfford", "1.5.2", "Yukitus")]
[assembly: MelonGame("LuisDev", "Can 'Em All")]

namespace CanAfford
{
    /// <summary>
    /// Marks every skill-tree node you can buy right now
    /// (not purchased, requirements met by the game's own check, price &lt;= current money):
    /// node turns neon green, the icon is replaced by the price in black, gently pulsing.
    /// Everything lives on the node's own graphics, so zoom / pan / fade of the tree just work.
    /// Permanent (prestige) upgrades: tinted with the same color (game's own CanPurchase).
    /// </summary>
    public class CanAffordMod : MelonMod
    {
        internal static MelonLogger.Instance Log;

        static MelonPreferences_Category _cat;
        internal static MelonPreferences_Entry<bool> Enabled;
        internal static MelonPreferences_Entry<bool> PermanentEnabled;
        internal static MelonPreferences_Entry<string> Style;
        internal static MelonPreferences_Entry<string> FillColorHex;
        internal static MelonPreferences_Entry<bool> ShowPrice;
        internal static MelonPreferences_Entry<string> PriceColorHex;
        internal static MelonPreferences_Entry<float> PriceSize;
        internal static MelonPreferences_Entry<bool> PriceDollar;
        internal static MelonPreferences_Entry<string> ColorHex;
        internal static MelonPreferences_Entry<float> Thickness;
        internal static MelonPreferences_Entry<bool> Pulse;
        internal static MelonPreferences_Entry<float> PulseSpeed;
        internal static MelonPreferences_Entry<bool> Border;
        internal static MelonPreferences_Entry<string> BorderColorHex;
        internal static MelonPreferences_Entry<float> BorderSize;
        internal static MelonPreferences_Entry<float> PriceOffsetX;
        internal static MelonPreferences_Entry<float> PriceOffsetY;
        internal static MelonPreferences_Entry<Key> ToggleKey;
        internal static MelonPreferences_Entry<Key> DumpKey;

        // parsed settings
        internal static Color FillColor, PriceColor, OutlineColor, BorderColor;
        internal static bool UseFill = true, UseOutline;
        internal static int SettingsVersion; // bumped on every (re)load so labels restyle

        static string _cfgPath, _dumpTrigger;
        static DateTime _cfgTime;
        static float _nextPoll;

        public override void OnInitializeMelon()
        {
            Log = LoggerInstance;
            _cat = MelonPreferences.CreateCategory("CanAfford");
            Enabled = _cat.CreateEntry("Enabled", true, description: "Mark skills you can afford right now (also F7)");
            PermanentEnabled = _cat.CreateEntry("PermanentUpgrades", true, description: "Also mark affordable permanent (prestige) upgrades");
            Style = _cat.CreateEntry("Style", "Fill", description: "Fill = color the whole node, Outline = outline only, Both = fill + outline");
            FillColorHex = _cat.CreateEntry("FillColor", "#39FF14", description: "Node color (#RRGGBB)");
            ShowPrice = _cat.CreateEntry("ShowPrice", true, description: "Replace the icon of affordable skills by their price");
            PriceColorHex = _cat.CreateEntry("PriceColor", "#0B1A0B", description: "Price text color (#RRGGBB)");
            PriceSize = _cat.CreateEntry("PriceSize", 0.62f, description: "Price text box size relative to the node (0.3 - 0.9)");
            PriceDollar = _cat.CreateEntry("PriceDollar", true, description: "Show a $ in front of the price");
            ColorHex = _cat.CreateEntry("Color", "#40FF59", description: "Outline color for Outline/Both style (#RRGGBB or #RRGGBBAA)");
            Thickness = _cat.CreateEntry("Thickness", 4f, description: "Outline thickness in UI pixels");
            Pulse = _cat.CreateEntry("Pulse", true, description: "Let marked nodes pulse");
            PulseSpeed = _cat.CreateEntry("PulseSpeed", 1f, description: "Pulse speed (1 = normal)");
            Border = _cat.CreateEntry("Border", true, description: "Colored border around marked nodes");
            BorderColorHex = _cat.CreateEntry("BorderColor", "#FF1A1A", description: "Border color (#RRGGBB)");
            BorderSize = _cat.CreateEntry("BorderSize", 0.08f, description: "Border width relative to the node size (0.05 - 0.4)");
            PriceOffsetX = _cat.CreateEntry("PriceOffsetX", 0f, description: "Move the price sideways (fraction of node size, + = right)");
            PriceOffsetY = _cat.CreateEntry("PriceOffsetY", 0f, description: "Move the price up/down (fraction of node size, + = up)");
            ToggleKey = _cat.CreateEntry("ToggleKey", Key.F7, description: "Key to switch the marking on/off in game");
            DumpKey = _cat.CreateEntry("DumpKey", Key.F8, description: "Key to write the list of affordable skills to the MelonLoader log");

            // the old v1.0 - v1.2 default green is replaced by neon green
            if (FillColorHex.Value == "#3FC85A") FillColorHex.Value = "#39FF14";

            string ud = MelonEnvironment.UserDataDirectory;
            _cfgPath = Path.Combine(ud, "MelonPreferences.cfg");
            _dumpTrigger = Path.Combine(ud, "CanAfford_dump.txt");
            _cfgTime = SafeTime(_cfgPath);

            ParseSettings();
            Log.Msg($"CanAfford loaded (style {Style.Value}, price {(ShowPrice.Value ? "on" : "off")}). F7 toggles, F8 logs affordable skills.");
        }

        internal static void ParseSettings()
        {
            FillColor = ParseColor(FillColorHex, new Color32(0x39, 0xFF, 0x14, 0xFF));
            PriceColor = ParseColor(PriceColorHex, new Color32(0x0B, 0x1A, 0x0B, 0xFF));
            OutlineColor = ParseColor(ColorHex, new Color32(0x40, 0xFF, 0x59, 0xFF));
            BorderColor = ParseColor(BorderColorHex, new Color32(0xFF, 0x1A, 0x1A, 0xFF));
            switch ((Style.Value ?? "").Trim().ToLowerInvariant())
            {
                case "outline": UseFill = false; UseOutline = true; break;
                case "both": UseFill = true; UseOutline = true; break;
                default: UseFill = true; UseOutline = false; break;
            }
            SettingsVersion++;
        }

        static Color ParseColor(MelonPreferences_Entry<string> e, Color fallback)
        {
            if (ColorUtility.TryParseHtmlString(e.Value, out var c)) return c;
            Log.Warning($"Invalid {e.Identifier} '{e.Value}', using default");
            return fallback;
        }

        static DateTime SafeTime(string p)
        {
            try { return File.Exists(p) ? File.GetLastWriteTimeUtc(p) : DateTime.MinValue; }
            catch { return DateTime.MinValue; }
        }

        public override void OnUpdate()
        {
            var kb = Keyboard.current;
            if (kb != null)
            {
                if (kb[ToggleKey.Value].wasPressedThisFrame)
                {
                    Enabled.Value = !Enabled.Value;
                    _cat.SaveToFile(false);
                    _cfgTime = SafeTime(_cfgPath);
                    Log.Msg("Marking " + (Enabled.Value ? "ON" : "OFF"));
                }
                if (kb[DumpKey.Value].wasPressedThisFrame) Highlighter.Dump();
            }

            // once per second: live-reload the settings file, check helper trigger files
            if (Time.unscaledTime < _nextPoll) return;
            _nextPoll = Time.unscaledTime + 1f;
            try
            {
                var t = SafeTime(_cfgPath);
                if (t != _cfgTime)
                {
                    _cfgTime = t;
                    MelonPreferences.Load();
                    ParseSettings();
                    Log.Msg("Settings reloaded");
                }
                if (File.Exists(_dumpTrigger))
                {
                    File.Delete(_dumpTrigger);
                    Highlighter.Dump();
                    Highlighter.DumpHierarchy();
                }
            }
            catch (Exception e) { Log.Warning("Settings poll: " + e.Message); }
        }

        public override void OnSceneWasUnloaded(int buildIndex, string sceneName) => Highlighter.Forget();
    }

    internal static class Highlighter
    {
        /// <summary>Per-node state so the game's own look can be put back exactly.</summary>
        sealed class Mark
        {
            public bool On;
            public Outline Outline;
            public Color BgOrig, BgSet;
            public bool IconHidden;
            public TextMeshProUGUI Label;
            public bool LabelTried;
            public int LabelStyle = -1;
            public long LabelPrice = -1;
            public Image Border;
            public bool BorderTried;
            public RectTransform BorderRef; // what the border is sized around (node root or background)
        }

        static bool _hierarchyLogged;

        // key = native pointer of the SkillNode / PermanentUpgrade
        static readonly Dictionary<IntPtr, Mark> Marks = new();

        static int _moneyFrame = -1;
        static long _money;

        internal static long Money
        {
            get
            {
                int f = Time.frameCount;
                if (f != _moneyFrame)
                {
                    _moneyFrame = f;
                    var mm = MoneyManager.Instance;
                    _money = mm != null ? mm.Money : long.MinValue;
                }
                return _money;
            }
        }

        internal static void Forget() => Marks.Clear();

        internal static bool CanAfford(SkillNode node)
        {
            var skill = node.skill;
            if (skill == null || skill.isPurchased) return false;
            if (node.IsLockedForDemo()) return false;
            if (!node.AreRequirementsMet()) return false;
            long money = Money;
            return money != long.MinValue && skill.price <= money;
        }

        internal static bool CanAfford(PermanentUpgrade up) =>
            up.skill != null && !up.IsPurchased && up.CanPurchase;

        /// <summary>0..1 smooth pulse shared by all nodes so they breathe in sync (1 = no pulse).</summary>
        static float PulseT() =>
            CanAffordMod.Pulse.Value
                ? 0.5f + 0.5f * Mathf.Sin(Time.unscaledTime * 3.2f * Mathf.Max(0.05f, CanAffordMod.PulseSpeed.Value))
                : 1f;

        static bool Same(Color a, Color b) =>
            Mathf.Abs(a.r - b.r) < 0.002f && Mathf.Abs(a.g - b.g) < 0.002f &&
            Mathf.Abs(a.b - b.b) < 0.002f && Mathf.Abs(a.a - b.a) < 0.002f;

        /// <summary>1000 -> $1,000 · 25000 -> $25K · 1500000 -> $1.5M</summary>
        internal static string FormatPrice(long p)
        {
            var ci = CultureInfo.InvariantCulture;
            string s;
            if (p < 10_000) s = p.ToString("#,0", ci);
            else if (p < 1_000_000) s = Short(p / 1_000d) + "K";
            else if (p < 1_000_000_000) s = Short(p / 1_000_000d) + "M";
            else s = Short(p / 1_000_000_000d) + "B";
            return CanAffordMod.PriceDollar.Value ? "$" + s : s;

            static string Short(double v) =>
                v < 100 ? Math.Round(v, 1, MidpointRounding.AwayFromZero).ToString("0.#", CultureInfo.InvariantCulture)
                        : Math.Round(v, 0, MidpointRounding.AwayFromZero).ToString("0", CultureInfo.InvariantCulture);
        }

        internal static void Apply(Component owner, Image bg, Image icon, long price, bool on, bool allowLabel)
        {
            if (bg == null) return;
            IntPtr key = owner.Pointer;
            Marks.TryGetValue(key, out var m);
            if (m == null)
            {
                if (!on) return;
                m = new Mark();
                Marks[key] = m;
            }

            if (on)
            {
                float p = PulseT();
                bool wasOn = m.On;
                m.On = true; // set first: if anything below throws, switching off still restores the node

                if (CanAffordMod.UseFill)
                {
                    // pulse between full neon and a slightly deeper shade (never washed out)
                    var c = Color.Lerp(CanAffordMod.FillColor * 0.80f, CanAffordMod.FillColor, p);
                    var cur = bg.color;
                    if (!wasOn || !Same(cur, m.BgSet)) m.BgOrig = cur; // game wrote its own color -> new original
                    c.a = m.BgOrig.a;
                    bg.color = c;
                    m.BgSet = c;
                }

                if (allowLabel && CanAffordMod.ShowPrice.Value)
                {
                    if (m.Label == null && !m.LabelTried) { m.LabelTried = true; m.Label = CreateLabel(bg); }
                    if (m.Label != null)
                    {
                        if (m.LabelStyle != CanAffordMod.SettingsVersion)
                        {
                            StyleLabel(m.Label, bg);
                            m.LabelStyle = CanAffordMod.SettingsVersion;
                            m.LabelPrice = -1;
                        }
                        if (m.LabelPrice != price) { m.Label.text = FormatPrice(price); m.LabelPrice = price; }
                        float s = 1f + 0.07f * p;
                        m.Label.rectTransform.localScale = new Vector3(s, s, 1f);
                        var br = bg.rectTransform.rect;
                        m.Label.rectTransform.anchoredPosition = new Vector2(
                            br.center.x + br.width * CanAffordMod.PriceOffsetX.Value,
                            br.center.y + br.height * CanAffordMod.PriceOffsetY.Value);
                        if (!m.Label.gameObject.activeSelf) m.Label.gameObject.SetActive(true);
                        if (icon != null && icon.enabled) { icon.enabled = false; m.IconHidden = true; }
                    }
                }
                else
                {
                    HideLabel(m, icon);
                }

                if (CanAffordMod.Border.Value)
                {
                    if (m.Border == null && !m.BorderTried) { m.BorderTried = true; m.Border = CreateBorder(owner.transform, bg, m); }
                    if (m.Border != null)
                    {
                        PlaceBorder(m.Border.rectTransform, m.BorderRef);
                        m.Border.color = CanAffordMod.BorderColor;
                        if (!m.Border.enabled) m.Border.enabled = true;
                    }
                }
                else if (m.Border != null && m.Border.enabled) m.Border.enabled = false;

                if (CanAffordMod.UseOutline)
                {
                    if (m.Outline == null)
                    {
                        m.Outline = bg.gameObject.AddComponent<Outline>();
                        m.Outline.useGraphicAlpha = false;
                    }
                    var oc = CanAffordMod.OutlineColor;
                    oc.a *= 0.65f + 0.35f * p;
                    float t = CanAffordMod.Thickness.Value;
                    m.Outline.effectColor = oc;
                    m.Outline.effectDistance = new Vector2(t, -t);
                    if (!m.Outline.enabled) m.Outline.enabled = true;
                }
                else if (m.Outline != null && m.Outline.enabled) m.Outline.enabled = false;

                m.On = true;
            }
            else if (m.On)
            {
                // put the game's color back, unless the game set a new one itself (e.g. purchased color)
                if (Same(bg.color, m.BgSet)) bg.color = m.BgOrig;
                HideLabel(m, icon);
                if (m.Outline != null) m.Outline.enabled = false;
                if (m.Border != null) m.Border.enabled = false;
                m.On = false;
            }
        }

        /// <summary>
        /// Border = the node's own fill shape (circle / diamond), colored and a bit bigger than the
        /// whole node, placed BEHIND everything of the node, so only the rim outside the node shows.
        /// </summary>
        static Image CreateBorder(Transform root, Image bg, Mark m)
        {
            if (!_hierarchyLogged)
            {
                _hierarchyLogged = true;
                try
                {
                    var sb = new StringBuilder();
                    Walk(root, 0, sb);
                    CanAffordMod.Log.Msg("Node layout (for diagnostics):\n" + sb);
                }
                catch (Exception e) { CanAffordMod.Log.Warning("Node layout dump failed: " + e.Message); }
            }

            var rootRt = root.TryCast<RectTransform>();
            bool bgIsRoot = bg.transform.Pointer == root.Pointer;
            // container + index so the border renders before (= behind) all of the node's graphics
            Transform container = bgIsRoot ? root.parent : root;
            if (container == null) return null;
            m.BorderRef = (!bgIsRoot && rootRt != null && rootRt.rect.width > 1f) ? rootRt : bg.rectTransform;

            var go = new GameObject("CanAffordBorder");
            var rt = go.AddComponent<RectTransform>();
            rt.SetParent(container, false);
            if (bgIsRoot) rt.SetSiblingIndex(root.GetSiblingIndex()); else rt.SetAsFirstSibling();
            rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0.5f);
            rt.pivot = new Vector2(0.5f, 0.5f);

            var img = go.AddComponent<Image>();
            img.sprite = bg.sprite;
            img.type = bg.type;
            img.preserveAspect = bg.preserveAspect;
            img.pixelsPerUnitMultiplier = bg.pixelsPerUnitMultiplier;
            img.raycastTarget = false;
            img.enabled = false;
            return img;
        }

        /// <summary>Center the border on the reference rect, size = reference * (1 + 2 * BorderSize).</summary>
        static void PlaceBorder(RectTransform border, RectTransform reference)
        {
            if (reference == null) return;
            var r = reference.rect;
            float k = 1f + 2f * Mathf.Clamp(CanAffordMod.BorderSize.Value, 0f, 0.6f);
            border.position = reference.TransformPoint(r.center);
            border.rotation = reference.rotation;
            // express the reference size in the border's parent space
            var parent = border.parent;
            Vector3 ls = reference.lossyScale, ps = parent != null ? parent.lossyScale : Vector3.one;
            float sx = ps.x != 0f ? ls.x / ps.x : 1f, sy = ps.y != 0f ? ls.y / ps.y : 1f;
            border.localScale = Vector3.one;
            border.sizeDelta = new Vector2(r.width * sx * k, r.height * sy * k);
        }

        static void HideLabel(Mark m, Image icon)
        {
            if (m.Label != null && m.Label.gameObject.activeSelf) m.Label.gameObject.SetActive(false);
            if (m.IconHidden)
            {
                if (icon != null) icon.enabled = true;
                m.IconHidden = false;
            }
        }

        static TMP_FontAsset _font;
        static Material _fontMat;

        static TMP_FontAsset GameFont()
        {
            if (_font != null) return _font;
            try
            {
                // the font the game uses for skill prices in its own popup
                var pop = SkillPopUp.Instance;
                if (pop != null && pop.priceText != null && pop.priceText.font != null)
                {
                    _font = pop.priceText.font;
                    _fontMat = pop.priceText.fontSharedMaterial;
                    return _font;
                }
            }
            catch { /* fall through to TMP default */ }
            return TMP_Settings.defaultFontAsset;
        }

        /// <summary>Price text as a child of the node's background image, centered inside it.</summary>
        static TextMeshProUGUI CreateLabel(Image bg)
        {
            var go = new GameObject("CanAffordPrice");
            var rt = go.AddComponent<RectTransform>();
            rt.SetParent(bg.rectTransform, false);
            rt.SetAsLastSibling();
            rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0.5f);
            rt.pivot = new Vector2(0.5f, 0.5f);
            rt.anchoredPosition = Vector2.zero;

            var tmp = go.AddComponent<TextMeshProUGUI>();
            tmp.raycastTarget = false;
            tmp.text = "";
            go.SetActive(false);
            return tmp;
        }

        static void StyleLabel(TextMeshProUGUI tmp, Image bg)
        {
            var font = GameFont();
            if (font != null) tmp.font = font;
            if (_fontMat != null && font == _font) tmp.fontSharedMaterial = _fontMat;

            var r = bg.rectTransform.rect;
            float k = Mathf.Clamp(CanAffordMod.PriceSize.Value, 0.3f, 0.9f);
            float w = r.width * k, h = r.height * k * 0.6f;
            tmp.rectTransform.sizeDelta = new Vector2(w, h);

            tmp.alignment = TextAlignmentOptions.CenterGeoAligned; // centered on the glyphs, not the line box
            tmp.fontStyle = FontStyles.Bold;
            tmp.color = CanAffordMod.PriceColor;
            tmp.enableAutoSizing = true;
            tmp.fontSizeMin = 4f;
            tmp.fontSizeMax = h;
            tmp.characterSpacing = -2f;
            tmp.overflowMode = TextOverflowModes.Overflow;
            tmp.margin = Vector4.zero;
        }

        internal static void Dump()
        {
            var nodes = UnityEngine.Object.FindObjectsByType<SkillNode>(FindObjectsInactive.Include, FindObjectsSortMode.None);
            var sb = new StringBuilder();
            int n = 0;
            foreach (var node in nodes)
            {
                if (node == null || !CanAfford(node)) continue;
                n++;
                sb.Append("  ").Append((int)node.skill.skillId).Append(' ').Append(node.skill.skillId.ToString())
                  .Append(" $").Append(node.skill.price).AppendLine();
            }
            CanAffordMod.Log.Msg($"Money ${Money} - {n} affordable of {nodes.Length} skills:\n{sb}");

            if (!CanAffordMod.PermanentEnabled.Value) return;
            var ups = UnityEngine.Object.FindObjectsByType<PermanentUpgrade>(FindObjectsInactive.Include, FindObjectsSortMode.None);
            sb.Clear(); n = 0;
            foreach (var up in ups)
            {
                if (up == null || !CanAfford(up)) continue;
                n++;
                sb.Append("  ").Append((int)up.skill.skillId).Append(' ').Append(up.skill.skillId.ToString())
                  .Append(" price ").Append(up.skill.price).AppendLine();
            }
            CanAffordMod.Log.Msg($"{n} affordable of {ups.Length} permanent upgrades:\n{sb}");
        }

        /// <summary>Logs the GameObject hierarchy of one skill node (layout diagnostics).</summary>
        internal static void DumpHierarchy()
        {
            var nodes = UnityEngine.Object.FindObjectsByType<SkillNode>(FindObjectsInactive.Include, FindObjectsSortMode.None);
            SkillNode pick = null;
            foreach (var nd in nodes) { if (nd != null && CanAfford(nd)) { pick = nd; break; } }
            if (pick == null) foreach (var nd in nodes) { if (nd != null && nd.gameObject.activeInHierarchy) { pick = nd; break; } }
            if (pick == null && nodes.Length > 0) pick = nodes[0];
            if (pick == null) { CanAffordMod.Log.Msg("No active skill node to dump"); return; }
            var sb = new StringBuilder();
            Walk(pick.transform, 0, sb);
            CanAffordMod.Log.Msg($"Hierarchy of {pick.skill?.skillId}:\n{sb}");
        }

        // ColorUtility.ToHtmlStringRGBA is stripped from this game's IL2CPP build
        static string Hex(Color c)
        {
            static int B(float v) => Mathf.Clamp(Mathf.RoundToInt(v * 255f), 0, 255);
            return $"#{B(c.r):X2}{B(c.g):X2}{B(c.b):X2}{B(c.a):X2}";
        }

        static void Walk(Transform t, int depth, StringBuilder sb)
        {
            sb.Append(new string(' ', depth * 2)).Append(t.name).Append(t.gameObject.activeSelf ? "" : " (inactive)");
            var rt = t.TryCast<RectTransform>();
            if (rt != null) sb.Append($" rect={rt.rect.width:0}x{rt.rect.height:0} scale={rt.localScale.x:0.##}");
            var img = t.GetComponent<Image>();
            if (img != null) sb.Append($" Image(color={Hex(img.color)}, enabled={img.enabled}, sprite={(img.sprite != null ? img.sprite.name : "-")})");
            sb.AppendLine();
            for (int i = 0; i < t.childCount; i++) Walk(t.GetChild(i), depth + 1, sb);
        }
    }

    [HarmonyPatch(typeof(SkillNode), nameof(SkillNode.Update))]
    internal static class SkillNodeUpdatePatch
    {
        static bool _logged;

        static void Postfix(SkillNode __instance)
        {
            try
            {
                bool on = CanAffordMod.Enabled.Value && Highlighter.CanAfford(__instance);
                long price = __instance.skill != null ? __instance.skill.price : 0;
                Highlighter.Apply(__instance, __instance.background, __instance.icon, price, on, allowLabel: true);
            }
            catch (Exception e)
            {
                if (_logged) return;
                _logged = true;
                CanAffordMod.Log.Error("SkillNode marking failed: " + e);
            }
        }
    }

    [HarmonyPatch(typeof(PermanentUpgrade), nameof(PermanentUpgrade.Update))]
    internal static class PermanentUpgradeUpdatePatch
    {
        static bool _logged;

        static void Postfix(PermanentUpgrade __instance)
        {
            try
            {
                bool on = CanAffordMod.Enabled.Value && CanAffordMod.PermanentEnabled.Value && Highlighter.CanAfford(__instance);
                // permanent cards already show their price -> color only, no price label
                Highlighter.Apply(__instance, __instance.background, __instance.icon, 0, on, allowLabel: false);
            }
            catch (Exception e)
            {
                if (_logged) return;
                _logged = true;
                CanAffordMod.Log.Error("PermanentUpgrade marking failed: " + e);
            }
        }
    }
}
