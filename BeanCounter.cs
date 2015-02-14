using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using UnityEngine;
using UObject = UnityEngine.Object;

namespace BeanCounter
{
    [KSPAddon(KSPAddon.Startup.Instantly, false)]
    public class BeanCounter : MonoBehaviour
    {
        private const int _height = 200;
        private const int _width = 300;
        public Rect windowPos = new Rect(40, 40, _width, _height);
        public bool showUI = false;

        // All the state we captured
        private List<BeanCount> collections;
        // All the type counted sorted in a easier to access object to avoid sorting each frame
        // Redundant with collections but allow for an easier to read code
        private List<TypeCount> sortedTypeCounts;
        // Reference to potential leaks
        private List<WeakReference> leaks;

        private int lastObjectCount = 5000;
        private int lastTypeCount = 40;
        private int displayedCount = 30;

        private int survivorsCount = 0;
        
        private static int leakOrigin = 0;
        private static int leakEnd = 0;

        private bool collectNextFrame = false;
        private bool sortNextFrame = false;
        private bool searchForSurvivorsNextFrame = false;
        private static GUIStyle _typeStyle;
        private static GUIStyle _valueStyle;
        private static GUIStyle _valueStyleRed;
        private static GUIStyle _toggleStyle;

        // Store a captured state
        private struct BeanCount
        {
            public float time;
            public GameScenes scene;
            public Dictionary<Type, int> count;
            // with access to source and Unity Pro profilter one could use Profiler.GetRuntimeMemorySize(obj)
            //public Dictionary<Type, int> mem; 
            // hopfully Unity does not recycle its objects instanceID
            public Dictionary<int, WeakReference> instanceIDs;
        }

        // 
        private struct TypeCount
        {
            public Type type;

            public int first
            {
                get { return counts != null && counts.Length > 0 ? counts[leakOrigin] : 0; }
            }

            public int last
            {
                get { return counts != null && counts.Length > 0 ? counts[leakEnd] : 0; }
            }

            public int[] counts;
        }

        internal void Awake()
        {
            DontDestroyOnLoad(gameObject);
            collections = new List<BeanCount>();
            sortedTypeCounts = new List<TypeCount>(lastTypeCount);
            leaks = new List<WeakReference>();
        }
        
        public void Update()
        {
            if (GameSettings.MODIFIER_KEY.GetKey() && Input.GetKeyDown(KeyCode.F3))
            {
                showUI = !showUI;
            }

            if (collectNextFrame)
            {
                collectNextFrame = false;
                Collect();
                sortNextFrame = true;
            }

            if (sortNextFrame)
            {
                sortNextFrame = false;
                Sort();
            }

            if (searchForSurvivorsNextFrame)
            {
                searchForSurvivorsNextFrame = false;
                SearchForSurvivors();
            }
        }

        public void OnGUI()
        {
            if (_typeStyle == null)
            {
                _typeStyle = new GUIStyle(GUI.skin.label)
                {
                    alignment = TextAnchor.MiddleLeft,
                    wordWrap = false,
                    padding = new RectOffset(2, 2, 0, 0)
                };

                _valueStyle = new GUIStyle(GUI.skin.label)
                {
                    alignment = TextAnchor.MiddleRight,
                    wordWrap = false,
                    padding = new RectOffset(2, 2, 0, 0)
                };

                _toggleStyle = new GUIStyle(GUI.skin.toggle)
                {
                    alignment = TextAnchor.MiddleCenter,
                    wordWrap = false,
                    padding = new RectOffset(2, 2, 0, 0)
                };

                _valueStyleRed = new GUIStyle(_valueStyle) { normal = { textColor = XKCDColors.Orange }, hover = { textColor = XKCDColors.Orange } };
            }

            if (showUI)
            {
                windowPos = GUILayout.Window(
                    8785479,
                    windowPos,
                    WindowGUI,
                    "GCMonitor",
                    GUILayout.ExpandWidth(true),
                    GUILayout.ExpandHeight(true));
            }
        }

        public void WindowGUI(int windowID)
        {
            //GUILayout.BeginVertical();

            GUILayout.BeginHorizontal();

            if (GUILayout.Button("Collect", GUILayout.ExpandWidth(false)))
            {
                collectNextFrame = true;
            }

            if (GUILayout.Button("Reset", GUILayout.ExpandWidth(false)))
            {
                Reset();
                sortNextFrame = true;
            }

            if (GUILayout.Button("Search Leaks", GUILayout.ExpandWidth(false)))
            {
                searchForSurvivorsNextFrame = true;
            }

            if (GUILayout.Button("Dump Leaks", GUILayout.ExpandWidth(false)))
            {
                DumpLeaks();
            }

            if (GUILayout.Button("-", GUILayout.ExpandWidth(false)))
            {
                displayedCount = Math.Max(displayedCount - 1, 1);
                windowPos.height = _height;
            }

            GUILayout.Label(displayedCount.ToString(), GUILayout.ExpandWidth(false));

            if (GUILayout.Button("+", GUILayout.ExpandWidth(false)))
            {
                displayedCount++;
            }

            GUILayout.EndHorizontal();
            GUILayout.BeginHorizontal();

            GUILayout.Label("Objects " + (collections.Count > 0 ? lastObjectCount : 0).ToString(), GUILayout.ExpandWidth(false));
            GUILayout.Label("Types " + (collections.Count > 0 ? lastTypeCount : 0).ToString(), GUILayout.ExpandWidth(false));
            GUILayout.Label("Potential leak " + survivorsCount, GUILayout.ExpandWidth(false));

            GUILayout.EndHorizontal();

            // The result table
            GUILayout.BeginHorizontal(GUILayout.ExpandWidth(false));

            GUILayout.BeginVertical();
            GUILayout.Label("Origin", _typeStyle);
            GUILayout.Label("End", _typeStyle);
            DrawColumn("", "Types", _typeStyle, sortedTypeCounts.Select(tc => tc.type.ToString()), displayedCount);
            GUILayout.EndVertical();

            int columns = collections.Count;

            leakOrigin = Math.Min(Math.Min(leakOrigin, leakEnd), columns);
            leakEnd = Math.Min(Math.Max(leakOrigin, leakEnd), columns);

            for (int column = 0; column < columns; column++)
            {
                TimeSpan t = TimeSpan.FromSeconds(collections[column].time - collections[0].time);
                string elapsed = string.Format("{0:D2}:{1:D2}:{2:D2}", t.Hours, t.Minutes, t.Seconds);
                string scene = collections[column].scene.ToString().Substring(0, 5);
                var dataStyles = sortedTypeCounts.Select(tc => column > 0 && tc.counts[column] > tc.counts[column - 1] ? _valueStyleRed : _valueStyle);
                GUILayout.BeginVertical();

                bool changedOrigin = GUILayout.Toggle(column == leakOrigin, "", _toggleStyle);
                if (changedOrigin)
                    leakOrigin = column;

                bool changedEnd = GUILayout.Toggle(column == leakEnd, "", _toggleStyle);
                if (changedEnd)
                    leakEnd = column;

                DrawColumn(elapsed, scene, _valueStyle, sortedTypeCounts.Select(tc => tc.counts[column].ToString()), displayedCount, dataStyles);
                GUILayout.EndVertical();
            }

            var deltaCounts = sortedTypeCounts.Select(tc => (tc.last - tc.first));
            var deltatyles = deltaCounts.Select(tc => tc > 0 ? _valueStyleRed : _valueStyle);
            GUILayout.BeginVertical();
            GUILayout.Label("", _valueStyle);
            GUILayout.Label("", _valueStyle);
            DrawColumn("", "Delta", _valueStyle, deltaCounts.Select(tc => tc.ToString()), displayedCount, deltatyles);
            GUILayout.EndVertical();

            GUILayout.EndHorizontal();

            //GUILayout.EndVertical();

            GUI.DragWindow();
        }

        private void DrawColumn(string header, string header2, GUIStyle s, IEnumerable<string> data, int lineLimit)
        {
            var dataStyles = Enumerable.Repeat(s, lineLimit);
            DrawColumn(header, header2, s, data, lineLimit, dataStyles);
        }

        private void DrawColumn(string header, string header2, GUIStyle s, IEnumerable<string> data, int lineLimit, IEnumerable<GUIStyle> dataStyles)
        {
            var dse = dataStyles.GetEnumerator();
            GUILayout.Label(header, s);
            GUILayout.Label(header2, s);
            int line = 0;
            foreach (string datum in data)
            {
                dse.MoveNext();
                GUILayout.Label(datum + "   ", dse.Current);
                if (++line == lineLimit)
                {
                    break;
                }
            }
        }

        private void Reset()
        {
            collections.Clear();
            sortedTypeCounts.Clear();
            sortNextFrame = true;
            leakOrigin = 0;
            windowPos.height = _height;
            windowPos.width= _width;
        }

        private void Collect()
        {
            print("Collect");
            BeanCount col = new BeanCount();

            col.time = Time.realtimeSinceStartup;
            col.scene = HighLogic.LoadedScene;
            col.count = new Dictionary<Type, int>(lastTypeCount);
            col.instanceIDs = new Dictionary<int, WeakReference>(lastObjectCount);

            UObject[] allObjects = FindObjectsOfType<UObject>();

            for (int i = 0; i < allObjects.Length; i++)
            {
                UObject obj = allObjects[i];
                col.instanceIDs.Add(obj.GetInstanceID(), new WeakReference(obj));

                int c;
                if (col.count.TryGetValue(obj.GetType(), out c))
                {
                    col.count[obj.GetType()] = c + 1;
                }
                else
                {
                    col.count[obj.GetType()] = 1;
                }
            }

            lastObjectCount = allObjects.Length;
            lastTypeCount = col.count.Count;
            leakEnd = collections.Count;

            collections.Add(col);
        }

        private void Sort()
        {
            if (collections.Count == 0)
            {
                return;
            }

            var sortedCounts = from pair in collections.Last().count
                orderby pair.Value descending
                select pair.Key;

            sortedTypeCounts.Clear();
            foreach (Type t in sortedCounts)
            {
                TypeCount count = new TypeCount();

                count.type = t;
                count.counts = new int[collections.Count];

                for (int i = 0; i < collections.Count; i++)
                {
                    int c;
                    if (collections[i].count.TryGetValue(t, out c))
                    {
                        count.counts[i] = c;
                    }
                }
                sortedTypeCounts.Add(count);
            }
        }

        private void SearchForSurvivors()
        {
            print("Starting Search for Survivors");
            // Create a list of the object common to all collections
            /*
            IEnumerable<int> common = collections[0].instanceIDs.Keys;
            for (int i = 1; i < collections.Count; i++)
            {
                common = common.Intersect(collections[i].instanceIDs.Keys);
            }

            print("List of common object ready");

            // Last collection minus the common elements shloud constain mostly leaks.
            IEnumerable<int> leakIDs = collections[collections.Count - 1].instanceIDs.Keys.Except(common);

            print("List of leaks IDs ready");
            */


            // For now consider anything created since the first capture as a leak. 
            // I m sure there are ways to clean that up but I m less sure about the time investment required to refine this.
            IEnumerable<int> leakIDs = collections[leakEnd].instanceIDs.Keys.Except(collections[leakEnd].instanceIDs.Keys);

            var lastInstances = collections[leakEnd].instanceIDs;

            leaks.Clear();
            foreach (var id in leakIDs)
            {
                leaks.Add(lastInstances[id]);
            }

            print("List of leaks ready");

            survivorsCount = leaks.Count();
        }

        private void DumpLeaks()
        {
            StringBuilder sb = new StringBuilder();

            sb.AppendFormat("found {0} potential leaks\n", leaks.Count);

            foreach (var obj in leaks)
            {
                UObject o = (UObject)obj.Target;
                if (o != null)
                {
                    sb.AppendFormat("{0} - {1}", o.GetType(), o.name);

                    if (o is GameObject)
                    {
                        GameObject go = (GameObject)o;
                        if (go.transform != null && go.transform.parent != null)
                        {
                            sb.AppendFormat(" - GameObject parent name {0}", go.transform.parent.name);
                        }
                    }
                    else if (o is Component)
                    {
                        Component c = (Component)o;
                        if (c.transform != null && c.transform.parent != null)
                        {
                            sb.AppendFormat(" - Component parent name {0}", c.transform.parent.name);
                        }
                    }

                    sb.AppendFormat("\n");
                }
            }
            print(sb.ToString());
        }

        public new static void print(object message)
        {
            MonoBehaviour.print("[BeanCounter] " + message);
        }
    }
}