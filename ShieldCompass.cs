using System;
using System.Collections.Generic;
using System.Linq;
using BepInEx;
using BepInEx.Configuration;
using UnityEngine;
using UnityEngine.UI;

namespace DSPShieldCompass
{
    [BepInPlugin("com.unclecompass.dsp.shieldcompass", "DSP Shield Compass", "0.2.6")]
    public sealed class ShieldCompass : BaseUnityPlugin
    {
        // Planetary Shield Generator item/prototype ID in vanilla DSP.
        private const int ShieldProtoId = 3008;
        private const float MatchRadiusDegrees = 6f;
        private const float BuildSpotToleranceDegrees = 2f;
        private const float ScanIntervalSeconds = 0.75f;

        // SSOT: poles first, then six equatorial positions at 60-degree intervals.
        private static readonly Target[] Targets =
        {
            new Target("North Pole", 90f, 0f),
            new Target("South Pole", -90f, 0f),
            new Target("Equator 0 E", 0f, 0f),
            new Target("Equator 60 E", 0f, 60f),
            new Target("Equator 120 E", 0f, 120f),
            new Target("Equator 180", 0f, 180f),
            new Target("Equator 120 W", 0f, -120f),
            new Target("Equator 60 W", 0f, -60f)
        };

        private ConfigEntry<bool> enabledSetting;
        // Independent right-edge dock. DockY is normalized (0..1) for resolution-independent persistence.
        private ConfigEntry<float> dockY;
        private ConfigEntry<bool> dockHidden;
        private ConfigEntry<int> dockFontSize;
        private ConfigEntry<float> dockUiScale;
        private ConfigEntry<int> compassFontSize;
        private ConfigEntry<float> compassUiScale;
        private bool dockSettingsOpen;
        private Texture2D dockShieldIcon;
        private Texture2D dockSettingsIcon;
        private bool dockDragging;
        private bool dockExpanded;
        // SSOT: independent hover dwell timers; never switch dock state in the same instant as pointer entry/exit.
        private const float DockOpenDelaySeconds = 0.20f;
        private const float DockCloseDelaySeconds = 0.32f;
        private float dockHoverEnterAt = -1f;
        private float dockHoverLeaveAt = -1f;
        private float dockDragOffset;
        private float dockPosition = -1f;
        private GUIStyle dockButtonStyle;
        private GUIStyle dockIconOnlyStyle;
        private GUIStyle dockTextStyle;
        private GUIStyle dockHeaderStyle;
        private static readonly Dictionary<string, DockModEntry> RegisteredMods =
            new Dictionary<string, DockModEntry>(StringComparer.Ordinal);
        private sealed class DockModEntry
        {
            public string Id;
            public string Label;
            public string Symbol;
            public Func<bool> Visible;
            public Action Toggle;
        }

        // Optional registration for other mods. Register in their Awake; Unregister in OnDestroy.
        // Visibility delegates belong to the registering mod and are not invoked after unregistering.
        public static void RegisterMod(string id, string label, string symbol,
            Func<bool> isVisible, Action toggle)
        {
            if (string.IsNullOrEmpty(id) || isVisible == null || toggle == null) return;
            RegisteredMods[id] = new DockModEntry
            {
                Id = id, Label = label ?? id, Symbol = symbol ?? "M", Visible = isVisible, Toggle = toggle
            };
        }

        public static void UnregisterMod(string id)
        {
            if (!string.IsNullOrEmpty(id)) RegisteredMods.Remove(id);
        }

        // Activation uses a Unity UI button anchored to the game's HUD, not a gameplay hotkey.
        private GameObject hudToggle;
        private RectTransform toolbarAnchor;
        private RectTransform toolbarPanel;
        private Texture2D toolbarTexture;
        private Sprite toolbarSprite;
        private int lastHudWidth;
        private int lastHudHeight;
        private Button hudButton;
        private Image hudIcon;
        private Texture2D hudIconTexture;
        private Sprite hudIconSprite;
        private bool draggingPanel;
        private Vector2 panelDragOffset;
        private bool[] occupied = new bool[Targets.Length];
        // Manual completion is saved per planet. Automatic detection remains read-only.
        private ConfigEntry<bool>[] manualDone = new ConfigEntry<bool>[Targets.Length];
        private int manualPlanetId = -1;
        private GUIStyle rowStyle;
        private float nextScanTime;
        private int scannedPlanetId = -1;
        private bool validScan;
        private int nextTarget = -1;
        private int occupiedCount;
        private GUIStyle labelStyle;
        private GUIStyle buttonStyle;
        private Texture2D compassArrowTexture;
        private GUIStyle statusStyle;
        private GUIStyle compassDetailStyle;
        // Optional audit of off-layout and duplicate built generators (read-only).
        private ConfigEntry<bool> auditEnabled;
        private readonly List<ShieldFinding> findings = new List<ShieldFinding>();
        private int selectedFindingEntityId = -1;
        private int selectedFindingPlanetId = -1;
        private Vector2 findingScroll;
        private int detectedGeneratorCount;
        private int duplicateCount;
        private int offLayoutCount;


        // Existing saved coordinates now belong to the draggable checklist panel.
        private ConfigEntry<float> buttonX;
        private ConfigEntry<float> buttonY;
        private ConfigEntry<float> arrowX;
        private ConfigEntry<float> arrowY;
        private Rect buttonRect;
        private Vector2 arrowCenter;
        private bool layoutLoaded;
        private bool draggingArrow;
        private Vector2 dragOffset;

        private void Awake()
        {
            enabledSetting = Config.Bind("General", "Enabled", false, "Show the shield placement compass.");
            dockY = Config.Bind("Dock", "VerticalPosition", 0.42f,
                "Right-edge dock vertical position (0 top / 1 bottom). Left-drag the MOD header to move.");
            dockHidden = Config.Bind("Dock", "Hidden", false,
                "Keep dock hidden, except for a small recovery handle on the right edge.");
            dockFontSize = Config.Bind("Dock", "FontSize", 13,
                "Font size for the mod dock (11 to 20).");
            dockUiScale = Config.Bind("Dock", "UIScale", 1f,
                "Independent mod dock scale (0.75 to 1.50).");
            compassFontSize = Config.Bind("Compass", "FontSize", 16,
                "Font size for the shield checklist and compass (12 to 18).");
            compassUiScale = Config.Bind("Compass", "UIScale", 1f,
                "Independent shield checklist and compass scale (0.75 to 1.50).");
            RegisterMod("shieldcompass", "Shield Compass", "S",
                () => enabledSetting.Value, ToggleHudOverlay);
            // Legacy ToggleKey setting intentionally unused to prevent quickbar conflicts.
            auditEnabled = Config.Bind("Audit", "Enabled", false,
                "Optional: list off-layout and duplicate shield generators and allow navigation to them.");
            buttonX = Config.Bind("Layout", "ButtonX", 12f, "Button left position in screen pixels.");
            buttonY = Config.Bind("Layout", "ButtonY", 108f, "Button top position in screen pixels.");
            arrowX = Config.Bind("Layout", "ArrowCenterX", -1f, "Arrow center X in screen pixels (-1 = initial screen center).");
            arrowY = Config.Bind("Layout", "ArrowCenterY", 133f, "Arrow center Y in screen pixels.");
            Logger.LogInfo("DSP Shield Compass loaded (display only; no game state changes). Version 0.2.6");
        }

        // Legacy toolbar code is retained for compatibility, but no longer invoked.
        private bool toolbarDiagnosticLogged;

        private void Update()
        {
            if (!enabledSetting.Value) return;

            try
            {
                PlanetData planet = GameMain.localPlanet;
                if (planet == null || planet.factory == null)
                {
                    validScan = false;
                    return;
                }

                if (planet.id != manualPlanetId) LoadManualFlags(planet.id);
                if (planet.id != scannedPlanetId || Time.unscaledTime >= nextScanTime)
                {
                    Scan(planet);
                    scannedPlanetId = planet.id;
                    nextScanTime = Time.unscaledTime + ScanIntervalSeconds;
                }
            }
            catch (Exception ex)
            {
                validScan = false;
                Logger.LogWarning("Shield scan failed: " + ex.Message);
                nextScanTime = Time.unscaledTime + 5f;
            }
        }

        // Read-only scan: no changes to factories, entities, shields or saves.
        private void Scan(PlanetData planet)
        {
            for (int i = 0; i < occupied.Length; i++) occupied[i] = false;
            findings.Clear();
            detectedGeneratorCount = 0;
            duplicateCount = 0;
            offLayoutCount = 0;
            // A shield close to a target is assigned to its nearest target, irrespective of entity-pool order.
            List<ShieldFinding> candidates = new List<ShieldFinding>();
            int[] closestEntity = new int[Targets.Length];
            float[] closestAngle = new float[Targets.Length];
            for (int t = 0; t < Targets.Length; t++) closestAngle[t] = float.MaxValue;

            PlanetFactory factory = planet.factory;
            EntityData[] pool = factory.entityPool;
            if (pool == null)
            {
                validScan = false;
                return;
            }

            int cursor = Math.Min(factory.entityCursor, pool.Length);
            for (int i = 1; i < cursor; i++)
            {
                EntityData entity = pool[i];
                if (entity.id != i || entity.protoId != ShieldProtoId) continue;
                Vector3 pos = entity.pos;
                if (pos.sqrMagnitude < 0.01f) continue;
                Vector3 unit = pos.normalized;

                detectedGeneratorCount++;
                int best = -1;
                float bestAngle = float.MaxValue;
                for (int t = 0; t < Targets.Length; t++)
                {
                    float degrees = Vector3.Angle(unit, Targets[t].Direction);
                    if (degrees < bestAngle)
                    {
                        best = t;
                        bestAngle = degrees;
                    }
                }
                float latitude = Mathf.Asin(Mathf.Clamp(unit.y, -1f, 1f)) * Mathf.Rad2Deg;
                float longitude = Mathf.Atan2(unit.x, -unit.z) * Mathf.Rad2Deg;
                ShieldFinding item = new ShieldFinding(i, unit, latitude, longitude, best, bestAngle);
                candidates.Add(item);
                if (best >= 0 && bestAngle <= MatchRadiusDegrees && bestAngle < closestAngle[best])
                {
                    closestEntity[best] = i;
                    closestAngle[best] = bestAngle;
                }
            }

            for (int i = 0; i < candidates.Count; i++)
            {
                ShieldFinding item = candidates[i];
                if (item.NearestTarget < 0 || item.TargetDistance > MatchRadiusDegrees)
                {
                    item.Classification = "OFF-LAYOUT";
                    offLayoutCount++;
                    findings.Add(item);
                }
                else if (item.EntityId != closestEntity[item.NearestTarget])
                {
                    item.Classification = "DUPLICATE";
                    duplicateCount++;
                    findings.Add(item);
                }
                else
                {
                    occupied[item.NearestTarget] = true;
                }
            }
            findings.Sort((a, b) => a.EntityId.CompareTo(b.EntityId));
            if (selectedFindingPlanetId != planet.id)
            {
                selectedFindingPlanetId = planet.id;
                selectedFindingEntityId = -1;
            }
            if (selectedFindingEntityId >= 0 && !HasSelectedFinding())
                selectedFindingEntityId = -1;

            occupiedCount = 0;
            nextTarget = -1;
            for (int i = 0; i < occupied.Length; i++)
            {
                if (IsComplete(i)) occupiedCount++;
                else if (nextTarget < 0) nextTarget = i;
            }
            validScan = true;
        }

        private void LoadManualFlags(int planetId)
        {
            manualPlanetId = planetId;
            string section = "Completed.Planet." + planetId;
            for (int i = 0; i < Targets.Length; i++)
                manualDone[i] = Config.Bind(section, "Position" + (i + 1), false,
                    "Manually mark " + Targets[i].Name + " as complete on this planet.");
        }

        private bool IsComplete(int index)
        {
            return occupied[index] || (manualDone[index] != null && manualDone[index].Value);
        }

        private void RefreshCompletion()
        {
            occupiedCount = 0;
            nextTarget = -1;
            for (int i = 0; i < Targets.Length; i++)
            {
                if (IsComplete(i)) occupiedCount++;
                else if (nextTarget < 0) nextTarget = i;
            }
        }

        private void EnsureLayout()
        {
            if (!layoutLoaded)
            {
                buttonRect = new Rect(buttonX.Value, buttonY.Value, 298f, 310f);
                arrowCenter = new Vector2(arrowX.Value < 0f ? Screen.width * .5f : arrowX.Value, arrowY.Value);
                layoutLoaded = true;
            }
            float scale = Mathf.Clamp(compassUiScale.Value, .75f, 1.5f);
            buttonRect.x = Mathf.Clamp(buttonRect.x, 0f,
                Mathf.Max(0f, Screen.width - buttonRect.width * scale));
            buttonRect.y = Mathf.Clamp(buttonRect.y, 0f,
                Mathf.Max(0f, Screen.height - buttonRect.height * scale));
            arrowCenter.x = Mathf.Clamp(arrowCenter.x, 28f * scale,
                Mathf.Max(28f * scale, Screen.width - 28f * scale));
            arrowCenter.y = Mathf.Clamp(arrowCenter.y, 62f * scale,
                Mathf.Max(62f * scale, Screen.height - 80f * scale));
        }

        // Left-drag only non-interactive areas: the checklist header or compass arrow.
        // Checklist row clicks and audit buttons retain their normal left-click actions.
        private void HandleDragging(Event e)
        {
            float scale = Mathf.Clamp(compassUiScale.Value, .75f, 1.5f);
            if (e.type == EventType.MouseDown && e.button == 0)
            {
                Rect arrowHit = new Rect(arrowCenter.x - 28f * scale,
                    arrowCenter.y - 28f * scale, 56f * scale, 56f * scale);
                if (enabledSetting.Value && validScan && (nextTarget >= 0 ||
                    (auditEnabled.Value && GetSelectedFinding() != null)) && arrowHit.Contains(e.mousePosition))
                {
                    draggingArrow = true;
                    draggingPanel = false;
                    dragOffset = e.mousePosition - arrowCenter;
                    e.Use();
                }
                else if (enabledSetting.Value &&
                    new Rect(buttonRect.x, buttonRect.y, buttonRect.width * scale,
                        27f * scale).Contains(e.mousePosition))
                {
                    draggingPanel = true;
                    draggingArrow = false;
                    panelDragOffset = e.mousePosition - new Vector2(buttonRect.x, buttonRect.y);
                    e.Use();
                }
            }
            else if (e.type == EventType.MouseDrag && e.button == 0 && (draggingPanel || draggingArrow))
            {
                if (draggingPanel)
                {
                    buttonRect.x = e.mousePosition.x - panelDragOffset.x;
                    buttonRect.y = e.mousePosition.y - panelDragOffset.y;
                }
                else arrowCenter = e.mousePosition - dragOffset;
                EnsureLayout();
                e.Use();
            }
            else if (e.type == EventType.MouseUp && e.button == 0 && (draggingPanel || draggingArrow))
            {
                if (draggingPanel)
                {
                    buttonX.Value = buttonRect.x;
                    buttonY.Value = buttonRect.y;
                }
                else
                {
                    arrowX.Value = arrowCenter.x;
                    arrowY.Value = arrowCenter.y;
                }
                draggingPanel = false;
                draggingArrow = false;
                Config.Save();
                e.Use();
            }
        }

        private void ToggleHudOverlay()
        {
            enabledSetting.Value = !enabledSetting.Value;
            nextScanTime = 0f;
        }

        // The extension belongs to the same UI canvas as the existing shortcut/build bar.
        // No original DSP components are modified or reparented.
        private void TryCreateHudToggle()
        {
            RectTransform bar = FindShortcutBar();
            if (bar == null)
            {
                LogToolbarDiagnostic("No matching shortcut/build bar");
                return;
            }
            RectTransform parent = bar.parent as RectTransform;
            if (parent == null)
            {
                LogToolbarDiagnostic("Matched bar has no RectTransform parent: " + GetUiPath(bar));
                return;
            }

            Canvas rootCanvas = bar.GetComponentInParent<Canvas>();
            if (rootCanvas == null)
            {
                LogToolbarDiagnostic("Matched bar has no Canvas: " + GetUiPath(bar));
                return;
            }
            Camera uiCamera = rootCanvas.renderMode == RenderMode.ScreenSpaceOverlay ? null : rootCanvas.worldCamera;

            GameObject old = GameObject.Find("DSPShieldCompass.ExtensionBar");
            if (old != null) Destroy(old);

            // Three logical slots reserved; only registered buttons are rendered.
            hudToggle = new GameObject("DSPShieldCompass.ExtensionBar", typeof(RectTransform),
                typeof(CanvasRenderer), typeof(Image));
            hudToggle.layer = bar.gameObject.layer;
            hudToggle.transform.SetParent(parent, false);
            toolbarAnchor = bar;
            toolbarPanel = hudToggle.GetComponent<RectTransform>();
            toolbarPanel.anchorMin = new Vector2(.5f, .5f);
            toolbarPanel.anchorMax = new Vector2(.5f, .5f);
            toolbarPanel.pivot = new Vector2(0f, .5f);

            Image panelImage = hudToggle.GetComponent<Image>();
            panelImage.raycastTarget = false;
            // Reuse DSP's own background if a suitable sprite is present.
            Image nativeBackground = bar.GetComponent<Image>();
            if (nativeBackground != null && nativeBackground.sprite != null)
            {
                panelImage.sprite = nativeBackground.sprite;
                panelImage.type = nativeBackground.type;
                panelImage.color = nativeBackground.color;
            }
            else
            {
                toolbarTexture = CreateToolbarTexture();
                toolbarSprite = Sprite.Create(toolbarTexture,
                    new Rect(0f, 0f, toolbarTexture.width, toolbarTexture.height),
                    new Vector2(.5f, .5f), 100f);
                panelImage.sprite = toolbarSprite;
                panelImage.color = Color.white;
            }

            GameObject slot = new GameObject("ShieldCompass.Slot1", typeof(RectTransform),
                typeof(CanvasRenderer), typeof(Image), typeof(Button));
            slot.layer = hudToggle.layer;
            slot.transform.SetParent(hudToggle.transform, false);
            RectTransform slotRect = slot.GetComponent<RectTransform>();
            slotRect.anchorMin = slotRect.anchorMax = new Vector2(0f, .5f);
            slotRect.pivot = new Vector2(.5f, .5f);
            slotRect.anchoredPosition = new Vector2(31f, 0f);
            slotRect.sizeDelta = new Vector2(44f, 44f);

            Image buttonBackground = slot.GetComponent<Image>();
            buttonBackground.color = new Color(.16f, .25f, .33f, .92f);
            hudButton = slot.GetComponent<Button>();
            hudButton.targetGraphic = buttonBackground;
            hudButton.onClick.AddListener(ToggleHudOverlay);

            GameObject iconObject = new GameObject("ShieldIcon", typeof(RectTransform),
                typeof(CanvasRenderer), typeof(Image));
            iconObject.layer = slot.layer;
            iconObject.transform.SetParent(slot.transform, false);
            RectTransform iconRect = iconObject.GetComponent<RectTransform>();
            iconRect.anchorMin = Vector2.zero;
            iconRect.anchorMax = Vector2.one;
            iconRect.offsetMin = new Vector2(8f, 6f);
            iconRect.offsetMax = new Vector2(-8f, -6f);
            hudIcon = iconObject.GetComponent<Image>();
            hudIcon.raycastTarget = false;
            hudIconTexture = CreateShieldIconTexture();
            hudIconSprite = Sprite.Create(hudIconTexture,
                new Rect(0f, 0f, hudIconTexture.width, hudIconTexture.height),
                new Vector2(.5f, .5f), 100f);
            hudIcon.sprite = hudIconSprite;
            hudIcon.preserveAspect = true;
            hudIcon.color = enabledSetting.Value ? new Color(.78f, .93f, 1f) : Color.white;

            PositionToolbar(uiCamera);
            lastHudWidth = Screen.width;
            lastHudHeight = Screen.height;
            Logger.LogInfo("Shield Compass extension attached to UI bar: " + GetUiPath(bar) +
                "; nativeActive=" + bar.gameObject.activeInHierarchy +
                "; panel=" + toolbarPanel.anchoredPosition + "; size=" + toolbarPanel.sizeDelta);
        }

        private void PositionToolbar(Camera uiCamera)
        {
            if (toolbarAnchor == null || toolbarPanel == null) return;
            RectTransform parent = toolbarPanel.parent as RectTransform;
            if (parent == null) return;
            // When the build menu itself is hidden, use the always-visible Function
            // Panel background edge, not stale corners of the inactive Build Menu.
            RectTransform placementAnchor = toolbarAnchor;
            if (!toolbarAnchor.gameObject.activeInHierarchy)
            {
                Transform background = parent.Find("bg-trans/bg");
                if (background != null && background.gameObject.activeInHierarchy)
                {
                    RectTransform visibleRect = background as RectTransform;
                    if (visibleRect != null) placementAnchor = visibleRect;
                }
            }
            Vector3[] corners = new Vector3[4];
            placementAnchor.GetWorldCorners(corners);
            Vector3 centerRight = (corners[2] + corners[3]) * .5f;
            Vector2 screenPoint = RectTransformUtility.WorldToScreenPoint(uiCamera, centerRight);
            Vector2 localPoint;
            if (!RectTransformUtility.ScreenPointToLocalPointInRectangle(
                parent, screenPoint, uiCamera, out localPoint)) return;
            // Fixed-size extension, designed for three slots (only first active for now).
            // Width grows as additional buttons are registered in future revisions.
            toolbarPanel.sizeDelta = new Vector2(66f, 62f);
            toolbarPanel.anchoredPosition = localPoint + new Vector2(6f, 0f);
        }

        // SSOT: The player's diagnostic confirmed this exact hierarchy. "Build Menu"
        // is not necessarily active while the player is in flight or in other modes.
        // Unity's FindObjectsOfType omits inactive children; search the ACTIVE Function
        // Panel for both active and inactive RectTransforms instead.
        private static RectTransform FindShortcutBar()
        {
            RectTransform[] active = FindObjectsOfType<RectTransform>();
            for (int i = 0; i < active.Length; i++)
            {
                RectTransform functionPanel = active[i];
                if (functionPanel == null || functionPanel.name != "Function Panel") continue;
                Transform inGame = functionPanel.parent;
                if (inGame == null || inGame.name != "In Game") continue;
                Transform overlayCanvas = inGame.parent;
                if (overlayCanvas == null || overlayCanvas.name != "Overlay Canvas") continue;

                RectTransform[] descendants = functionPanel.GetComponentsInChildren<RectTransform>(true);
                for (int j = 0; j < descendants.Length; j++)
                {
                    RectTransform child = descendants[j];
                    if (child == null || child.name != "Build Menu" || child.parent != functionPanel) continue;
                    Canvas canvas = child.GetComponentInParent<Canvas>();
                    if (canvas == null || canvas.renderMode == RenderMode.WorldSpace) continue;
                    return child;
                }
            }
            return null;
        }

        // Read-only, limited diagnostic: identify bottom-screen UI objects and likely bar parents.
        // No transforms are moved, changed or destroyed. Logs once per plugin load.
        private void LogToolbarDiagnostic(string reason)
        {
            if (toolbarDiagnosticLogged) return;
            toolbarDiagnosticLogged = true;
            Logger.LogWarning("[ShieldCompass/UI-DIAG] " + reason +
                "; screen=" + Screen.width + "x" + Screen.height);

            RectTransform[] all = FindObjectsOfType<RectTransform>();
            int[] bestScores = new int[16];
            RectTransform[] candidates = new RectTransform[16];
            int eligible = 0;
            for (int i = 0; i < all.Length; i++)
            {
                RectTransform rect = all[i];
                if (rect == null || !rect.gameObject.activeInHierarchy) continue;
                Canvas canvas = rect.GetComponentInParent<Canvas>();
                if (canvas == null || canvas.renderMode == RenderMode.WorldSpace) continue;
                if (rect.rect.width < 90f || rect.rect.height < 18f) continue;

                string name = rect.name.ToLowerInvariant();
                int score = 0;
                if (name.Contains("shortcut")) score += 80;
                if (name.Contains("build")) score += 60;
                if (name.Contains("quick")) score += 60;
                if (name.Contains("function")) score += 30;
                if (name.Contains("panel")) score += 10;
                if (name.Contains("menu")) score += 20;
                if (name.Contains("bar")) score += 20;
                if (name.Contains("hud")) score += 10;
                Vector3[] corners = new Vector3[4];
                rect.GetWorldCorners(corners);
                Vector2 center = RectTransformUtility.WorldToScreenPoint(
                    canvas.renderMode == RenderMode.ScreenSpaceOverlay ? null : canvas.worldCamera,
                    (corners[0] + corners[2]) * .5f);
                // Prefer visible elements in the lower section; also include named candidates.
                if (center.y < Screen.height * .43f && center.y > -60f && center.x > 0f && center.x < Screen.width)
                    score += 40;
                if (score < 40) continue;
                eligible++;

                for (int j = 0; j < candidates.Length; j++)
                {
                    if (score <= bestScores[j]) continue;
                    for (int k = candidates.Length - 1; k > j; k--)
                    {
                        candidates[k] = candidates[k - 1];
                        bestScores[k] = bestScores[k - 1];
                    }
                    candidates[j] = rect;
                    bestScores[j] = score;
                    break;
                }
            }
            Logger.LogInfo("[ShieldCompass/UI-DIAG] Active RectTransforms=" + all.Length +
                ", candidates=" + eligible + ", top=" + candidates.Length);
            for (int i = 0; i < candidates.Length; i++)
            {
                RectTransform rect = candidates[i];
                if (rect == null) continue;
                Logger.LogInfo("[ShieldCompass/UI-DIAG] #" + (i + 1) +
                    " score=" + bestScores[i] +
                    " size=" + rect.rect.width.ToString("F0") + "x" + rect.rect.height.ToString("F0") +
                    " path=" + GetUiPath(rect));
            }
            Logger.LogInfo("[ShieldCompass/UI-DIAG] End");
        }

        private static string GetUiPath(Transform element)
        {
            string path = element.name;
            Transform parent = element.parent;
            int depth = 0;
            while (parent != null && depth++ < 9)
            {
                path = parent.name + "/" + path;
                parent = parent.parent;
            }
            return path;
        }

        private static Texture2D CreateToolbarTexture()
        {
            const int width = 128;
            const int height = 128;
            Texture2D texture = new Texture2D(width, height, TextureFormat.ARGB32, false);
            texture.name = "DSP Shield Compass Extension Background";
            texture.wrapMode = TextureWrapMode.Clamp;
            texture.filterMode = FilterMode.Bilinear;
            Color[] pixels = new Color[width * height];
            for (int y = 0; y < height; y++)
                for (int x = 0; x < width; x++)
                {
                    // Slanted outer end similar to the dark DSP shortcut panel.
                    float maxY = height - 1f - (x / (width - 1f)) * 13f;
                    float minY = (x / (width - 1f)) * 13f;
                    pixels[y * width + x] = y >= minY && y <= maxY
                        ? new Color(.085f, .15f, .20f, .95f) : Color.clear;
                }
            texture.SetPixels(pixels);
            texture.Apply(false, true);
            return texture;
        }

        private void DestroyHudToggle()
        {
            if (hudButton != null) hudButton.onClick.RemoveListener(ToggleHudOverlay);
            if (hudToggle != null) Destroy(hudToggle);
            if (hudIconSprite != null) Destroy(hudIconSprite);
            if (hudIconTexture != null) Destroy(hudIconTexture);
            if (toolbarSprite != null) Destroy(toolbarSprite);
            if (toolbarTexture != null) Destroy(toolbarTexture);
            hudButton = null;
            hudIcon = null;
            hudToggle = null;
            toolbarAnchor = null;
            toolbarPanel = null;
            toolbarSprite = null;
            toolbarTexture = null;
            hudIconSprite = null;
            hudIconTexture = null;
        }

        private static Texture2D CreateShieldIconTexture()
        {
            const int size = 64;
            Texture2D texture = new Texture2D(size, size, TextureFormat.ARGB32, false);
            texture.name = "DSP Shield Compass Menu Icon";
            texture.wrapMode = TextureWrapMode.Clamp;
            texture.filterMode = FilterMode.Bilinear;
            Vector2[] outer = { new Vector2(32, 60), new Vector2(54, 51),
                new Vector2(52, 27), new Vector2(45, 13), new Vector2(32, 4),
                new Vector2(19, 13), new Vector2(12, 27), new Vector2(10, 51) };
            Vector2[] inner = { new Vector2(32, 53), new Vector2(47, 47),
                new Vector2(45, 28), new Vector2(40, 18), new Vector2(32, 12),
                new Vector2(24, 18), new Vector2(19, 28), new Vector2(17, 47) };
            // Distinct compass arrow inside the shield; kept monochrome for DSP styling.
            Vector2[] pointer = { new Vector2(32, 46), new Vector2(40, 28),
                new Vector2(35, 30), new Vector2(35, 21), new Vector2(29, 21),
                new Vector2(29, 30), new Vector2(24, 28) };
            Color[] pixels = new Color[size * size];
            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                {
                    Vector2 point = new Vector2(x + .5f, y + .5f);
                    bool shieldOutline = IsInsidePolygon(point, outer) &&
                        !IsInsidePolygon(point, inner);
                    pixels[y * size + x] = shieldOutline || IsInsidePolygon(point, pointer)
                        ? Color.white : Color.clear;
                }
            texture.SetPixels(pixels);
            texture.Apply(false, true);
            return texture;
        }

        private bool HasSelectedFinding()
        {
            for (int i = 0; i < findings.Count; i++)
                if (findings[i].EntityId == selectedFindingEntityId) return true;
            return false;
        }

        private ShieldFinding GetSelectedFinding()
        {
            for (int i = 0; i < findings.Count; i++)
                if (findings[i].EntityId == selectedFindingEntityId) return findings[i];
            return null;
        }

        private void InitDockStyles()
        {
            if (dockButtonStyle == null)
            {
                dockButtonStyle = new GUIStyle(GUI.skin.button)
                {
                    fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter
                };
                dockButtonStyle.normal.textColor = new Color(.80f, .91f, .96f);
                dockIconOnlyStyle = new GUIStyle(GUI.skin.label)
                {
                    fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter
                };
                dockIconOnlyStyle.normal.textColor = new Color(.85f, .92f, .97f);
                dockTextStyle = new GUIStyle(GUI.skin.label)
                {
                    alignment = TextAnchor.MiddleLeft
                };
                dockTextStyle.normal.textColor = new Color(.83f, .92f, .97f);
                dockHeaderStyle = new GUIStyle(GUI.skin.label)
                {
                    fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleLeft
                };
                dockHeaderStyle.normal.textColor = new Color(.87f, .96f, 1f);
            }
            int font = Mathf.Clamp(dockFontSize.Value, 11, 20);
            float scale = Mathf.Clamp(dockUiScale.Value, .75f, 1.5f);
            dockButtonStyle.fontSize = Mathf.RoundToInt((font + 2) * scale);
            dockIconOnlyStyle.fontSize = Mathf.RoundToInt((font + 2) * scale);
            dockTextStyle.fontSize = Mathf.RoundToInt(font * scale);
            dockHeaderStyle.fontSize = Mathf.RoundToInt(font * scale);
        }

        // A single draw-time icon texture is shared by both dock presentations.
        // The shield outline and compass arrow are rendered from geometry, not a font glyph.
        private void DrawDockModButton(DockModEntry entry, Rect rect, bool active)
        {
            string caption = entry.Id == "shieldcompass" ? "" : entry.Symbol;
            if (GUI.Button(rect, caption, dockButtonStyle)) SafeToggle(entry);
            if (entry.Id != "shieldcompass") return;

            if (dockShieldIcon == null) dockShieldIcon = CreateShieldIconTexture();
            float side = Mathf.Min(rect.width, rect.height) * .70f;
            Rect iconRect = new Rect(rect.center.x - side * .5f,
                rect.center.y - side * .5f, side, side);
            Color previous = GUI.color;
            try
            {
                GUI.color = active ? new Color(.55f, .91f, 1f) : new Color(.85f, .92f, .97f);
                GUI.DrawTexture(iconRect, dockShieldIcon, ScaleMode.ScaleToFit, true);
            }
            finally { GUI.color = previous; }
        }

        // Collapsed dock: transparent clickable hit area with ONLY the mod icon drawn.
        // Shared icon source and toggle callback remain the same as in the expanded dock.
        private void DrawCollapsedModIcon(DockModEntry entry, Rect rect)
        {
            if (GUI.Button(rect, new GUIContent("", entry.Label), GUIStyle.none)) SafeToggle(entry);

            if (entry.Id != "shieldcompass")
            {
                GUI.Label(rect, entry.Symbol, dockIconOnlyStyle);
                return;
            }

            if (dockShieldIcon == null) dockShieldIcon = CreateShieldIconTexture();
            float side = Mathf.Min(rect.width, rect.height) * .80f;
            Rect iconRect = new Rect(rect.center.x - side * .5f,
                rect.center.y - side * .5f, side, side);
            Color previous = GUI.color;
            try
            {
                GUI.color = new Color(.75f, .91f, .98f);
                GUI.DrawTexture(iconRect, dockShieldIcon, ScaleMode.ScaleToFit, true);
            }
            finally { GUI.color = previous; }
        }

        // Time-based hysteresis prevents the dock from opening or collapsing on brief pointer passes.
        private void UpdateDockHover(Rect collapsedRect, Rect expandedRect, Vector2 mousePosition)
        {
            if (dockDragging) return;

            float now = Time.unscaledTime;
            if (!dockExpanded)
            {
                dockHoverLeaveAt = -1f;
                if (!collapsedRect.Contains(mousePosition))
                {
                    dockHoverEnterAt = -1f;
                    return;
                }
                if (dockHoverEnterAt < 0f) dockHoverEnterAt = now;
                if (now - dockHoverEnterAt >= DockOpenDelaySeconds)
                {
                    dockExpanded = true;
                    dockHoverEnterAt = -1f;
                }
            }
            else
            {
                dockHoverEnterAt = -1f;
                if (expandedRect.Contains(mousePosition))
                {
                    dockHoverLeaveAt = -1f;
                    return;
                }
                if (dockHoverLeaveAt < 0f) dockHoverLeaveAt = now;
                if (now - dockHoverLeaveAt >= DockCloseDelaySeconds)
                {
                    dockExpanded = false;
                    dockHoverLeaveAt = -1f;
                }
            }
        }

        // Monochrome vector-derived gear icon (no font glyph or external asset).
        private static Texture2D CreateSettingsGearTexture()
        {
            const int size = 64;
            Texture2D texture = new Texture2D(size, size, TextureFormat.ARGB32, false);
            texture.name = "RIOTdock Settings Icon";
            texture.wrapMode = TextureWrapMode.Clamp;
            texture.filterMode = FilterMode.Bilinear;

            // Eight teeth with a central cutout. Angles are in radians.
            Vector2[] outline = new Vector2[8 * 4];
            const float center = 31.5f;
            for (int i = 0; i < 8; i++)
            {
                float angle = (i * 45f + 22.5f) * Mathf.Deg2Rad;
                for (int j = 0; j < 4; j++)
                {
                    float offset = (j == 0 ? -21f : j == 1 ? -12f : j == 2 ? 12f : 21f) * Mathf.Deg2Rad;
                    float radius = (j == 1 || j == 2) ? 27f : 20.5f;
                    float direction = angle + offset;
                    outline[i * 4 + j] = new Vector2(center + radius * Mathf.Cos(direction),
                        center + radius * Mathf.Sin(direction));
                }
            }
            Color[] pixels = new Color[size * size];
            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                {
                    Vector2 point = new Vector2(x + .5f, y + .5f);
                    bool insideGear = IsInsidePolygon(point, outline);
                    bool centerHole = (point - new Vector2(center, center)).sqrMagnitude < 8.5f * 8.5f;
                    pixels[y * size + x] = insideGear && !centerHole ? Color.white : Color.clear;
                }
            texture.SetPixels(pixels);
            texture.Apply(false, true);
            return texture;
        }

        private void DrawDockSettings(float x, float top, float width, float scale)
        {
            GUI.Label(new Rect(x + 12f * scale, top + 2f * scale,
                width - 24f * scale, 23f * scale), "Dock", dockHeaderStyle);
            int dockFont = Mathf.Clamp(dockFontSize.Value, 11, 20);
            GUI.Label(new Rect(x + 12f * scale, top + 28f * scale,
                width - 24f * scale, 20f * scale), "Text: " + dockFont, dockTextStyle);
            float newDockFont = GUI.HorizontalSlider(new Rect(x + 18f * scale,
                top + 49f * scale, width - 36f * scale, 18f * scale), dockFont, 11f, 20f);
            int dockSize = Mathf.Clamp(Mathf.RoundToInt(newDockFont), 11, 20);
            if (dockSize != dockFontSize.Value) dockFontSize.Value = dockSize;

            float dockSizeScale = Mathf.Clamp(dockUiScale.Value, .75f, 1.5f);
            GUI.Label(new Rect(x + 12f * scale, top + 70f * scale,
                width - 24f * scale, 20f * scale), "UI size: " + dockSizeScale.ToString("F2") + "x", dockTextStyle);
            float newDockScale = GUI.HorizontalSlider(new Rect(x + 18f * scale,
                top + 92f * scale, width - 36f * scale, 18f * scale), dockSizeScale, .75f, 1.5f);
            float snapDockScale = Mathf.Round(newDockScale * 20f) / 20f;
            if (Mathf.Abs(snapDockScale - dockUiScale.Value) > .001f)
                dockUiScale.Value = snapDockScale;

            GUI.Label(new Rect(x + 12f * scale, top + 115f * scale,
                width - 24f * scale, 23f * scale), "Shield Compass", dockHeaderStyle);
            int compassFont = Mathf.Clamp(compassFontSize.Value, 12, 18);
            GUI.Label(new Rect(x + 12f * scale, top + 140f * scale,
                width - 24f * scale, 20f * scale), "Text: " + compassFont, dockTextStyle);
            float newCompassFont = GUI.HorizontalSlider(new Rect(x + 18f * scale,
                top + 160f * scale, width - 36f * scale, 18f * scale), compassFont, 12f, 18f);
            int compassSize = Mathf.Clamp(Mathf.RoundToInt(newCompassFont), 12, 18);
            if (compassSize != compassFontSize.Value) compassFontSize.Value = compassSize;

            float compassScale = Mathf.Clamp(compassUiScale.Value, .75f, 1.5f);
            GUI.Label(new Rect(x + 12f * scale, top + 181f * scale,
                width - 24f * scale, 20f * scale), "UI size: " + compassScale.ToString("F2") + "x", dockTextStyle);
            float newCompassScale = GUI.HorizontalSlider(new Rect(x + 18f * scale,
                top + 201f * scale, width - 36f * scale, 18f * scale), compassScale, .75f, 1.5f);
            float snapCompassScale = Mathf.Round(newCompassScale * 20f) / 20f;
            if (Mathf.Abs(snapCompassScale - compassUiScale.Value) > .001f)
                compassUiScale.Value = snapCompassScale;
        }

        // Right-edge dock, rendered independently of DSP's changing build-menu hierarchy.
        // Hover the icon strip to expand; left-drag the expanded header to reposition.
        // Collapsed state has no background; buttons and settings remain clickable.
        private void DrawModDock(Event e)
        {
            InitDockStyles();
            float scale = Mathf.Clamp(dockUiScale.Value, .75f, 1.5f);
            float collapsedWidth = 42f * scale;
            float expandedWidth = 260f * scale;
            int count = Math.Min(3, RegisteredMods.Count);

            // Collapsed mode shows only currently visible mod icons; no placeholders or labels.
            DockModEntry[] collapsedEntries = RegisteredMods.Values.Where(SafeVisible).Take(3).ToArray();
            const float iconSize = 34f;
            const float iconGap = 14f;
            float collapsedHeight = (8f + iconSize * Math.Max(1, collapsedEntries.Length)
                + iconGap * Math.Max(0, collapsedEntries.Length - 1)) * scale;
            float baseHeight = (34f + 41f * count + 37f) * scale;
            float settingsHeight = dockSettingsOpen ? 237f * scale : 0f;
            float height = baseHeight + settingsHeight;
            if (dockPosition < 0f) dockPosition = Mathf.Clamp01(dockY.Value);
            float y = dockPosition * Mathf.Max(0f, Screen.height - height);
            float x = Screen.width - collapsedWidth;

            // A narrow recovery handle is always available, even when permanently hidden.
            if (dockHidden.Value)
            {
                dockExpanded = false;
                dockHoverEnterAt = -1f;
                dockHoverLeaveAt = -1f;
                Rect recovery = new Rect(Screen.width - 15f * scale, y,
                    15f * scale, 54f * scale);
                GUI.Box(recovery, GUIContent.none);
                if (GUI.Button(new Rect(recovery.x, recovery.y + 6f * scale,
                    recovery.width, 40f * scale), "<", dockButtonStyle))
                {
                    dockHidden.Value = false;
                    Config.Save();
                }
                return;
            }

            Rect tab = new Rect(x, y, collapsedWidth, collapsedHeight);
            Rect expanded = new Rect(Screen.width - expandedWidth, y, expandedWidth, height);
            // Event.current.mousePosition may be invalid for Layout/Repaint events;
            // poll the actual mouse position for stable hover timing across IMGUI event types.
            Vector2 hoverMouse = new Vector2(Input.mousePosition.x,
                Screen.height - Input.mousePosition.y);
            UpdateDockHover(tab, expanded, hoverMouse);
            bool open = dockExpanded || dockDragging;
            Rect panel = open ? expanded : tab;
            Rect settingsButton = new Rect(expanded.x + expandedWidth - 39f * scale,
                y + 4f * scale, 30f * scale, 26f * scale);
            Rect dragHeader = open
                ? new Rect(panel.x, y, panel.width, 32f * scale)
                : new Rect(0f, 0f, 0f, 0f);

            if (e.type == EventType.MouseDown && e.button == 0 &&
                dragHeader.Contains(e.mousePosition) &&
                !(open && settingsButton.Contains(e.mousePosition)))
            {
                dockDragging = true;
                dockDragOffset = e.mousePosition.y - y;
                e.Use();
            }
            else if (e.type == EventType.MouseDrag && e.button == 0 && dockDragging)
            {
                float maxY = Mathf.Max(1f, Screen.height - height);
                dockPosition = Mathf.Clamp01((e.mousePosition.y - dockDragOffset) / maxY);
                e.Use();
            }
            else if (e.type == EventType.MouseUp && e.button == 0 && dockDragging)
            {
                dockDragging = false;
                dockY.Value = dockPosition;
                Config.Save();
                e.Use();
            }

            if (!open)
            {
                // No panel box or button backgrounds in collapsed mode.
                for (int i = 0; i < collapsedEntries.Length; i++)
                {
                    Rect button = new Rect(x + 4f * scale,
                        y + (4f + i * (iconSize + iconGap)) * scale,
                        iconSize * scale, iconSize * scale);
                    DrawCollapsedModIcon(collapsedEntries[i], button);
                }
                return;
            }

            Color prior = GUI.color;
            GUI.color = new Color(.10f, .22f, .30f, .96f);
            GUI.Box(panel, GUIContent.none);
            GUI.color = prior;

            GUI.Label(new Rect(expanded.x + 12f * scale, y + 7f * scale,
                150f * scale, 23f * scale), "RIOTdock", dockHeaderStyle);
            if (GUI.Button(settingsButton, new GUIContent("", "Settings"), dockButtonStyle))
                dockSettingsOpen = !dockSettingsOpen;
            if (dockSettingsIcon == null) dockSettingsIcon = CreateSettingsGearTexture();
            float gearSize = Mathf.Min(settingsButton.width, settingsButton.height) * .76f;
            Rect gearRect = new Rect(settingsButton.center.x - gearSize * .5f,
                settingsButton.center.y - gearSize * .5f, gearSize, gearSize);
            Color previousGearColor = GUI.color;
            try
            {
                GUI.color = dockSettingsOpen ? new Color(.55f, .91f, 1f) : new Color(.85f, .92f, .97f);
                GUI.DrawTexture(gearRect, dockSettingsIcon, ScaleMode.ScaleToFit, true);
            }
            finally { GUI.color = previousGearColor; }
            int n = 0;
            foreach (DockModEntry entry in RegisteredMods.Values)
            {
                if (n >= 3) break;
                float rowY = y + (34f + n * 41f) * scale;
                bool visible = SafeVisible(entry);
                Rect icon = new Rect(expanded.x + 9f * scale,
                    rowY, 37f * scale, 34f * scale);
                DrawDockModButton(entry, icon, visible);
                GUI.Label(new Rect(expanded.x + 52f * scale, rowY + 3f * scale,
                    154f * scale, 27f * scale), entry.Label, dockTextStyle);
                bool value = GUI.Toggle(new Rect(expanded.x + 223f * scale,
                    rowY + 9f * scale, 22f * scale, 22f * scale), visible, GUIContent.none);
                if (value != visible) SafeToggle(entry);
                n++;
            }

            if (dockSettingsOpen)
            {
                float settingsTop = y + (34f + 41f * count + 6f) * scale;
                DrawDockSettings(expanded.x, settingsTop, expandedWidth, scale);
            }
            bool hide = GUI.Toggle(new Rect(expanded.x + 12f * scale,
                y + height - 30f * scale, expandedWidth - 24f * scale, 24f * scale),
                false, "Hide dock (keep recovery tab)");
            if (hide)
            {
                dockHidden.Value = true;
                Config.Save();
            }
        }

        private bool SafeVisible(DockModEntry entry)
        {
            try { return entry.Visible(); }
            catch (Exception ex) { Logger.LogWarning("Mod dock status failed: " + ex.Message); return false; }
        }

        private void SafeToggle(DockModEntry entry)
        {
            try { entry.Toggle(); }
            catch (Exception ex) { Logger.LogWarning("Mod dock toggle failed: " + ex.Message); }
        }

        private void OnGUI()
        {
            if (GameMain.mainPlayer == null) return;
            Event e = Event.current;
            if (e.type != EventType.Repaint && e.type != EventType.MouseDown &&
                e.type != EventType.MouseUp && e.type != EventType.MouseDrag && e.type != EventType.Layout) return;
            InitStyles();
            DrawModDock(e);
            if (!enabledSetting.Value) return;

            float panelHeight = auditEnabled.Value ? 510f : 310f;
            EnsureLayout();
            buttonRect.height = panelHeight;
            EnsureLayout();
            HandleDragging(e);

            float scale = Mathf.Clamp(compassUiScale.Value, .75f, 1.5f);
            Matrix4x4 originalMatrix = GUI.matrix;
            GUI.matrix = ScaleAround(new Vector2(buttonRect.x, buttonRect.y), scale) * originalMatrix;
            try
            {
            Rect panel = new Rect(buttonRect.x, buttonRect.y, 298f, panelHeight);
            GUI.Box(panel, GUIContent.none);
            if (!validScan)
            {
                GUI.Label(new Rect(panel.x + 8f, panel.y + 8f, 282f, 48f), "Land on a planet to scan shields", labelStyle);
                return;
            }

            GUI.Label(new Rect(panel.x + 8f, panel.y + 7f, 280f, 23f),
                "Shields: " + occupiedCount + "/8   Remaining: " + (Targets.Length - occupiedCount), labelStyle);
            GUI.Label(new Rect(panel.x + 8f, panel.y + 28f, 280f, 20f),
                "Click a row to mark / unmark manually", rowStyle);

            for (int i = 0; i < Targets.Length; i++)
            {
                bool manual = manualDone[i] != null && manualDone[i].Value;
                string state = occupied[i] ? "[BUILT]" : (manual ? "[DONE]" : "[    ]");
                string description = (i + 1) + ". " + Targets[i].Name + "  " + state;
                Rect row = new Rect(panel.x + 8f, panel.y + 51f + i * 23f, 282f, 22f);
                if (GUI.Button(row, description, buttonStyle) && manualDone[i] != null)
                {
                    manualDone[i].Value = !manual;
                    Config.Save();
                    RefreshCompletion();
                }
            }

            if (nextTarget < 0)
            {
                GUI.Label(new Rect(panel.x + 8f, panel.y + 244f, 282f, 24f),
                    "All eight positions completed", labelStyle);
            }
            else
            {
                Target target = Targets[nextTarget];
                GUI.Label(new Rect(panel.x + 8f, panel.y + 242f, 282f, 22f),
                    "Next: " + target.Name, labelStyle);
                GUI.Label(new Rect(panel.x + 8f, panel.y + 265f, 282f, 22f),
                    FormatCoordinate(target.Latitude, true) + "  " + FormatCoordinate(target.Longitude, false), labelStyle);
            }

            // Optional panel: diagnostics are defined relative to the selected eight-position layout.
            if (GUI.Button(new Rect(panel.x + 8f, panel.y + 287f, 282f, 24f),
                auditEnabled.Value ? "Find extra shields: ON" : "Find extra shields: OFF", buttonStyle))
            {
                auditEnabled.Value = !auditEnabled.Value;
                if (!auditEnabled.Value) selectedFindingEntityId = -1;
                Config.Save();
            }

            if (auditEnabled.Value)
            {
                GUI.Label(new Rect(panel.x + 8f, panel.y + 316f, 282f, 23f),
                    "Detected: " + detectedGeneratorCount + "  Off-layout: " + offLayoutCount +
                    "  Duplicates: " + duplicateCount, rowStyle);
                GUI.Label(new Rect(panel.x + 8f, panel.y + 338f, 282f, 22f),
                    "Select a finding to navigate / clear", rowStyle);

                Rect viewport = new Rect(panel.x + 8f, panel.y + 361f, 282f, 111f);
                Rect content = new Rect(0f, 0f, 264f, Mathf.Max(111f, findings.Count * 29f));
                findingScroll = GUI.BeginScrollView(viewport, findingScroll, content);
                if (findings.Count == 0)
                    GUI.Label(new Rect(0f, 0f, 264f, 24f), "No off-layout / duplicate shields", rowStyle);
                for (int i = 0; i < findings.Count; i++)
                {
                    ShieldFinding item = findings[i];
                    string name = item.Classification + " #" + item.EntityId +
                        "  " + FormatCoordinate(item.Latitude, true) +
                        " " + FormatCoordinate(item.Longitude, false);
                    bool selected = item.EntityId == selectedFindingEntityId;
                    if (GUI.Button(new Rect(0f, i * 29f, 264f, 27f),
                        (selected ? "> " : "") + name, buttonStyle))
                        selectedFindingEntityId = selected ? -1 : item.EntityId;
                }
                GUI.EndScrollView();
                ShieldFinding active = GetSelectedFinding();
                GUI.Label(new Rect(panel.x + 8f, panel.y + 476f, 282f, 24f),
                    active == null ? "Navigation: next free position" :
                        "Navigate: " + active.Classification + " #" + active.EntityId, rowStyle);
            }

            }
            finally { GUI.matrix = originalMatrix; }

            ShieldFinding navigationFinding = auditEnabled.Value ? GetSelectedFinding() : null;
            if (navigationFinding != null || nextTarget >= 0)
            {
                GUI.matrix = ScaleAround(arrowCenter, scale) * originalMatrix;
                try
                {
                    if (navigationFinding != null) RenderCompassToFinding(navigationFinding);
                    else RenderCompass(Targets[nextTarget]);
                }
                finally { GUI.matrix = originalMatrix; }
            }
        }

        private static Matrix4x4 ScaleAround(Vector2 center, float scale)
        {
            return Matrix4x4.TRS(new Vector3(center.x, center.y, 0f),
                Quaternion.identity, new Vector3(scale, scale, 1f)) *
                Matrix4x4.TRS(new Vector3(-center.x, -center.y, 0f),
                    Quaternion.identity, Vector3.one);
        }

        private int FindCurrentTargetSlot(Vector3 currentDirection)
        {
            int best = -1;
            float bestAngle = BuildSpotToleranceDegrees;
            for (int i = 0; i < Targets.Length; i++)
            {
                float angle = Vector3.Angle(currentDirection, Targets[i].Direction);
                if (angle <= bestAngle)
                {
                    best = i;
                    bestAngle = angle;
                }
            }
            return best;
        }

        private void RenderCompass(Target target)
        {
            Camera camera = Camera.main;
            if (camera == null || GameMain.mainPlayer == null) return;
            Vector3 playerPos = GameMain.mainPlayer.position;
            if (playerPos.sqrMagnitude < 1f) return;

            Vector3 up = playerPos.normalized;
            Vector3 destination = target.Direction;
            float angularDistance = Vector3.Angle(up, destination);
            int currentSlot = FindCurrentTargetSlot(up);

            // SSOT: green = next free location; yellow = completed location; red = elsewhere.
            bool atNextSpot = currentSlot == nextTarget;
            bool atCompletedSpot = currentSlot >= 0 && IsComplete(currentSlot);
            Color indicatorColor = atNextSpot ? Color.green :
                atCompletedSpot ? Color.yellow : Color.red;
            statusStyle.normal.textColor = indicatorColor;

            string status = atNextSpot ? "BUILD SPOT OK" :
                atCompletedSpot ? "SHIELD POSITION DONE" : "OFF TARGET";
            GUI.Label(new Rect(arrowCenter.x - 128f, arrowCenter.y - 61f, 256f, 25f),
                status, statusStyle);

            float rotation = 0f;
            if (currentSlot < 0)
            {
                // Great-circle heading toward the next free target, projected to the planet surface.
                Vector3 tangent = Vector3.ProjectOnPlane(destination, up);
                Vector3 forward = Vector3.ProjectOnPlane(camera.transform.forward, up);
                Vector3 right = Vector3.ProjectOnPlane(camera.transform.right, up);
                if (tangent.sqrMagnitude > 0.00001f &&
                    forward.sqrMagnitude > 0.00001f && right.sqrMagnitude > 0.00001f)
                {
                    tangent.Normalize();
                    forward.Normalize();
                    right.Normalize();
                    rotation = Mathf.Atan2(Vector3.Dot(tangent, right),
                        Vector3.Dot(tangent, forward)) * Mathf.Rad2Deg;
                }
            }

            DrawCompassArrow(rotation, indicatorColor);
            if (currentSlot >= 0)
            {
                string detail = atNextSpot ? "Build here" :
                    atCompletedSpot ? "Already completed" : "Valid position (not next)";
                GUI.Label(new Rect(arrowCenter.x - 140f, arrowCenter.y + 34f, 280f, 43f),
                    "Spot: " + Targets[currentSlot].Name + "\n" + detail, compassDetailStyle);
            }
            else
            {
                GUI.Label(new Rect(arrowCenter.x - 118f, arrowCenter.y + 34f, 236f, 26f),
                    angularDistance.ToString("F1") + "° away", compassDetailStyle);
            }
        }

        private void RenderCompassToFinding(ShieldFinding finding)
        {
            Camera camera = Camera.main;
            if (camera == null || GameMain.mainPlayer == null) return;
            Vector3 playerPos = GameMain.mainPlayer.position;
            if (playerPos.sqrMagnitude < 1f) return;
            Vector3 up = playerPos.normalized;
            float distance = Vector3.Angle(up, finding.Direction);
            bool arrived = distance <= BuildSpotToleranceDegrees;
            Color color = arrived ? Color.yellow : Color.red;
            statusStyle.normal.textColor = color;
            GUI.Label(new Rect(arrowCenter.x - 128f, arrowCenter.y - 61f, 256f, 25f),
                arrived ? "SHIELD FOUND" : "LOCATE " + finding.Classification, statusStyle);
            float rotation = 0f;
            if (!arrived)
            {
                Vector3 tangent = Vector3.ProjectOnPlane(finding.Direction, up);
                Vector3 forward = Vector3.ProjectOnPlane(camera.transform.forward, up);
                Vector3 right = Vector3.ProjectOnPlane(camera.transform.right, up);
                if (tangent.sqrMagnitude > 0.00001f && forward.sqrMagnitude > 0.00001f &&
                    right.sqrMagnitude > 0.00001f)
                {
                    tangent.Normalize(); forward.Normalize(); right.Normalize();
                    rotation = Mathf.Atan2(Vector3.Dot(tangent, right),
                        Vector3.Dot(tangent, forward)) * Mathf.Rad2Deg;
                }
            }
            DrawCompassArrow(rotation, color);
            GUI.Label(new Rect(arrowCenter.x - 140f, arrowCenter.y + 34f, 280f, 45f),
                finding.Classification + "  " + distance.ToString("F1") + "° away\n" +
                FormatCoordinate(finding.Latitude, true) + " " +
                FormatCoordinate(finding.Longitude, false), compassDetailStyle);
        }

        private void DrawCompassArrow(float rotation, Color color)
        {
            if (compassArrowTexture == null) compassArrowTexture = CreateCompassArrowTexture();
            Rect rect = new Rect(arrowCenter.x - 28f, arrowCenter.y - 28f, 56f, 56f);
            Matrix4x4 oldMatrix = GUI.matrix;
            Color oldColor = GUI.color;
            try
            {
                GUIUtility.RotateAroundPivot(rotation, arrowCenter);
                GUI.color = color;
                GUI.DrawTexture(rect, compassArrowTexture, ScaleMode.StretchToFill, true);
            }
            finally
            {
                GUI.color = oldColor;
                GUI.matrix = oldMatrix;
            }
        }

        // One white alpha texture with a hollow navigation-arrow contour (no external assets).
        // The same texture is tinted red, green or yellow at draw time.
        private static Texture2D CreateCompassArrowTexture()
        {
            const int size = 128;
            Texture2D texture = new Texture2D(size, size, TextureFormat.ARGB32, false);
            texture.name = "DSP Shield Compass Arrow";
            texture.wrapMode = TextureWrapMode.Clamp;
            texture.filterMode = FilterMode.Bilinear;

            // Coordinates are in texture space, where y=0 is the bottom edge.
            Vector2[] outline =
            {
                new Vector2(64f, 124f),
                new Vector2(119f, 4f),
                new Vector2(64f, 29f),
                new Vector2(9f, 4f)
            };
            Vector2[] cutout =
            {
                new Vector2(64f, 92f),
                new Vector2(94f, 35f),
                new Vector2(64f, 47f),
                new Vector2(34f, 35f)
            };

            Color[] pixels = new Color[size * size];
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    Vector2 point = new Vector2(x + .5f, y + .5f);
                    bool visible = IsInsidePolygon(point, outline) &&
                        !IsInsidePolygon(point, cutout);
                    pixels[y * size + x] = visible ? Color.white : Color.clear;
                }
            }
            texture.SetPixels(pixels);
            texture.Apply(false, true);
            return texture;
        }

        private static bool IsInsidePolygon(Vector2 point, Vector2[] polygon)
        {
            bool inside = false;
            for (int i = 0, j = polygon.Length - 1; i < polygon.Length; j = i++)
            {
                Vector2 a = polygon[i];
                Vector2 b = polygon[j];
                if ((a.y > point.y) != (b.y > point.y) &&
                    point.x < (b.x - a.x) * (point.y - a.y) / (b.y - a.y) + a.x)
                    inside = !inside;
            }
            return inside;
        }

        private void OnDestroy()
        {
            UnregisterMod("shieldcompass");
            DestroyHudToggle();
            if (dockShieldIcon != null)
            {
                Destroy(dockShieldIcon);
                dockShieldIcon = null;
            }
            if (dockSettingsIcon != null)
            {
                Destroy(dockSettingsIcon);
                dockSettingsIcon = null;
            }
            if (compassArrowTexture != null)
            {
                Destroy(compassArrowTexture);
                compassArrowTexture = null;
            }
        }

        private void InitStyles()
        {
            if (labelStyle == null)
            {
                labelStyle = new GUIStyle(GUI.skin.label)
                    { fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleLeft };
                labelStyle.normal.textColor = Color.white;
                buttonStyle = new GUIStyle(GUI.skin.button);
                rowStyle = new GUIStyle(GUI.skin.label)
                    { alignment = TextAnchor.MiddleLeft };
                rowStyle.normal.textColor = Color.white;
                statusStyle = new GUIStyle(GUI.skin.label)
                    { fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter };
                statusStyle.normal.textColor = Color.red;
                compassDetailStyle = new GUIStyle(GUI.skin.label)
                    { fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter };
                compassDetailStyle.normal.textColor = Color.white;
            }
            int font = Mathf.Clamp(compassFontSize.Value, 12, 18);
            labelStyle.fontSize = font;
            buttonStyle.fontSize = Mathf.Max(11, font - 2);
            rowStyle.fontSize = Mathf.Max(11, font - 3);
            statusStyle.fontSize = Mathf.Max(11, font - 2);
            compassDetailStyle.fontSize = font;
        }

        private static string FormatCoordinate(float angle, bool latitude)
        {
            return Math.Abs(angle).ToString("F0") + "° " + (latitude ? (angle >= 0 ? "N" : "S") : (angle >= 0 ? "E" : "W"));
        }

        private sealed class ShieldFinding
        {
            public readonly int EntityId;
            public readonly Vector3 Direction;
            public readonly float Latitude;
            public readonly float Longitude;
            public readonly int NearestTarget;
            public readonly float TargetDistance;
            public string Classification;

            public ShieldFinding(int entityId, Vector3 direction, float latitude,
                float longitude, int nearestTarget, float targetDistance)
            {
                EntityId = entityId;
                Direction = direction;
                Latitude = latitude;
                Longitude = longitude;
                NearestTarget = nearestTarget;
                TargetDistance = targetDistance;
            }
        }

        private struct Target
        {
            public readonly string Name;
            public readonly float Latitude;
            public readonly float Longitude;
            public readonly Vector3 Direction;

            public Target(string name, float latitude, float longitude)
            {
                Name = name;
                Latitude = latitude;
                Longitude = longitude;
                float phi = latitude * Mathf.Deg2Rad;
                float lambda = longitude * Mathf.Deg2Rad;
                // DSP grid coordinates: longitude 0 at -Z (atan2(x, -z)); positive toward +X.
                Direction = new Vector3(Mathf.Cos(phi) * Mathf.Sin(lambda), Mathf.Sin(phi), -Mathf.Cos(phi) * Mathf.Cos(lambda));
            }
        }
    }
}
