using M2C.Checkout;
using UnityEditor;
using UnityEngine;

namespace M2C.Checkout.Editor
{
    internal static class M2CCheckoutSettingsEditor
    {
        private const string ResourcesFolder = "Assets/Resources";

        /// <summary>Finds the settings asset anywhere in the project, or null.</summary>
        public static M2CCheckoutSettings FindAsset()
        {
            string path = FindAssetPath();
            if (string.IsNullOrEmpty(path)) return null;
            return AssetDatabase.LoadAssetAtPath<M2CCheckoutSettings>(path);
        }

        public static string FindAssetPath()
        {
            string[] guids = AssetDatabase.FindAssets("t:M2CCheckoutSettings");
            if (guids == null || guids.Length == 0) return null;

            string firstPath = null;
            foreach (string guid in guids)
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                if (string.IsNullOrEmpty(firstPath)) firstPath = path;
                if (IsRuntimeLoadablePath(path)) return path;
            }
            return firstPath;
        }

        [MenuItem("Assets/M2C/Find or Create Checkout Settings")]
        public static void OpenAsset()
        {
            var asset = FindOrCreateAsset();
            if (asset == null) return;
            Selection.activeObject = asset;
            EditorGUIUtility.PingObject(asset);
        }

        private static M2CCheckoutSettings FindOrCreateAsset()
        {
            string existingPath = FindAssetPath();
            if (!string.IsNullOrEmpty(existingPath))
            {
                var existing = AssetDatabase.LoadAssetAtPath<M2CCheckoutSettings>(existingPath);
                if (!IsRuntimeLoadablePath(existingPath)
                    && AssetDatabase.LoadAssetAtPath<M2CCheckoutSettings>(M2CCheckoutSettings.DefaultAssetPath) == null
                    && EditorUtility.DisplayDialog(
                        "M2C Checkout",
                        "Move the existing settings asset to Assets/Resources so runtime code can load it automatically?",
                        "Move",
                        "Keep"))
                {
                    EnsureResourcesFolder();
                    string error = AssetDatabase.MoveAsset(existingPath, M2CCheckoutSettings.DefaultAssetPath);
                    if (string.IsNullOrEmpty(error))
                    {
                        AssetDatabase.SaveAssets();
                        existing = AssetDatabase.LoadAssetAtPath<M2CCheckoutSettings>(M2CCheckoutSettings.DefaultAssetPath);
                    }
                    else
                    {
                        Debug.LogWarning("[M2C] Could not move checkout settings asset: " + error);
                    }
                }

                return existing;
            }

            EnsureResourcesFolder();
            var asset = ScriptableObject.CreateInstance<M2CCheckoutSettings>();
            AssetDatabase.CreateAsset(asset, M2CCheckoutSettings.DefaultAssetPath);
            AssetDatabase.SaveAssets();
            return asset;
        }

        public static bool MoveAssetToDefaultPath(M2CCheckoutSettings asset)
        {
            if (asset == null) return false;
            string currentPath = AssetDatabase.GetAssetPath(asset);
            if (IsRuntimeLoadablePath(currentPath)) return true;

            var existing = AssetDatabase.LoadAssetAtPath<M2CCheckoutSettings>(M2CCheckoutSettings.DefaultAssetPath);
            if (existing != null && existing != asset)
            {
                Debug.LogWarning("[M2C] Could not move checkout settings asset because " +
                                 M2CCheckoutSettings.DefaultAssetPath + " already exists.");
                return false;
            }

            EnsureResourcesFolder();
            string error = AssetDatabase.MoveAsset(currentPath, M2CCheckoutSettings.DefaultAssetPath);
            if (!string.IsNullOrEmpty(error))
            {
                Debug.LogWarning("[M2C] Could not move checkout settings asset: " + error);
                return false;
            }

            AssetDatabase.SaveAssets();
            Selection.activeObject = AssetDatabase.LoadAssetAtPath<M2CCheckoutSettings>(M2CCheckoutSettings.DefaultAssetPath);
            return true;
        }

        private static void EnsureResourcesFolder()
        {
            if (!AssetDatabase.IsValidFolder(ResourcesFolder))
                AssetDatabase.CreateFolder("Assets", "Resources");
        }

        public static bool IsRuntimeLoadablePath(string path)
        {
            string normalized = (path ?? string.Empty).Replace('\\', '/');
            return normalized.EndsWith("/Resources/" + M2CCheckoutSettings.ResourceName + ".asset");
        }
    }

    [CustomEditor(typeof(M2CCheckoutSettings))]
    internal sealed class M2CCheckoutSettingsInspector : UnityEditor.Editor
    {
        private const string RequestIdToken = "{request_id}";
        private SerializedProperty _publishableKey;
        private SerializedProperty _returnUrl;
        private SerializedProperty _cancelUrl;
        private SerializedProperty _statusUrlTemplate;
        private SerializedProperty _browserMode;
        private SerializedProperty _statusPollTimeoutSeconds;
        private SerializedProperty _deepLinkScheme;
        private SerializedProperty _useAssociatedDomains;
        private SerializedProperty _associatedDomain;

        private static Texture2D _logoLight;
        private static Texture2D _logoDark;
        private static bool _advancedExpanded;
        private static GUIStyle _titleStyle;
        private static GUIStyle _subtitleStyle;
        private static GUIStyle _sectionStyle;

        private void OnEnable()
        {
            _publishableKey = serializedObject.FindProperty("PublishableKey");
            _returnUrl = serializedObject.FindProperty("ReturnUrl");
            _cancelUrl = serializedObject.FindProperty("CancelUrl");
            _statusUrlTemplate = serializedObject.FindProperty("StatusUrlTemplate");
            _browserMode = serializedObject.FindProperty("BrowserMode");
            _statusPollTimeoutSeconds = serializedObject.FindProperty("StatusPollTimeoutSeconds");
            _deepLinkScheme = serializedObject.FindProperty("DeepLinkScheme");
            _useAssociatedDomains = serializedObject.FindProperty("UseAssociatedDomains");
            _associatedDomain = serializedObject.FindProperty("AssociatedDomain");
        }

        public override void OnInspectorGUI()
        {
            serializedObject.Update();
            EnsureStyles();

            DrawBrandingHeader();
            DrawRuntimeLocation();
            DrawApiSection();
            DrawStatusSection();
            DrawReturnSection();
            DrawNativeSection();
            DrawAdvancedSection();

            serializedObject.ApplyModifiedProperties();
        }

        private void DrawBrandingHeader()
        {
            EditorGUILayout.BeginHorizontal(EditorStyles.helpBox);
            Texture2D logo = LoadLogo();
            if (logo != null)
                GUILayout.Label(logo, GUILayout.Width(56), GUILayout.Height(56));

            EditorGUILayout.BeginVertical();
            EditorGUILayout.LabelField("M2C Checkout", _titleStyle);
            EditorGUILayout.LabelField("Unity SDK " + PackageVersion(), _subtitleStyle);
            EditorGUILayout.LabelField("Project settings", _subtitleStyle);
            EditorGUILayout.EndVertical();
            EditorGUILayout.EndHorizontal();
        }

        private void DrawRuntimeLocation()
        {
            string path = AssetDatabase.GetAssetPath(target);
            if (M2CCheckoutSettingsEditor.IsRuntimeLoadablePath(path))
                return;

            EditorGUILayout.Space(4);
            EditorGUILayout.HelpBox(
                "Move this asset to Assets/Resources/M2CCheckoutSettings.asset so runtime code can load it and package updates do not overwrite it.",
                MessageType.Warning);

            bool defaultExists = AssetDatabase.LoadAssetAtPath<M2CCheckoutSettings>(M2CCheckoutSettings.DefaultAssetPath) != null;
            EditorGUI.BeginDisabledGroup(defaultExists);
            if (GUILayout.Button("Move to runtime settings path"))
                M2CCheckoutSettingsEditor.MoveAssetToDefaultPath((M2CCheckoutSettings)target);
            EditorGUI.EndDisabledGroup();

            if (defaultExists)
                EditorGUILayout.HelpBox("The runtime settings path already has an M2CCheckoutSettings asset.", MessageType.Info);
        }

        private void DrawApiSection()
        {
            DrawSection("API", "These values are used when code calls M2CConfig.FromProjectSettings() or new M2CCheckoutClient().");
            DrawProperty(_publishableKey, "Publishable Key", "Client-safe key for client-initiated checkout and M2C status polling. Never put a secret key in Unity.");
        }

        private void DrawStatusSection()
        {
            DrawSection("Status", "Leave the status URL blank to poll M2C with the publishable key. Set it to your backend endpoint when you want the client to read webhook-fed fulfillment state.");
            DrawProperty(_statusUrlTemplate, "Status URL", "Optional URL template. Include {request_id}; the SDK replaces it with the checkout request id.");

            string statusUrl = Trim(_statusUrlTemplate);
            if (!string.IsNullOrEmpty(statusUrl) && !statusUrl.Contains(RequestIdToken))
            {
                EditorGUILayout.HelpBox("Status URL must contain " + RequestIdToken + ".", MessageType.Error);
            }
            else if (string.IsNullOrEmpty(statusUrl) && string.IsNullOrEmpty(Trim(_publishableKey)))
            {
                EditorGUILayout.HelpBox("Blank status URL uses M2C status polling, which requires a publishable key at runtime. Backend-only flows should set a status URL or create a callback in code.", MessageType.Warning);
            }
        }

        private void DrawAdvancedSection()
        {
            EditorGUILayout.Space(10);
            _advancedExpanded = EditorGUILayout.Foldout(_advancedExpanded, "Advanced Settings", true, _sectionStyle);
            if (!_advancedExpanded) return;

            EditorGUILayout.HelpBox("Less common checkout defaults. Per-request auction metadata should stay on AuctionRequest.", MessageType.None);
            DrawProperty(_browserMode, "Browser Mode", "Choose whether checkout prefers the in-app browser or always opens the external system browser.");
            DrawProperty(_statusPollTimeoutSeconds, "Status Poll Timeout", "Total seconds to poll conversion status before resolving PendingTimeout.");

            if (_statusPollTimeoutSeconds.floatValue <= 0f
                || float.IsNaN(_statusPollTimeoutSeconds.floatValue)
                || float.IsInfinity(_statusPollTimeoutSeconds.floatValue))
            {
                EditorGUILayout.HelpBox("Status Poll Timeout must be greater than 0 seconds.", MessageType.Error);
            }
        }

        private void DrawReturnSection()
        {
            DrawSection("Return URLs", "These defaults are sent with client-initiated checkout and used by native browser return handling.");
            DrawProperty(_returnUrl, "Success URL", "Explicit success return URL. Leave blank to derive it from the deep link scheme.");
            DrawProperty(_cancelUrl, "Cancel URL", "Explicit cancel return URL. Leave blank to derive it from the deep link scheme.");

            EditorGUILayout.HelpBox("Runtime URLs show what the SDK will actually send: the explicit URL above, or the Deep Link Scheme-derived URL when the field is blank.", MessageType.None);
            DrawReadOnlyText("Runtime Success URL", EffectiveUrl(_returnUrl, "checkout/return"));
            DrawReadOnlyText("Runtime Cancel URL", EffectiveUrl(_cancelUrl, "checkout/cancel"));
        }

        private void DrawNativeSection()
        {
            DrawSection("Native Return Setup", "Build post-processors use these values to register iOS URL schemes, Android intent filters, and optional Universal/App Links.");
            DrawProperty(_deepLinkScheme, "Deep Link Scheme", "Scheme only, without ://. Example: mygame for mygame://checkout/return.");
            DrawProperty(_useAssociatedDomains, "Use Associated Domains", "Also configure HTTPS Universal Links on iOS and App Links on Android.");

            EditorGUI.BeginDisabledGroup(!_useAssociatedDomains.boolValue);
            DrawProperty(_associatedDomain, "Associated Domain", "Host only. Example: links.mygame.com.");
            EditorGUI.EndDisabledGroup();

            if (string.IsNullOrEmpty(Trim(_deepLinkScheme))
                && (!_useAssociatedDomains.boolValue || string.IsNullOrEmpty(Trim(_associatedDomain))))
            {
                EditorGUILayout.HelpBox("Set a deep link scheme or an associated domain so checkout can return to the app on device.", MessageType.Warning);
            }
        }

        private static void DrawSection(string title, string description)
        {
            EditorGUILayout.Space(10);
            EditorGUILayout.LabelField(title, _sectionStyle);
            if (!string.IsNullOrEmpty(description))
                EditorGUILayout.HelpBox(description, MessageType.None);
        }

        private static void DrawProperty(SerializedProperty property, string label, string tooltip, bool includeChildren = false)
        {
            EditorGUILayout.PropertyField(property, new GUIContent(label, tooltip), includeChildren);
        }

        private static void DrawReadOnlyText(string label, string value)
        {
            EditorGUI.BeginDisabledGroup(true);
            EditorGUILayout.TextField(label, value);
            EditorGUI.EndDisabledGroup();
        }

        private string EffectiveUrl(SerializedProperty explicitUrl, string path)
        {
            string value = Trim(explicitUrl);
            if (!string.IsNullOrEmpty(value)) return value;

            string scheme = Trim(_deepLinkScheme);
            return string.IsNullOrEmpty(scheme) ? string.Empty : scheme + "://" + path;
        }

        private static string Trim(SerializedProperty property)
        {
            return (property.stringValue ?? string.Empty).Trim();
        }

        private static void EnsureStyles()
        {
            if (_titleStyle != null) return;

            _titleStyle = new GUIStyle(EditorStyles.boldLabel)
            {
                fontSize = 17,
                alignment = TextAnchor.LowerLeft,
            };
            _subtitleStyle = new GUIStyle(EditorStyles.miniLabel)
            {
                alignment = TextAnchor.UpperLeft,
            };
            _sectionStyle = new GUIStyle(EditorStyles.boldLabel)
            {
                fontSize = 12,
            };
        }

        private static Texture2D LoadLogo()
        {
            if (EditorGUIUtility.isProSkin)
            {
                if (_logoLight == null)
                    _logoLight = AssetDatabase.LoadAssetAtPath<Texture2D>("Packages/com.m2c.checkout/Editor/m2c_logo_lt_96.png");
                return _logoLight;
            }

            if (_logoDark == null)
                _logoDark = AssetDatabase.LoadAssetAtPath<Texture2D>("Packages/com.m2c.checkout/Editor/m2c_logo_dk_96.png");
            return _logoDark;
        }

        private static string PackageVersion()
        {
            var info = UnityEditor.PackageManager.PackageInfo.FindForAssembly(typeof(M2CCheckoutSettings).Assembly);
            return info != null && !string.IsNullOrEmpty(info.version) ? info.version : "0.1.0";
        }
    }
}
