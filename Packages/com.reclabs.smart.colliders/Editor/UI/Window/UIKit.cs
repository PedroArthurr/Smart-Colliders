using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace RecLabs.SmartColliders.UI
{
    /// <summary>Small UI Toolkit building blocks sharing the Smart Colliders look (see SmartCollidersWindow.uss).</summary>
    internal static class UIKit
    {
        public const string WindowStyle = "Packages/com.reclabs.smart.colliders/Editor/UI/Window/SmartCollidersWindow.uss";
        public static readonly Color IconColor = new Color(0.86f, 0.87f, 0.9f);
        public static readonly Color IconMuted = new Color(0.6f, 0.62f, 0.68f);

        public static void ApplyStyle(VisualElement root)
        {
            var sheet = AssetDatabase.LoadAssetAtPath<StyleSheet>(WindowStyle);
            if (sheet != null && !root.styleSheets.Contains(sheet)) root.styleSheets.Add(sheet);
            root.AddToClassList("sc-root");
        }

        public static T Class<T>(this T e, params string[] classes) where T : VisualElement
        {
            foreach (var c in classes) e.AddToClassList(c);
            return e;
        }

        public static Label Text(string text, params string[] classes) => new Label(text).Class(classes);

        public static HudIconElement Icon(HudIcon icon, Color? color = null, string cls = null)
        {
            var i = new HudIconElement(icon) { Color = color ?? IconColor };
            if (cls != null) i.AddToClassList(cls);
            return i;
        }

        /// <summary>Card with an icon + title header; content goes into the returned body.</summary>
        public static VisualElement Section(VisualElement parent, HudIcon icon, string title, out VisualElement body, string hint = null)
        {
            var card = new VisualElement().Class("sc-card");
            var header = new VisualElement().Class("sc-card__header");
            header.Add(Icon(icon, SmartCollidersTheme.Accent, "sc-card__icon"));
            header.Add(Text(title, "sc-card__title"));
            if (hint != null) header.Add(Text(hint, "sc-card__hint"));
            card.Add(header);
            body = new VisualElement().Class("sc-card__body");
            card.Add(body);
            parent.Add(card);
            return card;
        }

        public static VisualElement Row(VisualElement parent, string label = null, string tooltip = null)
        {
            var row = new VisualElement { tooltip = tooltip }.Class("sc-row");
            if (label != null) row.Add(Text(label, "sc-row__label"));
            parent.Add(row);
            return row;
        }

        public static VisualElement Button(string text, Action onClick, string tooltip = null, string cls = "sc-button")
        {
            var b = new Label(text) { tooltip = tooltip }.Class(cls);
            b.AddManipulator(new Clickable(onClick));
            return b;
        }

        public static VisualElement Primary(string text, Action onClick, string tooltip = null) =>
            Button(text, onClick, tooltip, "sc-button--primary").Class("sc-button");

        public static VisualElement IconButton(HudIcon icon, Action onClick, string tooltip, out HudIconElement iconElement)
        {
            var b = new VisualElement { tooltip = tooltip }.Class("sc-icon-button");
            iconElement = Icon(icon);
            b.Add(iconElement);
            b.AddManipulator(new Clickable(onClick));
            return b;
        }

        /// <summary>Divider line inside a card.</summary>
        public static VisualElement Divider(VisualElement parent)
        {
            var d = new VisualElement().Class("sc-divider-h");
            parent.Add(d);
            return d;
        }
    }

    /// <summary>Segmented control: one selected option out of N (text or icon), with a sliding highlight.</summary>
    internal sealed class Segmented : VisualElement
    {
        readonly List<VisualElement> _items = new List<VisualElement>();
        int _value = -1;
        public event Action<int> Changed;

        public Segmented(IReadOnlyList<string> labels, IReadOnlyList<string> tooltips = null)
        {
            AddToClassList("sc-segmented");
            for (int i = 0; i < labels.Count; i++)
            {
                int index = i;
                var item = new Label(labels[i]) { tooltip = tooltips != null && i < tooltips.Count ? tooltips[i] : null };
                item.AddToClassList("sc-segmented__item");
                item.AddManipulator(new Clickable(() =>
                {
                    if (!item.enabledSelf) return;
                    SetValueWithoutNotify(index);
                    Changed?.Invoke(index);
                }));
                _items.Add(item);
                Add(item);
            }
        }

        public int Value
        {
            get => _value;
            set { SetValueWithoutNotify(value); Changed?.Invoke(value); }
        }

        public void SetValueWithoutNotify(int value)
        {
            _value = value;
            for (int i = 0; i < _items.Count; i++) _items[i].EnableInClassList("sc-segmented__item--on", i == value);
        }

        public void SetItemEnabled(int index, bool enabled, string tooltip = null)
        {
            _items[index].SetEnabled(enabled);
            if (tooltip != null) _items[index].tooltip = tooltip;
        }
    }

    /// <summary>Row of preset chips (e.g. 2 / 5 / 10 / 25 / 50 / 100); highlights the matching preset.</summary>
    internal sealed class ChipRow : VisualElement
    {
        readonly List<(Label chip, float value)> _chips = new List<(Label, float)>();
        public event Action<float> Picked;

        public ChipRow(IReadOnlyList<float> values, Func<float, string> format)
        {
            AddToClassList("sc-chiprow");
            foreach (float v in values)
            {
                float value = v;
                var chip = new Label(format(v));
                chip.AddToClassList("sc-chip");
                chip.AddManipulator(new Clickable(() => Picked?.Invoke(value)));
                _chips.Add((chip, v));
                Add(chip);
            }
        }

        public void Highlight(float value)
        {
            foreach (var (chip, v) in _chips) chip.EnableInClassList("sc-chip--on", Mathf.Abs(v - value) < 1e-5f);
        }
    }

    /// <summary>Thin rounded progress bar.</summary>
    internal sealed class Bar : VisualElement
    {
        readonly VisualElement _fill;
        public Bar()
        {
            AddToClassList("sc-bar");
            _fill = new VisualElement();
            _fill.AddToClassList("sc-bar__fill");
            Add(_fill);
        }

        public float Value
        {
            set => _fill.style.width = Length.Percent(Mathf.Clamp01(value) * 100f);
        }

        public bool Paused
        {
            set => _fill.EnableInClassList("sc-bar__fill--paused", value);
        }
    }
}
