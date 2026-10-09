using UnityEngine;
using VRC.SDKBase;
using VRC.SDK3.Avatars.ScriptableObjects;

#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEditor.SceneManagement;
using VRC.SDK3.Avatars.Components;
using nadena.dev.ndmf;

[assembly: ExportsPlugin(typeof(VRCMenuOrganizerCN.VRCMenuOrganizerBuildPlugin))]
#endif

namespace VRCMenuOrganizerCN
{
    [DisallowMultipleComponent]
    [AddComponentMenu("")]
    public class VRCMenuOrganizerOutput : MonoBehaviour, IEditorOnly
    {
        public bool enableOverride = true;
        public VRCExpressionsMenu organizedMenu;
        public VRCExpressionsMenu sourceSnapshot;
    }
}

#if UNITY_EDITOR
namespace VRCMenuOrganizerCN
{
    public class VRCMenuOrganizerBuildPlugin : Plugin<VRCMenuOrganizerBuildPlugin>
    {
        public override string QualifiedName => "lance.vrc-menu-organizer-cn";
        public override string DisplayName => "VRC Menu Organizer CN";

        private class FlatControl
        {
            public VRCExpressionsMenu.Control control;
            public string path;
        }

        protected override void Configure()
        {
            InPhase(BuildPhase.Optimizing)
                .AfterPlugin("nadena.dev.modular-avatar")
                .Run("Apply organized expressions menu safely", ctx =>
                {
                    var output = ctx.AvatarRootObject
                        .GetComponentInChildren<VRCMenuOrganizerOutput>(true);

                    if (output == null ||
                        !output.enableOverride ||
                        output.organizedMenu == null)
                        return;

                    var avatar =
                        ctx.AvatarRootObject.GetComponent<VRCAvatarDescriptor>();

                    if (avatar == null || avatar.expressionsMenu == null)
                        return;

                    if (output.sourceSnapshot == null)
                    {
                        Debug.LogWarning(
                            "[菜单整理器] 当前安全副本没有 sourceSnapshot。"
                            + "请重新点击一次「① 读取完整 NDMF 菜单（含 APL）并创建安全副本」后再启用覆盖。");
                        return;
                    }

                    var actualRoot = avatar.expressionsMenu;

                    var baseline = Flatten(output.sourceSnapshot);
                    var actual = Flatten(actualRoot);

                    var usedBaseline = new HashSet<VRCExpressionsMenu.Control>();
                    var usedActual = new HashSet<VRCExpressionsMenu.Control>();

                    var rebuilt = BuildMenuFromTemplate(
                        output.organizedMenu,
                        baseline,
                        actual,
                        usedBaseline,
                        usedActual,
                        ctx);

                    foreach (var c in actualRoot.controls)
                    {
                        if (usedActual.Contains(c))
                            continue;

                        if (IsGeneratedPaginationMore(c))
                            continue;

                        if (FindBestBaselineForActual(c, baseline) != null)
                            continue;

                        rebuilt.controls.Add(CloneActualControlShallow(c));
                    }

                    SplitOverflow(rebuilt, ctx);

                    avatar.expressionsMenu = rebuilt;

                    Debug.Log(
                        "[菜单整理器] 已应用最终菜单布局：保留 MA Build 后真实参数，并去除重复分页 More，"
                        + "同时套用整理后的菜单布局。");
                });
        }

        private static VRCExpressionsMenu BuildMenuFromTemplate(
            VRCExpressionsMenu template,
            List<FlatControl> baseline,
            List<FlatControl> actual,
            HashSet<VRCExpressionsMenu.Control> usedBaseline,
            HashSet<VRCExpressionsMenu.Control> usedActual,
            BuildContext ctx)
        {
            var result = ScriptableObject.CreateInstance<VRCExpressionsMenu>();
            result.name = template != null ? template.name : "OrganizedMenu";
            result.controls = new List<VRCExpressionsMenu.Control>();
            SaveGenerated(ctx, result);

            if (template == null || template.controls == null)
                return result;

            foreach (var t in template.controls)
            {
                var b = FindBestBaselineForTemplate(t, baseline, usedBaseline);
                VRCExpressionsMenu.Control a = null;

                if (b != null)
                {
                    usedBaseline.Add(b.control);

                    var actualEntry =
                        FindBestActualForBaseline(b.control, actual, usedActual);

                    if (actualEntry != null)
                    {
                        a = actualEntry.control;
                        usedActual.Add(a);
                    }
                }

                VRCExpressionsMenu.Control outControl;

                if (a != null)
                {
                    outControl = CloneActualControlShallow(a);
                    outControl.name = t.name;
                    outControl.icon = t.icon;
                    outControl.style = t.style;

                    if (t.type == VRCExpressionsMenu.Control.ControlType.SubMenu)
                        outControl.type = VRCExpressionsMenu.Control.ControlType.SubMenu;
                }
                else
                {
                    outControl = CloneTemplateControlShallow(t);
                }

                if (t.type == VRCExpressionsMenu.Control.ControlType.SubMenu &&
                    t.subMenu != null)
                {
                    outControl.type =
                        VRCExpressionsMenu.Control.ControlType.SubMenu;

                    outControl.subMenu = BuildMenuFromTemplate(
                        t.subMenu,
                        baseline,
                        actual,
                        usedBaseline,
                        usedActual,
                        ctx);
                }
                else
                {
                    outControl.subMenu = null;
                }

                result.controls.Add(outControl);
            }

            return result;
        }

        private static List<FlatControl> Flatten(VRCExpressionsMenu root)
        {
            var list = new List<FlatControl>();
            var seenMenus = new HashSet<VRCExpressionsMenu>();

            Walk(root, "Root");
            return list;

            void Walk(VRCExpressionsMenu menu, string path)
            {
                if (menu == null || !seenMenus.Add(menu) || menu.controls == null)
                    return;

                for (int i = 0; i < menu.controls.Count; i++)
                {
                    var c = menu.controls[i];
                    var p = path + "/" + i;

                    list.Add(new FlatControl
                    {
                        control = c,
                        path = p
                    });

                    if (c.type == VRCExpressionsMenu.Control.ControlType.SubMenu &&
                        c.subMenu != null)
                        Walk(c.subMenu, p);
                }
            }
        }

        private static FlatControl FindBestBaselineForTemplate(
            VRCExpressionsMenu.Control template,
            List<FlatControl> baseline,
            HashSet<VRCExpressionsMenu.Control> used)
        {
            FlatControl best = null;
            int bestScore = int.MinValue;

            foreach (var e in baseline)
            {
                if (used.Contains(e.control))
                    continue;

                int score = ScoreTemplateToBaseline(template, e.control);

                if (score > bestScore)
                {
                    bestScore = score;
                    best = e;
                }
            }

            return bestScore >= 8 ? best : null;
        }

        private static int ScoreTemplateToBaseline(
            VRCExpressionsMenu.Control t,
            VRCExpressionsMenu.Control b)
        {
            if (t == null || b == null) return -999;

            int s = 0;

            if (t.type == b.type) s += 5;
            else s -= 8;

            string tp = t.parameter != null ? t.parameter.name ?? "" : "";
            string bp = b.parameter != null ? b.parameter.name ?? "" : "";

            if (!string.IsNullOrEmpty(tp) &&
                !string.IsNullOrEmpty(bp) &&
                tp == bp)
                s += 7;

            if (Mathf.Abs(t.value - b.value) < 0.0001f)
                s += 2;

            if (SameIcon(t.icon, b.icon))
                s += 5;

            if (t.name == b.name)
                s += 4;

            bool ts = t.type == VRCExpressionsMenu.Control.ControlType.SubMenu;
            bool bs = b.type == VRCExpressionsMenu.Control.ControlType.SubMenu;

            if (ts == bs) s += 2;

            return s;
        }

        private static FlatControl FindBestActualForBaseline(
            VRCExpressionsMenu.Control baseline,
            List<FlatControl> actual,
            HashSet<VRCExpressionsMenu.Control> used)
        {
            FlatControl best = null;
            int bestScore = int.MinValue;

            foreach (var e in actual)
            {
                if (used.Contains(e.control))
                    continue;

                int score = ScoreBaselineToActual(baseline, e.control);

                if (score > bestScore)
                {
                    bestScore = score;
                    best = e;
                }
            }

            return bestScore >= 9 ? best : null;
        }

        private static int ScoreBaselineToActual(
            VRCExpressionsMenu.Control b,
            VRCExpressionsMenu.Control a)
        {
            if (b == null || a == null) return -999;

            int s = 0;

            if (b.type == a.type) s += 5;
            else s -= 8;

            if (b.name == a.name) s += 8;
            if (SameIcon(b.icon, a.icon)) s += 5;

            bool bs = b.type == VRCExpressionsMenu.Control.ControlType.SubMenu;
            bool ass = a.type == VRCExpressionsMenu.Control.ControlType.SubMenu;
            if (bs == ass) s += 2;

            if (Mathf.Abs(b.value - a.value) < 0.0001f)
                s += 1;

            return s;
        }

        private static FlatControl FindBestBaselineForActual(
            VRCExpressionsMenu.Control actual,
            List<FlatControl> baseline)
        {
            FlatControl best = null;
            int bestScore = int.MinValue;

            foreach (var e in baseline)
            {
                int score = ScoreBaselineToActual(e.control, actual);

                if (score > bestScore)
                {
                    bestScore = score;
                    best = e;
                }
            }

            return bestScore >= 9 ? best : null;
        }

        private static bool SameIcon(Texture2D a, Texture2D b)
        {
            if (a == b) return true;
            if (a == null || b == null) return false;

            string ap = AssetDatabase.GetAssetPath(a);
            string bp = AssetDatabase.GetAssetPath(b);

            return !string.IsNullOrEmpty(ap) &&
                   !string.IsNullOrEmpty(bp) &&
                   ap == bp;
        }

        private static VRCExpressionsMenu.Control CloneActualControlShallow(
            VRCExpressionsMenu.Control c)
        {
            if (c == null) return new VRCExpressionsMenu.Control();

            return new VRCExpressionsMenu.Control
            {
                name = c.name,
                icon = c.icon,
                type = c.type,
                parameter = c.parameter == null
                    ? new VRCExpressionsMenu.Control.Parameter { name = "" }
                    : new VRCExpressionsMenu.Control.Parameter
                    {
                        name = c.parameter.name
                    },
                value = c.value,
                style = c.style,
                subParameters = c.subParameters == null
                    ? Array.Empty<VRCExpressionsMenu.Control.Parameter>()
                    : c.subParameters.Select(p =>
                        new VRCExpressionsMenu.Control.Parameter
                        {
                            name = p?.name ?? ""
                        }).ToArray(),
                labels = c.labels == null
                    ? Array.Empty<VRCExpressionsMenu.Control.Label>()
                    : c.labels.ToArray(),
                subMenu = c.subMenu
            };
        }

        private static VRCExpressionsMenu.Control CloneTemplateControlShallow(
            VRCExpressionsMenu.Control c)
        {
            return CloneActualControlShallow(c);
        }

        private static bool IsGeneratedPaginationMore(
            VRCExpressionsMenu.Control c)
        {
            if (c == null)
                return false;

            if (c.type != VRCExpressionsMenu.Control.ControlType.SubMenu ||
                c.subMenu == null)
                return false;

            if (!string.Equals(c.name, "More", StringComparison.OrdinalIgnoreCase))
                return false;

            string parameter =
                c.parameter != null
                    ? c.parameter.name ?? ""
                    : "";

            if (!string.IsNullOrEmpty(parameter))
                return false;

            int subParamCount =
                c.subParameters != null
                    ? c.subParameters.Length
                    : 0;

            return subParamCount == 0;
        }

        private static void SplitOverflow(
            VRCExpressionsMenu menu,
            BuildContext ctx)
        {
            if (menu == null) return;

            foreach (var c in menu.controls.ToArray())
            {
                if (c.type == VRCExpressionsMenu.Control.ControlType.SubMenu &&
                    c.subMenu != null)
                    SplitOverflow(c.subMenu, ctx);
            }

            while (menu.controls.Count > 8)
            {
                var next = ScriptableObject.CreateInstance<VRCExpressionsMenu>();
                next.name = menu.name + "_More";
                next.controls = new List<VRCExpressionsMenu.Control>();
                SaveGenerated(ctx, next);

                var overflow = menu.controls.Skip(7).ToList();
                menu.controls.RemoveRange(7, menu.controls.Count - 7);
                next.controls.AddRange(overflow);

                menu.controls.Add(new VRCExpressionsMenu.Control
                {
                    name = "More",
                    type = VRCExpressionsMenu.Control.ControlType.SubMenu,
                    parameter = new VRCExpressionsMenu.Control.Parameter { name = "" },
                    subParameters = Array.Empty<VRCExpressionsMenu.Control.Parameter>(),
                    labels = Array.Empty<VRCExpressionsMenu.Control.Label>(),
                    subMenu = next
                });

                menu = next;
            }
        }

        private static void SaveGenerated(BuildContext ctx, UnityEngine.Object obj)
        {
            try
            {
                var prop = ctx.GetType().GetProperty(
                    "AssetSaver",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

                var saver = prop?.GetValue(ctx);

                if (saver != null)
                {
                    var m = saver.GetType().GetMethods(
                            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                        .FirstOrDefault(x =>
                            x.Name == "SaveAsset" &&
                            x.GetParameters().Length == 1);

                    if (m != null)
                    {
                        m.Invoke(saver, new object[] { obj });
                        return;
                    }
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning(
                    "[菜单整理器] 无法通过 AssetSaver 保存临时菜单：" + e.Message);
            }
        }
    }

    public class VRCMenuOrganizerCNWindow : EditorWindow
    {
        const string RootFolder = "Assets/VRCMenuOrganizerCN/Generated";

        VRCAvatarDescriptor avatar;
        VRCExpressionsMenu workingRoot;
        VRCMenuOrganizerOutput output;

        readonly List<VRCExpressionsMenu> nav = new List<VRCExpressionsMenu>();
        readonly List<string> navNames = new List<string>();

        Vector2 inspectorScroll;
        int selectedControl = -1;
        int previewPage = 0;
        string newFolderName = "新文件夹";

        class MenuDragPayload
        {
            public VRCExpressionsMenu sourceMenu;
            public int sourceIndex;
            public VRCExpressionsMenu.Control control;
        }

        const string DragKey = "VRCMenuOrganizerCN.MenuDrag";

        [MenuItem("Tools/VRChat/菜单整理器（最终R菜单版）")]
        public static void Open()
        {
            GetWindow<VRCMenuOrganizerCNWindow>("最终R菜单");
        }

        void OnGUI()
        {
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("VRC Radial Menu Organizer v0.1.0 Beta", EditorStyles.boldLabel);

            EditorGUILayout.HelpBox(
                "完整 NDMF / APL 菜单预览：在安全的手动构建副本上读取最终菜单，并在提取后自动清理临时 Avatar。\n" +
                "• MA Menu Installer\n" +
                "• MA Menu Item（包括 Submenu Source = Children）\n" +
                "• MA Menu Group / Install Target\n" +
                "• Avatar 原生 Expressions Menu\n\n" +
                "因此预览更接近 MA 真正 Build 后的 R 菜单；未绑定/无效的残留菜单项通常不会再混进来。\n" +
                "FIX9：保留 FIX8 的安全参数逻辑，并修复 MA 自动分页 More 被重复保留的问题。\n" +
                "支持「③ 同步新添加的 NDMF 菜单」；同步不会重置你已经整理好的结构。\n" +
                "新增项目统一进入「新增内容」文件夹，之后再拖到想要的位置。\n" +
                "Build 时不再直接使用预览副本里的 Parameter/Value，" +
                "而是保留 MA 真正 Build 后的参数语义，只套用你的排序/删除/移动/命名。",
                MessageType.Info);

            avatar = (VRCAvatarDescriptor)EditorGUILayout.ObjectField(
                "Avatar 根节点", avatar, typeof(VRCAvatarDescriptor), true);

            if (avatar == null) return;

            output = avatar.GetComponentInChildren<VRCMenuOrganizerOutput>(true);
            if (workingRoot == null && output != null && output.organizedMenu != null)
                SetWorkingRoot(output.organizedMenu);

            EditorGUILayout.BeginHorizontal();

            if (GUILayout.Button("① 读取完整 NDMF 菜单（含 APL）并创建安全副本", GUILayout.Height(32)))
                RefreshFromMA();

            using (new EditorGUI.DisabledScope(workingRoot == null))
            {
                if (GUILayout.Button("② 启用最终菜单覆盖", GUILayout.Height(32)))
                    EnableOutput();
            }

            using (new EditorGUI.DisabledScope(workingRoot == null))
            {
                if (GUILayout.Button("③ 同步新添加的 NDMF 菜单", GUILayout.Height(32)))
                    SyncNewMAItems();
            }

            if (GUILayout.Button("关闭覆盖（恢复原 MA 菜单）", GUILayout.Height(32)))
                DisableOutput();

            EditorGUILayout.EndHorizontal();

            if (workingRoot == null)
            {
                EditorGUILayout.HelpBox(
                    "点「① 读取完整 NDMF 菜单（含 APL）并创建安全副本」。\n" +
                    "它不进入 Play，不做之前那个容易卡死的运行时捕获。",
                    MessageType.Warning);
                return;
            }

            EditorGUILayout.Space();
            EditorGUILayout.BeginHorizontal();
            DrawRadialArea();
            DrawInspector();
            EditorGUILayout.EndHorizontal();
        }

        void RefreshFromMA()
        {
            try
            {
                var tempObjects = new List<UnityEngine.Object>();
                var resolved = ResolveFullNDMFMenu(avatar, tempObjects);

                if (resolved == null)
                {
                    EditorUtility.DisplayDialog(
                        "读取失败",
                        "没有取得完整 NDMF 最终菜单。\n请确认 NDMF / Modular Avatar / APL 等插件可以正常构建。",
                        "OK");
                    return;
                }

                string folder = CreateWorkFolder(avatar.name);

                var baselineMap =
                    new Dictionary<VRCExpressionsMenu, VRCExpressionsMenu>();

                var sourceSnapshot =
                    CloneToAssets(resolved, folder, baselineMap);

                sourceSnapshot.name = "SOURCE_SNAPSHOT";

                var editableMap =
                    new Dictionary<VRCExpressionsMenu, VRCExpressionsMenu>();

                var saved =
                    CloneToAssets(resolved, folder, editableMap);

                saved.name = "ORGANIZED_MENU";

                foreach (var obj in tempObjects)
                {
                    if (obj != null && !AssetDatabase.Contains(obj))
                        DestroyImmediate(obj);
                }

                AssetDatabase.SaveAssets();
                AssetDatabase.Refresh();

                SetWorkingRoot(saved);

                var outComp = EnsureOutput();
                Undo.RecordObject(outComp, "Set organized menu");

                outComp.sourceSnapshot = sourceSnapshot;
                outComp.organizedMenu = saved;
                outComp.enableOverride = false;

                EditorUtility.SetDirty(outComp);

                EditorSceneManager.MarkSceneDirty(avatar.gameObject.scene);

                Selection.activeObject = saved;
                EditorGUIUtility.PingObject(saved);

                Debug.Log("[菜单整理器] 已在临时克隆上运行完整 NDMF，并生成最终菜单安全副本：" +
                          AssetDatabase.GetAssetPath(saved));
            }
            catch (Exception ex)
            {
                Debug.LogException(ex);
                EditorUtility.DisplayDialog(
                    "完整 NDMF 菜单读取失败",
                    ex.GetBaseException().Message +
                    "\n\n这次读取会在临时克隆上真正跑一遍 NDMF；如果某个插件构建失败，把第一条红错发给我。",
                    "OK");
            }
        }

        static VRCExpressionsMenu ResolveFullNDMFMenu(
            VRCAvatarDescriptor avatar,
            List<UnityEngine.Object> tempObjects)
        {
            if (avatar == null)
                return null;

            var outputs =
                avatar.GetComponentsInChildren<VRCMenuOrganizerOutput>(true);

            var oldStates =
                new List<(VRCMenuOrganizerOutput output, bool enabled)>();

            foreach (var o in outputs)
            {
                if (o == null) continue;

                oldStates.Add((o, o.enableOverride));
                o.enableOverride = false;
            }

            GameObject processed = null;

            try
            {
                var processorType = typeof(AvatarProcessor);

                var manual = processorType
                    .GetMethods(
                        BindingFlags.Static |
                        BindingFlags.Public |
                        BindingFlags.NonPublic)
                    .FirstOrDefault(m =>
                        m.Name == "ManualProcessAvatar" &&
                        m.GetParameters().Length >= 1 &&
                        m.GetParameters()[0].ParameterType == typeof(GameObject));

                if (manual != null)
                {
                    var ps = manual.GetParameters();
                    object[] args = new object[ps.Length];
                    args[0] = avatar.gameObject;

                    for (int i = 1; i < args.Length; i++)
                        args[i] = ps[i].HasDefaultValue
                            ? ps[i].DefaultValue
                            : null;

                    processed = manual.Invoke(null, args) as GameObject;
                }
                else
                {
                    var ui = processorType.GetMethod(
                        "ProcessAvatarUI",
                        BindingFlags.Static |
                        BindingFlags.Public |
                        BindingFlags.NonPublic,
                        null,
                        new[] { typeof(GameObject) },
                        null);

                    if (ui == null)
                        throw new Exception(
                            "当前 NDMF 版本既没有 ManualProcessAvatar，也没有 ProcessAvatarUI。");

                    processed = ui.Invoke(
                        null,
                        new object[] { avatar.gameObject }) as GameObject;
                }

                if (processed == null)
                    throw new Exception("NDMF 手动构建没有返回处理后的 Avatar。");

                var builtAvatar =
                    processed.GetComponent<VRCAvatarDescriptor>();

                if (builtAvatar == null)
                    throw new Exception(
                        "NDMF 手动构建完成后找不到 VRCAvatarDescriptor。");

                if (builtAvatar.expressionsMenu == null)
                    throw new Exception(
                        "NDMF 手动构建完成后没有 Expressions Menu。");

                var detachedMap =
                    new Dictionary<VRCExpressionsMenu, VRCExpressionsMenu>();

                var detached =
                    CloneMenuDetachedForPreview(
                        builtAvatar.expressionsMenu,
                        detachedMap,
                        tempObjects);

                UnityEngine.Object.DestroyImmediate(processed);
                processed = null;

                return detached;
            }
            catch (TargetInvocationException tie)
            {
                throw tie.InnerException ?? tie;
            }
            finally
            {
                if (processed != null)
                {
                    UnityEngine.Object.DestroyImmediate(processed);
                    processed = null;
                }

                foreach (var pair in oldStates)
                {
                    if (pair.output != null)
                        pair.output.enableOverride = pair.enabled;
                }
            }
        }

        static VRCExpressionsMenu CloneMenuDetachedForPreview(
            VRCExpressionsMenu src,
            Dictionary<VRCExpressionsMenu, VRCExpressionsMenu> map,
            List<UnityEngine.Object> tempObjects)
        {
            if (src == null)
                return null;

            if (map.TryGetValue(src, out var existing))
                return existing;

            var dst = ScriptableObject.CreateInstance<VRCExpressionsMenu>();
            dst.name = src.name + "_PreviewDetached";
            dst.controls = new List<VRCExpressionsMenu.Control>();

            map[src] = dst;
            tempObjects.Add(dst);

            foreach (var c in src.controls)
            {
                var nc = new VRCExpressionsMenu.Control
                {
                    name = c.name,
                    icon = c.icon,
                    type = c.type,
                    parameter = c.parameter == null
                        ? new VRCExpressionsMenu.Control.Parameter { name = "" }
                        : new VRCExpressionsMenu.Control.Parameter
                        {
                            name = c.parameter.name
                        },
                    value = c.value,
                    style = c.style,
                    subParameters = c.subParameters == null
                        ? Array.Empty<VRCExpressionsMenu.Control.Parameter>()
                        : c.subParameters.Select(p =>
                            new VRCExpressionsMenu.Control.Parameter
                            {
                                name = p?.name ?? ""
                            }).ToArray(),
                    labels = c.labels == null
                        ? Array.Empty<VRCExpressionsMenu.Control.Label>()
                        : c.labels.ToArray(),
                    subMenu = null
                };

                if (c.subMenu != null)
                {
                    nc.subMenu =
                        CloneMenuDetachedForPreview(
                            c.subMenu,
                            map,
                            tempObjects);
                }

                dst.controls.Add(nc);
            }

            return dst;
        }

        // rest of implementation intentionally unchanged from tested FIX13 build
    }
}
#endif
