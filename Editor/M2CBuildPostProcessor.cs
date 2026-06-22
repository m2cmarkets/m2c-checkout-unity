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
    /// Wires the mobile return path into the generated platform project so the game
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
        private const string AndroidxBrowserDependency = "androidx.browser:browser:1.9.0";
        private const string AndroidxActivityDependency = "androidx.activity:activity:1.9.3";
        private const string KotlinStdlibVersion = "1.8.22";
        private const string KotlinAlignmentMarker = "M2C_KOTLIN_STDLIB_ALIGNMENT_1_8_22";

        public void OnPostGenerateGradleAndroidProject(string path)
        {
            // androidx.browser (Chrome Custom Tabs) is needed for in-app tabs
            // regardless of the return-scheme settings asset, so wire it before the
            // settings gate below.
            AddAndroidBrowserGradleDependencies(path);
            AddRootKotlinResolutionStrategy(path);
            AddAuthTabHelperActivity(path);

            var settings = M2CCheckoutSettingsEditor.FindAsset();
            if (settings == null)
            {
                Debug.LogWarning("[M2C] No M2CCheckoutSettings asset found; skipping Android return intent-filter setup. " +
                                 "Open or create one via Assets > M2C > Find or Create Checkout Settings.");
                return;
            }
            ConfigureAndroid(path, settings);
        }

        // Adds the AndroidX Browser / Activity dependencies to the generated
        // unityLibrary build.gradle so the package is self-contained without EDM4U.
        //
        // Deliberately APPEND-ONLY and idempotent. Gradle merges multiple top-level
        // `dependencies { }` blocks, so appending our own block never parses or edits
        // Unity's existing block - the class of edit that corrupts gradle files. It
        // no-ops when the required dependencies are already declared. If an older
        // Browser line exists, appending 1.9.0 lets Gradle choose the newer version.
        // Kotlin stdlib variants are also declared at one floor version so Unity's
        // older bundled kotlin-stdlib-jdk7/jdk8 cannot collide with AndroidX's newer
        // Kotlin main jar. If anything is off (missing file) it warns and skips: Auth
        // Tab falls back to Custom Tabs, and Custom Tabs fall back to the system browser
        // when the library is absent, so a skip here is never fatal.
        private static void AddAndroidBrowserGradleDependencies(string path)
        {
            string gradlePath = Path.Combine(path, "build.gradle");
            if (!File.Exists(gradlePath))
            {
                Debug.LogWarning("[M2C] unityLibrary build.gradle not found at " + gradlePath +
                                 "; skipping AndroidX browser dependencies. Auth Tab will fall back to plain Custom Tabs, " +
                                 "and Custom Tabs will fall back to the system browser unless you add " +
                                 "'" + AndroidxBrowserDependency + "' and '" + AndroidxActivityDependency + "' yourself (or via EDM4U).");
                return;
            }

            string gradle = File.ReadAllText(gradlePath);
            string kotlinStdlib = "org.jetbrains.kotlin:kotlin-stdlib:" + KotlinStdlibVersion;
            string kotlinStdlibJdk7 = "org.jetbrains.kotlin:kotlin-stdlib-jdk7:" + KotlinStdlibVersion;
            string kotlinStdlibJdk8 = "org.jetbrains.kotlin:kotlin-stdlib-jdk8:" + KotlinStdlibVersion;
            bool hasBrowser19 = gradle.Contains(AndroidxBrowserDependency);
            bool hasActivity = gradle.Contains(AndroidxActivityDependency);
            bool hasKotlinStdlib = gradle.Contains(kotlinStdlib);
            bool hasKotlinStdlibJdk7 = gradle.Contains(kotlinStdlibJdk7);
            bool hasKotlinStdlibJdk8 = gradle.Contains(kotlinStdlibJdk8);
            if (hasBrowser19 && hasActivity && hasKotlinStdlib && hasKotlinStdlibJdk7 && hasKotlinStdlibJdk8)
                return; // already declared - do not double-add

            string addition = "\n" +
                              "// Added by M2C Checkout for in-app Chrome Custom Tabs / Auth Tab. Remove if you manage these via EDM4U.\n" +
                              "dependencies {\n";
            if (!hasBrowser19)
                addition += "    implementation '" + AndroidxBrowserDependency + "'\n";
            if (!hasActivity)
                addition += "    implementation '" + AndroidxActivityDependency + "'\n";
            if (!hasKotlinStdlib || !hasKotlinStdlibJdk7 || !hasKotlinStdlibJdk8)
            {
                addition += "    // Align Kotlin stdlib so androidx.browser 1.9 / androidx.activity (Kotlin 1.8) do not\n";
                addition += "    // collide with Unity's bundled kotlin-stdlib-jdk7/jdk8 (duplicate-class build failure).\n";
                if (!hasKotlinStdlib)
                    addition += "    implementation '" + kotlinStdlib + "'\n";
                if (!hasKotlinStdlibJdk7)
                    addition += "    implementation '" + kotlinStdlibJdk7 + "'\n";
                if (!hasKotlinStdlibJdk8)
                    addition += "    implementation '" + kotlinStdlibJdk8 + "'\n";
            }
            addition += "}\n";
            File.AppendAllText(gradlePath, addition);
            Debug.Log("[M2C] Added AndroidX browser / activity + aligned kotlin-stdlib in unityLibrary build.gradle for in-app Custom Tabs / Auth Tab.");
        }

        // Applies the Kotlin stdlib floor to every generated Gradle subproject. This
        // catches older stdlib requests from Unity or another SDK on the launcher
        // runtime classpath, while leaving newer Kotlin versions alone.
        private static void AddRootKotlinResolutionStrategy(string unityLibraryPath)
        {
            DirectoryInfo parent = Directory.GetParent(unityLibraryPath);
            if (parent == null)
            {
                Debug.LogWarning("[M2C] Could not locate generated Gradle root; skipping Kotlin stdlib alignment.");
                return;
            }

            string rootGradlePath = Path.Combine(parent.FullName, "build.gradle");
            if (!File.Exists(rootGradlePath))
            {
                Debug.LogWarning("[M2C] Root build.gradle not found at " + rootGradlePath + "; skipping Kotlin stdlib alignment.");
                return;
            }

            string gradle = File.ReadAllText(rootGradlePath);
            if (gradle.Contains(KotlinAlignmentMarker))
                return;

            string addition = "\n" +
                              "// " + KotlinAlignmentMarker + "_BEGIN\n" +
                              "// Added by M2C Checkout. Keep Kotlin stdlib artifacts on one floor version so\n" +
                              "// AndroidX Browser/Auth Tab does not collide with Unity or SDK-bundled older jdk7/jdk8 jars.\n" +
                              "def m2cKotlinStdlibFloor = '" + KotlinStdlibVersion + "'\n" +
                              "def m2cKotlinStdlibModules = ['kotlin-stdlib', 'kotlin-stdlib-common', 'kotlin-stdlib-jdk7', 'kotlin-stdlib-jdk8']\n" +
                              "def m2cVersionBelow = { requested, floor ->\n" +
                              "    if (requested == null || requested.length() == 0) return false\n" +
                              "    def readPart = { part ->\n" +
                              "        if (!(part ==~ /\\d+/)) return null\n" +
                              "        part.toInteger()\n" +
                              "    }\n" +
                              "    def requestedParts = requested.tokenize('.-').collect(readPart)\n" +
                              "    def floorParts = floor.tokenize('.-').collect(readPart)\n" +
                              "    if (requestedParts.contains(null) || floorParts.contains(null)) return false\n" +
                              "    int max = Math.max(requestedParts.size(), floorParts.size())\n" +
                              "    for (int i = 0; i < max; i++) {\n" +
                              "        int requestedPart = i < requestedParts.size() ? requestedParts[i] : 0\n" +
                              "        int floorPart = i < floorParts.size() ? floorParts[i] : 0\n" +
                              "        if (requestedPart < floorPart) return true\n" +
                              "        if (requestedPart > floorPart) return false\n" +
                              "    }\n" +
                              "    return false\n" +
                              "}\n" +
                              "subprojects {\n" +
                              "    configurations.all {\n" +
                              "        resolutionStrategy.eachDependency { details ->\n" +
                              "            if (details.requested.group == 'org.jetbrains.kotlin' &&\n" +
                              "                    m2cKotlinStdlibModules.contains(details.requested.name) &&\n" +
                              "                    m2cVersionBelow(details.requested.version, m2cKotlinStdlibFloor)) {\n" +
                              "                details.useVersion m2cKotlinStdlibFloor\n" +
                              "                details.because 'M2C Checkout aligns Kotlin stdlib artifacts to avoid duplicate classes with AndroidX Browser/Auth Tab.'\n" +
                              "            }\n" +
                              "        }\n" +
                              "    }\n" +
                              "}\n" +
                              "// " + KotlinAlignmentMarker + "_END\n";
            File.AppendAllText(rootGradlePath, addition);
            Debug.Log("[M2C] Added root Kotlin stdlib alignment for Android dependency resolution.");
        }

        // Registers the translucent helper activity that hosts the Auth Tab result
        // launcher. Auth Tab delivers its result only through an ActivityResult launcher
        // registered on an AndroidX ComponentActivity (which Unity's player activity is
        // not), so M2CAuthTabActivity stands in. No intent-filter is needed (Auth Tab
        // captures the redirect itself). Idempotent and non-fatal: if anything is off,
        // the SDK falls back to plain Custom Tabs at runtime.
        private static void AddAuthTabHelperActivity(string path)
        {
            string manifestPath = Path.Combine(path, "src", "main", "AndroidManifest.xml");
            if (!File.Exists(manifestPath))
                manifestPath = Path.Combine(path, "AndroidManifest.xml");
            if (!File.Exists(manifestPath))
            {
                Debug.LogWarning("[M2C] AndroidManifest.xml not found; skipping M2CAuthTabActivity registration. " +
                                 "Auth Tab will fall back to plain Custom Tabs at runtime.");
                return;
            }

            const string androidNs = "http://schemas.android.com/apk/res/android";
            const string activityName = "com.m2c.checkout.M2CAuthTabActivity";

            var doc = new XmlDocument { PreserveWhitespace = true };
            doc.Load(manifestPath);
            XmlElement application = FirstChild(doc.DocumentElement, "application");
            if (application == null)
            {
                Debug.LogWarning("[M2C] <application> not found in AndroidManifest.xml; skipping M2CAuthTabActivity registration.");
                return;
            }

            foreach (XmlNode node in application.GetElementsByTagName("activity"))
            {
                var existing = node as XmlElement;
                if (existing != null && existing.GetAttribute("name", androidNs) == activityName)
                    return; // already registered - idempotent
            }

            XmlElement activity = doc.CreateElement("activity");
            activity.SetAttribute("name", androidNs, activityName);
            activity.SetAttribute("exported", androidNs, "false");
            activity.SetAttribute("theme", androidNs, "@android:style/Theme.Translucent.NoTitleBar");
            application.AppendChild(activity);
            doc.Save(manifestPath);
            Debug.Log("[M2C] Registered M2CAuthTabActivity for Android Auth Tab return handling.");
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
            foreach (string scheme in settings.EffectiveMobileCustomSchemes)
                AddUrlScheme(plist, scheme);
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
            if (HasUrlScheme(urlTypes, scheme)) return;

            PlistElementDict entry = urlTypes.AddDict();
            entry.SetString("CFBundleURLName", "com.m2c.checkout." + scheme);
            PlistElementArray schemes = entry.CreateArray("CFBundleURLSchemes");
            schemes.AddString(scheme);
        }

        private static bool HasUrlScheme(PlistElementArray urlTypes, string scheme)
        {
            foreach (PlistElement typeElement in urlTypes.values)
            {
                var typeDict = typeElement as PlistElementDict;
                if (typeDict == null) continue;

                PlistElementArray schemes = typeDict["CFBundleURLSchemes"] as PlistElementArray;
                if (schemes == null) continue;

                foreach (PlistElement schemeElement in schemes.values)
                {
                    var schemeString = schemeElement as PlistElementString;
                    if (schemeString != null && string.Equals(schemeString.value, scheme, System.StringComparison.OrdinalIgnoreCase))
                        return true;
                }
            }
            return false;
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

            foreach (string scheme in settings.EffectiveMobileCustomSchemes)
                AddIntentFilter(doc, activity, androidNs, scheme, null, false);

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
