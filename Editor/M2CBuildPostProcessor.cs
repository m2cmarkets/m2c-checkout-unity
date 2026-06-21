using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;
#if UNITY_IOS || UNITY_ANDROID
using System.IO;
#endif
#if UNITY_ANDROID
using System.Xml;
using UnityEditor.Android;
#endif
#if UNITY_IOS
using UnityEditor.iOS.Xcode;
#endif

namespace M2C.Checkout.Editor
{
    /// <summary>
    /// Wires the native return path into the generated platform project so the game
    /// developer just imports the package and builds. iOS: links
    /// AuthenticationServices.framework and registers URL schemes / Associated
    /// Domains. Android: adds the return intent-filter to the generated manifest.
    ///
    /// VERIFY ON DEVICE: build post-processors can't be exercised without the iOS /
    /// Android build modules and a real build.
    /// </summary>
    public sealed class M2CBuildPostProcessor : IPostprocessBuildWithReport
#if UNITY_ANDROID
        , IPostGenerateGradleAndroidProject
#endif
    {
        public int callbackOrder => 0;

        public void OnPostprocessBuild(BuildReport report)
        {
            var settings = M2CCheckoutSettingsEditor.FindAsset();
#if UNITY_IOS
            if (report.summary.platform == BuildTarget.iOS)
            {
                ConfigureiOS(report.summary.outputPath, settings);
                return;
            }
#endif

            if (settings == null)
            {
                Debug.LogWarning("[M2C] No M2CCheckoutSettings asset found; skipping return deep-link / framework setup. " +
                                 "Open or create one via Assets > M2C > Find or Create Checkout Settings.");
                return;
            }
        }

#if UNITY_ANDROID
        public void OnPostGenerateGradleAndroidProject(string path)
        {
            // androidx.browser (Chrome Custom Tabs) is needed for in-app tabs
            // regardless of the return-scheme settings asset, so wire it before the
            // settings gate below.
            AddCustomTabsGradleDependency(path);

            var settings = M2CCheckoutSettingsEditor.FindAsset();
            if (settings == null)
            {
                Debug.LogWarning("[M2C] No M2CCheckoutSettings asset found; skipping Android return intent-filter setup. " +
                                 "Open or create one via Assets > M2C > Find or Create Checkout Settings.");
                return;
            }
            ConfigureAndroid(path, settings);
        }

        // Adds the AndroidX Browser dependency (in-app Custom Tabs) to the generated
        // unityLibrary build.gradle so the package is self-contained without EDM4U.
        //
        // Deliberately APPEND-ONLY and idempotent. Gradle merges multiple top-level
        // `dependencies { }` blocks, so appending our own block never parses or edits
        // Unity's existing block - the class of edit that corrupts gradle files. It
        // no-ops if the artifact is already declared (EDM4U, a hand edit, or a prior
        // run write that survived), and if anything is off (missing file) it warns and
        // skips: the Custom Tabs path falls back to the system browser when the library
        // is absent, so a skip here is never fatal.
        private static void AddCustomTabsGradleDependency(string path)
        {
            string gradlePath = Path.Combine(path, "build.gradle");
            if (!File.Exists(gradlePath))
            {
                Debug.LogWarning("[M2C] unityLibrary build.gradle not found at " + gradlePath +
                                 "; skipping the androidx.browser dependency. In-app Custom Tabs will fall back to the " +
                                 "system browser unless you add 'androidx.browser:browser:1.8.0' yourself (or via EDM4U).");
                return;
            }

            string gradle = File.ReadAllText(gradlePath);
            if (gradle.Contains("androidx.browser:browser"))
                return; // already declared - do not double-add

            string addition = "\n" +
                              "// Added by M2C Checkout for in-app Chrome Custom Tabs. Remove if you manage this via EDM4U.\n" +
                              "dependencies {\n" +
                              "    implementation 'androidx.browser:browser:1.8.0'\n" +
                              "}\n";
            File.AppendAllText(gradlePath, addition);
            Debug.Log("[M2C] Added androidx.browser:browser:1.8.0 to unityLibrary build.gradle for in-app Custom Tabs.");
        }
#endif

#if UNITY_IOS
        private static void ConfigureiOS(string projectPath, M2CCheckoutSettings settings)
        {
            string pbxPath = PBXProject.GetPBXProjectPath(projectPath);
            var pbx = new PBXProject();
            pbx.ReadFromFile(pbxPath);
            string mainTarget = pbx.GetUnityMainTargetGuid();
            string frameworkTarget = pbx.GetUnityFrameworkTargetGuid();
            pbx.AddFrameworkToProject(frameworkTarget, "AuthenticationServices.framework", false);
            pbx.AddFrameworkToProject(mainTarget, "AuthenticationServices.framework", false);
            pbx.WriteToFile(pbxPath);

            if (settings == null)
            {
                Debug.LogWarning("[M2C] No M2CCheckoutSettings asset found; linked AuthenticationServices.framework but skipped iOS return deep-link / associated-domain setup. " +
                                 "Open or create one via Assets > M2C > Find or Create Checkout Settings.");
                return;
            }

            string plistPath = Path.Combine(projectPath, "Info.plist");
            var plist = new PlistDocument();
            plist.ReadFromFile(plistPath);
            string deepLinkScheme = settings.EffectiveDeepLinkScheme;
            if (!string.IsNullOrEmpty(deepLinkScheme))
                AddUrlScheme(plist, deepLinkScheme);
            plist.WriteToFile(plistPath);

            string associatedDomain = settings.EffectiveAssociatedDomain;
            if (settings.UseAssociatedDomains && !string.IsNullOrEmpty(associatedDomain))
            {
                AddAssociatedDomains(pbxPath, mainTarget, associatedDomain);
            }
        }

        private static void AddUrlScheme(PlistDocument plist, string scheme)
        {
            PlistElementDict root = plist.root;
            PlistElementArray urlTypes = root["CFBundleURLTypes"] as PlistElementArray ?? root.CreateArray("CFBundleURLTypes");
            PlistElementDict entry = urlTypes.AddDict();
            entry.SetString("CFBundleURLName", "com.m2c.checkout." + scheme);
            PlistElementArray schemes = entry.CreateArray("CFBundleURLSchemes");
            schemes.AddString(scheme);
        }

        private static void AddAssociatedDomains(string pbxPath, string targetGuid, string host)
        {
            object manager = CreateCapabilityManager(pbxPath, "M2CCheckout.entitlements", "Unity-iPhone", targetGuid);
            var domains = new[] { "applinks:" + host };
            manager.GetType().GetMethod("AddAssociatedDomains", new[] { typeof(string[]) }).Invoke(manager, new object[] { domains });
            manager.GetType().GetMethod("WriteToFile").Invoke(manager, null);
        }

        private static object CreateCapabilityManager(string pbxPath, string entitlementsFile, string targetName, string targetGuid)
        {
            var type = typeof(ProjectCapabilityManager);
            var fourArg = type.GetConstructor(new[] { typeof(string), typeof(string), typeof(string), typeof(string) });
            if (fourArg != null)
                return fourArg.Invoke(new object[] { pbxPath, entitlementsFile, targetName, targetGuid });
            return type.GetConstructor(new[] { typeof(string), typeof(string), typeof(string) })
                .Invoke(new object[] { pbxPath, entitlementsFile, targetName });
        }
#endif

#if UNITY_ANDROID
        private static void ConfigureAndroid(string projectPath, M2CCheckoutSettings settings)
        {
            string manifestPath = Path.Combine(projectPath, "src", "main", "AndroidManifest.xml");
            if (!File.Exists(manifestPath))
                manifestPath = Path.Combine(projectPath, "AndroidManifest.xml");
            if (!File.Exists(manifestPath))
            {
                Debug.LogWarning("[M2C] AndroidManifest.xml not found; skipping M2C return intent-filter setup.");
                return;
            }

            var doc = new XmlDocument { PreserveWhitespace = true };
            doc.Load(manifestPath);
            XmlElement manifest = doc.DocumentElement;
            if (manifest == null)
            {
                Debug.LogWarning("[M2C] AndroidManifest.xml had no manifest root; skipping M2C return intent-filter setup.");
                return;
            }

            const string androidNs = "http://schemas.android.com/apk/res/android";
            if (string.IsNullOrEmpty(manifest.GetAttribute("xmlns:android")))
                manifest.SetAttribute("xmlns:android", androidNs);

            XmlElement application = FirstChild(manifest, "application");
            XmlElement activity = FindUnityActivity(application, androidNs);
            if (activity == null)
            {
                Debug.LogWarning("[M2C] No Android activity found; skipping M2C return intent-filter setup.");
                return;
            }

            string deepLinkScheme = settings.EffectiveDeepLinkScheme;
            if (!string.IsNullOrEmpty(deepLinkScheme))
                AddIntentFilter(doc, activity, androidNs, deepLinkScheme, null, false);

            string associatedDomain = settings.EffectiveAssociatedDomain;
            if (settings.UseAssociatedDomains && !string.IsNullOrEmpty(associatedDomain))
                AddIntentFilter(doc, activity, androidNs, "https", associatedDomain, true);

            doc.Save(manifestPath);
        }

        private static XmlElement FindUnityActivity(XmlElement application, string androidNs)
        {
            if (application == null) return null;
            XmlElement firstActivity = null;
            foreach (XmlNode node in application.ChildNodes)
            {
                var activity = node as XmlElement;
                if (activity == null || activity.Name != "activity") continue;
                if (firstActivity == null) firstActivity = activity;
                string name = activity.GetAttribute("name", androidNs);
                if (name.Contains("UnityPlayerActivity")) return activity;
            }
            return firstActivity;
        }

        private static void AddIntentFilter(XmlDocument doc, XmlElement activity, string androidNs, string scheme, string host, bool autoVerify)
        {
            if (HasIntentFilter(activity, androidNs, scheme, host)) return;

            XmlElement filter = doc.CreateElement("intent-filter");
            filter.SetAttribute("autoVerify", androidNs, autoVerify ? "true" : "false");
            AddNamedElement(doc, filter, androidNs, "action", "android.intent.action.VIEW");
            AddNamedElement(doc, filter, androidNs, "category", "android.intent.category.DEFAULT");
            AddNamedElement(doc, filter, androidNs, "category", "android.intent.category.BROWSABLE");

            XmlElement data = doc.CreateElement("data");
            data.SetAttribute("scheme", androidNs, scheme);
            if (!string.IsNullOrEmpty(host))
                data.SetAttribute("host", androidNs, host);
            filter.AppendChild(data);
            activity.AppendChild(filter);
        }

        private static bool HasIntentFilter(XmlElement activity, string androidNs, string scheme, string host)
        {
            foreach (XmlNode filterNode in activity.GetElementsByTagName("intent-filter"))
            {
                var filter = filterNode as XmlElement;
                if (filter == null) continue;
                foreach (XmlNode dataNode in filter.GetElementsByTagName("data"))
                {
                    var data = dataNode as XmlElement;
                    if (data == null) continue;
                    if (data.GetAttribute("scheme", androidNs) != scheme) continue;
                    if ((host ?? string.Empty) == data.GetAttribute("host", androidNs)) return true;
                }
            }
            return false;
        }

        private static void AddNamedElement(XmlDocument doc, XmlElement parent, string androidNs, string elementName, string value)
        {
            XmlElement element = doc.CreateElement(elementName);
            element.SetAttribute("name", androidNs, value);
            parent.AppendChild(element);
        }

        private static XmlElement FirstChild(XmlElement parent, string name)
        {
            if (parent == null) return null;
            foreach (XmlNode node in parent.ChildNodes)
            {
                var element = node as XmlElement;
                if (element != null && element.Name == name) return element;
            }
            return null;
        }
#endif
    }
}
