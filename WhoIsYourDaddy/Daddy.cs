#define daddy

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Text;
using AClockworkBerry;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.SceneManagement;
using Debug = UnityEngine.Debug;
using Object = UnityEngine.Object;

namespace UnityToolbag.WhoIsYourDaddy
{
    public class Daddy : MonoBehaviour
    {
        public const string DONE = ">> DONE";
        public const string ERROR = ">> ERROR : {0}";
        public const string MSG = ">> {0}";


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

        public int FontSize = 25;

        [Range(0f, 01f)] public float BackgroundOpacity = 0.5f;
        public Color BackgroundColor = Color.black;

        GUIStyle styleContainer, styleButton, styleText;
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

#if daddy
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
                            rule.methodName = method.Name;
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

            Application.logMessageReceived += HandleLog;
#if UNITY_EDITOR
            isOpen = true;
#endif
        }

        void OnDisable()
        {
            Application.logMessageReceived -= HandleLog;
            // If destroyed because already exists, don't need to de-register callback
            if (destroying) return;
        }

        void HandleLog(string condition, string stackTrace, LogType type)
        {
            switch (type)
            {
                case LogType.Error:
                case LogType.Assert:
                    PrintResult(string.Format(Daddy.ERROR, condition + "\n" + stackTrace));
                    break;
                case LogType.Warning:
//                   PrintResult(string.Format(Daddy.ERROR,condition +"\n"+stackTrace));
                    break;
                case LogType.Log:
//                    PrintResult(string.Format(Daddy.MSG,condition +"\n"+stackTrace));
                    break;
                case LogType.Exception:
                    PrintResult(string.Format(Daddy.ERROR, condition + "\n" + stackTrace));
                    break;
                default:
                    throw new ArgumentOutOfRangeException("type", type, null);
            }
        }

        void PrintResult(string res)
        {
            if (result.Length >= 500)
            {
                result = string.Empty;
            }

            result += "\n" + res + "\n";
        }

        void Update()
        {
            if (!ShowInEditor && Application.isEditor) return;

            float InnerHeight = (Screen.height - 2 * Margin) * Height - 2 * padding;
            int TotalRows = (int) (InnerHeight / styleText.lineHeight);

            // Remove overflowing rows
//            while (queue.Count > TotalRows)
//                queue.Dequeue();

            CheckVibrate();
        }

        void OnGUI()
        {
            if (!ShowInEditor && Application.isEditor || !isOpen) return;

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
            styleText.fontSize = ItemHeight / 2;
            float scrollHeight = _attributes.Count * ItemHeight * 3;
            scrollHeight = scrollHeight < h ? h : scrollHeight;

            GUILayout.BeginArea(new Rect(x, y, w, h), styleContainer);
            Debug.Log("----> " + Event.current.type);
            scrollPos = GUI.BeginScrollView(new Rect(0, 0, w, h / 2), scrollPos,
                new Rect(0, 0, w - 6 * padding, scrollHeight), false,
                false);
            GUILayout.BeginArea(new Rect(0, 0, w, scrollHeight), styleContainer);
            for (var index = 0; index < _attributes.Count; index++)
            {
                var attribute = _attributes[index];


                var rectX = padding;
                var rectY = (ItemHeight * 2 + padding * 6) * index + padding;
                var rectWidth = w - 6 * padding;
                var rectHeight = ItemHeight;
                var rect = new Rect(rectX, rectY, rectWidth, rectHeight);
                GUI.Label(rect, attribute.msg, styleContainer);

                GUILayout.BeginHorizontal();
                rect.width = w / 2f - 4 * padding;
                rect.y += ItemHeight + padding;
                inputStrs[index] = GUI.TextField(rect, inputStrs[index] ?? "", styleContainer);

                rect.x = rect.width + 3 * padding;
                if (GUI.Button(rect, "RUN", styleButton))
                {
                    PrintResult(attribute.CommandInvoker.Invoke(inputStrs[index]));
                }
                GUILayout.EndHorizontal();
            }
            GUILayout.EndArea();
            GUI.EndScrollView();


            GUI.TextArea(new Rect(0, h / 2, w, h / 2), result);

            GUILayout.EndArea();
        }
#endif
        Vector2 scrollPos = Vector2.one;
        private string[] inputStrs;
        string result = "";
        private bool m_isOpen = false;
        private Action<string> OnUnityLog;

        private bool isOpen
        {
            get { return m_isOpen; }
            set
            {
                m_isOpen = value;
//                EventSystem.current.enabled = !m_isOpen;
            }
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

            styleContainer = new GUIStyle
            {
                alignment = TextAnchor.MiddleCenter,
                normal =
                {
                    textColor = Color.green,
                    background = back
                },
                wordWrap = true,
                padding = new RectOffset(padding, padding, padding, padding)
            };


            styleButton = new GUIStyle
            {
                alignment = TextAnchor.MiddleCenter,
                normal =
                {
                    textColor = Color.yellow,
                    background = back
                },
                wordWrap = true,
                padding = new RectOffset(padding, padding, padding, padding)
            };

            styleText = new GUIStyle
            {
                alignment = TextAnchor.MiddleCenter,
                fontSize = FontSize,
                normal =
                {
                    background = back,
                    textColor = Color.white
                },
                wordWrap = true,
                padding = new RectOffset(ItemHeight / 3, ItemHeight / 3, ItemHeight / 3, ItemHeight / 3)
            };
        }

        protected float m_checkValue = 0.8f;

        private Vector3 m_detalAcceleration;
        private Vector3 m_oldAcceleration;
        private Vector3 m_newAcceleration;
        private const float m_interval = 1f;
        private float m_delta = 0f;

        private void CheckVibrate()
        {
            m_delta += Time.deltaTime;
            if (m_delta < m_interval)
            {
                return;
            }

            m_newAcceleration = Input.acceleration;
            m_detalAcceleration = m_newAcceleration - m_oldAcceleration;
            m_oldAcceleration = m_newAcceleration;

            if (m_detalAcceleration.x > m_checkValue ||
                m_detalAcceleration.y > m_checkValue ||
                m_detalAcceleration.z > m_checkValue)
            {
                isOpen = !isOpen;
                m_delta = 0;
#if UNITY_ANDROID

                /// 手机震动  
                Handheld.Vibrate();

                /////同样是震动，但是这个接口已经过时的，不要用了  
                //iPhoneUtils.Vibrate();  
#elif UNIYT_IPHONE
/// 手机震动，是不是这个接口,没测试过  
            Handheld.Vibrate();  
#endif
            }
        }
    }


    [AttributeUsage(AttributeTargets.Method)]
    public class DaddyCommandAttribute : Attribute
    {
        public string methodName;
        public string msg;

        public DaddyCommandAttribute(string str)
        {
            msg = str;
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

        [DaddyCommand("Show all audioclip in scene")]
        public static string ShowPlayingSound(string para)
        {
            try
            {
                var audiosources = GameObject.FindObjectsOfType<AudioSource>();
                var sb = new StringBuilder();
                sb.Append(string.Format(Daddy.MSG, "Audios:\n"));
                foreach (var audiosource in audiosources)
                {
                    if (audiosource.clip != null)
                    {
                        var msg = string.Format(Daddy.MSG,
                            "[" + audiosource.clip.name + "] is " + (audiosource.isPlaying ? "" : " NOT ") +
                            " playing\n");
                        sb.Append(msg);
                    }
                }

                return sb.ToString();
            }
            catch (Exception e)
            {
                return string.Format(Daddy.ERROR, e.Message);
            }
        }

        [DaddyCommand("Check whether a gameobject exists")]
        public static string CheckGameObjectExist(string path)
        {
            try
            {
                //TODO:
                return Daddy.DONE;
            }
            catch (Exception e)
            {
                return string.Format(Daddy.ERROR, e.Message);
            }
        }

        [DaddyCommand("Write key/STRING value to playerprefs, use ',' to split")]
        public static string WriteKeyStringValue(string vals)
        {
            try
            {
                var strs = vals.Split(',');
                var oldStr = PlayerPrefs.GetString(strs[0]);
                PlayerPrefs.SetString(strs[0], strs[1]);
                return string.Format(Daddy.MSG, strs[0] + " : " + oldStr + " --> " + strs[1]);
            }
            catch (Exception e)
            {
                return string.Format(Daddy.ERROR, e.Message);
            }
        }
    }
}