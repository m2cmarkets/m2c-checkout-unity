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
        private SerializedProperty _webGLPublishableKey;
        private SerializedProperty _iosPublishableKey;
        private SerializedProperty _androidPublishableKey;
        private SerializedProperty _returnUrl;
        private SerializedProperty _cancelUrl;
        private SerializedProperty _webGLReturnUrl;
        private SerializedProperty _webGLCancelUrl;
        private SerializedProperty _webGLLaunchMode;
        private SerializedProperty _statusUrlTemplate;
        private SerializedProperty _browserMode;
        private SerializedProperty _statusPollTimeoutSeconds;
        private SerializedProperty _useM2CStatusFallback;
        private SerializedProperty _m2cFallbackAfterSeconds;
        private SerializedProperty _deepLinkScheme;
        private SerializedProperty _useAssociatedDomains;
        private SerializedProperty _associatedDomain;

        private static Texture2D _logoLight;
        private static Texture2D _logoDark;
        private static bool _advancedExpanded;
        private static bool _webGLExpanded;
        private static bool _mobileKeyOverridesExpanded;
        private static bool _mobileUrlOverridesExpanded;
        private static bool _statusUrlExpanded;
        private static GUIStyle _titleStyle;
        private static GUIStyle _subtitleStyle;
        private static GUIStyle _sectionStyle;
        private static GUIStyle _hintStyle;

        private void OnEnable()
        {
            _publishableKey = serializedObject.FindProperty("PublishableKey");
            _webGLPublishableKey = serializedObject.FindProperty("WebGLPublishableKey");
            _iosPublishableKey = serializedObject.FindProperty("IosPublishableKey");
            _androidPublishableKey = serializedObject.FindProperty("AndroidPublishableKey");
            _returnUrl = serializedObject.FindProperty("ReturnUrl");
            _cancelUrl = serializedObject.FindProperty("CancelUrl");
            _webGLReturnUrl = serializedObject.FindProperty("WebGLReturnUrl");
            _webGLCancelUrl = serializedObject.FindProperty("WebGLCancelUrl");
            _webGLLaunchMode = serializedObject.FindProperty("WebGLLaunchMode");
            _statusUrlTemplate = serializedObject.FindProperty("StatusUrlTemplate");
            _browserMode = serializedObject.FindProperty("BrowserMode");
            _statusPollTimeoutSeconds = serializedObject.FindProperty("StatusPollTimeoutSeconds");
            _useM2CStatusFallback = serializedObject.FindProperty("UseM2CStatusFallback");
            _m2cFallbackAfterSeconds = serializedObject.FindProperty("M2CFallbackAfterSeconds");
            _deepLinkScheme = serializedObject.FindProperty("DeepLinkScheme");
            _useAssociatedDomains = serializedObject.FindProperty("UseAssociatedDomains");
            _associatedDomain = serializedObject.FindProperty("AssociatedDomain");

            if (HasAnyText(_webGLPublishableKey, _webGLReturnUrl, _webGLCancelUrl)
                || _webGLLaunchMode.enumValueIndex != (int)M2CWebGLLaunchMode.Auto
                || IsWebGLBuildTarget())
                _webGLExpanded = true;
            if (HasAnyText(_iosPublishableKey, _androidPublishableKey))
                _mobileKeyOverridesExpanded = true;
            if (HasAnyText(_returnUrl, _cancelUrl))
                _mobileUrlOverridesExpanded = true;
            if (HasText(_statusUrlTemplate))
                _statusUrlExpanded = true;
        }

        public override void OnInspectorGUI()
        {
            serializedObject.Update();
            EnsureStyles();

            DrawBrandingHeader();
            DrawRuntimeLocation();
            DrawCheckoutKeysSection();
            DrawMobileReturnSection();
            DrawStatusSection();
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

        private void DrawCheckoutKeysSection()
        {
            DrawSection("Checkout Keys", "Start with your mobile publishable key. Platform-specific keys stay hidden until you need them.");
            DrawProperty(_publishableKey, "Mobile Publishable Key", "Mobile publishable key for iOS and Android. Never put a secret key in Unity.");

            bool hasMobileOverrides = HasAnyText(_iosPublishableKey, _androidPublishableKey);
            if (DrawOptionalToggle(ref _mobileKeyOverridesExpanded, "Use separate iOS and Android keys", hasMobileOverrides, "Only needed if you create separate mobile publishable keys per platform."))
            {
                using (new EditorGUI.IndentLevelScope())
                {
                    DrawProperty(_iosPublishableKey, "iOS Publishable Key", "Optional iOS mobile publishable key override.");
                    DrawProperty(_androidPublishableKey, "Android Publishable Key", "Optional Android mobile publishable key override.");
                }
            }

            bool hasWebGLSettings = HasAnyText(_webGLPublishableKey, _webGLReturnUrl, _webGLCancelUrl)
                                    || _webGLLaunchMode.enumValueIndex != (int)M2CWebGLLaunchMode.Auto;
            if (DrawOptionalToggle(ref _webGLExpanded, "Configure WebGL builds", hasWebGLSettings, "Show browser/WebGL key and return URL fields."))
            {
                using (new EditorGUI.IndentLevelScope())
                {
                    DrawProperty(_webGLPublishableKey, "WebGL Publishable Key", "Web/browser publishable key for WebGL client-initiated checkout and M2C status polling. Backend-session flows with a custom status URL can leave this blank.");

                    if (string.IsNullOrEmpty(Trim(_webGLPublishableKey)) && string.IsNullOrEmpty(Trim(_statusUrlTemplate)))
                    {
                        EditorGUILayout.HelpBox("WebGL client-initiated checkout and M2C status polling need a web/browser publishable key. Mobile keys are not used by WebGL.", MessageType.Warning);
                    }

                    DrawProperty(_webGLReturnUrl, "WebGL Success URL", "Success page for WebGL. Must be http(s), and its origin must match the game page or be allowed on the web publishable key when using client-initiated checkout.");
                    DrawProperty(_webGLCancelUrl, "WebGL Cancel URL", "Cancel page for WebGL. Must be http(s), and its origin must match the game page or be allowed on the web publishable key when using client-initiated checkout.");
                    DrawProperty(_webGLLaunchMode, "WebGL Launch Mode", "Browser hint for WebGL checkout. Browsers may still choose whether the checkout appears as a tab, popup window, or mobile tab sheet.");

                    ValidateWebGLUrl(_webGLReturnUrl, "WebGL Success URL");
                    ValidateWebGLUrl(_webGLCancelUrl, "WebGL Cancel URL");

                    DrawReadOnlyTextIfDifferent("Effective WebGL Success URL", _webGLReturnUrl, EffectiveWebGLUrl(_webGLReturnUrl, _returnUrl));
                    DrawReadOnlyTextIfDifferent("Effective WebGL Cancel URL", _webGLCancelUrl, EffectiveWebGLUrl(_webGLCancelUrl, _cancelUrl));
                }
            }
        }

        private void DrawStatusSection()
        {
            DrawSection("Fulfillment Status", "By default the SDK polls M2C with the publishable key. Use a custom URL only for backend-fed fulfillment state.");

            string statusUrl = Trim(_statusUrlTemplate);
            bool showStatusUrl = DrawOptionalToggle(ref _statusUrlExpanded, "Use custom status URL", !string.IsNullOrEmpty(statusUrl), "Show the backend status URL template field.");
            if (showStatusUrl)
            {
                using (new EditorGUI.IndentLevelScope())
                {
                    DrawProperty(_statusUrlTemplate, "Status URL", "Optional URL template. Include {request_id}; the SDK replaces it with the checkout request id.");
                }
            }

            if (!string.IsNullOrEmpty(statusUrl) && !statusUrl.Contains(RequestIdToken))
            {
                EditorGUILayout.HelpBox("Status URL must contain " + RequestIdToken + ".", MessageType.Error);
            }
            else if (string.IsNullOrEmpty(statusUrl)
                     && !HasAnyPublishableKey())
            {
                EditorGUILayout.HelpBox("Blank status URL uses M2C status polling, which requires a publishable key for each platform that uses it. Backend-only flows should set a status URL or create a callback in code.", MessageType.Warning);
            }
        }

        private void DrawAdvancedSection()
        {
            EditorGUILayout.Space(10);
            _advancedExpanded = EditorGUILayout.Foldout(_advancedExpanded, "Advanced Settings", true, _sectionStyle);
            if (!_advancedExpanded) return;

            DrawProperty(_browserMode, "Browser Mode", "Choose whether checkout prefers the in-app browser or always opens the external system browser.");
            DrawProperty(_statusPollTimeoutSeconds, "Status Poll Timeout", "Total seconds to poll conversion status before resolving PendingTimeout.");

            if (_statusPollTimeoutSeconds.floatValue <= 0f
                || float.IsNaN(_statusPollTimeoutSeconds.floatValue)
                || float.IsInfinity(_statusPollTimeoutSeconds.floatValue))
            {
                EditorGUILayout.HelpBox("Status Poll Timeout must be greater than 0 seconds.", MessageType.Error);
            }

            EditorGUILayout.Space(6);
            DrawProperty(_useM2CStatusFallback, "Use M2C Status Fallback", "If your backend status endpoint lags, also check M2C's checkout status after a short delay. Customer-facing progress only - grant goods from your signed conversion webhook.");
            if (_useM2CStatusFallback.boolValue)
            {
                using (new EditorGUI.IndentLevelScope())
                {
                    DrawProperty(_m2cFallbackAfterSeconds, "Fallback After Seconds", "Seconds to wait before also checking M2C status. Clamped to 5-60.");
                    if (_m2cFallbackAfterSeconds.floatValue < M2CCheckoutSettings.MinFallbackAfterSeconds
                        || _m2cFallbackAfterSeconds.floatValue > M2CCheckoutSettings.MaxFallbackAfterSeconds)
                    {
                        EditorGUILayout.HelpBox(
                            "Fallback After Seconds is clamped to " + M2CCheckoutSettings.MinFallbackAfterSeconds
                            + "-" + M2CCheckoutSettings.MaxFallbackAfterSeconds + " seconds.",
                            MessageType.Info);
                    }
                    if (!HasAnyPublishableKey())
                    {
                        EditorGUILayout.HelpBox("The M2C status fallback reads M2C with a publishable key. Set a publishable key for the platforms that use it, or turn this off.", MessageType.Warning);
                    }
                }
            }
        }

        private void DrawMobileReturnSection()
        {
            DrawSection("Mobile Return", "Most games only need a deep link scheme. The success and cancel URLs are derived automatically.");
            DrawProperty(_deepLinkScheme, "Deep Link Scheme", "Scheme only, without ://. Example: mygame for mygame://checkout/return.");

            bool hasMobileUrlOverrides = HasAnyText(_returnUrl, _cancelUrl);
            if (DrawOptionalToggle(ref _mobileUrlOverridesExpanded, "Use custom mobile return URLs", hasMobileUrlOverrides, "Only needed when you do not want URLs derived from the deep link scheme."))
            {
                using (new EditorGUI.IndentLevelScope())
                {
                    DrawProperty(_returnUrl, "Success URL", "Explicit mobile success return URL. Leave blank to derive it from the deep link scheme.");
                    DrawProperty(_cancelUrl, "Cancel URL", "Explicit mobile cancel return URL. Leave blank to derive it from the deep link scheme.");
                }
            }

            string effectiveReturnUrl = EffectiveMobileUrl(_returnUrl, "checkout/return");
            string effectiveCancelUrl = EffectiveMobileUrl(_cancelUrl, "checkout/cancel");
            DrawReadOnlyTextIfDifferent("Effective Success URL", _returnUrl, effectiveReturnUrl);
            DrawReadOnlyTextIfDifferent("Effective Cancel URL", _cancelUrl, effectiveCancelUrl);

            DrawProperty(_useAssociatedDomains, "Use Universal/App Links", "Also configure HTTPS Universal Links on iOS and App Links on Android.");
            if (_useAssociatedDomains.boolValue)
            {
                using (new EditorGUI.IndentLevelScope())
                {
                    DrawProperty(_associatedDomain, "Associated Domain", "Host only. Example: links.mygame.com.");
                }
            }

            if (!IsCustomSchemeUrl(effectiveReturnUrl)
                && !IsCustomSchemeUrl(effectiveCancelUrl)
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
                EditorGUILayout.LabelField(description, _hintStyle);
        }

        private static bool DrawOptionalToggle(ref bool expanded, string label, bool forceExpanded, string tooltip)
        {
            bool current = forceExpanded || expanded;
            bool next = EditorGUILayout.ToggleLeft(new GUIContent(label, tooltip), current);
            expanded = forceExpanded || next;
            return expanded;
        }

        private static void DrawProperty(SerializedProperty property, string label, string tooltip, bool includeChildren = false)
        {
            EditorGUILayout.PropertyField(property, new GUIContent(label, tooltip), includeChildren);
        }

        private static void DrawReadOnlyText(string label, string value)
        {
            EditorGUI.BeginDisabledGroup(true);
            EditorGUILayout.TextField(label, string.IsNullOrEmpty(value) ? "(not set)" : value);
            EditorGUI.EndDisabledGroup();
        }

        private static void DrawReadOnlyTextIfDifferent(string label, SerializedProperty input, string value)
        {
            if (string.IsNullOrEmpty(value)) return;
            if (string.Equals(Trim(input), value, System.StringComparison.Ordinal)) return;
            DrawReadOnlyText(label, value);
        }

        private string EffectiveMobileUrl(SerializedProperty explicitUrl, string path)
        {
            string value = Trim(explicitUrl);
            if (!string.IsNullOrEmpty(value)) return value;

            string scheme = Trim(_deepLinkScheme);
            return string.IsNullOrEmpty(scheme) ? string.Empty : scheme + "://" + path;
        }

        private static string EffectiveWebGLUrl(SerializedProperty webGLUrl, SerializedProperty legacyUrl)
        {
            string value = Trim(webGLUrl);
            if (!string.IsNullOrEmpty(value)) return value;

            value = Trim(legacyUrl);
            return M2CCheckoutSettings.IsHttpUrl(value) ? value : string.Empty;
        }

        private static void ValidateWebGLUrl(SerializedProperty property, string label)
        {
            string value = Trim(property);
            if (!string.IsNullOrEmpty(value) && !M2CCheckoutSettings.IsHttpUrl(value))
                EditorGUILayout.HelpBox(label + " must be an http:// or https:// URL.", MessageType.Error);
        }

        private static bool IsCustomSchemeUrl(string value)
        {
            value = (value ?? string.Empty).Trim();
            int schemeEnd = value.IndexOf("://", System.StringComparison.Ordinal);
            if (schemeEnd <= 0) return false;
            string scheme = value.Substring(0, schemeEnd);
            return !string.Equals(scheme, "http", System.StringComparison.OrdinalIgnoreCase)
                   && !string.Equals(scheme, "https", System.StringComparison.OrdinalIgnoreCase);
        }

        private static bool HasAnyText(params SerializedProperty[] properties)
        {
            for (int i = 0; i < properties.Length; i++)
            {
                if (HasText(properties[i])) return true;
            }
            return false;
        }

        private static bool HasText(SerializedProperty property)
        {
            return !string.IsNullOrEmpty(Trim(property));
        }

        private bool HasAnyPublishableKey()
        {
            return HasAnyText(_publishableKey, _webGLPublishableKey, _iosPublishableKey, _androidPublishableKey);
        }

        private static bool IsWebGLBuildTarget()
        {
            return EditorUserBuildSettings.activeBuildTarget == BuildTarget.WebGL;
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
            _hintStyle = new GUIStyle(EditorStyles.wordWrappedMiniLabel)
            {
                padding = new RectOffset(0, 0, 0, 4),
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
            return info != null && !string.IsNullOrEmpty(info.version) ? info.version : "0.2.3";
        }
    }
}
