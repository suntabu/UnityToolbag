#if daddy
#define daddy
#endif

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using AClockworkBerry;
using UnityEngine;
using UnityEngine.SceneManagement;
using Debug = UnityEngine.Debug;
using Object = UnityEngine.Object;

namespace UnityToolbag.WhoIsYourDaddy
{
    public class Daddy : MonoBehaviour
    {
        public const string DONE = "DONE";
        public const string ERROR = "ERROR:{0}";


        public static bool IsPersistent = true;

        private static Daddy instance;
        private static bool instantiated = false;

        static List<DaddyCommandAttribute> _attributes = new List<DaddyCommandAttribute>();

        public enum PanelAnchor
        {
            TopLeft,
            TopRight,
            BottomLeft,
            BottomRight
        }

        [Tooltip("Height of the log area as a percentage of the screen height")] [Range(0.3f, 1.0f)]
        public float Height = 0.5f;

        [Tooltip("Width of the log area as a percentage of the screen width")] [Range(0.3f, 1.0f)]
        public float Width = 0.5f;

        public int Margin = 20;

        public int ItemHeight = 40;

        public PanelAnchor AnchorPosition = PanelAnchor.BottomLeft;

        public int FontSize = 14;

        [Range(0f, 01f)] public float BackgroundOpacity = 0.5f;
        public Color BackgroundColor = Color.black;

        GUIStyle styleContainer, styleText;
        int padding = 5;

        private bool destroying = false;
        public bool ShowInEditor = true;

        public static Daddy Instance
        {
            get
            {
                if (instantiated) return instance;

                instance = GameObject.FindObjectOfType(typeof(Daddy)) as Daddy;

                // Object not found, we create a new one
                if (instance == null)
                {
                    // Try to load the default prefab
                    try
                    {
                        instance = Instantiate(Resources.Load("DaddyPrefab", typeof(Daddy))) as Daddy;
                    }
                    catch (Exception e)
                    {
                        Debug.Log("Failed to load default Daddy prefab...");
                        instance = new GameObject("ScreenLogger", typeof(Daddy)).GetComponent<Daddy>();
                    }

                    // Problem during the creation, this should not happen
                    if (instance == null)
                    {
                        Debug.LogError("Problem during the creation of Daddy");
                    }
                    else instantiated = true;
                }
                else
                {
                    instantiated = true;
                }

                return instance;
            }
        }


        public void Awake()
        {
            Daddy[] obj = GameObject.FindObjectsOfType<Daddy>();

            if (obj.Length > 1)
            {
                Debug.Log("Destroying Daddy Script, already exists...");

                destroying = true;

                Destroy(gameObject);
                return;
            }

            InitStyles();

            if (IsPersistent)
                DontDestroyOnLoad(this);

            _attributes = new List<DaddyCommandAttribute>(16);
            foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                foreach (Type type in assembly.GetTypes())
                {
                    foreach (MethodInfo method in type.GetMethods(BindingFlags.Public | BindingFlags.Static))
                    {
                        DaddyCommandAttribute[] attrs =
                            method.GetCustomAttributes(typeof(DaddyCommandAttribute), true) as DaddyCommandAttribute[];
                        if (attrs.Length == 0)
                            continue;

                        Func<string, string> cbm =
                            Delegate.CreateDelegate(typeof(Func<string, string>), method,
                                false) as Func<string, string>;
                        if (cbm == null)
                        {
                            Debug.LogError(string.Format("Method {0}.{1} takes the wrong arguments for a rule checker.",
                                type, method.Name));
                            continue;
                        }

                        // try with a bare action
                        foreach (DaddyCommandAttribute rule in attrs)
                        {
                            rule.CommandInvoker = cbm;

                            _attributes.Add(rule);

                            Debug.Log("add :" + rule.msg);
                        }
                    }
                }
            }

            inputStrs = new string[_attributes.Count];
        }

        void OnEnable()
        {
            if (!ShowInEditor && Application.isEditor) return;
        }

        void OnDisable()
        {
            // If destroyed because already exists, don't need to de-register callback
            if (destroying) return;
        }

        [Conditional("daddy")]
        void Update()
        {
            if (!ShowInEditor && Application.isEditor) return;

            float InnerHeight = (Screen.height - 2 * Margin) * Height - 2 * padding;
            int TotalRows = (int) (InnerHeight / styleText.lineHeight);

            // Remove overflowing rows
//            while (queue.Count > TotalRows)
//                queue.Dequeue();
        }

        Vector2 scrollPos = Vector2.one;
        private string[] inputStrs;
        string result = "";

        [Conditional("daddy")]
        void OnGUI()
        {
            if (!ShowInEditor && Application.isEditor) return;

            float w = (Screen.width - 2 * Margin) * Width;
            float h = (Screen.height - 2 * Margin) * Height;
            float x = 1, y = 1;

            switch (AnchorPosition)
            {
                case PanelAnchor.BottomLeft:
                    x = Margin;
                    y = Margin + (Screen.height - 2 * Margin) * (1 - Height);
                    break;

                case PanelAnchor.BottomRight:
                    x = Margin + (Screen.width - 2 * Margin) * (1 - Width);
                    y = Margin + (Screen.height - 2 * Margin) * (1 - Height);
                    break;

                case PanelAnchor.TopLeft:
                    x = Margin;
                    y = Margin;
                    break;

                case PanelAnchor.TopRight:
                    x = Margin + (Screen.width - 2 * Margin) * (1 - Width);
                    y = Margin;
                    break;
            }

            float scrollHeight = _attributes.Count * ItemHeight;
            scrollHeight = scrollHeight < h ? h : scrollHeight;

            GUILayout.BeginArea(new Rect(x, y, w, h), styleContainer);

            scrollPos = GUI.BeginScrollView(new Rect(0, 0, w, h / 2), scrollPos, new Rect(0, 0, w, scrollHeight), false,
                false);

            for (var index = 0; index < _attributes.Count; index++)
            {
                GUILayout.BeginHorizontal();
                var rectX = 0;
                var rectY = ItemHeight * index;
                var rectWidth = w / 2f;
                var rectHeight = ItemHeight;
                var rect = new Rect(rectX, rectY, rectWidth, rectHeight);
                inputStrs[index] = GUI.TextField(rect, inputStrs[index] ?? "");
                rect.x = rectWidth;
                var attribute = _attributes[index];
                if (GUI.Button(rect, attribute.msg))
                {
                    result = attribute.CommandInvoker.Invoke(inputStrs[index]);
                }
                GUILayout.EndHorizontal();
            }
            GUI.EndScrollView();


            GUI.TextArea(new Rect(0, h / 2, w, h / 2), result);

            GUILayout.EndArea();
        }

        public void InspectorGUIUpdated()
        {
            InitStyles();
        }

        private void InitStyles()
        {
            Texture2D back = new Texture2D(1, 1);
            BackgroundColor.a = BackgroundOpacity;
            back.SetPixel(0, 0, BackgroundColor);
            back.Apply();

            styleContainer = new GUIStyle();
            styleContainer.normal.background = back;
            styleContainer.wordWrap = false;
            styleContainer.padding = new RectOffset(padding, padding, padding, padding);

            styleText = new GUIStyle();
            styleText.fontSize = FontSize;
        }
    }


    [AttributeUsage(AttributeTargets.Method)]
    public class DaddyCommandAttribute : Attribute
    {
        public string msg;

        public DaddyCommandAttribute(string str)
        {
            msg = str;
        }

        public void LogAssert(bool isPass, Object asset)
        {
            Debug.Assert(isPass, msg + " : " + asset.name, asset);
        }


        public Func<string, string> CommandInvoker;
    }


    public class DaddyCommands
    {
        [DaddyCommand("Load a scene with its name")]
        public static string LoadScene(string sceneName)
        {
            try
            {
                SceneManager.LoadScene(sceneName);
                return Daddy.DONE;
            }
            catch (Exception e)
            {
                return string.Format(Daddy.ERROR, e.Message);
            }
        }
    }
}