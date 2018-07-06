using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading;
using UnityEngine;

namespace UnityToolbag.ConsoleServer
{
    public class ConsoleServer
    {
        private int mPort = 55055;

        public bool mRegisterLogCallback = false;

        #region singleton

        private ConsoleServer()
        {
            mainThread = Thread.CurrentThread;
            fileRoot = Path.Combine(Application.streamingAssetsPath, "/");
        }

        private static ConsoleServer mInstance;

        public static ConsoleServer Instance
        {
            get { return mInstance ?? (mInstance = new ConsoleServer()); }
        }

        #endregion singleton

        private Thread mRunningThread;

        private static Thread mainThread;
        private static string fileRoot;
        private static HttpListener listener;
        private static List<RouteAttribute> registeredRoutes;
        private static Queue<RequestContext> mainRequests = new Queue<RequestContext>();
        internal static Dictionary<string, Action<string>> customActions = new Dictionary<string, Action<string>>();

        // List of supported files
        // FIXME add an api to register new types
        private static Dictionary<string, string> fileTypes = new Dictionary<string, string>
        {
            {"js", "application/javascript"},
            {"json", "application/json"},
            {"jpg", "image/jpeg"},
            {"jpeg", "image/jpeg"},
            {"gif", "image/gif"},
            {"png", "image/png"},
            {"css", "text/css"},
            {"htm", "text/html"},
            {"html", "text/html"},
            {"ico", "image/x-icon"},
        };

        private static Dictionary<string, string> internalRes = new Dictionary<string, string>
        {
            {"/index.html", Res.INDEX_HTML},
            {"/console.css", Res.INDEX_CSS},
            {"/favicon.ico", Res.INDEX_ICO},
        };

        private void RegisterRoutes()
        {
            if (registeredRoutes == null)
            {
                registeredRoutes = new List<RouteAttribute>();

                foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
                {
                    foreach (Type type in assembly.GetTypes())
                    {
                        // FIXME add support for non-static methods (FindObjectByType?)
                        foreach (MethodInfo method in type.GetMethods(BindingFlags.Public | BindingFlags.Static))
                        {
                            RouteAttribute[] attrs =
                                method.GetCustomAttributes(typeof(RouteAttribute), true) as RouteAttribute[];
                            if (attrs.Length == 0)
                                continue;

                            RouteAttribute.Callback cbm =
                                Delegate.CreateDelegate(typeof(RouteAttribute.Callback), method, false) as
                                    RouteAttribute.Callback;
                            if (cbm == null)
                            {
                                Debug.LogError(string.Format(
                                    "Method {0}.{1} takes the wrong arguments for a console route.", type,
                                    method.Name));
                                continue;
                            }

                            // try with a bare action
                            foreach (RouteAttribute route in attrs)
                            {
                                if (route.m_route == null)
                                {
                                    Debug.LogError(string.Format("Method {0}.{1} needs a valid route regexp.", type,
                                        method.Name));
                                    continue;
                                }

                                route.m_callback = cbm;
                                registeredRoutes.Add(route);
                            }
                        }
                    }
                }

                RegisterFileHandlers();
            }
        }

        public delegate void FileHandlerDelegate(RequestContext context, bool download);

        static void WWWFileHandler(RequestContext context, bool download)
        {
            string path, type;
            FindFileType(context, download, out path, out type);

            WWW req = new WWW(path);
            while (!req.isDone)
            {
                Thread.Sleep(0);
            }

            if (string.IsNullOrEmpty(req.error))
            {
                context.Response.ContentType = type;
                if (download)
                    context.Response.AddHeader("Content-disposition",
                        string.Format("attachment; filename={0}", Path.GetFileName(path)));

                context.Response.WriteBytes(req.bytes);
                return;
            }

            if (req.error.StartsWith("Couldn't open file"))
            {
                context.pass = true;
            }
            else
            {
                context.Response.StatusCode = (int) HttpStatusCode.InternalServerError;
                context.Response.StatusDescription = string.Format("Fatal error:\n{0}", req.error);
            }
        }

        static void FileHandler(RequestContext context, bool download)
        {
            string path, type;
            FindFileType(context, download, out path, out type);

            if (File.Exists(path))
            {
                context.Response.WriteFile(path, type, download);
            }
            else if (internalRes.ContainsKey(path))
            {
                context.Response.WriteBytes(Convert.FromBase64String(internalRes[path]));
            }
            else
            {
                context.pass = true;
            }
        }

        static void RegisterFileHandlers()
        {
            string pattern = string.Format("({0})", string.Join("|", fileTypes.Select(x => x.Key).ToArray()));
            RouteAttribute downloadRoute = new RouteAttribute(string.Format(@"^/download/(.*\.{0})$", pattern));
            RouteAttribute fileRoute = new RouteAttribute(string.Format(@"^/(.*\.{0})$", pattern));

            bool needs_www = fileRoot.Contains("://");
            downloadRoute.m_runOnMainThread = needs_www;
            fileRoute.m_runOnMainThread = needs_www;

            FileHandlerDelegate callback = FileHandler;
            if (needs_www)
                callback = WWWFileHandler;

            downloadRoute.m_callback = delegate(RequestContext context) { callback(context, true); };
            fileRoute.m_callback = delegate(RequestContext context) { callback(context, false); };

            registeredRoutes.Add(downloadRoute);
            registeredRoutes.Add(fileRoute);
        }

        static void FindFileType(RequestContext context, bool download, out string path, out string type)
        {
            path = Path.Combine(fileRoot, context.match.Groups[1].Value);

            string ext = Path.GetExtension(path).ToLower().TrimStart(new char[] {'.'});
            if (download || !fileTypes.TryGetValue(ext, out type))
                type = "application/octet-stream";
        }

        void HandleRequest(RequestContext context)
        {
            RegisterRoutes();

            try
            {
                bool handled = false;

                for (; context.currentRoute < registeredRoutes.Count; ++context.currentRoute)
                {
                    RouteAttribute route = registeredRoutes[context.currentRoute];
                    Match match = route.m_route.Match(context.path);
                    if (!match.Success)
                        continue;

                    if (!route.m_methods.IsMatch(context.Request.HttpMethod))
                        continue;

                    // Upgrade to main thread if necessary
                    if (route.m_runOnMainThread && Thread.CurrentThread != mainThread)
                    {
                        Dispatcher.Invoke(() =>
                        {
                            context.match = match;
                            route.m_callback(context);
                        });

                        handled = !context.pass;
                        if (handled)
                            break;
                    }
                    else
                    {
                        context.match = match;
                        route.m_callback(context);
                        handled = !context.pass;
                        if (handled)
                            break;
                    }
                }

                if (!handled)
                {
                    context.Response.StatusCode = (int) HttpStatusCode.NotFound;
                    context.Response.StatusDescription = "Not Found";
                }
            }
            catch (Exception exception)
            {
                context.Response.StatusCode = (int) HttpStatusCode.InternalServerError;
                context.Response.StatusDescription = string.Format("Fatal error:\n{0}", exception);

                Debug.LogException(exception);
            }

            context.Response.OutputStream.Close();
        }

        void HandleRequests()
        {
            while (true)
            {
                while (mainRequests.Count == 0)
                {
                    Thread.Sleep(100);
                }

                RequestContext context = null;
                lock (mainRequests)
                {
                    context = mainRequests.Dequeue();
                }

                HandleRequest(context);

                Thread.Sleep(16);
            }
        }

        void ListenerCallback(IAsyncResult result)
        {
            RequestContext context = new RequestContext(listener.EndGetContext(result));

            HandleRequest(context);

            if (listener.IsListening)
            {
                listener.BeginGetContext(ListenerCallback, null);
            }
        }


        public void Start(int port = 55055, bool isRegisterLogCallback = true)
        {
            mPort = port;
            mRegisterLogCallback = isRegisterLogCallback;
            // Start server
            Debug.Log("Starting Console Server on port : " + mPort);
            listener = new HttpListener();
            listener.Prefixes.Add("http://*:" + mPort + "/");
            listener.Start();
            listener.BeginGetContext(ListenerCallback, null);

            if (mRegisterLogCallback)
            {
                // Capture Console Logs
#if UNITY_5_3_OR_NEWER
                Application.logMessageReceived += Console.LogCallback;
#else
        Application.RegisterLogCallback(Console.LogCallback);
#endif
            }

            if (mRunningThread == null)
            {
                mRunningThread = new Thread(HandleRequests);
            }

            if (!mRunningThread.IsAlive)
                mRunningThread.Start();
        }

        public void Stop()
        {
            if (mRegisterLogCallback)
            {
#if UNITY_5_3_OR_NEWER
                Application.logMessageReceived -= Console.LogCallback;
#else
        Application.RegisterLogCallback(null);
#endif
            }

            if (listener != null)
            {
                listener.Stop();
                listener.Close();
                listener = null;
            }

            if (mRunningThread != null)
            {
                mRunningThread.Abort();
                mRunningThread = null;
            }
        }

        public void AddCustomAction(string key, Action<string> action)
        {
            customActions[key] = action;
        }
    }

    public class RequestContext
    {
        public HttpListenerContext context;
        public Match match;
        public bool pass;
        public string path;
        public int currentRoute;

        public HttpListenerRequest Request
        {
            get { return context.Request; }
        }

        public HttpListenerResponse Response
        {
            get { return context.Response; }
        }

        public RequestContext(HttpListenerContext ctx)
        {
            context = ctx;
            match = null;
            pass = false;
            path = WWW.UnEscapeURL(context.Request.Url.AbsolutePath);
            if (path == "/")
                path = "/index.html";
            currentRoute = 0;
        }
    }

    [AttributeUsage(AttributeTargets.Method)]
    public class CommandAttribute : Attribute
    {
        public delegate void CallbackSimple();

        public delegate void Callback(string[] args);

        public CommandAttribute(string cmd, string help, bool runOnMainThread = true)
        {
            m_command = cmd;
            m_help = help;
            m_runOnMainThread = runOnMainThread;
        }

        public string m_command;
        public string m_help;
        public bool m_runOnMainThread;
        public Callback m_callback;
    }

    [AttributeUsage(AttributeTargets.Method)]
    public class RouteAttribute : Attribute
    {
        public delegate void Callback(RequestContext context);

        public RouteAttribute(string route, string methods = @"(GET|HEAD)", bool runOnMainThread = false)
        {
            m_route = new Regex(route, RegexOptions.IgnoreCase);
            m_methods = new Regex(methods);
            m_runOnMainThread = runOnMainThread;
        }

        public Regex m_route;
        public Regex m_methods;
        public bool m_runOnMainThread;
        public Callback m_callback;
    }


    public static class ResponseExtension
    {
        public static void WriteString(this HttpListenerResponse response, string input, string type = "text/plain")
        {
            response.StatusCode = (int) HttpStatusCode.OK;
            response.StatusDescription = "OK";

            if (!string.IsNullOrEmpty(input))
            {
                byte[] buffer = System.Text.Encoding.UTF8.GetBytes(input);
                response.ContentLength64 = buffer.Length;
                response.ContentType = type;
                response.OutputStream.Write(buffer, 0, buffer.Length);
            }
        }

        public static void WriteBytes(this HttpListenerResponse response, byte[] bytes)
        {
            response.StatusCode = (int) HttpStatusCode.OK;
            response.StatusDescription = "OK";
            response.ContentLength64 = bytes.Length;
            response.OutputStream.Write(bytes, 0, bytes.Length);
        }

        public static void WriteFile(this HttpListenerResponse response, string path,
            string type = "application/octet-stream", bool download = false)
        {
            using (FileStream fs = File.OpenRead(path))
            {
                response.StatusCode = (int) HttpStatusCode.OK;
                response.StatusDescription = "OK";
                response.ContentLength64 = fs.Length;
                response.ContentType = type;
                if (download)
                    response.AddHeader("Content-disposition",
                        string.Format("attachment; filename={0}", Path.GetFileName(path)));

                byte[] buffer = new byte[64 * 1024];
                int read;
                while ((read = fs.Read(buffer, 0, buffer.Length)) > 0)
                {
                    // FIXME required?
                    System.Threading.Thread.Sleep(0);
                    response.OutputStream.Write(buffer, 0, read);
                }
            }
        }
    }


    internal class Res
    {
        public static string INDEX_HTML =
            "PCFET0NUWVBFIGh0bWw+CjxodG1sPgogIDxoZWFkPgogICAgPGxpbmsgcmVsPSJzdHlsZXNoZWV0IiB0eXBlPSJ0ZXh0L2NzcyIgaHJlZj0iY29uc29sZS5jc3MiPgogICAgPGxpbmsgcmVsPSJzaG9ydGN1dCBpY29uIiBocmVmPSJmYXZpY29uLmljbyIgdHlwZT0iaW1hZ2UveC1pbWFnZSI+CiAgICA8bGluayByZWw9Imljb24iIGhyZWY9ImZhdmljb24uaWNvbiIgdHlwZT0iaW1hZ2UveC1pbWFnZSI+CiAgICA8dGl0bGU+Q1VETFI8L3RpdGxlPgoKICAgIDxzY3JpcHQgc3JjPSJodHRwOi8vYWpheC5nb29nbGVhcGlzLmNvbS9hamF4L2xpYnMvanF1ZXJ5LzEuMTAuMi9qcXVlcnkubWluLmpzIj4KICAgIDwvc2NyaXB0PgoKICAgIDxzY3JpcHQ+CiAgICAgIHZhciBjb21tYW5kSW5kZXggPSAtMTsKICAgICAgdmFyIGhhc2ggPSBudWxsOwogICAgICB2YXIgaXNVcGRhdGVQYXVzZWQgPSBmYWxzZTsKCiAgICAgIGZ1bmN0aW9uIHNjcm9sbEJvdHRvbSgpIHsKICAgICAgICAkKCcjb3V0cHV0Jykuc2Nyb2xsVG9wKCQoJyNvdXRwdXQnKVswXS5zY3JvbGxIZWlnaHQpOwogICAgICB9CgogICAgICBmdW5jdGlvbiBydW5Db21tYW5kKGNvbW1hbmQpIHsKICAgICAgICBzY3JvbGxCb3R0b20oKTsKICAgICAgICAkLmdldCgiY29uc29sZS9ydW4/Y29tbWFuZD0iK2VuY29kZVVSSShlbmNvZGVVUklDb21wb25lbnQoY29tbWFuZCkpLCBmdW5jdGlvbiAoZGF0YSwgc3RhdHVzKSB7CiAgICAgICAgICB1cGRhdGVDb25zb2xlKGZ1bmN0aW9uICgpIHsKICAgICAgICAgICAgdXBkYXRlQ29tbWFuZChjb21tYW5kSW5kZXggLSAxKTsKICAgICAgICAgIH0pOwogICAgICAgIH0pOwogICAgICAgIHJlc2V0SW5wdXQoKTsKICAgICAgfQoKICAgICAgZnVuY3Rpb24gdXBkYXRlQ29uc29sZShjYWxsYmFjaykgewogICAgICAgIGlmIChpc1VwZGF0ZVBhdXNlZCkgcmV0dXJuOwogICAgICAgICQuZ2V0KCJjb25zb2xlL291dCIsIGZ1bmN0aW9uIChkYXRhLCBzdGF0dXMpIHsKICAgICAgICAgIC8vIENoZWNrIGlmIHdlIGFyZSBzY3JvbGxlZCB0byB0aGUgYm90dG9tIHRvIGZvcmNlIHNjcm9sbGluZyBvbiB1cGRhdGUKICAgICAgICAgIHZhciBvdXRwdXQgPSAkKCcjb3V0cHV0Jyk7CiAgICAgICAgICBzaG91bGRTY3JvbGwgPSBNYXRoLmFicygob3V0cHV0WzBdLnNjcm9sbEhlaWdodCAtIG91dHB1dC5zY3JvbGxUb3AoKSkgLSBvdXRwdXQuaW5uZXJIZWlnaHQoKSkgPCA1OwogICAgICAgICAgb3V0cHV0Lmh0bWwoU3RyaW5nKGRhdGEpLnJlcGxhY2UoL1xufFxyL2csICc8YnI+JykgKyAiPGJyPjxicj48YnI+Iik7CiAgICAgICAgICAvL2NvbnNvbGUubG9nKHNob3VsZFNjcm9sbCArICIgOj0gIiArIG91dHB1dFswXS5zY3JvbGxIZWlnaHQgKyAiIC0gIiArIG91dHB1dC5zY3JvbGxUb3AoKSArICIgKCIgKyBNYXRoLmFicygob3V0cHV0WzBdLnNjcm9sbEhlaWdodCAtIG91dHB1dC5zY3JvbGxUb3AoKSkgLSBvdXRwdXQuaW5uZXJIZWlnaHQoKSkgKyAiKSA9PSAiICsgb3V0cHV0LmlubmVySGVpZ2h0KCkpOwogICAgICAgICAgLy9jb25zb2xlLmxvZyhTdHJpbmcoZGF0YSkpOwogICAgICAgICAgaWYgKGNhbGxiYWNrKSBjYWxsYmFjaygpOwogICAgICAgICAgaWYgKHNob3VsZFNjcm9sbCkgc2Nyb2xsQm90dG9tKCk7CiAgICAgICAgfSk7CiAgICAgIH0KCiAgICAgIGZ1bmN0aW9uIHJlc2V0SW5wdXQoKSB7CiAgICAgICAgY29tbWFuZEluZGV4ID0gLTE7CiAgICAgICAgJCgiI2lucHV0IikudmFsKCIiKTsKICAgICAgfQoKICAgICAgZnVuY3Rpb24gcHJldmlvdXNDb21tYW5kKCkgewogICAgICAgIHVwZGF0ZUNvbW1hbmQoY29tbWFuZEluZGV4ICsgMSk7CiAgICAgIH0KCiAgICAgIGZ1bmN0aW9uIG5leHRDb21tYW5kKCkgewogICAgICAgIHVwZGF0ZUNvbW1hbmQoY29tbWFuZEluZGV4IC0gMSk7CiAgICAgIH0KCiAgICAgIGZ1bmN0aW9uIHVwZGF0ZUNvbW1hbmQoaW5kZXgpIHsKICAgICAgICAvLyBDaGVjayBpZiB3ZSBhcmUgYXQgdGhlIGRlZnVhbHQgaW5kZXggYW5kIGNsZWFyIHRoZSBpbnB1dAogICAgICAgIGlmIChpbmRleCA8IDApIHsKICAgICAgICAgIHJlc2V0SW5wdXQoKTsKICAgICAgICAgIHJldHVybjsKICAgICAgICB9CgogICAgICAgICQuZ2V0KCJjb25zb2xlL2NvbW1hbmRIaXN0b3J5P2luZGV4PSIraW5kZXgsIGZ1bmN0aW9uIChkYXRhLCBzdGF0dXMpIHsKICAgICAgICAgIGlmIChkYXRhKSB7CiAgICAgICAgICAgIGNvbW1hbmRJbmRleCA9IGluZGV4OwogICAgICAgICAgICAkKCIjaW5wdXQiKS52YWwoU3RyaW5nKGRhdGEpKTsKICAgICAgICAgIH0KICAgICAgICB9KTsKICAgICAgfQoKICAgICAgZnVuY3Rpb24gY29tcGxldGUoY29tbWFuZCkgewogICAgICAgICQuZ2V0KCJjb25zb2xlL2NvbXBsZXRlP2NvbW1hbmQ9Iitjb21tYW5kLCBmdW5jdGlvbiAoZGF0YSwgc3RhdHVzKSB7CiAgICAgICAgICBpZiAoZGF0YSkgewogICAgICAgICAgICAkKCIjaW5wdXQiKS52YWwoU3RyaW5nKGRhdGEpKTsKICAgICAgICAgIH0KICAgICAgICB9KTsKICAgICAgfQoKICAgICAgLy8gUG9sbCB0byB1cGRhdGUgdGhlIGNvbnNvbGUgb3V0cHV0CiAgICAgIHdpbmRvdy5zZXRJbnRlcnZhbChmdW5jdGlvbiAoKSB7IHVwZGF0ZUNvbnNvbGUobnVsbCkgfSwgNTAwKTsKICAgIDwvc2NyaXB0PgogIDwvaGVhZD4KCiAgPGJvZHkgY2xhc3M9ImNvbnNvbGUiPgogICAgPGJ1dHRvbiBpZD0icGF1c2VVcGRhdGVzIj5QYXVzZSBVcGRhdGVzPC9idXR0b24+CiAgICA8ZGl2IGlkPSJvdXRwdXQiIGNsYXNzPSJjb25zb2xlIj48L2Rpdj4KICAgIDx0ZXh0YXJlYSBpZD0iaW5wdXQiIGNsYXNzPSJjb25zb2xlIiBhdXRvZm9jdXMgcm93cz0iMSI+PC90ZXh0YXJlYT4KCiAgICA8c2NyaXB0PgogICAgICAvLyBzZXR1cCBvdXIgcGF1c2UgdXBkYXRlcyBidXR0b24KICAgICAgJCgiI3BhdXNlVXBkYXRlcyIpLmNsaWNrKGZ1bmN0aW9uKCkgewogICAgICAgIC8vY29uc29sZS5sb2coInBhdXNlIHVwZGF0ZXMgIiArIGlzVXBkYXRlUGF1c2VkKTsKICAgICAgICBpc1VwZGF0ZVBhdXNlZCA9ICFpc1VwZGF0ZVBhdXNlZDsKICAgICAgICAkKCIjcGF1c2VVcGRhdGVzIikuaHRtbChpc1VwZGF0ZVBhdXNlZCA/ICJSZXN1bWUgVXBkYXRlcyIgOiAiUGF1c2UgVXBkYXRlcyIpOwogICAgICB9KTsKCiAgICAgICQoIiNpbnB1dCIpLmtleWRvd24oIGZ1bmN0aW9uIChlKSB7CiAgICAgICAgaWYgKGUua2V5Q29kZSA9PSAxMykgeyAvLyBFbnRlcgogICAgICAgICAgLy8gd2UgZG9uJ3Qgd2FudCBhIGxpbmUgYnJlYWsgaW4gdGhlIGNvbnNvbGUKICAgICAgICAgIGUucHJldmVudERlZmF1bHQoKTsKICAgICAgICAgIHJ1bkNvbW1hbmQoJCgiI2lucHV0IikudmFsKCkpOwogICAgICAgIH0gZWxzZSBpZiAoZS5rZXlDb2RlID09IDM4KSB7IC8vIFVwCiAgICAgICAgICBwcmV2aW91c0NvbW1hbmQoKTsKICAgICAgICB9IGVsc2UgaWYgKGUua2V5Q29kZSA9PSA0MCkgeyAvLyBEb3duCiAgICAgICAgICBuZXh0Q29tbWFuZCgpOwogICAgICAgIH0gZWxzZSBpZiAoZS5rZXlDb2RlID09IDI3KSB7IC8vIEVzY2FwZQogICAgICAgICAgcmVzZXRJbnB1dCgpOwogICAgICAgIH0gZWxzZSBpZiAoZS5rZXlDb2RlID09IDkpIHsgLy8gVGFiCiAgICAgICAgICBlLnByZXZlbnREZWZhdWx0KCk7CiAgICAgICAgICBjb21wbGV0ZSgkKCIjaW5wdXQiKS52YWwoKSk7CiAgICAgICAgfQogICAgICB9KTsKICAgIDwvc2NyaXB0PgogIDwvYm9keT4KCjwvaHRtbD4=";

        public static string INDEX_CSS =
            "aHRtbCwgYm9keSB7CgloZWlnaHQ6OTklOwp9Cgp0ZXh0YXJlYSB7CglyZXNpemU6bm9uZTsKfQoKYm9keS5jb25zb2xlIHsKICBiYWNrZ3JvdW5kLWNvbG9yOmJsYWNrOwp9CgpkaXYuY29uc29sZSB7CiAgaGVpZ2h0OjEwMCU7CiAgd2lkdGg6MTAwJTsKICBiYWNrZ3JvdW5kLWNvbG9yOiMzODM4Mzg7CiAgY29sb3I6I0YwRjBGMDsKICBmb250LXNpemU6MTRweDsKICBmb250LWZhbWlseTptb25vc3BhY2U7CiAgb3ZlcmZsb3cteTphdXRvOwogIG92ZXJmbG93LXg6YXV0bzsKICB3aGl0ZS1zcGFjZTpub3JtYWw7CiAgd29yZC13cmFwOmJyZWFrLXdvcmQ7Cn0KCnRleHRhcmVhLmNvbnNvbGUgewogIHdpZHRoOjEwMCU7CiAgYmFja2dyb3VuZC1jb2xvcjojMzgzODM4OwogIGNvbG9yOiNGMEYwRjA7CiAgZm9udC1zaXplOjE0cHg7CiAgZm9udC1mYW1pbHk6bW9ub3NwYWNlOwogIHBvc2l0aW9uOmZpeGVkOwogIGJvdHRvbTowJTsKfQoKc3Bhbi5XYXJuaW5nIHsKCWNvbG9yOiNmNGU1NDI7Cn0KCnNwYW4uQXNzZXJ0IHsKCWNvbG9yOiNmNGU1NDI7Cn0KCnNwYW4uRXJyb3IgewoJY29sb3I6I2ZmMDAwMDsKfQoKc3Bhbi5FeGNlcHRpb24gewoJY29sb3I6I2ZmMDAwMDsKfQoKc3Bhbi5IZWxwIHsKCWNvbG9yOiMxNmYzZmY7Cn0KCmJ1dHRvbiNwYXVzZVVwZGF0ZXMgewogIHdpZHRoOjE1MHB4OwogIGhlaWdodDo0MHB4OwogIHBvc2l0aW9uOmZpeGVkOwogIGZsb2F0OnJpZ2h0OwogIG1hcmdpbi1yaWdodDo1MHB4OwogIG1hcmdpbi10b3A6MTBweDsKICByaWdodDowcHg7CiAgb3BhY2l0eTouNTsKfQ==";

        public static string INDEX_ICO =
            "iVBORw0KGgoAAAANSUhEUgAAABAAAAAQCAYAAAAf8/9hAAABuklEQVQ4jaWTPUhbURiGn5t7vJJrUFuTXNKIVgsVQyQoikjF0g5VQToKQkXcXLq4CRUHwa1bJzsUCi3+tIsOoohCB8GCSBVCoZXrX9WkNUbIj5GbpINybUgTUvrBWc4573Pe7+U70lZzXRpAODUkWVBIpZMGRjAAgAWgvPspslUtSKx6faj1Xip6nwEghFMj7t8msacXBDBcboqcGjH/NsKpISRZkKi9z2XZrYIANk8DhEOU+Jo4X1lCAIS2Nokf/aB58i3HC/M4Hjzk68sJLo6PsgDFdgdl6k27GakFlheJ7elIHY/QHj+h3NdonkW+f2Nn8hXG6S/CKxsUudzAdYgmzWajqm8A/c1rSj1eXF095rrd0vrXljIcnG18Jrqrc2/oOUgS+7PvUSursLe158wkw0H4yyZ3+wext7VzMPMO//gohx+nc4qzAAAWRUGS5byinC38WZ4X49QNjyDU/AOWe3ZTKUinMKIRjGiEi8DJvwH8E2MEV5fzvg4g0kkDi1Jsbhx8mOLnp1Wi+k5eoUVRSMZjCCMYwNHZc+X6MkFofc28ZL3jzhIqFQ5EdQ1WTwPhhTmk//3OvwGiHYnCU40aDAAAAABJRU5ErkJggg==";
    }


    struct QueuedCommand
    {
        public CommandAttribute command;
        public string[] args;
    }


    public class Console
    {
        internal static int CurrentState;

        internal const int STATE_LUA = 1;
        internal const int STATE_NONE = 0;

        // Max number of lines in the console output
        const int MAX_LINES = 100;

        // Maximum number of commands stored in the history
        const int MAX_HISTORY = 50;

        // Prefix for user inputted command
        const string COMMAND_OUTPUT_PREFIX = "> ";

        private static Console instance;
        private CommandTree m_commands;
        private List<string> m_output;
        private List<string> m_history;

        private Console()
        {
            m_commands = new CommandTree();
            m_output = new List<string>();
            m_history = new List<string>();

            RegisterAttributes();
        }

        public static Console Instance
        {
            get
            {
                if (instance == null) instance = new Console();
                return instance;
            }
        }


        /* Execute a command */
        public static void Run(string str)
        {
            if (str.Length > 0)
            {
                LogCommand(str);
                Instance.RecordCommand(str);
                Instance.m_commands.Run(str);
            }
        }

        /* Clear all output from console */
        [Command("clear", "clears console output", false)]
        public static void Clear()
        {
            Instance.m_output.Clear();
        }


        [Command("lp", "list playerprefs", true)]
        public static void ShowPlayerPrefs(string[] args)
        {
            if (args.Length >= 1)
            {
                if (PlayerPrefs.HasKey(args[0]))
                {
                    Log("    " + args[0] + " = " + PlayerPrefs.GetString(args[0]));
                }
                else
                    Log("    has not a key: " + args[0]);
            }
            else
            {
                Log("    need a key for inspecting");
            }
        }

        [Command("as", "add string to playerprefs", true)]
        public static void AddString(string[] args)
        {
            if (args.Length >= 2)
            {
                PlayerPrefs.SetString(args[0], args[1]);
                Log("    added " + args[0] + " = " + args[1]);
            }
            else
            {
                Log("    need a key & value for inspecting");
            }
        }


        [Command("cp", "clear all playerprefs", true)]
        public static void DeleteAll(string[] args)
        {
            PlayerPrefs.DeleteAll();
            Log("    Deleted All");
        }

        /* Print a list of all console commands */
        [Command("help", "prints commands", false)]
        public static void Help()
        {
            string help = "Commands:";
            foreach (CommandAttribute cmd in Instance.m_commands.OrderBy(m => m.m_command))
            {
                help += string.Format("\n{0} : {1}", cmd.m_command, cmd.m_help);
            }

            Log("<span class='Help'>" + help + "</span>");
        }

        [Command("lua", "enter lua state")]
        public static void EnterLua(string[] args)
        {
            CurrentState = STATE_LUA;
            var key = "EnterLua";
            if (!ConsoleServer.customActions.ContainsKey(key))
            {
                Log("Lua callback not registered");
                return;
            }

            Action<string> action = ConsoleServer.customActions[key];


            if (args.Length > 0)
            {
                if (args[0].ToLower() == "exit")
                {
                    CurrentState = STATE_NONE;
                    Log(">>>>> exit lua <<<<<");
                }
                else
                {
                    Log("DOString: " + args[0]);
                    try
                    {
                        action(args[0]);
                    }
                    catch (Exception e)
                    {
                        Log(e.Message + "\n" + e.StackTrace);
                    }
                }
            }
            else
            {
                Log(">>>>> lua <<<<<");
            }
        }


        /* Find command based on partial string */
        public static string Complete(string partialCommand)
        {
            return Instance.m_commands.Complete(partialCommand);
        }

        /* Logs user input to output */
        public static void LogCommand(string cmd)
        {
            Log(COMMAND_OUTPUT_PREFIX + cmd);
        }

        /* Logs string to output */
        public static void Log(string str)
        {
            Instance.m_output.Add(str);
            if (Instance.m_output.Count > MAX_LINES)
                Instance.m_output.RemoveAt(0);
        }

        /* Callback for Unity logging */
        public static void LogCallback(string logString, string stackTrace, LogType type)
        {
            if (type != LogType.Log)
            {
                Console.Log("<span class='" + type + "'>" + logString);
                Console.Log(stackTrace + "</span>");
            }
            else
            {
                Console.Log(logString);
            }
        }

        /* Returns the output */
        public static string Output()
        {
            return string.Join("\n", Instance.m_output.ToArray());
        }

        /* Register a new console command */
        public static void RegisterCommand(string command, string desc, CommandAttribute.Callback callback,
            bool runOnMainThread = true)
        {
            if (command == null || command.Length == 0)
            {
                throw new Exception("Command String cannot be empty");
            }

            CommandAttribute cmd = new CommandAttribute(command, desc, runOnMainThread);
            cmd.m_callback = callback;

            Instance.m_commands.Add(cmd);
        }

        private void RegisterAttributes()
        {
            foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                // HACK: IL2CPP crashes if you attempt to get the methods of some classes in these assemblies.
                if (assembly.FullName.StartsWith("System") || assembly.FullName.StartsWith("mscorlib"))
                {
                    continue;
                }

                foreach (Type type in assembly.GetTypes())
                {
                    // FIXME add support for non-static methods (FindObjectByType?)
                    foreach (MethodInfo method in type.GetMethods(BindingFlags.Public | BindingFlags.Static))
                    {
                        CommandAttribute[] attrs =
                            method.GetCustomAttributes(typeof(CommandAttribute), true) as CommandAttribute[];
                        if (attrs.Length == 0)
                            continue;

                        CommandAttribute.Callback cb =
                            Delegate.CreateDelegate(typeof(CommandAttribute.Callback), method, false) as
                                CommandAttribute.Callback;
                        if (cb == null)
                        {
                            CommandAttribute.CallbackSimple cbs =
                                Delegate.CreateDelegate(typeof(CommandAttribute.CallbackSimple), method, false) as
                                    CommandAttribute.CallbackSimple;
                            if (cbs != null)
                            {
                                cb = delegate(string[] args) { cbs(); };
                            }
                        }

                        if (cb == null)
                        {
                            Debug.LogError(string.Format(
                                "Method {0}.{1} takes the wrong arguments for a console command.", type, method.Name));
                            continue;
                        }

                        // try with a bare action
                        foreach (CommandAttribute cmd in attrs)
                        {
                            if (string.IsNullOrEmpty(cmd.m_command))
                            {
                                Debug.LogError(string.Format("Method {0}.{1} needs a valid command name.", type,
                                    method.Name));
                                continue;
                            }

                            cmd.m_callback = cb;
                            m_commands.Add(cmd);
                        }
                    }
                }
            }
        }

        /* Get a previously ran command from the history */
        public static string PreviousCommand(int index)
        {
            return index >= 0 && index < Instance.m_history.Count ? Instance.m_history[index] : null;
        }

        /* Update history with a new command */
        private void RecordCommand(string command)
        {
            m_history.Insert(0, command);
            if (m_history.Count > MAX_HISTORY)
                m_history.RemoveAt(m_history.Count - 1);
        }

        // Our routes
        [Route("^/console/out$")]
        public static void Output(RequestContext context)
        {
            context.Response.WriteString(Console.Output());
        }

        [Route("^/console/run$")]
        public static void Run(RequestContext context)
        {
            string command = Uri.UnescapeDataString(context.Request.QueryString.Get("command"));
            if (!string.IsNullOrEmpty(command))
                Console.Run(command);

            context.Response.StatusCode = (int) HttpStatusCode.OK;
            context.Response.StatusDescription = "OK";
        }

        [Route("^/console/commandHistory$")]
        public static void History(RequestContext context)
        {
            string index = context.Request.QueryString.Get("index");

            string previous = null;
            if (!string.IsNullOrEmpty(index))
                previous = Console.PreviousCommand(System.Int32.Parse(index));

            context.Response.WriteString(previous);
        }

        [Route("^/console/complete$")]
        public static void Complete(RequestContext context)
        {
            string partialCommand = context.Request.QueryString.Get("command");

            string found = null;
            if (partialCommand != null)
                found = Console.Complete(partialCommand);

            context.Response.WriteString(found);
        }
    }

    class CommandTree : IEnumerable<CommandAttribute>
    {
        private Dictionary<string, CommandTree> m_subcommands;
        private CommandAttribute m_command;

        public CommandTree()
        {
            m_subcommands = new Dictionary<string, CommandTree>();
        }

        public void Add(CommandAttribute cmd)
        {
            _add(cmd.m_command.ToLower().Split(' '), 0, cmd);
        }

        private void _add(string[] commands, int command_index, CommandAttribute cmd)
        {
            if (commands.Length == command_index)
            {
                m_command = cmd;
                return;
            }

            string token = commands[command_index];
            if (!m_subcommands.ContainsKey(token))
            {
                m_subcommands[token] = new CommandTree();
            }

            m_subcommands[token]._add(commands, command_index + 1, cmd);
        }

        public IEnumerator<CommandAttribute> GetEnumerator()
        {
            if (m_command != null && m_command.m_command != null)
                yield return m_command;

            foreach (KeyValuePair<string, CommandTree> entry in m_subcommands)
            {
                foreach (CommandAttribute cmd in entry.Value)
                {
                    if (cmd != null && cmd.m_command != null)
                        yield return cmd;
                }
            }
        }

        IEnumerator IEnumerable.GetEnumerator()
        {
            return GetEnumerator();
        }

        public string Complete(string partialCommand)
        {
            return _complete(partialCommand.Split(' '), 0, "");
        }

        public string _complete(string[] partialCommand, int index, string result)
        {
            if (partialCommand.Length == index && m_command != null)
            {
                // this is a valid command... so we do nothing
                return result;
            }
            else if (partialCommand.Length == index)
            {
                // This is valid but incomplete.. print all of the subcommands
                Console.LogCommand(result);
                foreach (string key in m_subcommands.Keys.OrderBy(m => m))
                {
                    Console.Log(result + " " + key);
                }

                return result + " ";
            }
            else if (partialCommand.Length == (index + 1))
            {
                string partial = partialCommand[index];
                if (m_subcommands.ContainsKey(partial))
                {
                    result += partial;
                    return m_subcommands[partial]._complete(partialCommand, index + 1, result);
                }

                // Find any subcommands that match our partial command
                List<string> matches = new List<string>();
                foreach (string key in m_subcommands.Keys.OrderBy(m => m))
                {
                    if (key.StartsWith(partial))
                    {
                        matches.Add(key);
                    }
                }

                if (matches.Count == 1)
                {
                    // Only one command found, log nothing and return the complete command for the user input
                    return result + matches[0] + " ";
                }
                else if (matches.Count > 1)
                {
                    // list all the options for the user and return partial
                    Console.LogCommand(result + partial);
                    foreach (string match in matches)
                    {
                        Console.Log(result + match);
                    }
                }

                return result + partial;
            }

            string token = partialCommand[index];
            if (!m_subcommands.ContainsKey(token))
            {
                return result;
            }

            result += token + " ";
            return m_subcommands[token]._complete(partialCommand, index + 1, result);
        }

        public void Run(string commandStr)
        {
            if (Console.CurrentState == Console.STATE_LUA)
            {
                _run(new string[] {"lua", commandStr.Replace("lua ", "")}, 0);
            }
            else
            {
                // Split user input on spaces ignoring anything in qoutes
                Regex regex = new Regex(@""".*?""|[^\s]+");
                MatchCollection matches = regex.Matches(commandStr);
                string[] tokens = new string[matches.Count];
                for (int i = 0; i < tokens.Length; ++i)
                {
                    tokens[i] = matches[i].Value.Replace("\"", "");
                    Debug.Log(tokens[i]);
                }

                _run(tokens, 0);
            }
        }

        static string[] emptyArgs = new string[0] { };

        private void _run(string[] commands, int index)
        {
            if (commands.Length == index)
            {
                RunCommand(emptyArgs);
                return;
            }

            string token = commands[index].ToLower();
            if (!m_subcommands.ContainsKey(token))
            {
                RunCommand(commands.Skip(index).ToArray());
                return;
            }

            m_subcommands[token]._run(commands, index + 1);
        }

        private void RunCommand(string[] args)
        {
            if (m_command == null)
            {
                Console.Log("command not found");
            }
            else
            {
                if (m_command.m_runOnMainThread)
                {
                    Dispatcher.Invoke(() => { m_command.m_callback(args); });
                }
                else
                    m_command.m_callback(args);
            }
        }
    }
}