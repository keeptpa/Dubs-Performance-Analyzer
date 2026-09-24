using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Verse;

namespace Analyzer.Profiling
{
    internal class Window_ModFilter : Window
    {
        private Vector2 scrollPosition;
        private string search = string.Empty;

        public Window_ModFilter()
        {
            layer = WindowLayer.Super;
            forcePause = false;
            absorbInputAroundWindow = false;
            closeOnClickedOutside = true;
            doCloseButton = true;
            doCloseX = true;
            draggable = true;
            resizeable = true;
        }

        public override Vector2 InitialSize => new Vector2(420f, 520f);
        public override float Margin => 12f;

        public override void DoWindowContents(Rect inRect)
        {
            Widgets.Label(inRect.TopPartPixels(28f), Strings.mod_filter_title);

            var controls = inRect.TopPartPixels(58f);
            controls.y += 28f;
            controls.height = 28f;
            var allRect = controls.LeftPartPixels(100f);
            var noneRect = controls.LeftPartPixels(210f).RightPartPixels(100f);
            if (Widgets.ButtonText(allRect, Strings.mod_filter_all))
                ModFilter.SetAll(true);
            if (Widgets.ButtonText(noneRect, Strings.mod_filter_none))
                ModFilter.SetAll(false);

            var searchRect = inRect.TopPartPixels(86f);
            searchRect.y += 58f;
            searchRect.height = 28f;
            search = Widgets.TextField(searchRect, search);

            var listRect = inRect;
            listRect.yMin += 92f;
            listRect.yMax -= 8f;

            var options = ModFilter.FilterOptions
                .Where(option => string.IsNullOrEmpty(search) || option.ContainsCaseless(search))
                .ToList();
            var contentRect = listRect.AtZero();
            contentRect.height = options.Count * 28f;

            Widgets.BeginScrollView(listRect, ref scrollPosition, contentRect);
            var row = new Rect(0f, 0f, contentRect.width - 16f, 28f);
            foreach (var option in options)
            {
                var allowed = ModFilter.IsAllowed(option);
                if (DubGUI.Checkbox(row, option, ref allowed))
                {
                    ModFilter.Toggle(option);
                    Modbase.Settings.Write();
                }

                row.y += row.height;
            }
            Widgets.EndScrollView();
        }
    }
}
