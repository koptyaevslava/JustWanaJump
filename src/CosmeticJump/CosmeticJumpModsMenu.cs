using System;
using System.Collections.Generic;
using BepInEx.Logging;
using HarmonyLib;
using Il2CppInterop.Runtime;
using Sabotage.Localization;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.Events;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace CosmeticJump;

internal static class CosmeticJumpModsMenu
{
    private static readonly Dictionary<IntPtr, MenuView> Views = new();
    private static ManualLogSource logger;

    public static void Install(Harmony harmony, ManualLogSource log)
    {
        logger = log;
        var init = AccessTools.Method(typeof(OptionsScreen), "Init")
            ?? throw new MissingMethodException(nameof(OptionsScreen), "Init");
        var update = AccessTools.Method(typeof(OptionsScreen), "Update")
            ?? throw new MissingMethodException(nameof(OptionsScreen), "Update");
        var before = new HarmonyMethod(typeof(CosmeticJumpModsMenu), nameof(BeforeInit))
            { priority = Priority.Last };
        var after = new HarmonyMethod(typeof(CosmeticJumpModsMenu), nameof(AfterInit))
            { priority = Priority.Last };
        var tick = new HarmonyMethod(typeof(CosmeticJumpModsMenu), nameof(AfterUpdate))
            { priority = Priority.Last };
        harmony.Patch(init, prefix: before, postfix: after);
        harmony.Patch(update, postfix: tick);
        logger.LogInfo("Installed shared Mods-tab integration for JustWanaJump.");
    }

    public static void Dispose() => Views.Clear();

    private static void BeforeInit(OptionsScreen __instance)
    {
        try { GetOrCreate(__instance); }
        catch (Exception ex) { logger?.LogError("Could not prepare the shared Mods tab: " + ex); }
    }

    private static void AfterInit(OptionsScreen __instance)
    {
        try { GetOrCreate(__instance); }
        catch (Exception ex) { logger?.LogError("Could not retain the shared Mods tab: " + ex); }
    }

    private static void AfterUpdate(OptionsScreen __instance)
    {
        try
        {
            MenuView view = GetOrCreate(__instance);
            view.TryInitialize();
            view.Update();
        }
        catch (Exception ex)
        {
            logger?.LogError("JustWanaJump settings update failed: " + ex);
        }
    }

    private static MenuView GetOrCreate(OptionsScreen screen)
    {
        if (Views.TryGetValue(screen.Pointer, out MenuView existing) && existing.IsAlive) return existing;
        var created = new MenuView(screen);
        Views[screen.Pointer] = created;
        return created;
    }

    private sealed class MenuView
    {
        private readonly OptionsScreen screen;
        private readonly GameObject pageRoot;
        private readonly RectTransform pageRect;
        private readonly GameObject tabSelector;
        private readonly TextMeshProUGUI tabLabel;
        private readonly UITabPage page;
        private readonly List<GameObject> foreignContent = new();
        private readonly List<Selectable> foreignRows = new();
        private readonly List<SettingRow> ownRows = new();
        private DurationSliderRow durationRow;
        private TextMeshProUGUI ownTitle;
        private ELanguage lastLanguage = (ELanguage)(-1);
        private GameObject lastScrollSelection;
        private bool initialized;
        private bool layoutLogged;
        private bool scrollDirty = true;

        public bool IsAlive => pageRoot != null && tabSelector != null;

        public MenuView(OptionsScreen owner)
        {
            screen = owner;
            Transform pageParent = owner.generalTab.transform.parent;
            Transform selectorParent = owner.keyboardTabSelector.transform.parent;
            Transform existingPage = FindDirect(pageParent, "Mods tab");
            Transform existingSelector = FindDirect(selectorParent, "Mods tab selector");

            if (existingPage != null && existingSelector != null)
            {
                pageRoot = existingPage.gameObject;
                pageRect = existingPage.GetComponent<RectTransform>();
                tabSelector = existingSelector.gameObject;
                tabLabel = FirstText(tabSelector);
                page = pageRoot.GetComponent<UITabPage>();
                logger?.LogInfo("Reusing the Mods tab created by another installed mod.");
                return;
            }

            tabSelector = Object.Instantiate(owner.keyboardTabSelector, selectorParent);
            tabSelector.name = "Mods tab selector";
            int nextButtonIndex = owner.nextTabBtn != null
                ? owner.nextTabBtn.transform.GetSiblingIndex()
                : owner.keyboardTabSelector.transform.GetSiblingIndex() + 1;
            tabSelector.transform.SetSiblingIndex(nextButtonIndex);
            DisableLocalizers(tabSelector);
            tabLabel = FirstText(tabSelector);
            tabLabel.text = Translate(TextKey.Mods, CurrentLanguage());

            pageRoot = NewRectObject("Mods tab", pageParent);
            pageRect = pageRoot.GetComponent<RectTransform>();
            CopyRect(owner.generalTab.GetComponent<RectTransform>(), pageRect);
            CopyVerticalLayout(owner.generalTab.gameObject, pageRoot);
            page = pageRoot.AddComponent<UITabPage>();
            page.label = tabLabel;
            pageRoot.SetActive(false);
            owner.tabController.tabs.Add(page);
            logger?.LogInfo("Created one shared Mods tab for JustWanaJump.");
        }

        public void TryInitialize()
        {
            if (initialized) return;
            CaptureForeignContent();

            PrepareSpacer();
            ownTitle = CreateTitle(SharedSectionHeaderSource(), pageRoot.transform, out GameObject ownHeader);
            ownHeader.name = "JustWanaJump header";
            CreateOwnRows();
            PrepareNativeScrolling();
            initialized = true;
            RefreshLanguage(true);
            SyncRowsFromConfig();
            RebuildNavigation();
            LayoutRebuilder.MarkLayoutForRebuild(pageRect);
            logger?.LogInfo(foreignContent.Count > 0
                ? "JustWanaJump is visible together with other mods on one Mods page."
                : "JustWanaJump settings initialized in the Mods tab.");
        }

        private void CaptureForeignContent()
        {
            foreignContent.Clear();
            foreignRows.Clear();
            for (int i = 0; i < pageRoot.transform.childCount; i++)
            {
                GameObject child = pageRoot.transform.GetChild(i).gameObject;
                if (child.name == "Spacer" || child.name.StartsWith("JustWanaJump")) continue;
                foreignContent.Add(child);
                Selectable selectable = child.GetComponent<Selectable>();
                if (selectable != null) foreignRows.Add(selectable);
                else if (child.name.IndexOf("description", StringComparison.OrdinalIgnoreCase) >= 0)
                    child.SetActive(false);
            }
        }

        private void PrepareSpacer()
        {
            Transform existing = FindDirect(pageRoot.transform, "Spacer");
            if (existing != null)
            {
                existing.GetComponent<RectTransform>().sizeDelta = Vector2.zero;
                return;
            }
            Transform native = screen.generalTab.transform.Find("Spacer");
            if (native != null)
            {
                GameObject spacer = Object.Instantiate(native.gameObject, pageRoot.transform);
                spacer.name = "Spacer";
                spacer.GetComponent<RectTransform>().sizeDelta = Vector2.zero;
                spacer.transform.SetAsFirstSibling();
                return;
            }
            GameObject fallback = NewRectObject("Spacer", pageRoot.transform);
            fallback.GetComponent<RectTransform>().sizeDelta = Vector2.zero;
            fallback.transform.SetAsFirstSibling();
        }

        private void CreateOwnRows()
        {
            ownRows.Add(new SettingRow(CreateToggleObject("JustWanaJump enabled", out UIMultiValueToggle enabled,
                out TextMeshProUGUI enabledLabel), enabled, enabledLabel));
            durationRow = new DurationSliderRow(CreateDurationSlider());
        }

        private UIIntSlider CreateDurationSlider()
        {
            GameObject row = Object.Instantiate(screen.brightnessBtn.gameObject, pageRoot.transform);
            row.name = "JustWanaJump duration";
            row.SetActive(true);
            DisableLocalizers(row);
            UIIntSlider slider = row.GetComponent<UIIntSlider>();
            slider.onToggled = new UnityEvent();
            slider.onSelected = new UnityEvent();
            slider.interactable = true;
            RectTransform rect = row.GetComponent<RectTransform>();
            rect.sizeDelta = new Vector2(0f, 28f);
            rect.localRotation = Quaternion.identity;
            rect.localScale = Vector3.one;
            return slider;
        }

        private GameObject CreateToggleObject(string name, out UIMultiValueToggle toggle,
            out TextMeshProUGUI displayLabel)
        {
            GameObject row = Object.Instantiate(screen.crashReportsBtn.gameObject, pageRoot.transform);
            row.name = name;
            DisableLocalizers(row);
            toggle = row.GetComponent<UIMultiValueToggle>();
            toggle.mode = UIMultiValueToggle.UIMultiValueToggleMode.Strings;
            toggle.onToggled = new UnityEvent();
            toggle.onSelected = new UnityEvent();
            toggle.itemLabel.gameObject.SetActive(true);
            toggle.interactable = true;
            displayLabel = CreateRowLabel(toggle.label, row.transform);
            toggle.label.gameObject.SetActive(false);
            RectTransform rect = row.GetComponent<RectTransform>();
            rect.sizeDelta = new Vector2(0f, 28f);
            rect.localRotation = Quaternion.identity;
            rect.localScale = Vector3.one;
            return row;
        }

        public void Update()
        {
            if (!initialized) return;
            RefreshLanguage(false);
            for (int i = 0; i < ownRows.Count; i++)
            {
                ownRows[i].SyncStyle(screen.crashReportsBtn.itemLabel);
                ownRows[i].ApplyChange();
            }
            durationRow?.SyncStyle(screen.crashReportsBtn.itemLabel);
            durationRow?.ApplyChange();
            RebuildNavigation();

            UpdateNativeScrolling();

            if (!layoutLogged && pageRoot.activeInHierarchy)
            {
                Canvas.ForceUpdateCanvases();
                LayoutRebuilder.ForceRebuildLayoutImmediate(pageRect);
                logger?.LogInfo($"Shared Mods layout: {pageRect.rect.width:0.#}x{pageRect.rect.height:0.#}, " +
                                $"foreignRows={foreignRows.Count}, jumpRows={ownRows.Count + (durationRow != null ? 1 : 0)}.");
                layoutLogged = true;
            }
        }

        private void RebuildNavigation()
        {
            var active = new List<Selectable>();
            for (int i = 0; i < foreignRows.Count; i++)
                if (foreignRows[i] != null && foreignRows[i].gameObject.activeInHierarchy)
                    active.Add(foreignRows[i]);
            for (int i = 0; i < ownRows.Count; i++)
                if (ownRows[i].Toggle != null && ownRows[i].Toggle.gameObject.activeInHierarchy)
                    active.Add(ownRows[i].Toggle);
            if (durationRow?.Slider != null && durationRow.Slider.gameObject.activeInHierarchy)
                active.Add(durationRow.Slider);
            if (active.Count == 0) return;
            for (int i = 0; i < active.Count; i++)
            {
                Navigation navigation = active[i].navigation;
                navigation.mode = Navigation.Mode.Explicit;
                navigation.selectOnUp = active[(i + active.Count - 1) % active.Count];
                navigation.selectOnDown = active[(i + 1) % active.Count];
                active[i].navigation = navigation;
            }
            page.defaultSelected = active[0].gameObject;
        }

        private void RefreshLanguage(bool force)
        {
            ELanguage language = CurrentLanguage();
            if (!force && language == lastLanguage) return;
            lastLanguage = language;
            tabLabel.text = Translate(TextKey.Mods, language);
            if (ownTitle != null) ownTitle.text = Translate(TextKey.Title, language);
            CopyTextStyle(FirstText(screen.keyboardTabSelector), tabLabel);
            if (ownTitle != null) CopyTextStyle(FirstText(SharedSectionHeaderSource()), ownTitle);
            for (int i = 0; i < ownRows.Count; i++)
                ownRows[i].RefreshLanguage(language, screen.crashReportsBtn.itemLabel);
            durationRow?.RefreshLanguage(language, screen.crashReportsBtn.itemLabel);
            LayoutRebuilder.MarkLayoutForRebuild(pageRect);
            scrollDirty = true;
        }

        private void SyncRowsFromConfig()
        {
            for (int i = 0; i < ownRows.Count; i++) ownRows[i].SyncFromConfig();
            durationRow?.SyncFromConfig();
        }

        private GameObject NativeSectionHeader()
        {
            Transform header = screen.gamepadControlsTab.transform.Find("Player/Header");
            if (header == null) throw new InvalidOperationException("Native section header was not found.");
            return header.gameObject;
        }

        private GameObject SharedSectionHeaderSource()
        {
            // If Parries and Blocks created this shared page, its own header is the exact
            // geometry we need: compact height, baseline and underline all stay identical.
            for (int i = 0; i < foreignContent.Count; i++)
            {
                GameObject candidate = foreignContent[i];
                if (candidate == null || !candidate.activeSelf ||
                    candidate.GetComponent<Selectable>() != null) continue;
                TextMeshProUGUI[] texts = candidate.GetComponentsInChildren<TextMeshProUGUI>(true);
                if (texts.Length == 0 || string.IsNullOrWhiteSpace(texts[0].text)) continue;
                return candidate;
            }
            return NativeSectionHeader();
        }

        private void PrepareNativeScrolling()
        {
            ContentSizeFitter fitter = pageRoot.GetComponent<ContentSizeFitter>();
            if (fitter == null) fitter = pageRoot.AddComponent<ContentSizeFitter>();
            fitter.horizontalFit = ContentSizeFitter.FitMode.Unconstrained;
            fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
            scrollDirty = true;
        }

        private void UpdateNativeScrolling()
        {
            if (!pageRoot.activeInHierarchy)
            {
                lastScrollSelection = null;
                scrollDirty = true;
                return;
            }

            Canvas.ForceUpdateCanvases();
            LayoutRebuilder.ForceRebuildLayoutImmediate(pageRect);
            VerticalScrollList scroll = screen.verticalScrollList;
            if (scroll == null) return;

            if (scrollDirty)
            {
                scroll.InitScroll();
                scroll.UpdateScrollbar();
                scrollDirty = false;
            }

            GameObject selected = EventSystem.current?.currentSelectedGameObject;
            if (selected == null || selected == lastScrollSelection ||
                !selected.transform.IsChildOf(pageRoot.transform)) return;
            lastScrollSelection = selected;
            RectTransform selectedRect = selected.GetComponent<RectTransform>();
            if (selectedRect != null)
            {
                scroll.ScrollToItem(selectedRect);
                scroll.UpdateScrollbar();
            }
        }
    }

    private sealed class SettingRow
    {
        public readonly UIMultiValueToggle Toggle;
        private readonly TextMeshProUGUI label;
        private readonly Il2CppSystem.Collections.Generic.List<string> values = new();
        private int observed = -1;

        public SettingRow(GameObject _, UIMultiValueToggle toggle, TextMeshProUGUI displayLabel)
        {
            Toggle = toggle;
            label = displayLabel;
        }

        public void RefreshLanguage(ELanguage language, TextMeshProUGUI style)
        {
            values.Clear();
            values.Add(Translate(TextKey.Off, language));
            values.Add(Translate(TextKey.On, language));
            label.text = Translate(TextKey.Enabled, language);
            int index = CurrentIndex();
            Toggle.Init(new Il2CppSystem.Collections.Generic.IList<string>(values.Pointer), index);
            Toggle.Enable();
            observed = index;
            SyncStyle(style);
        }

        public void SyncStyle(TextMeshProUGUI source)
        {
            if (source == null || label == null) return;
            if (source.font != null)
            {
                label.font = source.font;
                Toggle.itemLabel.font = source.font;
            }
            if (source.fontSharedMaterial != null)
            {
                label.fontSharedMaterial = source.fontSharedMaterial;
                Toggle.itemLabel.fontSharedMaterial = source.fontSharedMaterial;
            }
            label.fontSize = source.fontSize;
            label.fontStyle = source.fontStyle;
            label.color = source.color;
            label.rectTransform.localScale = Vector3.one;
        }

        public void SyncFromConfig()
        {
            int index = CurrentIndex();
            Toggle.ShowItem(index);
            observed = index;
        }

        public void ApplyChange()
        {
            int index = Toggle.selectedIdx;
            if (index == observed) return;
            observed = index;
            ModSettings.Enabled.Value = index != 0;
        }

        private int CurrentIndex() => ModSettings.Enabled.Value ? 1 : 0;
    }

    private sealed class DurationSliderRow
    {
        private const float Minimum = 0.30f;
        private const float Maximum = 0.60f;
        public readonly UIIntSlider Slider;
        private int observed = -1;

        public DurationSliderRow(UIIntSlider slider)
        {
            Slider = slider;
        }

        public void RefreshLanguage(ELanguage language, TextMeshProUGUI style)
        {
            Slider.label.text = Translate(TextKey.Duration, language);
            SyncStyle(style);
            SyncFromConfig();
        }

        public void SyncStyle(TextMeshProUGUI source)
        {
            if (source == null || Slider?.label == null) return;
            if (source.font != null) Slider.label.font = source.font;
            if (source.fontSharedMaterial != null) Slider.label.fontSharedMaterial = source.fontSharedMaterial;
            Slider.label.fontSize = source.fontSize;
            Slider.label.fontStyle = source.fontStyle;
            Slider.label.color = source.color;
            Slider.label.rectTransform.localScale = Vector3.one;
        }

        public void SyncFromConfig()
        {
            int level = LevelFromDuration(ModSettings.DurationSeconds.Value);
            Slider.Init(level);
            Slider.Enable();
            observed = level;
        }

        public void ApplyChange()
        {
            int level = Slider.level;
            if (level == observed) return;
            observed = level;
            ModSettings.DurationSeconds.Value = DurationFromLevel(level);
        }

        private int MaximumLevel => Slider.levelPins != null && Slider.levelPins.Count > 0
            ? Slider.levelPins.Count
            : 10;

        private int LevelFromDuration(float value)
        {
            float ratio = Mathf.InverseLerp(Minimum, Maximum, Mathf.Clamp(value, Minimum, Maximum));
            return Mathf.RoundToInt(ratio * MaximumLevel);
        }

        private float DurationFromLevel(int level)
        {
            float ratio = Mathf.Clamp(level, 0, MaximumLevel) / (float)MaximumLevel;
            return Mathf.Lerp(Minimum, Maximum, ratio);
        }
    }

    private enum TextKey
    {
        Mods, Title, Enabled, Duration, On, Off
    }

    private static ELanguage CurrentLanguage()
    {
        try { return LocalizationManager.Instance != null ? LocalizationManager.Instance.CurrentLanguage : ELanguage.EN; }
        catch { return ELanguage.EN; }
    }

    private static string Translate(TextKey key, ELanguage language)
    {
        return language switch
        {
            ELanguage.JP => key switch
            {
                TextKey.Mods => "MOD", TextKey.Title => "ジャンプしたいだけ",
                TextKey.Enabled => "有効", TextKey.Duration => "ジャンプ時間",
                TextKey.On => "はい", TextKey.Off => "いいえ", _ => key.ToString()
            },
            ELanguage.RU => key switch
            {
                TextKey.Mods => "Моды", TextKey.Title => "ПРОСТО ХОЧУ ПРЫГАТЬ",
                TextKey.Enabled => "Включено", TextKey.Duration => "Длительность прыжка",
                TextKey.On => "Да", TextKey.Off => "Нет", _ => key.ToString()
            },
            ELanguage.KO => key switch
            {
                TextKey.Mods => "모드", TextKey.Title => "그냥 점프하고 싶어",
                TextKey.Enabled => "활성화", TextKey.Duration => "점프 시간",
                TextKey.On => "예", TextKey.Off => "아니요", _ => key.ToString()
            },
            ELanguage.QC => key switch
            {
                TextKey.Mods => "Mods", TextKey.Title => "JE VEUX JUSTE SAUTER",
                TextKey.Enabled => "Activé", TextKey.Duration => "Durée du saut",
                TextKey.On => "Oui", TextKey.Off => "Non", _ => key.ToString()
            },
            ELanguage.FR => key switch
            {
                TextKey.Mods => "Mods", TextKey.Title => "JE VEUX JUSTE SAUTER",
                TextKey.Enabled => "Activé", TextKey.Duration => "Durée du saut",
                TextKey.On => "Oui", TextKey.Off => "Non", _ => key.ToString()
            },
            ELanguage.DE => key switch
            {
                TextKey.Mods => "Mods", TextKey.Title => "ICH WILL EINFACH SPRINGEN",
                TextKey.Enabled => "Aktiviert", TextKey.Duration => "Sprungdauer",
                TextKey.On => "Ja", TextKey.Off => "Nein", _ => key.ToString()
            },
            ELanguage.ES => key switch
            {
                TextKey.Mods => "Mods", TextKey.Title => "SOLO QUIERO SALTAR",
                TextKey.Enabled => "Activado", TextKey.Duration => "Duración del salto",
                TextKey.On => "Sí", TextKey.Off => "No", _ => key.ToString()
            },
            ELanguage.ptBR => key switch
            {
                TextKey.Mods => "Mods", TextKey.Title => "SÓ QUERO PULAR",
                TextKey.Enabled => "Ativado", TextKey.Duration => "Duração do pulo",
                TextKey.On => "Sim", TextKey.Off => "Não", _ => key.ToString()
            },
            ELanguage.zhCN => key switch
            {
                TextKey.Mods => "模组", TextKey.Title => "我只想跳跃",
                TextKey.Enabled => "启用", TextKey.Duration => "跳跃时长",
                TextKey.On => "是", TextKey.Off => "否", _ => key.ToString()
            },
            ELanguage.zhHK => key switch
            {
                TextKey.Mods => "模組", TextKey.Title => "我只想跳躍",
                TextKey.Enabled => "啟用", TextKey.Duration => "跳躍時長",
                TextKey.On => "是", TextKey.Off => "否", _ => key.ToString()
            },
            ELanguage.IT => key switch
            {
                TextKey.Mods => "Mod", TextKey.Title => "VOGLIO SOLO SALTARE",
                TextKey.Enabled => "Attivato", TextKey.Duration => "Durata del salto",
                TextKey.On => "Sì", TextKey.Off => "No", _ => key.ToString()
            },
            _ => key switch
            {
                TextKey.Mods => "Mods", TextKey.Title => "JUSTWANAJUMP",
                TextKey.Enabled => "Enabled", TextKey.Duration => "Jump duration",
                TextKey.On => "Yes", TextKey.Off => "No", _ => key.ToString()
            }
        };
    }

    private static Transform FindDirect(Transform parent, string name)
    {
        if (parent == null) return null;
        for (int i = 0; i < parent.childCount; i++)
        {
            Transform child = parent.GetChild(i);
            if (child != null && child.name == name) return child;
        }
        return null;
    }

    private static void CopyVerticalLayout(GameObject source, GameObject target)
    {
        VerticalLayoutGroup native = source.GetComponent<VerticalLayoutGroup>();
        var layout = target.AddComponent<VerticalLayoutGroup>();
        if (native != null)
        {
            layout.padding = native.padding;
            layout.childAlignment = native.childAlignment;
            layout.spacing = native.spacing;
            layout.childControlWidth = native.childControlWidth;
            layout.childControlHeight = native.childControlHeight;
            layout.childForceExpandWidth = native.childForceExpandWidth;
            layout.childForceExpandHeight = native.childForceExpandHeight;
            layout.childScaleWidth = native.childScaleWidth;
            layout.childScaleHeight = native.childScaleHeight;
            layout.reverseArrangement = native.reverseArrangement;
            return;
        }
        layout.childAlignment = TextAnchor.UpperCenter;
        layout.childControlWidth = true;
        layout.childControlHeight = false;
        layout.childForceExpandWidth = true;
        layout.childForceExpandHeight = false;
    }

    private static TextMeshProUGUI CreateTitle(GameObject source, Transform parent, out GameObject clone)
    {
        clone = Object.Instantiate(source, parent);
        DisableLocalizers(clone);
        TextMeshProUGUI text = FirstText(clone);
        text.raycastTarget = false;
        return text;
    }

    private static TextMeshProUGUI CreateDescription(GameObject source, Transform parent)
    {
        GameObject clone = Object.Instantiate(source, parent);
        DisableLocalizers(clone);
        clone.SetActive(true);
        TextMeshProUGUI text = FirstText(clone);
        text.raycastTarget = false;
        return text;
    }

    private static TextMeshProUGUI CreateRowLabel(TextMeshProUGUI source, Transform parent)
    {
        GameObject clone = Object.Instantiate(source.gameObject, parent);
        clone.name = "JustWanaJump row label";
        DisableLocalizers(clone);
        TextMeshProUGUI text = clone.GetComponent<TextMeshProUGUI>();
        RectTransform rect = text.rectTransform;
        rect.anchorMin = new Vector2(0f, 0f);
        rect.anchorMax = new Vector2(0f, 1f);
        rect.pivot = new Vector2(0f, 0.5f);
        rect.anchoredPosition = new Vector2(9f, 0f);
        rect.sizeDelta = new Vector2(160f, 0f);
        rect.localRotation = Quaternion.identity;
        rect.localScale = Vector3.one;
        text.alignment = TextAlignmentOptions.MidlineLeft;
        text.enableWordWrapping = false;
        text.enableAutoSizing = false;
        text.overflowMode = TextOverflowModes.Ellipsis;
        text.raycastTarget = false;
        return text;
    }

    private static void CopyTextStyle(TextMeshProUGUI source, TextMeshProUGUI target)
    {
        if (source == null || target == null) return;
        if (source.font != null) target.font = source.font;
        if (source.fontSharedMaterial != null) target.fontSharedMaterial = source.fontSharedMaterial;
        target.fontStyle = source.fontStyle;
        target.color = source.color;
    }

    private static void DisableLocalizers(GameObject root)
    {
        foreach (TextLocalizer localizer in root.GetComponentsInChildren<TextLocalizer>(true)) localizer.enabled = false;
        foreach (ImageLocalizer localizer in root.GetComponentsInChildren<ImageLocalizer>(true)) localizer.enabled = false;
    }

    private static TextMeshProUGUI FirstText(GameObject root)
    {
        var values = root.GetComponentsInChildren<TextMeshProUGUI>(true);
        if (values.Length == 0) throw new InvalidOperationException(root.name + " contains no text field.");
        return values[0];
    }

    private static GameObject NewRectObject(string name, Transform parent)
    {
        var result = new GameObject(name, new[] { Il2CppType.Of<RectTransform>() });
        result.layer = 5;
        result.transform.SetParent(parent, false);
        result.transform.localScale = Vector3.one;
        return result;
    }

    private static void CopyRect(RectTransform source, RectTransform target)
    {
        target.anchorMin = source.anchorMin;
        target.anchorMax = source.anchorMax;
        target.pivot = source.pivot;
        target.anchoredPosition = source.anchoredPosition;
        target.sizeDelta = source.sizeDelta;
        target.localRotation = source.localRotation;
        target.localScale = source.localScale;
    }
}
