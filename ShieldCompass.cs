using System;
using System.Collections.Generic;
using BepInEx;
using BepInEx.Configuration;
using UnityEngine;
using UnityEngine.UI;

namespace DSPShieldCompass
{
    [BepInPlugin("com.unclecompass.dsp.shieldcompass", "DSP Shield Compass", "0.1.7")]
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
        private float nextHudAttempt;
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
            // Legacy ToggleKey setting intentionally unused to prevent quickbar conflicts.
            auditEnabled = Config.Bind("Audit", "Enabled", false,
                "Optional: list off-layout and duplicate shield generators and allow navigation to them.");
            buttonX = Config.Bind("Layout", "ButtonX", 12f, "Button left position in screen pixels.");
            buttonY = Config.Bind("Layout", "ButtonY", 108f, "Button top position in screen pixels.");
            arrowX = Config.Bind("Layout", "ArrowCenterX", -1f, "Arrow center X in screen pixels (-1 = initial screen center).");
            arrowY = Config.Bind("Layout", "ArrowCenterY", 133f, "Arrow center Y in screen pixels.");
            Logger.LogInfo("DSP Shield Compass loaded (display only; no game state changes). Version 0.1.7");
        }

        private void Update()
        {
            if (GameMain.mainPlayer != null)
            {
                if (hudToggle == null && Time.unscaledTime >= nextHudAttempt)
                {
                    TryCreateHudToggle();
                    nextHudAttempt = Time.unscaledTime + 3f;
                }
                if (hudToggle != null && !hudToggle.activeSelf) hudToggle.SetActive(true);
                if (hudToggle != null && (lastHudWidth != Screen.width || lastHudHeight != Screen.height))
                {
                    // UI scale and resolution can change without recreating the plugin.
                    DestroyHudToggle();
                    nextHudAttempt = 0f;
                }
            }
            else if (hudToggle != null && hudToggle.activeSelf) hudToggle.SetActive(false);
            if (hudIcon != null) hudIcon.color = enabledSetting.Value ? new Color(.78f, .93f, 1f) : Color.white;
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
            buttonRect.x = Mathf.Clamp(buttonRect.x, 0f, Mathf.Max(0f, Screen.width - buttonRect.width));
            buttonRect.y = Mathf.Clamp(buttonRect.y, 0f, Mathf.Max(0f, Screen.height - buttonRect.height));
            arrowCenter.x = Mathf.Clamp(arrowCenter.x, 28f, Mathf.Max(28f, Screen.width - 28f));
            arrowCenter.y = Mathf.Clamp(arrowCenter.y, 28f, Mathf.Max(28f, Screen.height - 55f));
        }

        // Drag the overlay by its top header, or the compass arrow, with the right mouse button.
        private void HandleDragging(Event e)
        {
            if (e.type == EventType.MouseDown && e.button == 1)
            {
                Rect arrowHit = new Rect(arrowCenter.x - 28f, arrowCenter.y - 28f, 56f, 56f);
                if (enabledSetting.Value && validScan && (nextTarget >= 0 ||
                    (auditEnabled.Value && GetSelectedFinding() != null)) && arrowHit.Contains(e.mousePosition))
                {
                    draggingArrow = true;
                    draggingPanel = false;
                    dragOffset = e.mousePosition - arrowCenter;
                    e.Use();
                }
                else if (enabledSetting.Value &&
                    new Rect(buttonRect.x, buttonRect.y, buttonRect.width, 27f).Contains(e.mousePosition))
                {
                    draggingPanel = true;
                    draggingArrow = false;
                    panelDragOffset = e.mousePosition - new Vector2(buttonRect.x, buttonRect.y);
                    e.Use();
                }
            }
            else if (e.type == EventType.MouseDrag && e.button == 1 && (draggingPanel || draggingArrow))
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
            else if (e.type == EventType.MouseUp && e.button == 1 && (draggingPanel || draggingArrow))
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
            if (bar == null) return;
            RectTransform parent = bar.parent as RectTransform;
            if (parent == null) return;

            Canvas rootCanvas = bar.GetComponentInParent<Canvas>();
            if (rootCanvas == null) return;
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
            Logger.LogInfo("Shield Compass extension attached to UI bar: " + bar.name);
        }

        private void PositionToolbar(Camera uiCamera)
        {
            if (toolbarAnchor == null || toolbarPanel == null) return;
            RectTransform parent = toolbarPanel.parent as RectTransform;
            if (parent == null) return;
            Vector3[] corners = new Vector3[4];
            toolbarAnchor.GetWorldCorners(corners);
            Vector3 centerRight = (corners[2] + corners[3]) * .5f;
            Vector2 screenPoint = RectTransformUtility.WorldToScreenPoint(uiCamera, centerRight);
            Vector2 localPoint;
            if (!RectTransformUtility.ScreenPointToLocalPointInRectangle(
                parent, screenPoint, uiCamera, out localPoint)) return;
            // Fixed-size extension, designed for three slots (only first active for now).
            // Width grows as additional buttons are registered in future revisions.
            toolbarPanel.sizeDelta = new Vector2(66f, Mathf.Max(62f, toolbarAnchor.rect.height * .65f));
            toolbarPanel.anchoredPosition = localPoint + new Vector2(6f, 0f);
        }

        // Choose only recognizable shortcut/build-bar elements. Avoid snapping to arbitrary HUDs.
        private static RectTransform FindShortcutBar()
        {
            RectTransform[] all = FindObjectsOfType<RectTransform>();
            RectTransform best = null;
            int scoreBest = 0;
            for (int i = 0; i < all.Length; i++)
            {
                RectTransform rect = all[i];
                if (rect == null || !rect.gameObject.activeInHierarchy) continue;
                string name = rect.gameObject.name.ToLowerInvariant();
                int score = 0;
                if (name.Contains("shortcut")) score += 40;
                if (name.Contains("build-menu") || name.Contains("buildmenu")) score += 35;
                if (name.Contains("quickbar") || name.Contains("quick-bar")) score += 40;
                if (name.Contains("function-panel") || name.Contains("functionpanel")) score += 20;
                if (score == 0) continue;
                if (rect.rect.width < 250f || rect.rect.height < 35f) continue;
                Canvas canvas = rect.GetComponentInParent<Canvas>();
                if (canvas == null || canvas.renderMode == RenderMode.WorldSpace) continue;
                if (score > scoreBest)
                {
                    best = rect;
                    scoreBest = score;
                }
            }
            return best;
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
            Color[] pixels = new Color[size * size];
            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                {
                    Vector2 point = new Vector2(x + .5f, y + .5f);
                    pixels[y * size + x] = IsInsidePolygon(point, outer) &&
                        !IsInsidePolygon(point, inner) ? Color.white : Color.clear;
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

        private void OnGUI()
        {
            if (GameMain.mainPlayer == null) return;
            Event e = Event.current;
            if (e.type != EventType.Repaint && e.type != EventType.MouseDown &&
                e.type != EventType.MouseUp && e.type != EventType.MouseDrag && e.type != EventType.Layout) return;

            InitStyles();
            if (hudToggle != null && hudToggle.activeInHierarchy)
            {
                RectTransform hitRect = hudToggle.GetComponent<RectTransform>();
                Canvas parentCanvas = hudToggle.GetComponentInParent<Canvas>();
                Camera eventCamera = parentCanvas != null &&
                    parentCanvas.renderMode != RenderMode.ScreenSpaceOverlay ? parentCanvas.worldCamera : null;
                if (RectTransformUtility.RectangleContainsScreenPoint(hitRect, Input.mousePosition, eventCamera))
                    GUI.Label(new Rect(Input.mousePosition.x + 10f, Screen.height - Input.mousePosition.y - 27f, 230f, 24f),
                        "Shield Compass - Show/Hide", labelStyle);
            }
            EnsureLayout();
            HandleDragging(e);

            if (!enabledSetting.Value) return;

            // The HUD button is fixed at the lower right; the checklist remains freely movable.
            float panelHeight = auditEnabled.Value ? 510f : 310f;
            buttonRect.height = panelHeight;
            EnsureLayout();
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

            ShieldFinding navigationFinding = auditEnabled.Value ? GetSelectedFinding() : null;
            if (navigationFinding != null)
                RenderCompassToFinding(navigationFinding);
            else if (nextTarget >= 0)
                RenderCompass(Targets[nextTarget]);
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
                    "Spot: " + Targets[currentSlot].Name + "\n" + detail, labelStyle);
            }
            else
            {
                GUI.Label(new Rect(arrowCenter.x - 118f, arrowCenter.y + 34f, 236f, 26f),
                    angularDistance.ToString("F1") + " deg to target", labelStyle);
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
                finding.Classification + "  " + distance.ToString("F1") + " deg away\n" +
                FormatCoordinate(finding.Latitude, true) + " " +
                FormatCoordinate(finding.Longitude, false), labelStyle);
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
            DestroyHudToggle();
            if (compassArrowTexture != null)
            {
                Destroy(compassArrowTexture);
                compassArrowTexture = null;
            }
        }

        private void InitStyles()
        {
            if (labelStyle != null) return;
            labelStyle = new GUIStyle(GUI.skin.label) { fontSize = 16, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleLeft };
            labelStyle.normal.textColor = Color.white;
            buttonStyle = new GUIStyle(GUI.skin.button) { fontSize = 14 };
            rowStyle = new GUIStyle(GUI.skin.label) { fontSize = 13, alignment = TextAnchor.MiddleLeft };
            rowStyle.normal.textColor = Color.white;
            statusStyle = new GUIStyle(GUI.skin.label) { fontSize = 14, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter };
            statusStyle.normal.textColor = Color.red;
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
